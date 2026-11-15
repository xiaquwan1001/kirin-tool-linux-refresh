# Round 3 加固（2026-11-15 审计后续）— 施工记录

来源：`/home/xuqianhui/P40pro/docs/logs/审计-2026-11-15-脚本孤儿fastboot与串号匹配.md` §4。
真实事故：2026-11-15 17:41 一个无 `-s` 的 GUI 救援流程把 `fastboot oem dump-emmc ptable`
打到旁边 y700 项目的 fastboot 会话上；GUI 退出后该 fastboot 成孤儿又跑了 4 分钟。

纪律：不碰 loaders/fastboot payload/硬件 blob/串口时序；StableParity 冻结面零改动（无需刷 hash）；
全程不用真机（只用 stub/harness）。

## 1. K6 写入前身份闸门 —— 已落地

- 新文件 `Services/DeviceIdentityGate.cs`：`DeviceIdentity`（Read/Unreadable/Invalid）+ 纯判据。
  - **拒绝**：loader 有回答但解析不出 `product:` 行（事故形状：裸 `OKAY`）、`product` 是
    OKAY/FAILED/unknown/error/null/… 或含空白/控制字符/超长。
  - **放行**：探测根本没跑成（无设备、stub、超时、非 0 退出、空输出）记为 Unreadable——不因
    读不到就拒绝，也不做机型白名单（hisi980/hisi990/hisi9000 命名字符串不受影响）。
  - 家族标记（ELS-/ANA-/NOH-/…、含 "kirin"）只作 `LooksLikeKirinFamily` 提示，从不用于拒绝。
- `Services/FastbootClient.cs`：`EnsureWritableIdentityAsync()`，每个 client 只探测一次并缓存
  （含拒绝缓存）。接入全部不可逆写：`flash`、`flash oeminfo`、`getvar nve:SN@`、
  `erase frp` 三步、4×`oem oeminfoerase-*`、`oem unlock`。拒绝时写一条
  `write.identity` op（verdictSource=`device-identity`）并抛
  `DeviceIdentityRefusedException`。
- `Services/PreOpBackup.cs`：身份判据进 op step + manifest 新增 `identity` 块
  （status/product/serial/refused/reason），结果新增 `Identity` 属性；硬拒绝仍在 FastbootClient。
- `Cli/Commands/DeviceWriteCommands.cs`：`GatedDeviceWriteCommand` 捕获拒绝异常 →
  `exit 3 / applied=false / reason=device_identity_refused`（原来会 unhandled 崩掉）。
- 断言：tests/WriteGates 新增 6e（端到端拒绝）+ 6f（判据纯单测：ELS/NOH 接受、SM-G991B 这类
  未知但合法机型放行、裸 OKAY 拒绝、FAILED/unknown 拒绝、探测失败放行、空输出放行、解析前缀）。
  tests/PreOpBackup 新增 manifest identity 块断言。

## 2. K7 VCOM 串口唯一性 —— 已落地

- `Utils/LinuxSerialPortFinder.FindPort`：先 `MatchingPorts` 收集所有命中端口并标注其
  **物理 USB 设备**（最近的带 idVendor/idProduct 的 sysfs 祖先），再按物理设备去重：
  - 1 台（ttyUSB0..3 四个 option 接口）→ 仍返回第一个端口（行为不变）；
  - >1 台 → `AmbiguousSerialDeviceException`（新增，继承 IOException），消息列出两个设备
    及各自 tty，并提示 `--port` / 拔掉其它设备。
- 未改 `Services/VcomFlasher.cs`（StableParity 冻结面）：`Connect()` 里
  `FindIdtDevicePort()` 抛出的就是 IOException，GUI/CLI 现有错误路径直接显示可读消息。
- 断言：tests/WriteGates 第 8 节用假 sysfs 树（同 StableParity 手法）覆盖
  「一台设备四接口 = 一台」「PID 不匹配 = null」「两台设备 = 拒绝且消息含 --port/ttyUSB0/ttyUSB4」。

## 3. 显式 `-s <SN>` —— 已落地

- `Utils/ProcessRunner.cs`（唯一子进程创建点）：已知 SN（KIRIN_SERIAL > ANDROID_SERIAL >
  P40_SERIAL）时，fastboot/adb 的 argv 前面插入 `-s <SN>`（子命令之前），并把改写后的
  argv 写进 op log / kirintool_log；`devices`/`--version`/help 不加（枚举仍看全部设备）。
- `DeriveOp` 跳过 `-s` 的值，op 名仍是 `fastboot.getvar`，不会被 SN 污染。
- 断言：tests/WriteGates 第 9 节（stub 回显 argv）：`-s SN flash boot …`、
  `devices` 不加、caller argv 保留、DeriveOp 结果正确。

## 4. 同类缺陷审查 —— 部分落地

- 已修：`FastbootClient` 暴露 `ExecutablePath`/`IsSystemFastboot`；回退到
  `/usr/bin/fastboot`（Google platform-tools）时，写路径在诊断日志里先报警。
- 已确认无需改：GUI 所有写入口最终走 `FastbootClient.FlashPartition` /
  `DeviceWriteOps`（`MainWindow.axaml.cs:666/1401`、CLI flash-ota/flash-xml），所以 K6 闸门
  覆盖 GUI 与 CLI 两个 face。
- 保留：ProcessRunner 的多设备拒绝（verdict=device-selection）与宿主退出时 kill 整棵树。

## 5. 验证数字

- `dotnet build -c Debug`：0 error（3 个既有 AVLN3001 警告，与本轮无关）。
- `dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false`：0 error。
- `tests/suites.txt` 全量：**25/25 rc=0**。关键套件条数：
  WriteGates **70 → 90**、Kirin9000 48、OpLogging 163、StableParity 35（冻结面未改，未刷 hash）、
  SurfaceParity 413、ApprovalGate 88、PreOpBackup 全绿。
- 全程未执行 adb/fastboot，未接触真机。

## 6. TODO

- `oem dump-emmc …` 是读命令，K6 只挡写；误机 dump 仍只靠「未固定 SN + >1 台 fastboot」拒绝。
- GUI 关窗仍不「中断」在跑的操作，只靠宿主退出 kill 进程树；显式 Cancel 需改 MainWindow（冻结面）。
- Windows `FindWindowsPortByVidPid` 仍取第一个 COM（K7 只做了 Linux）；Windows 侧同类多设备
  判据待做。
- 回退 `/usr/bin/fastboot` 目前只是诊断日志告警，未在 CLI 结果里作为 degraded 字段输出。
