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

## 2. D-06（audit H20 / H23）pwn 跳转字面量外置

状态: 待做

## 3. D-14（BB-13 / BB-16 GUI 侧）dload 临时目录 + sentinel

状态: 待做

## 4. 全量验证

状态: 待做
