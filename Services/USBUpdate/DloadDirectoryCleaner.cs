// Licensed under the repository's Business Source License 1.1. See LICENSE.
using System;
using Kirin_Tool.Services.Diagnostics;

namespace Kirin_Tool.Services.USBUpdate;

/// <summary>
/// The one way the USB-update workflow removes its scratch directory (D-14 / audit BB-13+BB-16).
/// The old code called <c>OpLog.CleanDirectory</c> (which recursively deletes) directly from three
/// call sites; this wrapper asks <see cref="DloadSentinel"/> first and, when the sentinel refuses,
/// deletes nothing and records the refusal in the op log instead of continuing silently.
/// </summary>
public static class DloadDirectoryCleaner
{
    /// <summary>
    /// Delete <paramref name="directory"/> only when <see cref="DloadSentinel"/> recognises it as the
    /// workflow's own scratch directory. Returns the verdict so the caller can log/report it; a
    /// refusal is not an exception (the caller continues, exactly like a failed delete did before).
    /// </summary>
    public static DloadCleanupVerdict Clean(string directory, Action<string>? log = null, string? opName = null)
    {
        DloadCleanupVerdict verdict = DloadSentinel.EvaluateDirectory(directory);
        if (verdict.MayDelete)
        {
            OpLog.CleanDirectory(directory, opName);
            log?.Invoke("dload directory removed: " + verdict.Reason);
            return verdict;
        }

        if (verdict.DirectoryExists)
        {
            // Evidence that the delete was refused on purpose, with what was actually found.
            using var op = OpLog.Begin(opName ?? OpNames.UsbUpdateCleanup, new[] { directory },
                "dload directory cleanup refused by the sentinel check");
            op.Step("sentinel refused: " + verdict.Reason);
            op.Result(false, null, null, null, verdict.Reason, "refused:sentinel",
                "the directory was left untouched");
        }

        log?.Invoke("refusing to delete the dload directory: " + verdict.Reason);
        return verdict;
    }
}
