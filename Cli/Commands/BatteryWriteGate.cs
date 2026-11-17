// Licensed under the repository's Business Source License 1.1. See LICENSE.
using System.Threading.Tasks;
using Kirin_Tool.Services.Verdict;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// The CLI/MCP half of the shared pre-write battery floor (D-16 / audit S14). Every write command
/// that writes flash calls <see cref="CheckAsync"/> after its approval gate and before the first
/// frame/partition write; a refusal prints the structured verdict the debt asks for
/// (<c>gate: "battery"</c> + <c>level: N</c>) and returns a non-zero exit code.
///
/// The floor is measured with the same <see cref="BatteryGate"/> object the GUI uses, so there is
/// one definition of "too low" in the tree. An unreadable level proceeds
/// (<c>gate: "battery-unknown"</c>, informational only) — see BatteryGate for why.
/// </summary>
internal static class BatteryWriteGate
{
    /// <summary>
    /// Ask the battery floor through <paramref name="session"/>. Returns null when the write may
    /// proceed, otherwise the exit code the command must return (the verdict is already printed).
    /// A null/disconnected session is not this gate's business: the command's own device check
    /// reports that, and the gate would otherwise mask "no device" behind "battery unknown".
    /// </summary>
    public static async Task<int?> CheckAsync(CliContext context, string command, FastbootSession? session)
    {
        if (session is not { Connected: true }) return null;

        BatteryGateVerdict verdict = await BatteryGate
            .CheckAsync(variable => DeviceVars.TryReadAsync(session.Client, variable))
            .ConfigureAwait(false);

        if (!verdict.Refuse)
        {
            // Never silent: "the floor was not applied" is a fact the operator needs on stderr.
            context.Diag("battery: " + verdict.Reason);
            return null;
        }

        if (context.Json)
        {
            context.WriteJson(new
            {
                command,
                dryRun = true,
                applied = false,
                gate = verdict.Gate,
                level = verdict.Level,
                reason = verdict.Reason,
                exitCode = verdict.ExitCode,
            });
        }
        else
        {
            context.Output.WriteLine($"gate: {verdict.Gate} (level {verdict.Level}%, exit {verdict.ExitCode})");
            context.Output.WriteLine(verdict.Reason);
        }

        context.Diag($"battery gate refused: {verdict.Reason}");
        return verdict.ExitCode;
    }
}
