# lane B3 — 不可逆写类债务清偿（D-16 / D-06 / D-14）

工作目录: `/home/xuqianhui/src/wt/r7-b3-writegates`（分支 `fix/r7-b3-writegates`，基线 main `cd060f8`）
纪律: 零真实设备操作（未跑 adb / fastboot / VCOM / 串口 / usb 探针），未碰 `loaders/`、fastboot payload、硬件 blob，未改 `docs/debt-register.md`。

## 0. 基线

`git status` 开工时干净，HEAD = `cd060f8`（main），分支 `fix/r7-b3-writegates`。
三条债的 commit：

| 债 | commit | 类型 |
|---|---|---|
| D-16（S14）电量闸门 | `7687cd5` | 行为变更（低电量现在拒绝写入） |
| D-06（H20/H23）pwn 跳转表 | `cb5d3e4` | 行为保持 |
| D-14（BB-13/BB-16 GUI）dload + sentinel | `e9155cc` | 行为保持（删除前多一道拒绝） |
| 报告 | `6786f9c` | docs |

三条改动都用「新套件 + 源码扫描」双保险钉住，防止回退到裸字面量/无校验删除。

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

## 3. D-14（BB-13 / BB-16 GUI 侧）dload 临时目录 + sentinel — commit `e9155cc`

状态: **完成**

### 判据（旧行为）
`MainWindow.axaml.cs` 用 CWD 相对字面量 `"dload"` 构造 `USBUpdateFlasherService` 与
`SoftwareTestpointService`；两者开跑前都 `OpLog.CleanDirectory(dload)` → `Directory.Delete(d, true)`。
用户从任意目录启动 GUI 时，CWD 下任何同名目录会被递归删除，且删除前不看内容。CLI 侧
（`UsbTestpointCommand`）早已改用 `PrivateWorkDirectory.Create("kirin-tool-dload")`（随机名 + 0700）。

### 落地
| 文件 | 改动 |
|---|---|
| `MainWindow.axaml.cs` | 两个服务共享一个 `PrivateWorkDirectory.Create("kirin-tool-dload")`；`"dload"` 字面量删除（与 CLI 同一做法） |
| `Services/USBUpdate/DloadSentinel.cs`（新） | 纯函数 `Evaluate(directory, directoryExists, entries)` + `EvaluateDirectory`。规则：空目录可删；否则**每个**条目必须是本工作流写的（`list.txt`/`unlockcode`/`*_BAK.img` + 经 `PartitionNames` 白名单的 `*.img`/`*.img.header`）**且**至少存在一个工作流标记。文件系统根 / 用户 home / 路径不可解析 / 不存在 / 列举失败 → 一律拒绝（fail-closed） |
| `Services/USBUpdate/DloadDirectoryCleaner.cs`（新） | `Clean()`：先 sentinel，通过才 `OpLog.CleanDirectory`；拒绝时写 op 证据（verdict `refused:sentinel`）并**不删任何东西** |
| `SoftwareTestpointService.cs`（2 处）、`USBUpdateFlasherService.cs`（1 处） | 改走 cleaner；`OpLog.CleanDirectory` 现在全树只有一个调用点 |
| `tests/DloadSentinel`（新套件 38 checks） | 见下 |

### 断言
- 纯规则：空目录可删；8 个「本工作流文件」组合可删；混入 1 个外来文件即拒绝且回执点名该文件；
  只有 `*.img` 而无标记文件 → 拒绝（「名字看起来无害」不算识别）；`/`、home、null、空串、
  不存在的目录全部拒绝；`IsOwnEntry` 对 `.git`/`list.txt.bak`/`../evil.img`/`null` 为假。
- 真实临时目录（非设备操作）：造一个「`list.txt` + `XLOADER.img` + `family-photos.jpg`」目录，
  `DloadDirectoryCleaner.Clean` 拒绝后**目录与外来文件仍在**（这就是债务本身的回归测试）；
  子目录也算外来条目；把外来内容清掉后同一目录变可删并确实被删。
- 源码扫描（防回退）：GUI 代码行里不再有 `"dload"` 字面量、改为
  `PrivateWorkDirectory.Create("kirin-tool-dload")` 且只创建一次（两服务共享）；
  `SoftwareTestpointService`/`USBUpdateFlasherService` 里已无 `Directory.Delete(`；
  `OpLog.CleanDirectory(` 在 gate 之外无任何调用者。

### 证据
```
$ dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false   -> 3 警告（既有）/ 0 个错误
$ dotnet run --project tests/DloadSentinel -c Release   rc=0  DloadSentinel ALL PASS (38 checks)
$ dotnet run --project tests/StableParity -c Release    rc=0  35 stable parity checks passed
$ dotnet run --project tests/UsbUpdateUi -c Release     rc=0  PASS ...
$ dotnet run --project tests/UsbUpdateVerdict -c Release rc=0 all 39 USB-update verdict checks passed
$ dotnet run --project tests/OpLogging -c Release       rc=0  168 operation-logging checks passed
$ dotnet run --project tests/GuiQol -c Release          rc=0  8 GUI QoL checks passed
```
冻结面：`MainWindow.axaml.cs` 与 `Services/USBUpdate/SoftwareTestpointService.cs` 都在
boundary.json，同 commit 刷新哈希并写明「行为保持型：提取/传输/打补丁命令、wire 字节、时序、
重试未变，仅删除点的前置校验」。**一处有意保留的行为差异**：旧行为是「删除失败也继续」，
现在是「拒绝删除并继续 + 记录证据」，调用方控制流一致（提取仍使用该目录）。
注：`USBUpdateFlasherService.cs` 不在 boundary 清单内。

## 4. 全量验证（真跑，非「相关目录能编译」）

### 两条 build 的最后一行
```
$ dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false     （-t:Rebuild 全量）
    3 个警告        0 个错误        # 3 条均为既有 AVLN3001（XAML 运行时加载器不可达，仓库已知）
$ dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false
    3 个警告        0 个错误        # 同一批 AVLN3001 的重复报告；CLI 自身 0 警告
```

### 每个 suite 的 PASS/FAIL 汇总（`tests/suites.txt`，28 套）
| suite | rc | 最后一行 |
|---|---|---|
| FlashPolicy | 0 | 55 flash policy checks passed. |
| StableParity | 0 | 35 stable parity checks passed; no hardware accessed. |
| FastbootReads | 0 | PASS partial failed table |
| VcomPromptUi | 0 | PASS dead FlashPartitionsWithProgress(List<PartitionInfo>) overload removed |
| GuiQol | 0 | 8 GUI QoL checks passed; headless, no device access. |
| Extractor | 0 | 43 extractor checks passed. |
| UsbDiagnostics | 0 | 49 USB diagnostic checks passed; simulated transport only. |
| UsbRedlineGate | 0 | 13 redline gate checks passed; no device accessed. |
| UsbTransport | 0 | PASS extractor: the refused entry left nothing behind inside either |
| UsbUpdateUi | 0 | PASS rendered rows, reboot success/failure and completion controls; sources=True |
| UsbUpdateVerdict | 0 | all 39 USB-update verdict checks passed |
| OemInfoUi | 0 | 5 OEMInfo UI checks passed; headless, no device access. |
| UdevNotice | 0 | 24 udev notice checks passed; no host rule writes or USB operations. |
| Kirin9000 | 0 | 102 Kirin9000/PSID checks passed; no device operations. |
| WriteGates | 0 | 141 write gate checks passed. |
| McpProtocol | 0 | 17 MCP protocol checks passed. |
| OtaPlan | 0 | 20 ota plan checks passed. |
| PreOpBackup | 0 | BackupPlan: all checks passed |
| SurfaceParity | 0 | 415 surface parity checks passed. |
| OpLogging | 0 | 168 operation-logging checks passed; no device, fastboot or adb was contacted. |
| LogMerge | 0 | 79 log-merge checks passed; no device, fastboot or adb was contacted. |
| VcomUnlockCli | 0 | 98 VCOM unlock parity checks passed; no hardware accessed. |
| ApprovalGate | 0 | 111 approval-gate checks passed. |
| VerdictHonesty | 0 | 81 verdict-honesty checks passed; no hardware accessed. |
| VersionMetadata | 0 | 7 version-metadata checks passed; no device was contacted. |
| **BatteryGate（新）** | 0 | BatteryGate ALL PASS (45 checks) |
| **PwnJumpTable（新）** | 0 | PwnJumpTable ALL PASS (53 checks) |
| **DloadSentinel（新）** | 0 | DloadSentinel ALL PASS (38 checks) |

**28/28 套件 rc=0，0 FAIL。** 三个新套件（`BatteryGate`/`PwnJumpTable`/`DloadSentinel`）
已写入 `tests/suites.txt`，两条 CI 流水线会自动纳入。

### 复现命令（本报告全部数字均由此产生）
```
cd /home/xuqianhui/src/wt/r7-b3-writegates
dotnet build Kirin-Tool.csproj -c Release -p:NuGetAudit=false
dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false
while read -r t; do case "$t" in ''|'#'*) continue ;; esac; \
  echo "== $t"; dotnet run --project "tests/$t" -c Release -p:NuGetAudit=false || echo "FAILED $t"; \
done < tests/suites.txt
```

### 冻结面汇总（boundary.json 刷新，全部为行为保持型）
| 文件 | 触发的债 | 原因 |
|---|---|---|
| `MainWindow.axaml.cs` | D-16、D-14 | 写入口加前置电量判据；dload 服务改私有临时目录 |
| `Services/VcomFlasher.cs` | D-06 | 跳转字面量改为查表（wire/时序未变） |
| `Services/USBUpdate/SoftwareTestpointService.cs` | D-14 | 删除点前置 sentinel（命令/时序未变） |

未动：`Services/FirmwareUnlocker.cs`（D-06 用测试断言对齐，不改表）、`loaders/`、
fastboot payload 二进制、任何硬件 blob、串口时序/协议、`docs/debt-register.md`。

### 纪律自查
- 零真实设备操作：全程未执行 adb / fastboot / VCOM / 串口 / usb 探针，未跑
  `VcomOpenProbe`/`UsbUpdateProbe`（也不在 `suites.txt` 里）。
- 三条债各自独立 commit：`7687cd5`(D-16) / `cb5d3e4`(D-06) / `e9155cc`(D-14)，报告 `6786f9c`。
- 时间盒内完成，无卡住条目，无遗留阻塞点。
