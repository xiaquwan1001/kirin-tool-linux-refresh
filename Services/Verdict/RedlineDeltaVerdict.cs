// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// D-23(c) / audit S34: the three answers a redline re-confirmation can have. The GUI used to
/// collapse them into a <c>bool</c>, so "the user refused" and "the dialog was never answered
/// within ten minutes" were the same <c>false</c> — a timeout cancelled the write silently and
/// looked exactly like a deliberate decision.
/// </summary>
public enum RedlineDeltaOutcome
{
    /// <summary>The user accepted the newly discovered redline partitions.</summary>
    Approved,

    /// <summary>The user refused them.</summary>
    Declined,

    /// <summary>Nobody answered within the bounded wait; the operation is cancelled by timeout.</summary>
    TimedOut,
}

/// <summary>
/// The mapping between "how did the bounded confirmation end" and "what do we tell the user and
/// the log". Pure: the GUI only calls <see cref="FromAnswer"/> with the result of its
/// <c>Task.WhenAny(..., Task.Delay(timeout))</c> race and prints <see cref="Describe"/>.
/// </summary>
public static class RedlineDeltaVerdict
{
    /// <summary>Stable token: the re-confirmation was answered with "yes".</summary>
    public const string ApprovedToken = "redline-delta:approved";

    /// <summary>Stable token: the user said no.</summary>
    public const string DeclinedToken = "redline-delta:declined";

    /// <summary>Stable token: the bounded wait expired with no answer.</summary>
    public const string TimedOutToken = "redline-delta:timed-out";

    /// <summary>How long the background worker waits for the modal answer before giving up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <paramref name="answered"/> = the answer task won the race (the dialog produced a result);
    /// <paramref name="approved"/> is only meaningful in that case. A race that ended on the
    /// timeout arm is <see cref="RedlineDeltaOutcome.TimedOut"/>, never "declined".
    /// </summary>
    public static RedlineDeltaOutcome FromAnswer(bool answered, bool approved) =>
        !answered ? RedlineDeltaOutcome.TimedOut
                  : approved ? RedlineDeltaOutcome.Approved
                             : RedlineDeltaOutcome.Declined;

    /// <summary>Only an explicit "yes" lets the write continue.</summary>
    public static bool AllowsWrite(RedlineDeltaOutcome outcome) => outcome == RedlineDeltaOutcome.Approved;

    /// <summary>Stable token for the log/verdict source.</summary>
    public static string Token(RedlineDeltaOutcome outcome) => outcome switch
    {
        RedlineDeltaOutcome.Approved => ApprovedToken,
        RedlineDeltaOutcome.Declined => DeclinedToken,
        _ => TimedOutToken,
    };

    /// <summary>
    /// The one place the user-visible wording lives. Timeout and refusal must not read the same:
    /// a timeout says the question was never answered and that nothing was written.
    /// </summary>
    public static string Describe(RedlineDeltaOutcome outcome, IEnumerable<string> partitions)
    {
        string names = string.Join(", ", (partitions ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (names.Length == 0) names = "(none listed)";

        return outcome switch
        {
            RedlineDeltaOutcome.Approved =>
                "Redline re-confirmation accepted (" + ApprovedToken + ") for the newly discovered partition(s): " + names,
            RedlineDeltaOutcome.Declined =>
                "USB Update flash aborted: the newly discovered redline partition(s) were DECLINED by the user ("
                + DeclinedToken + "); nothing was written: " + names,
            _ =>
                "USB Update flash CANCELLED BY TIMEOUT: the confirmation for the newly discovered redline partition(s) "
                + "was never answered within " + (int)Timeout.TotalMinutes + " minutes (" + TimedOutToken
                + "); nothing was written, and this is NOT a user refusal: " + names,
        };
    }
}
