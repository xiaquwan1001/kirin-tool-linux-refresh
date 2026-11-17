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
