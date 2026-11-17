// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// D-23(a) / audit BB-14: the GUI's write-before backup used to be an <b>implicit</b> step — it
/// ran on every write path and the user could neither see it beforehand nor turn it off. The
/// option is now explicit (a visible checkbox backed by
/// <c>UserSettings.PreOpBackup</c>) and its default is unchanged: <b>on</b>. Turning it off is
/// allowed but never silent: the decision produces a notice, a diagnostic line and a session
/// record, because "no backup copy of the redline partitions exists" is exactly the kind of
/// thing a later reviewer has to be able to reconstruct.
///
/// Pure by construction (no UI, no session): the GUI passes the setting in and prints/serialises
/// the returned strings, so the rule can be asserted without a window.
/// </summary>
public static class PreOpBackupChoice
{
    /// <summary>The safe default. Unchanged from the pre-D-23 behaviour (backup always ran).</summary>
    public const bool DefaultEnabled = true;

    /// <summary>Stable token for the "user turned the backup off" decision.</summary>
    public const string DisabledToken = "pre-op-backup:disabled-by-user";

    /// <summary>Checkbox label, shown next to the Full OTA start button.</summary>
    public const string Label = "Create a pre-op backup of the target partitions before writing (recommended)";

    /// <summary>
    /// The one decision rule: the backup runs exactly when the option is enabled.
    /// Written as a function (not an `if` at each of the four write paths) so every path answers
    /// the same way and a test can pin it.
    /// </summary>
    public static bool ShouldRun(bool enabled) => enabled;

    /// <summary>
    /// What the user sees in the progress window when the backup is off. Deliberately explicit
    /// about the consequence: the redline partitions of this write have no dump to restore from.
    /// </summary>
    public static string DisabledNotice(string trigger) =>
        "pre-op backup SKIPPED (disabled in settings, " + DisabledToken + "): no dump of the target "
        + "partitions is created before this " + trigger + " write — a failed redline partition cannot "
        + "be restored from a backup. Re-enable it in the Full OTA panel if you are unsure.";

    /// <summary>One-line record text for the session log (no UI dependency).</summary>
    public static string DecisionRecord(string trigger, bool enabled) =>
        enabled
            ? "pre-op backup: enabled for " + trigger
            : "pre-op backup: " + DisabledToken + " for " + trigger;
}
