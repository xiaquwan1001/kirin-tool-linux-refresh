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
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Utils;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kirin_Tool.Services
{
    public class FastbootClient
    {
        private readonly string _fastbootPath;

        /// <summary>
        /// Cached pre-write identity probe (K6, audit 2026-11-15 §4). Read once per client: the
        /// product cannot change under a running fastboot session, and a write plan must not pay
        /// for one extra spawn per partition. A refusal is cached too, so every later write in the
        /// same session is refused without another probe.
        /// </summary>
        private DeviceIdentity? _identity;

        /// <summary>The probe result the cached <see cref="DeviceIdentity"/> was derived from, kept so a
        /// refusal can quote the loader answer without probing a second time.</summary>
        private ProcessResult? _identityRead;

        private const int UnlockTimeoutMinutes = 300;

        /// <summary>
        /// Vendor-fixed `oem unlock` magic (audit H39): a public constant, not a credential, so
        /// it stays readable in the JSONL log. Callers may override it, and a caller-supplied
        /// code is registered as a secret and masked instead.
        /// </summary>
        public const string DefaultUnlockCode = "UUUUUUUUUUUUUUUU";

        /// <summary>\`oem unlock\` rejects anything that is not exactly this long.</summary>
        public const int UnlockCodeLength = 16;

        public FastbootClient(string? executablePath = null)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                _fastbootPath = ResolveExecutablePath(executablePath);
            }
            else
            {
                _fastbootPath = ResolveDefaultFastbootPath(out bool systemFallback);
                IsSystemFastboot = systemFallback;
            }

            Parity = FastbootParity.Classify(_fastbootPath);

            // D-07 / audit P11+P12+H32: the fallback used to be silent — the first symptom was
            // "every read fails", which looks like a cable problem. Announce it once, at the moment
            // the client exists, whoever constructed it (GUI, CLI, MCP) and for reads as well as
            // writes. The write path keeps its own, more specific warning below.
            if (!string.Equals(Parity, FastbootParity.Huawei, StringComparison.Ordinal))
            {
                ProcessRunner.WriteDiagnostic("FastbootClient",
                    "warning: fastboot lineage " + Parity + " at " + _fastbootPath
                    + " — " + FastbootParity.Describe(Parity)
                    + (IsSystemFastboot ? " (the bundled Huawei client was not found)" : string.Empty));
            }
        }

        /// <summary>The fastboot executable every command of this client is sent to.</summary>
        public string ExecutablePath => _fastbootPath;

        /// <summary>
        /// True when the bundled Huawei client was missing and the system Google platform-tools
        /// <c>fastboot</c> was used instead. The two are not equivalent on these loaders (the write
        /// path warns about it in the diagnostics log); an explicit path never sets this.
        /// </summary>
        public bool IsSystemFastboot { get; }

        /// <summary>
        /// D-07: `huawei` / `google` / `unknown`, from <see cref="FastbootParity"/>. A command that
        /// reads device variables must be able to say which client it asked, because a platform-tools
        /// client failing every `getvar` is not evidence about the device.
        /// </summary>
        public string Parity { get; }

        /// <summary>
        /// 「有设备可写吗」的唯一判据。上游只看「任意一台处于 fastboot 的设备」，
        /// 多设备主机上（手表 18d1:d00d、别的手机）会误判成本项目设备可写（审计 B 类）。
        /// 配置了 <see cref="DeviceSerial"/>（KIRIN_SERIAL/ANDROID_SERIAL/P40_SERIAL）时
        /// 只认那一台；未配置时保持上游语义。
        /// </summary>
        public async Task<bool> IsDeviceConnected()
        {
            try
            {
                var result = await ProcessRunner.RunAsync(_fastbootPath, "devices", op: "fastboot.devices");
                if (!result.IsSuccess) return false;

                string? pinned = DeviceSerial.Resolve();
                return result.Output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Any(columns => columns.Length >= 2 &&
                        string.Equals(columns[1], "fastboot", StringComparison.OrdinalIgnoreCase) &&
                        (pinned == null || string.Equals(columns[0], pinned, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// K6 write gate: the loader must answer <c>getvar product</c> with a model name before any
        /// irreversible write is sent. Returns the verdict; throws <see cref="InvalidOperationException"/>
        /// (after recording a <c>write.identity</c> op with verdict <c>device-identity</c>) when the
        /// loader answered something that is not an identity — the 2026-11-15 rescue write ran on
        /// another phone because a bare "OKAY" was accepted as "product".
        ///
        /// Probes that cannot run (no device, stub binary, timeout) do not refuse the write: they are
        /// recorded as unreadable, so no chip family is blocked by a naming whitelist.
        /// </summary>
        public async Task<DeviceIdentity> EnsureWritableIdentityAsync()
        {
            // A write-path warning, not a refusal: Google platform-tools may behave differently
            // against these loaders, so the audit trail must say which binary was used.
            if (IsSystemFastboot && _identity == null)
                ProcessRunner.WriteDiagnostic("FastbootClient",
                    "warning: using the system fastboot at " + _fastbootPath
                    + " (the bundled Huawei client was not found); a write is being attempted with it");

            (DeviceIdentity identity, ProcessResult? read) = await ProbeIdentityAsync();
            if (identity.Refused) throw Refusal(identity, read);
            return identity;
        }

        /// <summary>
        /// R1 read gate (audit 2026-11-15 §4). The commands that *pull* data off the device
        /// (`oem dump-emmc`, `oem dump-storage`, `fetch`) write files on the host and have
        /// already been sent to the wrong phone. They are gated by the same probe as the write
        /// path (one `getvar product` per client, cached) but with a weaker rule:
        ///
        /// * <b>invalid</b> (the loader answered, and the answer is not a model name) — refuse.
        ///   That is exactly the 2026-11-15 shape, and a command that writes host files must not
        ///   run on a device that cannot name itself.
        /// * <b>unreadable</b> (no device, stub, timeout, non-zero exit) — allowed, like the write
        ///   path: a probe that could not run is not evidence of the wrong device.
        /// </summary>
        public async Task<DeviceIdentity> EnsureReadableIdentityAsync()
        {
            (DeviceIdentity identity, ProcessResult? read) = await ProbeIdentityAsync();
            if (identity.Refused) throw ReadRefusal(identity, read);
            return identity;
        }

        /// <summary>One `getvar product` per client; both gates share (and cache) the verdict.</summary>
        private async Task<(DeviceIdentity Identity, ProcessResult? Read)> ProbeIdentityAsync()
        {
            if (_identity != null) return (_identity, _identityRead);

            ProcessResult? read = null;
            try
            {
                read = await ProcessRunner.RunAsync(_fastbootPath, new[] { "getvar", "product" },
                    timeoutSeconds: 10, op: "fastboot.getvar");
            }
            catch (Exception)
            {
                read = null;
            }

            string? product = read == null ? null : DeviceIdentityGate.ParseProduct(read.Output);
            DeviceIdentity identity = DeviceIdentityGate.Evaluate(read, product);
            _identity = identity;
            _identityRead = read;
            return (identity, read);
        }

        /// <summary>One <c>write.identity</c> op per refusal, then the exception the callers already handle.</summary>
        private static DeviceIdentityGate.DeviceIdentityRefusedException Refusal(DeviceIdentity identity, ProcessResult? read)
        {
            string message = "Refusing to write: the device did not identify itself. " + identity.Reason
                + ". Reconnect the phone and confirm 'fastboot getvar product' answers a model name"
                + " (e.g. ELS-AN00) before writing anything.";
            using OperationScope op = OpLog.Begin(OpNames.WriteIdentity, OpLog.FlashTool,
                new[] { "getvar", "product" }, "pre-write identity gate (audit 2026-11-15 §4)");
            op.Step(OpLog.FailedPrefix + "identity: " + identity.Describe());
            op.Result(false, read?.ExitCode, null, read?.Output, null,
                DeviceIdentityGate.VerdictRefused, message);
            return new DeviceIdentityGate.DeviceIdentityRefusedException(message);
        }

        /// <summary>One `read.identity` op per read refusal, then the same exception type the callers handle.</summary>
        private static DeviceIdentityGate.DeviceIdentityRefusedException ReadRefusal(DeviceIdentity identity, ProcessResult? read)
        {
            string message = "Refusing to read from the device: it did not identify itself. " + identity.Reason
                + ". No dump/fetch command was sent (nothing was written on this host). Reconnect the phone"
                + " and confirm 'fastboot getvar product' answers a model name (e.g. ELS-AN00).";
            using OperationScope op = OpLog.Begin(OpNames.ReadIdentity, OpLog.FlashTool,
                new[] { "getvar", "product" }, "pre-read identity gate (audit 2026-11-15 §4)");
            op.Step(OpLog.FailedPrefix + "identity: " + identity.Describe());
            op.Result(false, read?.ExitCode, null, read?.Output, null,
                DeviceIdentityGate.VerdictRefusedRead, message);
            return new DeviceIdentityGate.DeviceIdentityRefusedException(message);
        }

        /// <summary>
        /// True when the subcommand pulls data off the device and therefore writes host files
        /// (`oem dump-emmc`/`oem dump-storage`, `fetch`). Options are skipped, the literal
        /// `oem` prefix is skipped, and only the first real subcommand decides.
        /// </summary>
        private static bool IsDevicePullingCommand(IReadOnlyList<string> tokens)
        {
            foreach (string token in tokens)
            {
                string value = token.Trim();
                if (value.Length == 0 || value.StartsWith("-", StringComparison.Ordinal)) continue;
                if (string.Equals(value, "oem", StringComparison.OrdinalIgnoreCase)) continue;
                return value.StartsWith("dump-", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "fetch", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static string ResolveDefaultFastbootPath(out bool systemFallback)
        {
            systemFallback = false;
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(AppContext.BaseDirectory, "fastboot", "fastboot.exe");
            }

            // Linux releases bundle the native Huawei client. Keep the system
            // fallback for manually built app directories without that binary.
            string bundledPath = Path.Combine(AppContext.BaseDirectory, "fastboot", "fastboot");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }

            const string systemPath = "/usr/bin/fastboot";
            systemFallback = true;
            return File.Exists(systemPath) ? systemPath : "fastboot";
        }

        private static string ResolveExecutablePath(string executablePath)
        {
            return Path.IsPathRooted(executablePath)
                ? executablePath
                : Path.Combine(AppContext.BaseDirectory, executablePath);
        }

        public async Task<string> ReadNveVariable(string variable)
        {
            var result = await QueryVariableAsync($"nve:{variable}");
            return FastbootReadResponses.Variable(result, $"nve:{variable}");
        }

        public async Task<IReadOnlyDictionary<string, string>> ReadPsidAsync()
        {
            var result = await ProcessRunner.RunAsync(_fastbootPath, new[] { "oem", "get-psid" }, timeoutSeconds: 10, op: "fastboot.get-psid");
            return FastbootReadResponses.Psid(result);
        }

        public Task<ProcessResult> QueryVariableAsync(string variable) =>
            ProcessRunner.RunAsync(_fastbootPath, new[] { "getvar", variable }, timeoutSeconds: 10, op: "fastboot.getvar");

        public async Task<ProcessResult> WriteNveVariable(string variable, string value)
        {
            await EnsureWritableIdentityAsync();
            // A write disguised as getvar (audit H34): the op name says "set", the argv says why.
            return await ProcessRunner.RunAsync(_fastbootPath, $"getvar nve:{variable}@{value}", op: "fastboot.setvar.nve");
        }

        public async Task<ProcessResult> FlashPartition(string partitionName, string filePath)
        {
            await EnsureWritableIdentityAsync();
            return await ProcessRunner.RunAsync(_fastbootPath, new[] { "flash", partitionName, filePath }, op: "fastboot.flash");
        }

        public async Task<FrpBypassResult> EraseFrpWithSteps()
        {
            await EnsureWritableIdentityAsync();
            var result = new FrpBypassResult();

            var step1Result = await ProcessRunner.RunAsync(_fastbootPath, "erase frp", op: "fastboot.erase-frp.step1");
            result.Step1Success = step1Result.IsSuccess && step1Result.Output.ToUpper().Contains("OKAY");
            result.Step1Output = step1Result.Output;

            var step2Result = await ProcessRunner.RunAsync(_fastbootPath, "erase config", op: "fastboot.erase-frp.step2");
            result.Step2Success = step2Result.IsSuccess && step2Result.Output.ToUpper().Contains("OKAY");
            result.Step2Output = step2Result.Output;

            var step3Result = await ProcessRunner.RunAsync(_fastbootPath, "oem frp-erase", op: "fastboot.erase-frp.step3");
            result.Step3Success = step3Result.IsSuccess && step3Result.Output.ToUpper().Contains("OKAY");
            result.Step3Output = step3Result.Output;


            return result;
        }

        public async Task<EnableDowngradeResult> EnableDowngradeWithSteps()
        {
            await EnsureWritableIdentityAsync();
            var result = new EnableDowngradeResult();

            var step1Result = await ProcessRunner.RunAsync(_fastbootPath, "oem oeminfoerase-amssver", op: "fastboot.enable-downgrade.step1");
            result.Step1Success = step1Result.IsSuccess && step1Result.Output.ToUpper().Contains("OKAY");
            result.Step1Output = step1Result.Output;

            var step2Result = await ProcessRunner.RunAsync(_fastbootPath, "oem oeminfoerase-basever", op: "fastboot.enable-downgrade.step2");
            result.Step2Success = step2Result.IsSuccess && step2Result.Output.ToUpper().Contains("OKAY");
            result.Step2Output = step2Result.Output;

            var step3Result = await ProcessRunner.RunAsync(_fastbootPath, "oem oeminfoerase-custver", op: "fastboot.enable-downgrade.step3");
            result.Step3Success = step3Result.IsSuccess && step3Result.Output.ToUpper().Contains("OKAY");
            result.Step3Output = step3Result.Output;

            var step4Result = await ProcessRunner.RunAsync(_fastbootPath, "oem oeminfoerase-preloadver", op: "fastboot.enable-downgrade.step4");
            result.Step4Success = step4Result.IsSuccess && step4Result.Output.ToUpper().Contains("OKAY");
            result.Step4Output = step4Result.Output;

            return result;
        }

        public async Task<ProcessResult> PullOemInfo(string outputPath)
        {
            return await ProcessRunner.RunAsync(_fastbootPath, $"oem dump-emmc oeminfo \"{outputPath}\"", op: "fastboot.dump-oeminfo");
        }

        public async Task<ProcessResult> FlashOemInfo(string filePath)
        {
            await EnsureWritableIdentityAsync();
            return await ProcessRunner.RunAsync(_fastbootPath, $"flash oeminfo \"{filePath}\"", op: "fastboot.flash-oeminfo");
        }

        public async Task<string> GetVarAsync(string variable)
        {
            var result = await ProcessRunner.RunAsync(_fastbootPath, $"getvar {variable}", timeoutSeconds: 10, op: "fastboot.getvar");
            return result.Output;
        }

        /// <summary>
        /// <c>fastboot oem &lt;command&gt;</c> from a verbatim command line. The string is tokenised
        /// with <see cref="OperationLogger.ParseArgv"/> (quote aware) and then handed to the argv
        /// overload, so the child process receives the same tokens the read gate judged and the
        /// op log recorded — one code path for both forms (audit round5 §10).
        /// </summary>
        public Task<string> OemCommandAsync(string command, int timeoutMinutes = 300) =>
            OemCommandAsync(OperationLogger.ParseArgv(command), timeoutMinutes);

        /// <summary>
        /// The same command as an argv list. Each token is passed as its own child argument
        /// (<see cref="ProcessRunner"/>'s ArgumentList path), so a host path that contains spaces or
        /// quotes cannot be re-split by a shell or by argument parsing. A leading <c>oem</c> token is
        /// accepted and not doubled, so callers can pass either the sub-command or the full command.
        /// </summary>
        public async Task<string> OemCommandAsync(IReadOnlyList<string> argv, int timeoutMinutes = 300)
        {
            ArgumentNullException.ThrowIfNull(argv);
            if (argv.Count == 0)
                throw new ArgumentException("an oem command needs at least one token", nameof(argv));

            IReadOnlyList<string> subCommand = argv[0].Trim().Equals("oem", StringComparison.OrdinalIgnoreCase)
                ? argv.Skip(1).ToList()
                : argv.ToList();
            if (subCommand.Count == 0)
                throw new ArgumentException("an oem command needs a sub-command after 'oem'", nameof(argv));

            // R1: `oem dump-emmc/dump-storage` write real files on the host; they are not writes
            // to the device and therefore were outside the K6 gate. They are gated here, on the same
            // token list the child will receive.
            if (IsDevicePullingCommand(subCommand)) await EnsureReadableIdentityAsync();

            var full = new List<string>(subCommand.Count + 1) { "oem" };
            full.AddRange(subCommand);
            var result = await ProcessRunner.RunAsync(_fastbootPath, full, timeoutMinutes: timeoutMinutes, op: "fastboot.oem." + FirstWord(subCommand));
            return result.Output;
        }

        public async Task<string> CommandAsync(string arguments, int timeoutMinutes = 300)
        {
            // R1: the same rule for a raw command line, so a `fetch` never bypasses the gate.
            if (IsDevicePullingCommand(OperationLogger.ParseArgv(arguments))) await EnsureReadableIdentityAsync();
            var result = await ProcessRunner.RunAsync(_fastbootPath, arguments, timeoutMinutes: timeoutMinutes, op: "fastboot." + FirstWord(arguments));
            return result.Output;
        }

        public async Task<string> RebootBootloaderAsync()
        {
            return await CommandAsync("reboot-bootloader");
        }

        /// <summary>First token of a command line, for op naming (`oem frp-erase` → `frp-erase`).</summary>
        private static string FirstWord(string commandLine) => FirstWord(OperationLogger.ParseArgv(commandLine));

        /// <summary>First non-option token of an already tokenised command, for op naming.</summary>
        private static string FirstWord(IReadOnlyList<string> tokens)
        {
            foreach (string token in tokens)
            {
                if (!token.StartsWith("-", StringComparison.Ordinal)) return token;
            }
            return "run";
        }

        /// <summary>
        /// `oem unlock &lt;code&gt;`. Streams stdout/stderr through <see cref="ProcessRunner.RunStreamingAsync"/>
        /// (the only permitted child-process entry point since lane D) so the operation gets the same
        /// timeout, kill-tree and JSONL logging as every other command instead of the previous
        /// unlogged bare <see cref="Process"/> (audit BB-34) that hard-coded the unlock magic (H39).
        ///
        /// `unlockCode` defaults to the vendor magic; the length is validated before anything is
        /// sent to the device and a rejection is recorded with `verdictSource=arg-validation`.
        /// </summary>
        public async Task<(bool IsSuccess, string Output)> UnlockBootloaderAsync(IProgress<string>? progress = null, string? unlockCode = null)
        {
            string code = string.IsNullOrEmpty(unlockCode) ? DefaultUnlockCode : unlockCode!;

            if (code.Length != UnlockCodeLength)
            {
                string message =
                    $"Unlock code must be exactly {UnlockCodeLength} characters long (got {code.Length}); nothing was sent to the device.";
                using var rejected = OperationLogger.Begin("fastboot.unlock", "fastboot", new[] { "oem", "unlock", code });
                rejected.Result(false, null, null, null, message, "arg-validation", "unlock code rejected before fastboot was started");
                return (false, message);
            }

            if (!string.Equals(code, DefaultUnlockCode, StringComparison.Ordinal))
            {
                // A caller-supplied code is treated as a credential: masked as ***(<len>) in the log.
                OperationLogger.RegisterSecret(code);
            }

            try
            {
                // K6: nothing irreversible is sent before the loader named its model.
                await EnsureWritableIdentityAsync();
                ProcessResult result = await ProcessRunner.RunStreamingAsync(
                    _fastbootPath,
                    new[] { "oem", "unlock", code },
                    progress,
                    timeoutMinutes: UnlockTimeoutMinutes,
                    op: "fastboot.unlock");

                return (result.IsSuccess, result.Output);
            }
            catch (TimeoutException)
            {
                return (false, $"Process timed out after {UnlockTimeoutMinutes} minutes");
            }
        }

        public async Task<ProcessResult> RebootAsync()
        {
            return await ProcessRunner.RunAsync(_fastbootPath, "reboot", op: "fastboot.reboot");
        }
    }
}
