# lane r7-b4 —— 面缺失/上界类债务清偿（D-12 / M2 / oem dump-emmc argv 重载）

- worktree：`/home/xuqianhui/src/wt/r7-b4-surface`，分支 `fix/r7-b4-surface`，基线 `cd060f8`
- 范围：债务登记 `docs/debt-register.md` 的 D-12（BB-09）、M2（SuperMerger 块数上界）、
  round5 §10 遗留（`oem dump-emmc` 的 argv 列表重载）
- 纪律：全程无真机操作（未跑 adb / fastboot / VCOM / 串口 / USB 探针），未改 `docs/debt-register.md`

## 0. 总览

| 条目 | 内容 | 状态 |
| :--- | :--- | :--- |
| D-12 | GUI「USB Update 模式刷整包」补 CLI + MCP 面（`usb-update-flash`），注册表同步 | 见 §1 |
| M2 | `SuperMerger` 补 `sum(chunk blocks) == TotalBlocks` 上界校验（不一致 = verdict failed） | 见 §2 |
| argv | `FastbootClient.OemCommandAsync` 增加 argv 列表重载（单 argv 行为保持） | 见 §3 |

（各节在条目完成时追加，含判据、证据、命令输出。）

## 1. D-12（BB-09）：USB Update 整包刷写的 CLI/MCP 面

**判据（改前）**：GUI 的「USB Update 模式刷整包」= `MainWindow.StartUsbUpdateFlash()`
（`MainWindow.axaml.cs:968,979` 起），落点是 `USBUpdateFlasherService.FlashPartitions(...)`；
`CapabilityRegistry.All` 全表没有对应能力，CLI 只有 fastboot 的 `flash-ota`，MCP 同理 ⇒
「同一件事 GUI 能做、agent 做不到」，且注册表无法声明这条写路径。

**处置**：

| 面 | 落点 |
| :--- | :--- |
| 注册表 | `Services/Capabilities/CapabilityRegistry.cs` 新增 `usb-update-flash`（Write / `Once` / **Irreversible** / `ServiceMethod USBUpdateFlasherService.FlashPartitions` / `Tool usb`，10 个参数与命令逐一对应；GUI 面记 `StartFullOtaButton_Click`） |
| CLI | 新 `Cli/Commands/UsbUpdateFlashCommand.cs`，注册进 `Cli/CliRunner.cs`（第 25 条命令） |
| MCP | 自动随 `ICliCommand.McpName = usb_update_flash` 生成（`McpServer.BuildTool` 加 `confirm` 必填，`SurfaceParity` 的 C 段断言） |
| 文档 | `docs/cli-mcp.md` 写操作表加一行（含 `I UNDERSTAND usb-update-flash` 与「设备必须已在 USB Update 模式」） |

**同源**（硬要求）：CLI 不重新实现解包/合并/传输，直接
`new USBUpdateFlasherService(log, dloadDir, refuseRetry).FlashPartitions(filesData, ct, allowRedline)`，
与 GUI 是同一个方法（`tests/WriteGates` 第 13 段用源码扫描把这条钉住，回退即 FAIL）。
`UsbUpdateVerdict → WriteReport` 走 `UsbUpdateFlashCommand.MapVerdict`：verified→applied/exit 0、
partial→partial/exit 3、failed→failed/exit 3（与 `usb-testpoint` 同口径）。

**闸门/红线/不可逆**（与 GUI 一致的部分一律复用，未改 GUI 路径）：

- 审批：`GatedDeviceWriteCommand` → `ApprovalGuard.AdmitAsync`（CLI 用 `CliApprovalGate`，MCP 用
  `McpApprovalGate` + GUI 带外批准）；`RunAsync` 第一行 `admission.RequireTicket()`，没有票据到不了传输。
- 红线：解包后由服务内 `USBUpdateFlasherService.EnforceRedlineWrite` 判定（未 `--allow-redline` 即
  抛错、本次 0 字节写入）——与 GUI 完全同一个判定点；MCP 侧 `allow-redline` 被 `ValidateArguments` 拒绝，
  因此红线计划在 MCP 上永远过不去（与 flash-ota 同规则）。
- 不可逆：注册表 `Reversibility = Irreversible` ⇒ `RequiresTypedConfirm` 恒真，CLI 必须交互式手输
  `I UNDERSTAND usb-update-flash` 且带 `--allow-redline`；MCP 只能由 GUI 人工批准。
- 写前备份：复用 `PreOpBackupCli`（与 flash-ota 同一 helper）；USB Update 模式下通常没有 fastboot
  会话 ⇒ 备份未取，此时 `--require-backup` 经 `BackupGate` 直接拒绝（exit 4），不静默放行。
- 暂存目录：默认 `PrivateWorkDirectory.Create("kirin-tool-dload")`（0700 + 随机名，避免固定
  世界可写路径），命令结束时删除；显式 `--work-dir` 则保留（与 flash-ota 口径一致）。

**未改**：`MainWindow.axaml.cs`（`StableParity` 冻结面哈希未动）、USB Update 协议/时序/CRC/重试、
`Services/USBUpdate/UsbDownloadService.cs` 等全部 USB 通道实现。

**证据（真跑）**：

```
dotnet run --project tests/WriteGates -> 157 write gate checks passed.
PASS D-12: capability 'usb-update-flash' is registered
PASS D-12: GUI + CLI + MCP faces are declared on the registry row
PASS D-12: it is a gated write capability (not read-only, not plan-only)
PASS D-12: the registry names the service method (got 'USBUpdateFlasherService.FlashPartitions')
PASS D-12: the GUI's USB Update path calls USBUpdateFlasherService.FlashPartitions
PASS D-12: the CLI command calls the same USBUpdateFlasherService.FlashPartitions
PASS D-12: usb-update-flash without --yes exits 4
PASS D-12: --yes alone is refused: ... pass --allow-redline as well as --yes
PASS D-12: --yes --allow-redline on a non-interactive stdin is refused with the exact phrase
PASS D-12: a verified transfer maps to applied/exit 0；partial → partial/exit 3；failed → failed/exit 3
dotnet run --project tests/SurfaceParity -> 430 surface parity checks passed. (H1/H3/H7 全绿)
   faces: 26 capabilities, 25 CLI commands, 25 MCP tools, 35 GUI handlers (26 with CLI+MCP, 0 GUI-only)
dotnet run --project tests/McpProtocol -> 17 MCP protocol checks passed.
```

> 全部断言都走「拒绝路径 + 纯函数映射 + 源码扫描」，**没有任何一次设备访问**（未跑 adb/fastboot/
> VCOM/串口；`usb-update-flash` 的 `--yes --allow-redline` 路径在测试里连 `RunAsync` 都没进）。

## 2. M2：sparse `TotalBlocks` 上界校验

（未开始）

## 3. `oem dump-emmc` argv 列表重载

（未开始）

## 4. 全量验证输出

（未开始）
