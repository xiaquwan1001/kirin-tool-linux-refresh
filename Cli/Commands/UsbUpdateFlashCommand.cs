// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Services.USBUpdate;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// BB-09 / D-12: the GUI could burn a whole package over the USB-update channel
/// (`MainWindow.StartUsbUpdateFlash`) while the CLI/MCP only knew `flash-ota`, which is fastboot
/// only. This is the missing face. It is not a second implementation: it drives exactly the same
/// service method as the GUI button (`USBUpdateFlasherService.FlashPartitions`), so the unpack
/// order, the super merge, the redline decision after discovery and the three-state verdict are
/// the same code. What this command adds is the shared gate: an `ApprovalRequest` built from the
/// registry row, a `WriteTicket` that must exist before the transfer starts, the same pre-op
/// backup helper as `flash-ota` and the same `WriteReport` mapping of a `UsbUpdateVerdict`.
///
/// Wire commands, timing and retries are untouched: everything below the service boundary is the
/// code the GUI already used.
/// </summary>
internal sealed class UsbUpdateFlashCommand : GatedDeviceWriteCommand
{
    public override string Name => "usb-update-flash";
    public override string McpName => "usb_update_flash";
    public override string Summary =>
        "Flash a full package over the USB Update channel (same service method as the GUI's USB Update mode; writes only after the gate).";
    public override string Usage =>
        "usb-update-flash <file>... [--partitions a,b] [--work-dir <dir>] [--allow-redline] [--yes] [--json]\n" +
        "  packages are unpacked in the order given (base PTABLE, base UPDATE, cust PTABLE, cust UPDATE, preload PTABLE, preload UPDATE)\n" +
        "  the device must already be in USB Update mode (the command never sends a mode-entry command)\n" +
        "  without --yes the request is refused and nothing is written (exit 4); a redline/irreversible\n" +
        "  transfer also needs the confirm phrase typed on an interactive stdin";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("file", "string[]", "UPDATE.APP / PTABLE.APP paths, in unpack order", Required: true, Positional: true),
        new CliParam("files", "string", "same as the positional files, comma separated (use from MCP)"),
        new CliParam("partitions", "string", "comma separated whitelist of partition names to transfer (all entries when omitted)"),
        new CliParam("work-dir", "string", "staging directory for the unpacked images (default: a fresh private temp directory)"),
        new CliParam("allow-redline", "boolean", "permit writing oeminfo/nvme/persist/modem_secure/certification/hisee_encos/veritykey"),
        new CliParam("no-backup", "boolean", "skip the automatic pre-op backup of redline partitions + ptable"),
        new CliParam("backup-dir", "string", "directory for the pre-op backup (default: $KIRIN_BACKUP_DIR or ~/.local/share/kirin-tool/backups)"),
        new CliParam("require-backup", "boolean", "abort instead of writing when the pre-op backup could not be taken"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required for a redline transfer)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "usb-update-flash";

    /// <summary>The positional packages plus the comma separated MCP form, resolved and checked.</summary>
    private static IReadOnlyList<string> ResolveInputs(ParsedArgs args, CliContext context)
    {
        var inputs = new List<string>(args.Positionals);
        string? filesValue = args.Value("files");
        if (!string.IsNullOrWhiteSpace(filesValue)) inputs.AddRange(FlashOtaCommand.SplitList(filesValue));
        if (inputs.Count == 0)
            throw new CliUsageException("usb-update-flash requires at least one UPDATE.APP / PTABLE.APP path");

        var paths = new List<string>();
        foreach (string input in inputs)
        {
            string resolved = Path.GetFullPath(input, context.WorkingDirectory);
            if (!File.Exists(resolved)) throw new CliFileException("file not found: " + resolved);
            paths.Add(resolved);
        }
        return paths;
    }

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context)
    {
        // Contradictory backup flags are a usage error before anything else happens (S10/S12 family).
        PreOpBackupCli.ValidateFlags(args);
        IReadOnlyList<string> paths = ResolveInputs(args, context);
        return ApprovalRequests.For(CapabilityId,
            argv: new[] { "usb-update-flash" }.Concat(paths),
            targets: FlashOtaCommand.SplitList(args.Value("partitions")));
    }

    /// <summary>
    /// The one mapping from the transfer verdict to the shared write report (docs/usb-update-verdict.md):
    /// verified -&gt; applied (exit 0), partial -&gt; partial (exit 3), failed -&gt; failed (exit 3).
    /// Internal + static so a hardware-free harness can pin the mapping without a device.
    /// </summary>
    internal static WriteReport MapVerdict(UsbUpdateVerdict verdict, object details) => verdict.Outcome switch
    {
        UsbUpdateOutcome.Verified => WriteReports.Applied(details),
        UsbUpdateOutcome.Partial => WriteReports.Partial(details, ExitCodes.DeviceRejected, verdict.Describe()),
        _ => WriteReports.Failed(details, ExitCodes.DeviceRejected, "usb update flash failed: " + verdict.Describe()),
    };

    /// <summary>Every verdict field, never a bare bool: the caller must be able to read why.</summary>
    internal static object Details(IReadOnlyList<string> files, IReadOnlyList<string> partitions, UsbUpdateVerdict verdict) => new
    {
        files,
        partitions,
        verdict = verdict.Token,
        required = verdict.RequiredPartitions,
        written = verdict.Written,
        skipped = verdict.Skipped.Select(skip => skip.Partition).ToArray(),
        missing = verdict.Missing,
        handshakeEstablished = verdict.HandshakeEstablished,
        events = verdict.Events.Select(entry => entry.Line).ToArray(),
        reason = verdict.Reason,
    };

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        // Nothing below may run without the ticket the gate minted; keeping it here makes the
        // "was this path gated?" question answerable from the code, not from a habit.
        _ = admission.RequireTicket();

        IReadOnlyList<string> paths = ResolveInputs(args, context);
        List<string> selected = FlashOtaCommand.SplitList(args.Value("partitions"));

        string? workDirArg = args.Value("work-dir");
        bool privateStaging = workDirArg is not { Length: > 0 };
        // The service deletes and recreates its staging directory; a fixed world-writable path
        // could be pre-created by another local user (audit §4.1), so the default is 0700 + random.
        string dloadDirectory = privateStaging
            ? PrivateWorkDirectory.Create("kirin-tool-dload")
            : Path.GetFullPath(workDirArg!, context.WorkingDirectory);
        Directory.CreateDirectory(dloadDirectory);

        try
        {
            // Pre-op backup: the same helper flash-ota uses. The USB-update channel does not need
            // fastboot, so with no fastboot session the backup is simply not attempted - and
            // --require-backup turns "not taken" into a refusal (BackupGate), never a silent pass.
            PreOpBackupResult backup = PreOpBackupResult.Skipped;
            FastbootSession? session = await FastbootSession.TryOpenAsync().ConfigureAwait(false);
            if (session is { Connected: true })
            {
                backup = await PreOpBackupCli.RunForWriteAsync(args, context, "usb-update-flash", selected, session, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                context.Diag("pre-op backup NOT taken: no fastboot session (a device in USB Update mode does not answer fastboot); "
                    + "restore the identity partitions from a previous backup if this transfers the redline segment");
            }
            int backupRefusal = PreOpBackupCli.RefusalExit(args, backup, context, Name);
            if (backupRefusal != ExitCodes.Ok) return backupRefusal;

            // No interactive retry channel in the CLI: a prompt nobody can answer would hang
            // automation, so the service gets a refusal and reports it as a degradation.
            var service = new USBUpdateFlasherService(
                message => context.Diag("usb-update: " + message),
                dloadDirectory,
                (title, message) =>
                {
                    context.Diag("retry required: " + title + ": " + message);
                    return Task.FromResult(false);
                });

            var filesData = paths
                .Select(path => ((string?)path, Path.GetFileName(path), new List<string>(selected)))
                .ToList();

            bool allowRedline = args.HasFlag("allow-redline");
            UsbUpdateVerdict verdict;
            try
            {
                // Same call the GUI makes (MainWindow.StartUsbUpdateFlash).
                verdict = await service.FlashPartitions(filesData, cancellationToken, allowRedline).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = new { files = paths, partitions = selected, verdict = "failed", error = ex.Message };
                return WriteResult(context, Name, CapabilityId,
                    WriteReports.Failed(failure, ExitCodes.DeviceRejected, "usb update flash failed: " + ex.Message),
                    "USB update flash FAILED: " + ex.Message);
            }

            WriteReport report = MapVerdict(verdict, Details(paths, selected, verdict));
            var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["verdict"] = verdict.Token,
                ["written"] = verdict.Written,
                ["handshakeEstablished"] = verdict.HandshakeEstablished,
            };
            return WriteResult(context, Name, CapabilityId, report,
                "USB update flash " + verdict.Token.ToUpperInvariant() + " - " + verdict.Describe(),
                evidence);
        }
        finally
        {
            // Per-run scratch only: a user supplied --work-dir is left alone on purpose.
            if (privateStaging)
            {
                try { Directory.Delete(dloadDirectory, true); } catch (Exception) { }
            }
        }
    }
}
