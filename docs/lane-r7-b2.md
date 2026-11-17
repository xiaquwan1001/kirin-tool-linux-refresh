# Lane R7-B2 —— 血统 / 身份类债务清偿（D-07 / D-08 / D-15）

分支：`fix/r7-b2-provenance`（基于 main `cd060f8`）
工作目录：`/home/xuqianhui/src/wt/r7-b2-provenance`
纪律：真实设备操作 0 次（无 adb / fastboot / VCOM / 串口 / usb 探针）；未改 `loaders/`、`fastboot/payload`、
未改串口时序与协议命令；未改 `docs/debt-register.md`。

## 行号漂移说明（动手前实测）

债务清单给的三处行号已经漂移，实际位置：

| 债务 | 清单行号 | 实测位置 |
| :--- | :--- | :--- |
| D-07 | `Services/FastbootClient.cs:78-86` | `Services/FastbootClient.cs:62-84`（构造函数 + `IsSystemFastboot`）、`:224-244`（`ResolveDefaultFastbootPath`） |
| D-07 / D-08 | `Cli/Commands/ReadCommands.cs:191` | `Cli/Commands/ReadCommands.cs:191-197`（check 表）与 `:214`（`ok = fastbootPath != null && fastbootVersion != null`） |
| D-08 | `Cli/Commands/ReadCommands.cs:27-36` | `Cli/Commands/ReadCommands.cs:23-25`（`info` 的 exit 计算） |
| D-15 | `MainWindow.axaml.cs:2027` | `MainWindow.axaml.cs:2149`（`RescuePartitionPlan.ForChip(GetSelectedCpu())`） |

## 交付节

（按条追加，每条一节，含判据 / 改动 / 断言 / 验证输出。）

### D-07（P11 / P12 / P13 / H32）——fastboot 血统不再静默

**判据（改前 / 改后）**

- 改前：`ResolveDefaultFastbootPath` 找不到随包客户端就回落到 `/usr/bin/fastboot` → PATH，**不产生任何信号**；
  警告只在 `EnsureWritableIdentityAsync`（写路径）里出现一次，读路径完全没有；`doctor` 的 `ok` 与
  `fastboot-found`/`fastboot-runs` 两个 check 只判「找到 && 能跑」，与血统无关。
- 改后：血统成为一等事实（`huawei` / `google` / `unknown`），三处可观测：
  1. **进程内警告事件**：`FastbootClient` 构造时就向诊断日志写一行 `warning: fastboot lineage <token> at <path> — …`
     （GUI / CLI / MCP 任一入口、读与写都覆盖），写路径原有那条更具体的警告保留不变；
  2. **CLI JSON**：`doctor.fastboot.parity` 与 `info.fastboot.parity`（新增字段，原有键不变），
     并沿用 R4 的 `degraded:["system-fastboot-fallback"]`；
  3. **`doctor` per-check 新增 `fastboot-parity`**（detail 形如 `unknown — lineage unverified: …`），
     且顶层 `ok = found && runs && parity == huawei`，exit code 由 0 改为 **6（degraded）**。

**判据设计（只承认能证明的东西）**：只有工具自己的随包目录（`<base>/fastboot/`）能证明华为血统；
`/usr/bin`、`/usr/local/bin`、`/bin`、`/opt/android-sdk`、路径含 `platform-tools`、或 `--version` 行含
`platform-tools` 判为 `google`；其余（`KIRIN_FASTBOOT` 覆盖到任意路径）判 `unknown` —— 既不当成华为，
也不诬告成 Google。因此 `ok` 在这些情况下都是 `false`（"不得无条件为 true"）。

**改动文件**

| 文件 | 改动 |
| :--- | :--- |
| `Services/FastbootParity.cs` | 新增：`Classify(path, versionLine?)` / `IsHuawei` / `Describe`，三 token 常量 |
| `Services/FastbootClient.cs` | 新增 `Parity` 属性；构造函数在非 huawei 血统时写诊断警告（唯一的血统事实来源） |
| `Cli/DegradedSignals.cs` | `IsSystemFastbootFallback` 改为复用 `FastbootParity`（语义与既有断言一致，去掉了第二份判定） |
| `Cli/ExitCodes.cs` | 新增 `Degraded = 6` |
| `Cli/Commands/ReadCommands.cs` | `doctor`：`fastboot-parity` per-check + `fastboot.parity` + `ok` 收紧 + exit 6；`info`：`fastboot.parity` |
| `docs/cli-mcp.md` | 退出码表补 `6 降级`（含两个触发条件） |
| `.github/workflows/cli-mcp-ci.yml`、`.gitlab-ci.yml` | 冒烟里 stub fastboot 的 `doctor` 期望值由「隐式 0」改为显式断言 exit 6 + `parity":"unknown"` + `fastboot-parity` |
| `tests/WriteGates/Program.cs` | 旧断言 `doctor … exits 0` 改为 exit 6；新增 11 条血统断言（见下） |

**断言（`tests/WriteGates`，全部真跑）**

```
PASS doctor against a non-bundled override fastboot exits 6/degraded (got 6): warning: fastboot parity unknown — lineage unverified: not the bundled client; reads against Huawei temporary loaders may fail; set KIRIN_FASTBOOT to the bundled client (exit 6)
PASS doctor --json ok is false for a non-Huawei-parity fastboot (was unconditionally true before D-07)
PASS doctor --json reports fastboot.parity=unknown for an unverifiable override path
PASS the per-check table carries fastboot-parity with ok=false, next to fastboot-found/fastboot-runs
PASS the fastboot-parity detail names the lineage token
PASS /usr/bin/fastboot is classified google (distro platform-tools)
PASS an SDK platform-tools layout is classified google
PASS the bundled directory is the only path that proves huawei lineage
PASS an absent fastboot has no lineage (unknown)
PASS an arbitrary override path is unknown, not silently assumed Huawei
PASS a version line that says platform-tools is google even from an odd path
PASS FastbootClient exposes the lineage of the binary every one of its commands will run
PASS /usr/bin/fastboot is classified as the system fallback   (R4 旧断言，行为保持)
PASS an absent fastboot is not a fallback token                (R4 旧断言，行为保持)
PASS doctor --json reports degraded:[system-fastboot-fallback] (R4 旧断言，行为保持)
```

**行为变更声明**：`doctor` 在非华为血统时由 exit 0 改为 exit 6，`ok` 由 true 改为 false。这正是本条债务要求的
「不得无条件为 true」，两条 CI 冒烟与 `tests/WriteGates` 的旧断言据此同步更新（不是放松：旧断言只要求 0，
新断言固定 6 + `ok:false` + `parity:unknown`）。`tests/StableParity/boundary.json` 未改。



### D-08（F17 / F18）——「连上」与「问出了东西」分成两个判据

**判据（改前 / 改后）**

- 改前：`doctor.ok = found && runs`（与设备无关）；`info` 是 `connected ? exit 0 : exit 2`。
  一个接受连接、却拒绝所有 `getvar` 的 loader 得到的结果与健康设备**完全一样**（`connected:true` + exit 0 + 空
  `variables`），于是「连上但一无所知」无法与「连上且正常」区分（审计 F17 / F18）。
- 改后：两个命令都算一个 `informationallyUseful`（`connected && (任一 getvar 有值 || oem get-psid 有值 ||
  ptable 可读)`），并给出三态 `status`，JSON 与 exit code 双通道可区分：

| 场景 | `info` | `doctor` | exit |
| :--- | :--- | :--- | :--- |
| 没有 fastboot | `device.status=fastboot-missing` | `status=fastboot-missing` | 5 |
| 有 fastboot、没有设备 | `not-connected` | `no-device`（仅在人读摘要里） | 2（doctor 保持 0/6 语义见下） |
| 连上但一无所知 | `connected-but-empty` + `informationallyUseful:false` | `status=connected-but-empty` + `device.informationallyUseful:false` + `ok:false` | **6** |
| 连上且能读出变量 | `ok` + `informationallyUseful:true` | `ok`（血统为 huawei 时）/ `degraded-fastboot-parity` | 0 / 6 |

`doctor` 的 `status` 优先级：设备级发现优先（`connected-but-empty` 直接回答「为什么每次读都失败」），
血统事实另有 `fastboot.parity` 与 `fastboot-parity` per-check，两者都落到 exit 6。
`doctor` 里 `fastboot-device` per-check 仍为 `ok=true`（它确实连上了），`psid-readable`/`ptable-readable` 为
`--` —— per-check 表本身就能看出「连上」与「问得出东西」是两件事。

**改动文件**：`Cli/Commands/ReadCommands.cs`（两个命令的 `informationallyUseful` / `status` / exit 映射、
人读输出的 `device: <status> (connected: …, readable info: …)`、stderr 上的具名警告）、
`tests/WriteGates/Program.cs`（stub 新增 `STUB_MODE=silent-device`：`devices` 有设备、其余全部 `FAILED`）。

**断言（`tests/WriteGates` §13，全部真跑）**

```
PASS info against a connecting but silent loader exits 6/degraded (got 6): warning: device is connected but answered no readable variable (exit 6); check the fastboot lineage (info --json fastboot.parity) before blaming the cable
PASS info still reports connected:true for a silent loader (no lie about the link)
PASS info distinguishes 'connected-but-empty' from 'ok' in device.status
PASS info says the device is not informationally useful
PASS info's variables map is empty (the loader rejected every getvar)
PASS doctor against the same silent loader exits 6/degraded (got 6): warning: fastboot parity unknown — lineage unverified: not the bundled client; reads against Huawei temporary loaders may fail; set KIRIN_FASTBOOT to the bundled client (exit 6)
PASS doctor carries the same device-level status token
PASS doctor ok is false when the device connects and answers nothing
PASS doctor device.informationallyUseful is false
PASS the per-check table keeps the two judgements apart: fastboot-device is ok (it IS connected) while psid/ptable are not
PASS info against an answering loader still exits 0 (got 0)
PASS info device.status is ok when a variable is readable
PASS info device.informationallyUseful is true when a variable is readable
PASS the readable variable is still reported unchanged
PASS doctor agrees the answering device is informationally useful
PASS with a usable device the remaining status is the lineage one (the stub is not the bundled client)
PASS doctor still exits 6 for the lineage reason, not because of the device (got 6)
```

**行为变更声明**：`info` 在 connected-but-empty 时由 exit 0 改为 exit 6；`doctor` 同理。新增字段全部是 additive
（`device.status`、`device.informationallyUseful`、顶层 `status`），原有键与含义未动；`boundary.json` 未改。
