// Licensed under the repository's Business Source License 1.1. See LICENSE.
using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// The verdict of the pre-write battery floor (D-16 / audit S14). <see cref="Refuse"/> is the
/// only thing a write path may branch on; <see cref="Level"/> is the number the device reported
/// (null when the loader cannot answer) and <see cref="Gate"/> is the machine-readable token a
/// CLI/MCP client sees (<c>battery</c> when refusing, <c>battery-unknown</c> when the level could
/// not be read). A readable level at or above the floor produces <c>battery</c> with
/// <see cref="Refuse"/> false.
/// </summary>
public sealed record BatteryGateVerdict(bool Refuse, int ExitCode, int? Level, string Gate, string Reason);

/// <summary>
/// The shared pre-write battery floor: GUI, CLI and MCP all ask this one object before the first
/// frame/partition write. Before D-16 the GUI only showed a non-modal banner and every write path
/// ignored the level entirely, so a 3%-battery device was flashed with no refusal anywhere.
///
/// Deliberately NOT a device driver: <see cref="ParsePercent"/> and <see cref="Evaluate"/> are
/// pure functions so the floor can be tested from stub getvar text, and the only device access is
/// the query delegate a caller injects. An unreadable level never refuses a write (temporary
/// loaders answer these getvars with "invalid command"; refusing there would make the tool
/// unusable on exactly the devices it exists for) — it is reported as
/// <see cref="UnknownGateName"/> so a client can tell "no floor applied" from "floor passed".
/// </summary>
public static class BatteryGate
{
    /// <summary>Below this percentage a write is refused (the documented safety floor).</summary>
    public const int MinimumPercent = 50;

    /// <summary>Mirrors Cli.ExitCodes.Gated: the gate refused and nothing was written.</summary>
    public const int RefusalExitCode = 4;

    /// <summary>Machine-readable gate token when a measured level refused the write.</summary>
    public const string GateName = "battery";

    /// <summary>Machine-readable gate token when the loader cannot report a level.</summary>
    public const string UnknownGateName = "battery-unknown";

    /// <summary>The getvars tried in order; the first plausible percentage wins.</summary>
    public static readonly IReadOnlyList<string> PercentVariables = new[] { "battery-soc", "battery-capacity", "battery-voltage" };

    /// <summary>A voltage reading is not a percentage; it is skipped even if it parses.</summary>
    public const string VoltageVariable = "battery-voltage";

    /// <summary>
    /// The floor itself. Pure: no device, no I/O, no clock.
    /// </summary>
    public static BatteryGateVerdict Evaluate(int? percent)
    {
        if (percent is int level)
        {
            if (level < MinimumPercent)
                return new BatteryGateVerdict(true, RefusalExitCode, level, GateName,
                    $"battery is at {level}%, below the {MinimumPercent}% write floor: a mid-write power loss can leave the device unbootable. Charge the device and retry.");
            return new BatteryGateVerdict(false, 0, level, GateName,
                $"battery is at {level}%, at or above the {MinimumPercent}% write floor");
        }

        return new BatteryGateVerdict(false, 0, null, UnknownGateName,
            "the loader does not report a battery level, so the write floor cannot be applied");
    }

    /// <summary>
    /// Parses one <c>fastboot getvar</c> answer into a percentage. Only a plausible 0-100 integer
    /// counts; a rejected/unsupported variable, a voltage reading or anything unparseable yields
    /// null. Pure: <paramref name="output"/> is whatever the caller captured.
    /// </summary>
    public static int? ParsePercent(string variable, string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        if (output.Contains("FAILED", StringComparison.OrdinalIgnoreCase)) return null;
        if (output.Contains("invalid command", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(variable, VoltageVariable, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("(bootloader)", StringComparison.Ordinal))
                trimmed = trimmed[12..].TrimStart();
            int colon = trimmed.IndexOf(':');
            if (colon >= 0) trimmed = trimmed[(colon + 1)..].Trim();
            if (int.TryParse(trimmed.Split(' ', '%')[0], out int percent) && percent is >= 0 and <= 100)
                return percent;
        }
        return null;
    }

    /// <summary>
    /// Ask the injected query delegate for each candidate getvar and return the first percentage.
    /// Nothing here touches a device directly: the CLI passes a fastboot query, the GUI passes the
    /// same client call through <see cref="CheckAsync"/>.
    /// </summary>
    public static async Task<int?> ReadPercentAsync(Func<string, Task<string?>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (string variable in PercentVariables)
        {
            string? output = await query(variable).ConfigureAwait(false);
            int? percent = ParsePercent(variable, output);
            if (percent is not null) return percent;
        }
        return null;
    }

    /// <summary>Read the level through <paramref name="query"/> and run the floor.</summary>
    public static async Task<BatteryGateVerdict> CheckAsync(Func<string, Task<string?>> query)
    {
        int? percent = await ReadPercentAsync(query).ConfigureAwait(false);
        return Evaluate(percent);
    }
}
