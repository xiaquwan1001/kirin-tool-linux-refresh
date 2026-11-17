using Kirin_Tool.Models;

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// 「有设备可写吗」这一问的判据（debt D-25: audit S25）。
///
/// <c>FastbootClient.IsDeviceConnected</c> used to answer with a <c>bool</c> that was <c>false</c>
/// for three different facts: no device, the binary exited non-zero, and the probe threw. Everything
/// downstream printed "no device in fastboot mode" and exited 2 — a broken fastboot, a timeout or a
/// refused mount therefore looked like "the phone is not plugged in".
///
/// This classifier keeps them apart and returns the status token the CLI prints:
/// <list type="bullet">
/// <item><c>ok</c> — <c>fastboot devices</c> ran and the answer is the bool (a device, or none);</item>
/// <item><c>probe-unsupported</c> — the client does not implement <c>devices</c>;</item>
/// <item><c>probe-timeout</c> — the probe did not finish;</item>
/// <item><c>probe-failed</c> — the probe ran and failed / could not start (the reason is carried).</item>
/// </list>
/// Pure: no process is started here, so a test can assert every branch with a synthetic
/// <see cref="ProcessResult"/> or exception.
/// </summary>
public static class ConnectionProbe
{
    public const string StatusOk = "ok";
    public const string StatusNotConnected = "not-connected";
    public const string StatusUnsupported = "probe-unsupported";
    public const string StatusTimeout = "probe-timeout";
    public const string StatusFailed = "probe-failed";

    /// <summary>
    /// Classify one <c>fastboot devices</c> attempt. <paramref name="error"/> is the exception the
    /// probe caught (null when the process ran). <paramref name="pinnedSerial"/> keeps the K7/B-class
    /// rule: when a target SN is configured only that device counts.
    /// </summary>
    public static ReadOutcome<bool> Classify(ProcessResult? result, Exception? error, string? pinnedSerial)
    {
        if (error != null) return ReadOutcomes.FromException<bool>(error, "fastboot devices");
        if (result == null) return ReadOutcome<bool>.Failed("fastboot devices produced no result");

        string stdout = string.IsNullOrEmpty(result.StandardOutput) ? result.Output ?? string.Empty : result.StandardOutput;
        string summary = Summary(stdout, result.ErrorOutput);
        if (ReadOutcomes.LooksUnsupported(result.ErrorOutput) || ReadOutcomes.LooksUnsupported(stdout))
            return ReadOutcome<bool>.Unsupported("fastboot devices is not supported here (" + summary + ")");
        if (!result.IsSuccess)
            return ReadOutcome<bool>.Failed("fastboot devices exited " + result.ExitCode + " (" + summary + ")");

        return ReadOutcome<bool>.Ok(ParseDevices(stdout, pinnedSerial));
    }

    /// <summary>
    /// True when the <c>fastboot devices</c> table lists a device in fastboot mode (optionally only
    /// <paramref name="pinnedSerial"/>). Pure, so the parsing itself is assertable.
    /// </summary>
    public static bool ParseDevices(string? stream, string? pinnedSerial)
    {
        if (string.IsNullOrWhiteSpace(stream)) return false;
        return stream!
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Any(columns => columns.Length >= 2 &&
                string.Equals(columns[1], "fastboot", StringComparison.OrdinalIgnoreCase) &&
                (pinnedSerial == null || string.Equals(columns[0], pinnedSerial, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The status token <c>info</c>/<c>doctor</c> print for a non-Ok probe kind.</summary>
    public static string StatusToken(ReadOutcomeKind kind) => kind switch
    {
        ReadOutcomeKind.Ok => StatusOk,
        ReadOutcomeKind.Unsupported => StatusUnsupported,
        ReadOutcomeKind.Timeout => StatusTimeout,
        _ => StatusFailed,
    };

    private static string Summary(string? stdout, string? stderr)
    {
        foreach (string? stream in new[] { stderr, stdout })
        {
            if (string.IsNullOrWhiteSpace(stream)) continue;
            string? line = stream!
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(text => text.Trim())
                .FirstOrDefault(text => text.Length > 0);
            if (line != null) return line;
        }

        return "no output";
    }
}
