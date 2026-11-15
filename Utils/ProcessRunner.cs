// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

/*
 * Copyright (c) 2026 Kethily Daniel & NDXCode. All rights reserved.
 * 
 * Use of this software is governed by the Business Source License included 
 * in the LICENSE file and at www.mariadb.com/bsl11.
 * 
 * Change Date: Four years from the date each version of the Licensed Work 
 * is first publicly distributed.
 * 
 * On the Change Date, in accordance with the Business Source License, 
 * use of this software will be governed by the GNU General Public License v3.0 
 * or later (GPL-3.0-or-later).
 * 
 * Contact Information: https://kirintool.cfd
 */

using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Diagnostics;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Threading;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace Kirin_Tool.Utils
{
    /// <summary>
    /// The single place in the process tree where a child process is started.
    /// Every run writes two things: the legacy human diagnostics file
    /// (`kirintool_log_*.log`, unchanged, fastboot only) and one structured JSONL
    /// operation file (<see cref="OperationLogger"/>), for every tool.
    /// </summary>
    public static class ProcessRunner
    {
        private static readonly object LogLock = new object();

        /// <summary>
        /// 活着的子进程登记表。超时路径已经 kill 整棵树，但宿主进程自己退出
        /// （GUI 关窗、崩溃、被 kill）时子进程会变孤儿——2026-11-15 17:41 就留了一个
        /// fastboot oem dump-emmc ptable 在 y700 的 fastboot 会话上跑了 4 分钟。
        /// 这里在进程退出时统一回收。
        /// </summary>
        private static readonly object LiveLock = new object();
        private static readonly List<Process> Live = new List<Process>();

        static ProcessRunner()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, __) => KillLiveChildren();
            AppDomain.CurrentDomain.UnhandledException += (_, __) => KillLiveChildren();
        }

        /// <summary>
        /// Kill every child this process still has running. Called from the process-exit hooks and
        /// from <see cref="OperationCancellation.CancelAll"/> (window close, R2).
        /// </summary>
        internal static void KillLiveChildren()
        {
            List<Process> snapshot;
            lock (LiveLock)
            {
                snapshot = new List<Process>(Live);
                Live.Clear();
            }

            foreach (Process process in snapshot)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 进程已经没了 / 没权限：退出路径上不抛
                }
            }
        }

        public static Task<ProcessResult> RunAsync(string executablePath, string arguments, int timeoutMinutes = 300, int timeoutSeconds = 0, string? op = null)
        {
            var info = StartInfo(executablePath);
            info.Arguments = arguments;
            return RunAsync(info, arguments, OperationLogger.ParseArgv(arguments), timeoutMinutes, timeoutSeconds, op, null);
        }

        public static Task<ProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, int timeoutMinutes = 300, int timeoutSeconds = 0, string? op = null)
        {
            var info = StartInfo(executablePath);
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
            return RunAsync(info, Quote(arguments), arguments, timeoutMinutes, timeoutSeconds, op, null);
        }

        /// <summary>
        /// Same contract as the list overload, but output is read chunk-by-chunk and echoed to
        /// <paramref name="progress"/> as it arrives, so a long bootloader unlock can be rendered
        /// live. Logging, timeout and kill-tree behaviour are shared with every other command:
        /// this overload exists so no caller has to build its own <see cref="ProcessStartInfo"/>
        /// (docs/op-logging.md §4).
        /// </summary>
        public static Task<ProcessResult> RunStreamingAsync(string executablePath, IReadOnlyList<string> arguments, IProgress<string>? progress = null, int timeoutMinutes = 300, int timeoutSeconds = 0, string? op = null)
        {
            var info = StartInfo(executablePath);
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
            return RunAsync(info, Quote(arguments), arguments, timeoutMinutes, timeoutSeconds, op, progress);
        }

        private static string Quote(IReadOnlyList<string> arguments) =>
            string.Join(" ", arguments.Select(a => System.Text.Json.JsonSerializer.Serialize(a)));

        /// <summary>
        /// 子进程唯一的创建点。除重定向外还做一件事：当操作者固定了目标 SN
        /// （<see cref="DeviceSerial"/>）时，把 <c>ANDROID_SERIAL</c> 注入环境——
        /// fastboot 与 adb 都认这个变量，于是即便某个调用点忘了写 `-s`，
        /// 也不会在多设备主机上自选「第一台」设备（审计 B 类：串号匹配缺失）。
        /// 未配置 SN 时不注入，保持上游行为不变。
        /// </summary>
        private static ProcessStartInfo StartInfo(string executablePath)
        {
            var info = new ProcessStartInfo
            {
                FileName = executablePath, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            string? serial = DeviceSerial.Resolve();
            string tool = ToolName(executablePath);
            if (serial != null && (tool == "fastboot" || tool == "adb"))
            {
                info.Environment["ANDROID_SERIAL"] = serial;
            }

            return info;
        }

        private static async Task<ProcessResult> RunAsync(ProcessStartInfo processStartInfo, string arguments, IReadOnlyList<string> argv, int timeoutMinutes, int timeoutSeconds, string? op, IProgress<string>? progress)
        {
            string executablePath = processStartInfo.FileName;
            string tool = ToolName(executablePath);

            // 显式 -s <SN>（审计 B 类）：环境变量 ANDROID_SERIAL 已经能防误选设备，但 op log
            // 里只有 argv，看不到目标机。已知 SN 时把 -s 真正写进命令行（fastboot 与 adb 都认，
            // 且必须在子命令之前），这样 JSONL 的 argv 和 kirintool_log 都能事后核对「打的是哪一台」。
            // 纯列举/版本探测不加：'fastboot devices' 要列出全部设备，不能只看一台。
            if (tool == "fastboot" || tool == "adb")
            {
                string? pinnedSerial = DeviceSerial.Resolve();
                if (pinnedSerial != null && !IsReadOnlyProbe(argv))
                {
                    argv = PinSerial(argv, pinnedSerial);
                    arguments = Quote(argv);
                    processStartInfo.ArgumentList.Clear();
                    processStartInfo.Arguments = arguments;
                }
            }

            string operation = string.IsNullOrWhiteSpace(op) ? DeriveOp(tool, argv) : op;

            using var scope = OperationLogger.Begin(operation, tool, argv);
            scope.Step("exec " + executablePath);

            // R2: a window that is already closing must not spawn one more child.
            if (OperationCancellation.Token.IsCancellationRequested)
            {
                string cancelledBeforeLaunch = "operation cancelled before launch (the window is closing)";
                scope.Result(false, null, null, null, cancelledBeforeLaunch, "cancelled", "refused before launch");
                throw new OperationCanceledException(cancelledBeforeLaunch);
            }

            using var process = new Process { StartInfo = processStartInfo };
            try
            {
                // 多设备主机上 fastboot 会自选「第一台」设备。未固定 SN 且同时在线 >1 台时
                // 直接拒绝执行，而不是猜一台（2026-11-15 y700 被 oem dump-emmc 打中的直接原因）。
                // 纯列举/版本查询不写入任何设备，放行（doctor、设备列表、CLI 自检都要用）
                if (tool == "fastboot" && DeviceSerial.Resolve() == null && !IsReadOnlyProbe(argv))
                {
                    string? multiple = MultipleFastbootDevices(executablePath);
                    if (multiple != null)
                    {
                        string why = $"多个 fastboot 设备同时在线（{multiple}）：拒绝执行 {arguments}。"
                                   + "请设置 KIRIN_SERIAL / ANDROID_SERIAL 指定目标机，或先拔掉其它设备。";
                        scope.Result(false, null, null, null, why, "device-selection", "refused before launch");
                        throw new InvalidOperationException(why);
                    }
                }

                process.Start();
                lock (LiveLock) Live.Add(process);
            }
            catch (Exception ex)
            {
                scope.Result(false, null, null, null, ex.Message, "exception:" + ex.GetType().Name, "child process could not be started");
                throw;
            }

            var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromMinutes(timeoutMinutes);
            // R2: the child is killed when either the command times out or the host operation is
            // cancelled (window close). Both paths kill the whole tree; only the verdict differs.
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, OperationCancellation.Token);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var combined = progress == null ? null : new StringBuilder();
                var combinedLock = new object();

                Task<string> outputTask = progress == null
                    ? process.StandardOutput.ReadToEndAsync()
                    : PumpAsync(process.StandardOutput, combined, combinedLock, progress);
                Task<string> errorTask = progress == null
                    ? process.StandardError.ReadToEndAsync()
                    : PumpAsync(process.StandardError, combined, combinedLock, progress);

                await process.WaitForExitAsync(cts.Token);

                string output = await outputTask;
                string error = await errorTask;

                var result = new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Output = output + error,
                    Arguments = arguments
                };

                WriteLog(executablePath, arguments, result.Output);

                // stdout and stderr stay split in the structured log, so "OKAY came back on
                // stderr" can no longer masquerade as a device answer (audit S24).
                scope.Result(
                    result.IsSuccess,
                    process.ExitCode,
                    stopwatch.ElapsedMilliseconds,
                    output,
                    error,
                    result.IsSuccess
                        ? "exit-code"
                        : "exit-code:" + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

                return result;
            }
            catch (OperationCanceledException)
            {
                // Kill the whole tree so child processes do not outlive the timeout or a close.
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                process.WaitForExit(3000);

                if (OperationCancellation.IsCancellationRequested)
                {
                    WriteLog(executablePath, arguments, "[Cancelled: the host operation was cancelled; the process tree was killed]");
                    scope.Result(false, null, stopwatch.ElapsedMilliseconds, null, null, "cancelled",
                        "operation cancelled by the host (window close or user cancel); child process tree killed");
                    throw new OperationCanceledException(
                        "operation cancelled by the host; the child process tree was killed");
                }

                WriteLog(executablePath, arguments, $"[Process Timed Out after {timeout.TotalSeconds} seconds]");
                scope.Result(false, null, stopwatch.ElapsedMilliseconds, null, null, "timeout", $"process timed out after {timeout.TotalSeconds:F0} seconds");
                throw new TimeoutException($"Process timed out after {timeout.TotalSeconds:F0} seconds");
            }
            catch (Exception ex)
            {
                scope.Result(false, null, stopwatch.ElapsedMilliseconds, null, ex.Message, "exception:" + ex.GetType().Name);
                throw;
            }
            finally
            {
                lock (LiveLock) Live.Remove(process);
            }
        }

        /// <summary>
        /// Chunked reader used by <see cref="RunStreamingAsync"/>. Returns the full text of the
        /// stream; <paramref name="combined"/> accumulates stdout+stderr in arrival order for the
        /// live progress callback (same text the GUI already showed).
        /// </summary>
        // Invariant: `combined` is non-null whenever `progress` is non-null (RunAsync creates
        // `combined = new StringBuilder()` exactly when `progress != null`, and PumpAsync is only
        // called on that branch). The `?.` therefore never skips a real append; it only exists
        // so the null-state of the shared local is expressible without `!` at the call sites.
        private static async Task<string> PumpAsync(StreamReader reader, StringBuilder? combined, object combinedLock, IProgress<string> progress)
        {
            var builder = new StringBuilder();
            var chunk = new char[256];
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(chunk, 0, chunk.Length);
                    if (read == 0) break;

                    string snapshot;
                    lock (builder)
                    {
                        builder.Append(chunk, 0, read);
                        snapshot = builder.ToString();
                    }

                    lock (combinedLock)
                    {
                        combined?.Append(chunk, 0, read);
                    }

                    progress.Report(snapshot);
                }
            }
            catch (Exception)
            {
                // The stream closed because the process died or was killed. The exit code is
                // the authoritative verdict (logged by the caller) but the text already read
                // must survive, which is what the audit's S26 finding was about.
            }

            lock (builder)
            {
                return builder.ToString();
            }
        }

        /// <summary>
        /// fastboot devices 里出现多于一台就返回它们的 SN 列表，否则 null。
        /// 直接起进程（不走 RunAsync）避免递归与日志噪声；5 s 超时、失败一律当作「不确定」放行。
        /// </summary>
        private static string? MultipleFastbootDevices(string executablePath)
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = executablePath, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                info.ArgumentList.Add("devices");

                using var probe = Process.Start(info);
                if (probe == null) return null;

                string output = probe.StandardOutput.ReadToEnd();
                if (!probe.WaitForExit(5000))
                {
                    try { probe.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                var serials = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(columns => columns.Length >= 2 &&
                        string.Equals(columns[1], "fastboot", StringComparison.OrdinalIgnoreCase))
                    .Select(columns => columns[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return serials.Length > 1 ? string.Join(", ", serials) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ToolName(string executablePath)
        {
            string name = Path.GetFileName(executablePath) ?? string.Empty;
            if (name.Length == 0) name = executablePath ?? string.Empty;
            if (name.Contains("fastboot", StringComparison.OrdinalIgnoreCase)) return "fastboot";
            if (name.Contains("adb", StringComparison.OrdinalIgnoreCase)) return "adb";
            return "internal";
        }

        /// <summary>Pure enumeration/version probes: never pinned and never subject to the multi-device refusal.</summary>
        private static bool IsReadOnlyProbe(IReadOnlyList<string> argv) =>
            argv.Count == 1 &&
            (string.Equals(argv[0], "devices", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(argv[0], "--version", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(argv[0], "help", StringComparison.OrdinalIgnoreCase));

        /// <summary>Insert `-s &lt;serial&gt;` before the subcommand (fastboot and adb both require that order).</summary>
        private static IReadOnlyList<string> PinSerial(IReadOnlyList<string> argv, string serial)
        {
            var pinned = new List<string>(argv.Count + 2) { "-s", serial };
            pinned.AddRange(argv);
            return pinned;
        }

        /// <summary>`fastboot flash boot x.img` → `fastboot.flash`; otherwise `tool.run`.
        /// Skips option values (`-s SN`) so the injected serial never becomes the op name.</summary>
        private static string DeriveOp(string tool, IReadOnlyList<string> argv)
        {
            for (int i = 0; i < argv.Count; i++)
            {
                string argument = argv[i] ?? string.Empty;
                if (argument is "-s" or "--serial")
                {
                    i++;
                    continue;
                }
                if (argument.Length == 0 || argument.StartsWith("-", StringComparison.Ordinal)) continue;
                return tool + "." + argument;
            }
            return tool + ".run";
        }

        private static void WriteLog(string executablePath, string arguments, string output)
        {
            if (!executablePath.Contains("fastboot", StringComparison.OrdinalIgnoreCase)) return;
            WriteDiagnostic($"{Path.GetFileName(executablePath)} {arguments}", output);
        }

        public static void WriteDiagnostic(string source, string output)
        {
            try
            {
                string fileName = $"kirintool_log_{DateTime.Now:yyyy_MM_dd}.log";
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string cmdText = source;
                string logMessage = $"[{timestamp}] {cmdText}\n{output}\n\n";

                // Prefer a log dir next to the executable; when that is not
                // writable (AppImage squashfs, root-owned pacman install),
                // fall back to the user's XDG state directory.
                string exeLogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                string? xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                if (string.IsNullOrWhiteSpace(xdgState))
                {
                    xdgState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
                }
                string userLogDir = Path.Combine(xdgState, "Kirin-Tool", "log");

                foreach (var logDir in new[] { exeLogDir, userLogDir })
                {
                    try
                    {
                        Directory.CreateDirectory(logDir);
                        lock (LogLock)
                        {
                            File.AppendAllText(Path.Combine(logDir, fileName), logMessage);
                        }
                        return;
                    }
                    catch
                    {
                        // try the next candidate
                    }
                }
            }
            catch {}
        }
    }
}
