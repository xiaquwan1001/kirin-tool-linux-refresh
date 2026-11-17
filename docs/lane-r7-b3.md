# lane B3 — 不可逆写类债务清偿（D-16 / D-06 / D-14）

工作目录: `/home/xuqianhui/src/wt/r7-b3-writegates`（分支 `fix/r7-b3-writegates`，基线 main `cd060f8`）
纪律: 零真实设备操作（未跑 adb / fastboot / VCOM / 串口 / usb 探针），未碰 `loaders/`、fastboot payload、硬件 blob，未改 `docs/debt-register.md`。

## 0. 基线

（见下文各节；基线干净与否在此记录）

## 1. D-16（S14）电量闸门 — commit `7687cd5`

状态: **完成**（行为变更型：低电量现在拒绝写入）

### 判据（旧行为）
`MainWindow.ChipCapabilities.cs` 里 `battery-soc/capacity/voltage` 的读取只喂一个非模态横幅;
`MainWindow.axaml.cs` 有 4 处注释自陈「writes are deliberately NOT blocked here — warning-only by
decision 2026-11-12」（旧的 banner 决策），CLI/MCP 侧完全没有电量判据。

### 落地
| 面 | 改动 |
|---|---|
| 共用判据 | 新增 `Services/Verdict/BatteryGate.cs`：纯函数 `Evaluate(int? percent)` + `ParsePercent(variable, output)`，注入式 `ReadPercentAsync/CheckAsync`。低于 `MinimumPercent=50` → `Refuse=true`、`ExitCode=4`（= `Cli.ExitCodes.Gated`）、`Gate="battery"`、`Level=N` |
| GUI | `TryReadBatteryPercentAsync` 改为委托 `BatteryGate.ReadPercentAsync`（同一 getvar 列表/同一解析/同样静默）；新增 `EnsureBatteryAllowsWriteAsync(operation)`，插在 4 个写入口第一行：`FlashPartitionsWithProgress`、`FlashFullOtaPartitions`、`StartUsbUpdateFlash`、`HandleRebootToUsbUpdate`（原来 4 段 banner 注释全部删除） |
| CLI/MCP | 新增 `Cli/Commands/BatteryWriteGate.cs`（打印 `gate`/`level`/`reason`/`exitCode` 结构化 verdict）；`GatedDeviceWriteCommand.ExecuteAsync` 在 approval 之后、写服务之前统一过闸；`flash-ota`/`flash-xml` 在 pre-op backup 之前过闸。MCP 走同一 `ICliCommand`，因此自动继承 |
| 测试 | 新套件 `tests/BatteryGate`（45 checks，纯桩数据、零设备）：阈值/解析/读取顺序 + 反射断言三入口共用同一对象 |

### 判据设计（写明理由，便于编排者复核）
- **读不到 level 不拒绝**：临时 loader 对 `battery-*` 常回 `invalid command`，在那里拒绝会让工具
  在它本来要支持的机型上完全不可用。因此 unknown → 放行但**显式**回报 `gate="battery-unknown"`
  （stderr 一行 + JSON 字段），而不是伪装成「闸门已通过」。
- **`reboot` 显式豁免**（`RequiresBatteryGate => false`）：它不写 flash，低电量时拒绝重启会把设备
  困在 fastboot 里无法充电。`reboot-usb-update` 不豁免（它要写 rescue 分区）。
- **VCOM 上传不在闸内**：VCOM 阶段设备在 BootROM，尚无任何 SOC 读数通道。报告在此显式记录该
  豁免，未改串口时序/协议。

### 证据
```
$ dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false
    3 个警告        0 个错误          （3 条均为既有 AVLN3001）
$ dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false
    0 个警告        0 个错误
$ dotnet run --project tests/BatteryGate -c Release
BatteryGate ALL PASS (45 checks)
$ dotnet run --project tests/StableParity -c Release   -> 35 stable parity checks passed
$ dotnet run --project tests/WriteGates -c Release     -> 141 write gate checks passed
$ dotnet run --project tests/ApprovalGate -c Release   -> 111 approval-gate checks passed
$ dotnet run --project tests/SurfaceParity -c Release  -> 415 surface parity checks passed
```
冻结面：`MainWindow.axaml.cs` 属 boundary.json，已在同一 commit 刷新哈希，reference 里写明
「行为保持型改动（写入口只是多了一个前置断言，审批/对话框/顺序/wire 未变）」。

## 2. D-06（audit H20 / H23）pwn 跳转字面量外置 — commit `cb5d3e4`

状态: **完成**（纯行为保持型：字面量来源替换）

### 判据（旧行为）
`Services/VcomFlasher.cs` 的 `UploadData` 与 `ExploitBootrom` 各有一个 `switch`：
`xloaderStart`（hisi710/970/980 = 0x2316D；710a/810/820/985/990 = 0x23155）与
`returnAddr`（970/980/810 = 0x4DBC8；710/710a = 0x49BC8；820/985/990 = 0x673c8），
两个 switch 都对未知 CPU 抛 `NotSupportedException("Exploit requested for an unsupported CPU: {cpu}.")`。

### 落地
| 文件 | 改动 |
|---|---|
| `Services/PwnJumpTable.cs`（新） | `readonly record struct PwnJumpAddresses(int XloaderStart, int ReturnAddr)`；`Entries` 显式表 8 项；`For(cpu)` 未知 CPU 抛同文本异常；`BootromHeadAddress = 0x22000` 常量 |
| `Services/VcomFlasher.cs` | `PwnJumpAddresses jump = PwnJumpTable.For(exploitInfo.Cpu); await ExploitBootrom(jump);`；`ExploitBootrom` 里两个 switch 删除，`0x22000` 三处改用 `PwnJumpTable.BootromHeadAddress`。帧顺序 / 布局 / 小端 payload / 延时 / ACK / 重试均未改（已删除的 `cpu` 形参不再需要） |
| `tests/PwnJumpTable`（新套件 53 checks） | 见下 |

### 断言（为什么能证明「逐字节一致」）
- 快照里的 8×2 个地址是**从被删除的 switch 抄下来的**，不是从新表抄的 —— 否则测试恒真。
  对每个 SoC 断言 `For(cpu)` 的两个字段与快照逐值相等，且**表里没有 switch 之外的 key**（双向）。
- 未知 CPU 仍抛异常、消息逐字相同（`Exploit requested for an unsupported CPU: hisi999.`），
  且受支持的 CPU 不会解析成 0 地址（默认值陷阱）。
- 与 `FirmwareUnlocker.CpuAddresses` 对齐：表的 key 集合 **等于** `PwnFlag=true` 的 SoC 集合；
  每个 SoC 的 `PwnJumpTable.BootromHeadAddress` 等于其 `XLOADER` 段的地址。
- 源码扫描（防回退）：`0x2316D / 0x23155 / 0x673c8 / 0x4DBC8 / 0x49BC8` 五个字面量在
  `VcomFlasher.cs` 中已不存在，`cpu switch` 模式不存在，`PwnJumpTable.For(` 存在。

### 证据
```
$ dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false   -> 3 警告（既有 AVLN3001）/ 0 个错误
$ dotnet run --project tests/PwnJumpTable -c Release   rc=0  PwnJumpTable ALL PASS (53 checks)
$ dotnet run --project tests/StableParity -c Release   rc=0  35 stable parity checks passed
$ dotnet run --project tests/VcomPromptUi -c Release   rc=0  PASS...
$ dotnet run --project tests/VcomUnlockCli -c Release  rc=0  98 VCOM unlock parity checks passed
```
冻结面：`Services/VcomFlasher.cs` 属 boundary.json，同 commit 刷新哈希并写明「仅字面量来源替换，
wire/时序/重试未变」。`Services/FirmwareUnlocker.cs` 未改（对齐由测试断言，而不是改表）。

## 3. D-14（BB-13 / BB-16 GUI 侧）dload 临时目录 + sentinel

状态: 待做

## 4. 全量验证

状态: 待做
