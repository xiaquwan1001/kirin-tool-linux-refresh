# Round 5 加固（2026-11-15 审计第三轮，续做 lane）— 施工记录

来源：`~/.cache/kt-audit-r2/report.md`（第三轮只读审计，S1/S2/S3 + M1–M5）与
`docs/audit/2026-11-15/round4.md` §6。基线 v2.4.22（HEAD 43a6f3b，分支 fix/sec-round3）。

纪律：不碰 loaders/、fastboot payload、硬件 blob、串口协议时序；全程不执行 adb/fastboot
（只用 stub/harness）；冻结面（tests/StableParity/boundary.json 列出的 13 个文件）只做
行为保持型改动，改完刷 sha256 并在 reference 追加原因。

## 1. S1 UPDATE.APP 分区名路径穿越（§1.1）— 已修

- `Utils/PartitionNames.cs`（新）：共用白名单 `IsSafe`（ASCII 字母数字 + `_` `-` `.`，
  禁 `.`/`..`，上限 64 字节）、`Require`（拒绝即抛 `InvalidDataException`）、
  `ChildPathWithin(dir, name, suffix)`（`Path.GetFullPath` 后要求最终路径落在
  `GetFullPath(dir) + separator` 子树内，且必须比前缀长）。
- 接入三处写盘 + 两侧解析口径：
  - header 写盘 `USBUpdateApp.ImagePath(name, ".img.header")` → `WriteBytesSafely`；
  - image 写盘 `USBUpdateApp.ImagePath(name, ".img")` → `ExtractImageData`（内部
    `CreateExtractionFile`：`FileMode.CreateNew` + Unix 0600，写前先删掉同名条目/符号链接，
    不再 `FileMode.Create` 跟随链接）；
  - unlockcode 也走 `ChildPathWithin` + 0600；
  - `Utils/UpdateApp.cs` 的 strict 分支改为 `PartitionNames.IsSafe(type)`（原来内联同一套字符
    判断，两份口径现在是一份）。
- 顺带收掉 §1.3 的两条低危：`FindUnlockCode`/`startAddr` 由 `int` 改 `long`（>2 GiB 包不再
  截断成负偏移）；`ParseImageHeader` 对 `headerLength` 加 98..4096 的区间校验（越界即抛，
  不再负 seek / 无上界读取）。
- 断言（tests/UsbTransport，新增 17 条）：白名单逐项拒绝（`..`、`.`、`../../evil`、含 `/`、
  含 `\\`、含空格、65 字节、空串）；`ChildPathWithin` 对 `../../evil` 抛错；伪造 UPDATE.APP
  （header 名字 = `../../evil`）→ `ExtractAllPartitions` 抛 `InvalidDataException`，传输目录
  外（`s1root`、`s1root/outer`）与目录内都**没有** `evil.img`/`evil.img.header`；
  合法包仍逐字节解出 `XLOADER.img` + header sidecar，二者权限 0600。

## 2. S2 审批 socket 可被本地用户伪造（§2.1 / §2.2）— 已修

- `Services/Approval/PeerCredentials.cs`（新）：`getuid()` + `getsockopt(SOL_SOCKET, SO_PEERCRED)`
  取对端 uid；`VerifyPeer` 在「平台不支持 / syscall 失败 / uid 不匹配」三种情况下都返回
  **refused + 原因**（fail closed，注释写明「拿不到 uid 绝不默认 Approved」）。
- `ApprovalIpc`（`GuiApprovalBridge.cs`）：
  - socket 目录不再退化到公共 /tmp：`XDG_RUNTIME_DIR` 缺失或不可用时用
    `<tmp>/kirin-tool-<uid>`，创建时 0700，并**校验属主**（`IsPrivateDirectory`：先看有无 group/other
    权限位，再用 chmod 回写做属主探针——别人的目录 chmod 会 UnauthorizedAccess）；
    世界可读的 `XDG_RUNTIME_DIR` 同样被拒；
  - 服务端 Bind 后对 socket 文件自身设 0600，`Listen(4)`；
  - `GuiApprovalServer.ServeOneAsync` 先 `PeerCredentials.VerifyPeer`，不匹配即回 Denied 并记诊断；
  - 每连接 30 s 读超时（`ApprovalIpc.ReadTimeout`）、同时挂起请求上限 4
    （`MaxPendingRequests`，超限回 Denied）、请求字段经 `ApprovalIpc.Sanitize`（长度/控制字符）
    才进对话框；
  - 客户端 `SocketGuiApprovalBridge` 连接后**自己**也验一次对端 uid（不信对端 JSON），
    失败即 `GuiNotRunningException` → MCP 面 NotSupported（fail closed）。
- `ApprovalGates.McpApprovalGate`：
  - 只接受 `PeerVerified == true` 且（红线时）短语原文与 `request.ConfirmPhrase` **Ordinal 相等**的
    答案；`TypedConfirm: true` 单独不再成立；
  - `ApprovalDecision.Approved` 新增 `TypedPhrase`（GUI/CLI gate 记录操作者实际输入），
    GUI 侧的 IPC 处理函数把它原样回传，MCP gate 自己比对；
  - `ApprovalGateBase`：红线（`RequiresTypedConfirm`）请求**不再走会话缓存复用**，同一请求第二次
    也必须重新输短语（原来第二次会拿到 cached Approved，等于短语被记住）。
- 断言（tests/ApprovalGate 62 → **111** 条）：不同 uid 的对端 → 服务端回 Denied、客户端亦拒绝
  （两个方向都 fail closed）；同 uid → 往返成功且 `PeerVerified=true`；socket 文件 0600；
  `TypedConfirm=true` 无短语 → Denied、错短语 → Denied、正确短语 → Approved；
  红线批准不被缓存复用（asked=2）；无 `XDG_RUNTIME_DIR` 时路径 = `<tmp>/kirin-tool-<uid>` 且 0700、
  socket 在其内；0755 的 `XDG_RUNTIME_DIR` 被拒并回退到私有目录。


## 3. S3 GUI「Convert OEM」直写绕过审批/身份闸门（§3.1）— 已修

- `Services/WriteOperations/DeviceWriteOps.cs`（上轮已加）：`OemInfoWriteFieldAsync(ticket, field,
  value)` 与 `OemInfoEraseWriteProtectionAsync(ticket)`——`Require(ticket, "oeminfo-flash")` +
  `EnsureWritableIdentityAsync()` 后才发 `oem oeminfowrite-KTModel@…` / `KTVendor@…` /
  `oeminfoerase-disablewp`；返回值仍是 loader 原始输出，成功判定仍是「输出含 OKAY」；
  字段值另有白名单（1..64 字符，字母数字 `._- `，把 §5.2 的 argv 拼接面一并收掉）。
- `MainWindow.axaml.cs` `BtnConvertOem_Click`（冻结面，行为保持型）：
  - 直写分支先 `RequestGuiApprovalAsync(ApprovalRequests.For("oeminfo-flash", …))`，
    红线能力 → 必须手输 `I UNDERSTAND oeminfo-flash`；取消即「nothing was written」并返回；
  - 三处 `_fastbootClient.OemCommandAsync(...)` 改为 `_deviceWriteOps.OemInfoEraseWriteProtectionAsync(ticket)`
    / `OemInfoWriteFieldAsync(ticket, "KTModel"/"KTVendor", …)`；
  - 进度对话框的两行条目、状态文案（Done/Failed/Unconfirmed）、`ClassifyCommandOutput` 的成功判定、
    `shouldDisableWp` 的 CPU 判据全部未变（只是 `shouldDisableWp` 提前算出来用于审批摘要）。
- `Services/Capabilities/CapabilityRegistry.cs`：`oeminfo-convert`（PlanOnly，本地生成镜像）与
  `oeminfo-flash`（Write/RedlineTypedConfirm）的说明追加「同一按钮的直写分支走 oeminfo-flash 的
  票据与身份闸门」，避免这一行再次「谎报自己」。
- `Services/USBUpdate/XloaderPatcher.cs:184`（`FlashXloaderViaFastbootAsync`）：核对结论 —— 有上层闸门。
  唯一调用点是 `SoftwareTestpointService.ExitSoftwareTestpoint`，唯一入口是
  `DeviceWriteOps.SoftwareTestpointAsync`（`Require(ticket, "usb-testpoint")`）；GUI/CLI/MCP
  三个面都只能经它调用。本轮不改该文件（不引入无意义 hash 变更），只在此记录依据。
- 断言：tests/Kirin9000 新增 7 条 headless 用例（stub fastboot 记录 argv）：直写分支弹出
  `Approve write: oeminfo-flash`（capability 正确）；未输短语时 Approve 按钮 disabled；审批前
  **零**条 `oeminfo*` 命令到达 loader；输短语后可批准；三条命令及顺序
  `oeminfoerase-disablewp → oeminfowrite-KTModel@NOH-AN00 → oeminfowrite-KTVendor@hw-cn` 不变；
  写前有 `getvar product`（身份闸门）；成功文案仍是「Both oeminfowrite commands ... returned OKAY」。


## 4. M1 临时文件与权限（§4.1 / §4.2）— 已修

- 新文件 `Utils/PrivateWorkDirectory.cs`：`Create(prefix)` = 随机后缀 + `CreateDirectory` + 0700；
  `RestrictDirectory` / `RestrictFile`（0600）两个显式 0600/0700 工具。
- `Cli/Commands/DeviceWriteCommands.cs`（usb-testpoint）：`/tmp/kirin-tool-dload` →
  `PrivateWorkDirectory.Create("kirin-tool-dload")`，跑完在 `finally` 里删除（服务自身启动时也会
  clean，双保险，不再往 /tmp 堆目录）。
- unlock code：`USBUpdateApp` 的两处写盘上轮已是 `CreateNew + 0600`；本轮把
  `USBUpdateFlasherService` 把 unlockcode 复制到上层目录的那一步补成 `RestrictFile`（`File.Copy`
  会继承 umask，原来可能 0644）。
- `Services/OemInfoService.cs`：pull 出来的设备 oeminfo 临时镜像落地后立即 0600。
- `Cli/Commands/OtaFlashRunner.cs`：staging 目录由 `yyyyMMdd_HHmmss` 改为
  `yyyyMMdd_HHmmss-<guid>` + 0700（共享 `--work-dir` 下不能再被预置同名目录/符号链接接管）。

## 5. M2 SuperMerger 上界与 checked 运算（§1.2）— 已修

- 新增 `ValidateHeader`：`FileHeaderSize == 28`、`ChunkHeaderSize == 12`、`BlockSize > 0`、
  `TotalChunks >= 1` 且 `TotalChunks <= (len - 28) / 12`（大文件和小文件都校验）。
- 每个 chunk：`TotalSize >= 12` 且 `position + TotalSize - 12 <= fileLength`（不再负 seek / 越界）；
  small 的第一个 chunk 同样校验，且 `newDataOffset` 必须落在文件内。
- `newTotalChunks` 改成 `checked((uint)((long)large.TotalChunks - 1 + small.TotalChunks - 1))`：
  `TotalChunks == 0` 在 header 校验阶段就被拒，下溢成 4294967295 的路径不复存在。
- **未做**：`sum(chunk blocks) == header.TotalBlocks` 校验。sparse 的 DONT_CARE/CRC chunk 在
  chunk header 里不携带块数（`chunk_sz` 不是块数的可靠来源），现有 Windows 产出的镜像用
  `chunk_sz=0 / total_sz=12` 表达一个非零块数的 DONT_CARE 段；按块数求和会把这类**合法**镜像
  误拒并破坏 golden 字节测试。已在 `ValidateHeader` 注释与本条记录，作为已知取舍。
- 保行为：`tests/StableParity` 的 4 条 golden Windows 合并字节断言仍逐字节相同。

## 6. M3 OemInfoEditor 长度/边界校验（§1.2）— 已修

- `EditEntries`：入口校验 model/vendor 非空、长度 ≤ 64（`MaxFieldLength`）、无控制字符。
- `UpdateEntry`：字长前缀 + payload + padding 全部加边界——`newLen <= BlockSize - 0x200 (=512)`、
  `blockOffset + BlockSize <= data.Length`、`dataStartOffset + newLen <= data.Length`，
  padding 上界 clamp 到 `Math.Min(blockOffset + BlockSize, data.Length)`。
- 保行为：正常 8 字符 model / 5 字符 vendor 的写入字节与原实现一致（长度前缀在 +20、payload 在
  +0x200、padding 为 0xFF）。

## 7. M4 gzip 解压上限（§1.2）— 已修

- `OtaFlashRunner.DecompressIfGzipAsync`：解压前算
  `limit = min(挂载点可用空间, max(512 MiB, 32 × 压缩体积))`，逐块写并累计；超限抛
  `InvalidDataException`，同时删除半截输出（不再可能把 tmpfs/工作盘写满）。

## 8. M5 flashing xml 的解析加固（§1.3）— 已修

- `XmlReaderSettings`：显式 `DtdProcessing = Prohibit`、`XmlResolver = null`、
  `MaxCharactersInDocument = 16 MiB`、`MaxCharactersFromEntities = 0`（不再只依赖 .NET 默认值，
  也不再允许几 GB 的 xml 一次性进内存）。
- `ParseFlashingXml`：`<image>` 的路径先 `GetFullPath`，要求落在 xml 同目录或其父目录子树内
  （这两处正是旧查找能落到的位置）；绝对路径同样受此约束，越界即抛 `InvalidDataException` 并
  指出条目名与目标路径——不再能把 `/etc/shadow` 当 payload 送进设备。
- 断言（tests/WriteGates，新增 M 段 27 条）：M2 的 7 种坏 header/chunk（含 0xFFFFFFFF chunks）
  全部被拒且 **0.00 s** 返回（不是遍历声明的 chunk 数），M3 正常写入 + 4 种非法值 + 反射调用
  `UpdateEntry` 的 0x201 越界被拒且下一块 magic 未被破坏，M5 正常相对路径不变、`../..` 逃逸、
  绝对 `/etc/shadow`、带 DTD/实体的 xml 全部被拒。


## 9. 验证数字（本机，未执行 adb/fastboot，未接触真机）

- `dotnet build -c Debug`：**0 error**（3 个既有 AVLN3001 警告，均与本轮无关）。
- `dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release -p:NuGetAudit=false`：**0 error**。
- `tests/suites.txt` 全量：**25/25 rc=0**。
- 关键计数（本轮新增）：
  - WriteGates **114 → 141**（新增 M2/M3/M5 共 27 条）；
  - Kirin9000 **52 → 60**（新增 S3 共 7 条 + 1 条既有 R2 用例因窗口复用重跑）；
  - ApprovalGate **62 → 111**（S2：peer uid 双向 fail closed、`TypedConfirm` 不再被采信、
    短语原文比对、红线不缓存、私有 socket 目录 0700、XDG 退化）；
  - UsbTransport **8 → 25**（S1：白名单逐项 + 伪造 UPDATE.APP 的路径穿越 + 0600）；
  - StableParity **35**（MainWindow / USBUpdateApp / SuperMerger / OemInfoEditor 4 个冻结文件
    刷新 sha256 并在 reference 追加原因；4 条 golden 合并字节断言仍逐字节相同）；
  - 未改动但复跑的基线：McpProtocol 17、OpLogging 163、SurfaceParity 415、Extractor 43、
    OtaPlan 20、UsbRedlineGate 13、FastbootReads 10、GuiQol 35、PreOpBackup 全绿。

## 10. 剩余 TODO / 已知取舍

- **M2 的 `sum(TotalBlocks)` 校验未做**：DONT_CARE/CRC chunk 不携带块数，按现有 Windows 产物
  的口径求和会误拒合法镜像（详见 §5）。
- **M5 绝对路径现在一律要求落在包目录子树内**：若真实第三方 flashing xml 里存在「包外绝对
  路径」的合法用法，会被 fail-closed 拒绝并给出条目名/目标路径；需要拿真实包验证后再决定是否
  加白名单（本轮按审计要求取 fail closed）。
- CLI `usb-testpoint` 的私有目录每次新建并在 `finally` 清理；进程被 SIGKILL 时会残留一个
  0700 随机名目录（无泄漏面，只是残留）。
- 继承 round4 §6：Windows 真机端口归并、`getvar` 不可用时的读闸门取舍、GUI 非 ProcessRunner
  子进程的关窗取消，均不在本轮范围。
- `§5.2 低危`（`oem dump-emmc` 手工引号拼接 → 改用 argv 列表重载）本轮未动：需要改
  `ProcessRunner.RunAsync` 调用签名并核对 FastbootFlasherService 的 dump 路径，留作下一轮。

