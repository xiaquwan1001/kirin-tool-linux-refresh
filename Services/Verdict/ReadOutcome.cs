namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// What one read attempt actually produced (debt D-25: audit S23/S25/S28/S31/S33).
///
/// The five call sites named by the audit all collapsed three different facts into one
/// <c>null</c>/<c>false</c>: "the loader answered", "the loader does not have this variable"
/// and "the tool could not run / timed out". A caller cannot choose the right exit code, warning
/// or retry from a value that only says "no". These four kinds are the smallest set that can:
///
///  * <see cref="Ok"/> — the read happened and <c>Value</c> is the answer;
///  * <see cref="Unsupported"/> — the read happened and the loader/environment said "I do not have
///    this" (unknown variable, not supported, invalid command, a missing sysfs attribute). Not a
///    failure of the tool, and never evidence about the device's health;
///  * <see cref="Timeout"/> — the read did not finish inside its budget;
///  * <see cref="Failed"/> — the read ran and failed, or could not start (with <c>Reason</c>).
/// </summary>
public enum ReadOutcomeKind
{
    Ok,
    Unsupported,
    Timeout,
    Failed,
}

/// <summary>
/// A read result that keeps "unsupported" and "timeout" apart from "failed" and from a real value.
/// <see cref="Reason"/> is always filled for the three non-<see cref="ReadOutcomeKind.Ok"/> kinds so
/// a caller can print why instead of guessing (the old code had nothing to print).
/// </summary>
public readonly struct ReadOutcome<T>
{
    private ReadOutcome(ReadOutcomeKind kind, T? value, string reason)
    {
        Kind = kind;
        Value = value;
        Reason = reason;
    }

    public ReadOutcomeKind Kind { get; }

    /// <summary>The answer. Only meaningful when <see cref="Succeeded"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Why the read did not produce a value; empty for <see cref="ReadOutcomeKind.Ok"/>.</summary>
    public string Reason { get; }

    public bool Succeeded => Kind == ReadOutcomeKind.Ok;
    public bool IsUnsupported => Kind == ReadOutcomeKind.Unsupported;
    public bool IsTimeout => Kind == ReadOutcomeKind.Timeout;
    public bool IsFailed => Kind == ReadOutcomeKind.Failed;

    public static ReadOutcome<T> Ok(T value) => new(ReadOutcomeKind.Ok, value, string.Empty);
    public static ReadOutcome<T> Unsupported(string reason) => new(ReadOutcomeKind.Unsupported, default, reason);
    public static ReadOutcome<T> Timeout(string reason) => new(ReadOutcomeKind.Timeout, default, reason);
    public static ReadOutcome<T> Failed(string reason) => new(ReadOutcomeKind.Failed, default, reason);

    /// <summary>Carry this non-Ok kind+reason into another payload type (for <c>return</c> in a generic helper).</summary>
    public ReadOutcome<TResult> CastFailure<TResult>() => new(Kind, default, Reason);

    /// <summary>One line for a diagnostic log / JSON field: <c>ok</c>, <c>unsupported: ...</c>, …</summary>
    public string Describe() =>
        Succeeded ? "ok" : Kind.ToString().ToLowerInvariant() + ": " + Reason;

    public override string ToString() => Describe();
}

/// <summary>Pure classifiers shared by every read site (no process, no device, no IO).</summary>
public static class ReadOutcomes
{
    /// <summary>
    /// Replies that mean "this loader/environment does not have that" rather than "the read broke".
    /// Matched case-insensitively as substrings because loaders word these differently
    /// (<c>FAILED (remote: 'unknown variable')</c>, <c>not support</c>, <c>invalid command</c>).
    /// </summary>
    private static readonly string[] UnsupportedPhrases =
    {
        "UNKNOWN VARIABLE", "NOT SUPPORT", "UNSUPPORTED", "INVALID COMMAND", "UNKNOWN COMMAND",
        "UNRECOGNIZED", "NOT ALLOWED", "NOT IMPLEMENTED", "NO SUCH",
    };

    /// <summary>True when the reply says the request itself is not answerable here.</summary>
    public static bool LooksUnsupported(string? stream)
    {
        if (string.IsNullOrWhiteSpace(stream)) return false;
        string upper = stream!.ToUpperInvariant();
        foreach (string phrase in UnsupportedPhrases)
        {
            if (upper.Contains(phrase, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Classify one <c>getvar</c> reply. Unsupported wins over Failed: a loader rejecting a variable
    /// it does not have is not a broken tool, and the difference is what D-25 is about.
    /// </summary>
    public static ReadOutcomeKind ClassifyVariableReply(bool exitSuccess, string? stdout, string? stderr)
    {
        if (LooksUnsupported(stderr) || LooksUnsupported(stdout)) return ReadOutcomeKind.Unsupported;
        if (!exitSuccess) return ReadOutcomeKind.Failed;
        if (CommandOutput.HasFailure(stdout) || CommandOutput.HasFailure(stderr)) return ReadOutcomeKind.Failed;
        return ReadOutcomeKind.Ok;
    }

    /// <summary>
    /// Map a thrown exception to an outcome. <see cref="TimeoutException"/> keeps its own kind (the
    /// process runner raises exactly that on a budget overrun); a host cancel is a failure with a
    /// reason, not a timeout and not "no device".
    /// </summary>
    public static ReadOutcome<T> FromException<T>(Exception exception, string what)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is TimeoutException)
            return ReadOutcome<T>.Timeout(what + " timed out: " + exception.Message);
        if (exception is OperationCanceledException)
            return ReadOutcome<T>.Failed(what + " was cancelled by the host: " + exception.Message);
        return ReadOutcome<T>.Failed(what + " failed: " + exception.GetType().Name + ": " + exception.Message);
    }
}
