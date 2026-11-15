# Round 4 加固（2026-11-15 审计后续，第二轮）— 施工记录

来源：`docs/audit/2026-11-15/round3.md` §6 TODO。版本：v2.4.22。
真实事故：2026-11-15 17:41 一个无 `-s` 的 GUI 救援流程把 `fastboot oem dump-emmc ptable`
打到旁边 y700 项目的 fastboot 会话上；GUI 退出后该 fastboot 成孤儿又跑了 4 分钟。

纪律：不碰 loaders/fastboot payload/硬件 blob/串口时序；全程不用真机（只用 stub/harness）。
冻结面（StableParity）只做行为保持型改动，改完刷新 hash 并在 reference 里写清改了什么。

## 1. R1 读命令也守身份 —— 已落地

- `Services/DeviceIdentityGate.cs`：新增 `VerdictRefusedRead = "device-identity-read"`
  （读拒绝不是写拒绝：设备没被写过，日志里两者必须可区分）。
- `Services/Diagnostics/OpLog.cs`：新增 `OpNames.ReadIdentity = "read.identity"`。
- `Services/FastbootClient.cs`：
  - 探测收敛到 `ProbeIdentityAsync()`（每个 client 一次 `getvar product`，读/写闸门共用缓存，
    连原始输出一起缓存，拒绝时不需要二次探测）；
  - 新 `EnsureReadableIdentityAsync()`：**只拦 invalid**，unreadable（无设备/stub/超时/非 0 退出）放行；
  - 接入 `OemCommandAsync()` 与 `CommandAsync()`：`IsDevicePullingCommand()` 只看第一个真子命令，
    `dump-*`（dump-emmc/dump-storage）与 `fetch` 走读闸门；写路径 `EnsureWritableIdentityAsync()`
    的语义不变（仍拦 invalid，只是复用探测）。
- `Services/PreOpBackup.cs`：身份判定**移到建备份目录之前**；`Identity.Refused` 时
  `Attempted=false` + `Error = "device did not identify itself; refusing to dump …"`，
  不建目录、不发任何 dump 命令、不留 dump 文件；op 记 `read.identity` 语义的
  verdict（`device-identity-read`）。测试构造器新增可选 `getVarAsync`（与生产同一条判据路径）。
- 断言：tests/WriteGates §10（stub 记录 argv：bad-product 时 `dump-emmc`/`fetch` 一次都没发；
  silent-product = unreadable 仍放行；真实 product 仍放行），tests/PreOpBackup
  「invalid identity → 0 次 dump、无目录、无 manifest、Error 说明未识别」。

## 2. R2 GUI 关窗中断在跑操作 —— 已落地

- 新文件 `Services/OperationCancellation.cs`：窗口级 kill switch。
  `Track(cts)` 登记每个 GUI 操作的 CancellationTokenSource；`CancelAll()` 取消登记项 +
  窗口级 token，并调用 ProcessRunner 的活子进程登记表做兜底 kill；`ResetForTests()` 供 harness 复位。
- `Utils/ProcessRunner.cs`（唯一子进程创建点）：
  - 每个子进程的等待 token 变成 `timeoutSource` 与 `OperationCancellation.Token` 的 linked token；
  - 取消时 `Kill(entireProcessTree: true)` + `WaitForExit(3000)`（宽限期 3 s），
    op 记 `verdict=cancelled` 并抛 `OperationCanceledException`（不再误记成 timeout/exit-code）；
  - 关窗后再launch的进程直接拒绝启动（`cancelled before launch`），避免关闭过程中又拉起一个子进程。
- `MainWindow.axaml.cs`（冻结面，行为保持型）：
  - 构造器新增 `Closing += (_, _) => OperationCancellation.CancelAll();`（原来只有宿主进程退出才 kill）；
  - 5 处 `new CancellationTokenSource()` 各加一行 `OperationCancellation.Track(...)` registration；
  - dump 进度对话框的完成分支新增「cancelled 优先」文案（原来无条件写 "completed with errors"，
    把取消显示成失败）；设备命令、顺序、时序、重试全部未变。
- `Services/FastbootFlasherService.cs`：`DumpPartitionAsync`/`FlashPartitionAsync` 新增
  `catch (OperationCanceledException)` → 记 `verdict=cancelled` 后 rethrow（原来被通用 catch 吞成
  "Dump failed"），并清掉半截 dump 文件。
- 断言：tests/Kirin9000 新增 4 条 headless 用例（stub fastboot 的 dump 睡 300 s → 记下子进程 pid →
  `owner.Close()` → 0.2 s 内操作结束、`/proc/<pid>` 消失、进度对话框文案含 "Cancelled"）。
- `tests/StableParity/boundary.json`：`MainWindow.axaml.cs` hash 刷新（reference 追加 R2 说明）。

## 3. R3 Windows 端口唯一性 —— 已落地

- 新文件 `Utils/WindowsSerialPortFinder.cs`：与 Linux K7 同口径。
  - 枚举是 injectable seam（`FindPort(vid, pid, enumerate)`，默认 `WindowsEnumerator`）；
  - 候选按**物理 USB 设备**归并：`ParentDeviceId` 优先，否则 PNPDeviceID 去掉 `&MI_xx` 与
    每个接口不同的 instance 尾巴（保留父路径派生的首段）；同一台机器的多个 COM 接口算一台；
  - `>1` 台 → 同一个 `AmbiguousSerialDeviceException`（消息列两台设备与各自 COM，并提示 `--port`）；
  - `PortFromName` 从 PnP 名字里取 `(COMnn)`（无正则转义坑，纯字符扫描）。
- `Services/VcomFlasher.cs`（冻结面）：`FindWindowsPortByVidPid` 变成一行委托，
  不再自己取「第一个 COM」，Linux 分支、帧协议、时序、重试不动；移除不再使用的 `System.Management` using。
- 断言：tests/WriteGates §11（假枚举数据）——单机两 COM 接口 = 一台（返回 COM3）、
  共享 Parent 也算一台、两台同 VID/PID 抛歧义异常（含 COM3/COM7/--port）、无匹配 = null、
  COM 号解析。Windows 未在本机实跑。
- `tests/StableParity/boundary.json`：`Services/VcomFlasher.cs` hash 刷新（reference 追加 R3 说明）。

## 4. R4 `/usr/bin/fastboot` 回退进结果 —— 已落地

- 新文件 `Cli/DegradedSignals.cs`：`system-fastboot-fallback` 判据（不在 `<app>/fastboot` 里的
  fastboot 都算：`/usr/bin/fastboot`、PATH 命中、`KIRIN_FASTBOOT` 覆盖），
  以及把字段合并进结果对象的 `Apply(JsonObject)`。
- `Cli/CliContext.cs`：唯一的 JSON 出口 `WriteJson` 统一注入——
  顶层 `degraded:["system-fastboot-fallback"]`（无回退时是空数组，字段恒在）；
  有 `fastboot` 对象的命令（info/doctor）在其中加 `fallback:true`，其余命令加 `fastbootFallback:true`。
  既有字段一个不改（只增）。CLI `info`/`doctor`/所有写命令都自动带上。
- MCP：tool 结果就是该 JSON 文本（`structuredContent` = 解析后的同一对象），因此同样可见。
- 断言：tests/WriteGates §12（`KIRIN_FASTBOOT` 指向非 bundled stub）——doctor 的 `degraded` 与
  `fastboot.fallback`、写命令（reboot）的 `degraded`+`fastbootFallback`、既有 `applied`/`path` 不变、
  bundled 路径不判为降级；tests/McpProtocol 新增 (g)：MCP `structuredContent.degraded` 与
  text content 都含 token。

## 5. 验证数字

- `dotnet build -c Debug`：0 error（3 个既有 AVLN3001 警告，与本轮无关）。
- `dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false`：0 error。
- `tests/suites.txt` 全量：**25/25 rc=0**。关键套件条数：
  WriteGates **90 → 114**、Kirin9000 **48 → 52**、OpLogging 163、SurfaceParity 413、
  StableParity 35（两个冻结文件刷新 hash 后仍绿）、PreOpBackup 全绿、FastbootReads 全绿、
  McpProtocol **14 → 17**。
- 全程未执行 adb/fastboot，未接触真机（P40 与 y700 未被本 lanes 触碰）。

## 6. 剩余 TODO

- Windows 真机未验证：`WindowsSerialPortFinder.PhysicalDeviceKey` 的 instance 归并是按 PNPDeviceID
  结构推断的，需要在 Windows 上用「一台双 COM + 两台同型号」实测一次（本轮只做了假数据断言）。
- `oem dump-emmc` 的读闸门依赖 `getvar product` 探测；若某 loader 在 dump 前拒绝 `getvar`
  （非 0 退出 → unreadable），仍会放行（与写闸门同一取舍，保留）。
- GUI 里其他非 fastboot 的外部进程（USB update / 测试点服务）在关窗时仍只靠宿主退出 kill；
  它们走各自服务且不带 ProcessRunner 的 linked token（本轮只覆盖 ProcessRunner，即 fastboot/adb）。
- `Cli/CliContext.WriteJson` 现在对所有 JSON 输出加 `degraded` 字段（空数组时也存在）；
  若下游有严格「未知字段即失败」的消费者，需要再评估（MCP 侧无此限制）。
