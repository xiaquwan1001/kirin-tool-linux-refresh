// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using Kirin_Tool.Services.Diagnostics;

namespace Kirin_Tool.Services.Verdict;

/// <summary>The five terminal states a GUI device operation can end in.</summary>
public enum OperationOutcome
{
    /// <summary>Every selected partition verified.</summary>
    Verified,

    /// <summary>Something was written, but not everything that was planned.</summary>
    Partial,

    /// <summary>Nothing usable was written / the operation reported an error.</summary>
    Failed,

    /// <summary>The user cancelled it.</summary>
    Cancelled,

    /// <summary>A bounded wait expired, so the operation cancelled itself.</summary>
    TimedOut,

    /// <summary>A step the user deliberately turned off; nothing was attempted (never a failure verdict).</summary>
    Skipped,
}

/// <summary>
/// D-23(b) / audit BB-25 + BB-29: the GUI conclusion used to live only in one line of a modal
/// progress window, which disappears the moment the user closes it - after that there was no
/// record of what the flash actually concluded.
///
/// The record is not a new store: it is written into the operation session the CLI, the approval
/// audit and the VCOM transcript already share (KIRIN_LOG_DIR/sessions/&lt;stamp&gt;-&lt;pid&gt;-&lt;op&gt;/session.jsonl
/// under the default ~/.local/state/kirin-tool root), so `kirin-tool-cli logs list|show` reviews
/// GUI operations exactly like CLI ones.
///
/// Nothing here may break a flash: every failure path returns <c>null</c> and the caller carries on.
/// </summary>
public static class OperationConclusionRecord
{
    /// <summary>Phase of the recorded line; the session reader already understands result.</summary>
    public const string Phase = "result";

    /// <summary>Step name, so a GUI conclusion is greppable without matching free text.</summary>
    public const string Step = "gui.conclusion";

    /// <summary>Tool name recorded for GUI-originated sessions.</summary>
    public const string Tool = "gui";

    /// <summary>Verdict of a successful operation.</summary>
    public const string VerdictVerified = "verified";

    /// <summary>Verdict of a partially successful operation.</summary>
    public const string VerdictPartial = "partial";

    /// <summary>Verdict of a failed operation.</summary>
    public const string VerdictFailed = "failed";

    /// <summary>Verdict of a user-cancelled operation.</summary>
    public const string VerdictCancelled = "cancelled";

    /// <summary>Verdict of an operation that cancelled itself on a timeout.</summary>
    public const string VerdictTimedOut = "timed-out";

    /// <summary>Verdict of a step the user turned off (no attempt, no failure claim).</summary>
    public const string VerdictSkipped = "skipped";

    /// <summary>True only for <see cref="OperationOutcome.Verified"/>; never for partial.</summary>
    public static bool IsSuccess(OperationOutcome outcome) => outcome == OperationOutcome.Verified;

    /// <summary>The stable verdict word written into the log (and shown in the CLI).</summary>
    public static string VerdictOf(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Verified => VerdictVerified,
        OperationOutcome.Partial => VerdictPartial,
        OperationOutcome.Cancelled => VerdictCancelled,
        OperationOutcome.TimedOut => VerdictTimedOut,
        OperationOutcome.Skipped => VerdictSkipped,
        _ => VerdictFailed,
    };

    /// <summary>
    /// Maps a legacy boolean conclusion (the pre-D-23 shape of several GUI paths) onto the
    /// richer enum: true is verified, false is failed. A caller that knows more (partial,
    /// cancelled, timed out) passes the enum directly.
    /// </summary>
    public static OperationOutcome FromSuccess(bool success) =>
        success ? OperationOutcome.Verified : OperationOutcome.Failed;

    /// <summary>The verdictSource written into the session line.</summary>
    public static string VerdictSource(OperationOutcome outcome) => "gui:" + VerdictOf(outcome);

    /// <summary>
    /// Writes the conclusion into the current operation session and returns its directory
    /// (null when logging is disabled or the write failed). <paramref name="operation"/> is the
    /// GUI operation id (gui-full-ota, gui-usb-update, gui-xml, gui-rescue, gui-oeminfo...).
    /// </summary>
    public static string? Record(string operation, OperationOutcome outcome, string message)
    {
        try
        {
            string op = string.IsNullOrWhiteSpace(operation) ? "gui" : operation!;
            string note = message ?? string.Empty;

            OperationSession session = OperationLogger.BeginSession(op, Tool, null, "gui conclusion");
            session.Event(
                Phase,
                step: Step,
                note: note,
                fields: LogFields.Of(
                    ("operation", op),
                    ("outcome", VerdictOf(outcome)),
                    ("message", note)),
                ok: IsSuccess(outcome),
                verdictSource: VerdictSource(outcome));

            return session.IsEnabled ? session.Directory : null;
        }
        catch (Exception)
        {
            // A broken log directory must never turn a finished write into a crash.
            return null;
        }
    }
}
