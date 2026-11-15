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
            if (_identity is { Refused: true } refused) throw Refusal(refused, null);
            if (_identity != null) return _identity;

            // A write-path warning, not a refusal: Google platform-tools may behave differently
            // against these loaders, so the audit trail must say which binary was used.
            if (IsSystemFastboot)
                ProcessRunner.WriteDiagnostic("FastbootClient",
                    "warning: using the system fastboot at " + _fastbootPath
                    + " (the bundled Huawei client was not found); a write is being attempted with it");

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

            if (identity.Refused) throw Refusal(identity, read);
            return identity;
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

        public async Task<string> OemCommandAsync(string command, int timeoutMinutes = 300)
        {
            var result = await ProcessRunner.RunAsync(_fastbootPath, $"oem {command}", timeoutMinutes: timeoutMinutes, op: "fastboot.oem." + FirstWord(command));
            return result.Output;
        }

        public async Task<string> CommandAsync(string arguments, int timeoutMinutes = 300)
        {
            var result = await ProcessRunner.RunAsync(_fastbootPath, arguments, timeoutMinutes: timeoutMinutes, op: "fastboot." + FirstWord(arguments));
            return result.Output;
        }

        public async Task<string> RebootBootloaderAsync()
        {
            return await CommandAsync("reboot-bootloader");
        }

        /// <summary>First token of a command line, for op naming (`oem frp-erase` → `frp-erase`).</summary>
        private static string FirstWord(string commandLine)
        {
            IReadOnlyList<string> tokens = OperationLogger.ParseArgv(commandLine);
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
