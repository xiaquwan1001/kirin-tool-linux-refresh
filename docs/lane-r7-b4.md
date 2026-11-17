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

**判据（改前）**：`Utils/SparseDisk.cs` 里 `SparseHeader.TotalBlocks` 只被读进结构体，
**全仓库没有任何一处读它**（`grep TotalBlocks` 唯一命中是定义处）。也就是说：`SuperMerger`
把两个分片拼成 `super.img` 时，header 声明的「整镜像展开成多少 512/4096-byte 块」是**无人校验的自述**；
一个 TotalBlocks 与自己 chunk 列表矛盾的镜像照样会被合并、并被上层刷进 `super` 分区。

**处置**：新增纯判定件 `Services/Verdict/SparseBlockSum.cs`
（`SparseChunkFact` + `SparseBlockSumVerdict` + `SparseBlockSum.Evaluate`），规则与 libsparse 读回时一致：

| chunk 类型 | 折算块数 |
| :--- | :--- |
| raw `0xCAC1` | `(TotalSize - 12) / BlockSize`，**必须整除**，否则 failed |
| fill `0xCAC2` / dontcare `0xCAC3` | `ChunkSize`（header 自己声明的块数） |
| crc32 `0xCAC4` | 0（4 字节校验和，不占块） |
| 其它 | failed（未知类型） |

`sum(chunk blocks) != TotalBlocks` ⇒ `Token = failed`，`Consistent = false`，带
`DeclaredBlocks`/`AccountedBlocks`/`Reason`。类型常量集中进 `Utils/SparseDisk.cs` 的
`SparseConstants`（`CHUNK_TYPE_RAW/FILL/DONT_CARE/CRC32`），不自造第二套。

`Utils/SuperMerger.cs` 里接两处（`EnforceBlockSum`）：大分片的 walk 顺带累计 chunk 事实后校验；
小分片用新增的 `ReadChunkFacts`（只 seek 读 chunk header，不读 payload）走一遍。不一致即抛
`InvalidDataException`（消息带 `block-sum verdict failed — <Reason>`），合并**不产生输出文件**，
上层 `USBUpdateFlasherService` / `OtaFlashRunner` / GUI 的现有异常路径把它报成失败。

**行为保持**：合法输入的合并字节与输出 layout 完全没变——`tests/StableParity` 的
Windows golden byte 测试（CRC×reverse 四种）仍逐字节相等；新增的只是对自相矛盾 header 的拒绝，
以及小分片多一遍「只读 chunk header」的扫描（不读 payload，不改变合并结果）。

**冻结面刷新**：`tests/StableParity/boundary.json` 只刷新 `Utils/SuperMerger.cs` 一个 hash，
`reference` 写明「新增 block-sum 校验 + 合法输入字节不变」，其余 12 个冻结文件 hash 未动。

**证据（真跑）**：

```
dotnet run --project tests/WriteGates   -> 164 write gate checks passed.
PASS M2: TotalBlocks smaller than what the chunks describe is refused
PASS M2: TotalBlocks larger than what the chunks describe is refused
PASS M2: a raw chunk whose payload is not a multiple of BlockSize is refused
PASS M2: an image whose TotalBlocks equals its chunk accounting still merges
PASS M2: TotalBlocks=2 with one raw 4096-byte chunk is verdict=failed (accounted 1)
PASS M2: raw(1) + dontcare(2) + crc32(0) is verdict=verified for TotalBlocks=3
PASS M2: an unknown chunk type is verdict=failed
dotnet run --project tests/StableParity -> 35 stable parity checks passed（golden merge bytes 全等）
dotnet run --project tests/SurfaceParity -> 430 surface parity checks passed.
```

> 顺手发现（未修，不入本轮范围）：`tests/WriteGates` 旧局部 helper `Sparse()` 写的是 **14 字节**
> chunk header（`0xcac1u`+ushort+uint+uint），与它自己声明的 `ChunkHeaderSize=12` 不一致；
> 旧断言只关心「被拒绝」，所以一直没暴露。新加的正例改用 `SparseDeclared`（12 字节 header + 真 payload）。

## 3. `oem dump-emmc` argv 列表重载（round5 §10 遗留）

**判据（改前）**：`FastbootClient.OemCommandAsync(string command, ...)` 只有**单字符串**形式：
调用点自己拼 `"dump-emmc ptable \"" + path + "\""`（`FastbootFlasherService` 的 dump 路径、
`PullOemInfo`、`CancelDump` 等），网关与 op 日志再靠 `OperationLogger.ParseArgv` 把字符串**反解**
成 argv。路径里带空格/引号时，反解结果与实际下达的命令是否同一份 token 只能靠解析器保证
（`tests/OpLogging` 里就有 `oem dump-emmc oeminfo "/tmp/a b.bin"` 的三 token 断言）。

**处置**（`Services/FastbootClient.cs`）：

- 新增 `OemCommandAsync(IReadOnlyList<string> argv, int timeoutMinutes = 300)`：token 逐个进
  `ProcessRunner` 的 `ArgumentList`（不经 shell、不再二次切分），可接受并**剥掉**开头的 `oem`
  token（不会出现 `oem oem`），空列表 / 只有 `oem` 都是 `ArgumentException`（不退化成裸
  `fastboot oem`）。
- 单字符串重载改为 **`ParseArgv(command)` → 委托给列表重载**：两条入口共用同一段
  「读闸门判定 + 组 argv + op 命名 + 执行」代码，不可能出现「闸门看的是 A、设备收到的是 B」。
- 读闸门（R1，`IsDevicePullingCommand`）与 op 名（`fastboot.oem.<subcommand>`）改为对
  **同一份 token 列表**判定；`FirstWord` 拆成 string 版（转发）与 list 版。

**行为保持**：单 argv 形式的语义与之前一致——同一字符串解析出的 token 发送到子进程
（`tests/WriteGates` 10e 用 stub 逐字节比对两种形式的子进程 argv：完全相同），
已有调用点（`FastbootFlasherService` dump、`PullOemInfo`）一行未改、继续走单字符串重载。

**证据（真跑）**：

```
PASS the argv list overload sends exactly the same child argv as the quoted single-string form
     (oem dump-emmc ptable .../list form.img vs oem dump-emmc ptable .../list form.img)
PASS a host path containing a space stays one argument in the list form
PASS a leading 'oem' token is accepted and not doubled
PASS the child argv never carries a doubled 'oem' prefix
PASS an empty argv list is a usage error, not a bare 'fastboot oem'
PASS a list that is only 'oem' is a usage error, not a bare 'fastboot oem'
PASS the argv list form is gated by the same read identity probe
PASS an invalid identity sends zero dump commands through the list form
-> WriteGates: 172 write gate checks passed.
```

## 4. 全量验证输出（真跑，最后一条改完后重跑）

```
$ dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false
    0 个警告
    0 个错误
已用时间 00:00:01.14

$ dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false
    0 个警告
    0 个错误
已用时间 00:00:01.80
```

`tests/suites.txt` 25/25（每行 = `dotnet run --project tests/<name> -c Release` 的结论）：

```
== FlashPolicy      PASS (55 flash policy checks passed.)
== StableParity     PASS (35 stable parity checks passed; no hardware accessed.)
== FastbootReads    PASS (last line: PASS partial failed table)
== VcomPromptUi     PASS (last line: PASS dead FlashPartitionsWithProgress(List<PartitionInfo>) overload removed)
== GuiQol           PASS (8 GUI QoL checks passed; headless, no device access.)
== Extractor        PASS (43 extractor checks passed.)
== UsbDiagnostics   PASS (49 USB diagnostic checks passed; simulated transport only.)
== UsbRedlineGate   PASS (13 redline gate checks passed; no device accessed.)
== UsbTransport     PASS (last line: PASS extractor: the refused entry left nothing behind inside either)
== UsbUpdateUi      PASS (last line: PASS rendered rows, reboot success/failure and completion controls; sources=True)
== UsbUpdateVerdict PASS (all 39 USB-update verdict checks passed)
== OemInfoUi        PASS (5 OEMInfo UI checks passed; headless, no device access.)
== UdevNotice       PASS (24 udev notice checks passed; no host rule writes or USB operations.)
== Kirin9000        PASS (102 Kirin9000/PSID checks passed; no device operations.)
== WriteGates       PASS (172 write gate checks passed.)
== McpProtocol      PASS (17 MCP protocol checks passed.)
== OtaPlan          PASS (20 ota plan checks passed.)
== PreOpBackup      PASS (BackupPlan: all checks passed)
== SurfaceParity    PASS (430 surface parity checks passed.)
== OpLogging        PASS (168 operation-logging checks passed; no device, fastboot or adb was contacted.)
== LogMerge         PASS (79 log-merge checks passed; no device, fastboot or adb was contacted.)
== VcomUnlockCli    PASS (98 VCOM unlock parity checks passed; no hardware accessed.)
== ApprovalGate     PASS (114 approval-gate checks passed.)
== VerdictHonesty   PASS (81 verdict-honesty checks passed; no hardware accessed.)
== VersionMetadata  PASS (7 version-metadata checks passed; no device was contacted.)
```

**没有 FAIL**（0 条），因此不存在「是我引入的还是既有问题」的判定。

## 5. 纪律自查

| 纪律 | 执行情况 |
| :--- | :--- |
| 工作目录 = worktree、先 `git status` 干净 | `fix/r7-b4-surface` @ `cd060f8`，起始 `git status --porcelain` 为空 |
| 无任何真实设备操作 | 未执行 adb / fastboot / VCOM / 串口 / usb 探针；`VcomOpenProbe`/`UsbUpdateProbe` 全程未跑；测试只用 stub 脚本与纯函数 |
| 不碰 loaders/、payload 二进制、硬件 blob | 未触碰（`git status` 只含本文件与源码/测试/文档） |
| 不改串口时序/协议命令 | USB 通道、VCOM 帧、fastboot 命令序列一行未改 |
| 不改 `docs/debt-register.md` | 未改（状态列留给编排者） |
| 冻结面只做行为保持型改动 | `Utils/SuperMerger.cs` 一个 hash 刷新（§2 已写理由）；其余 12 个冻结文件 hash 未动，`StableParity` 35/35 通过 |
| 报告先建骨架、逐条追加 | `docs/lane-r7-b4.md` 先落骨架，D-12/M2/argv 各一条完成后立即追加 |
| 一债一 commit | `9f13dd9`（D-12）、`b5d6384`（M2）、第三条见下 |
| 每条完成即跑针对性验证，最后跑全量 | 见各节「证据」与 §4 |

**提交**：

```
9f13dd9 fix(surface): usb-update-flash 补 CLI/MCP 面（D-12 / BB-09）
b5d6384 fix(merge): sparse TotalBlocks 与 chunk 块数一致性校验（M2 / audit §1.2）
8bd2c72 fix(fastboot): oem 命令补 argv 列表重载（round5 §10）
```
