using System.Reflection;
using System.Text;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Verdict;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Cli;

internal static class ToolInfo
{
    public const string Name = "KirinTool.Cli";

    public static string Version =>
        typeof(FastbootClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FastbootClient).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string Framework => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
}

internal sealed class FastbootSession
{
    private FastbootSession(string executablePath, FastbootClient client, ReadOutcome<bool> connection)
    {
        ExecutablePath = executablePath;
        Client = client;
        Connection = connection;
    }

    public string ExecutablePath { get; }
    public FastbootClient Client { get; }

    /// <summary>
    /// D-25 (audit S25): the probe verdict, not just a bool. <c>Ok(true)</c> = a device is in fastboot
    /// mode, <c>Ok(false)</c> = the probe ran and found none, anything else = the probe itself could
    /// not answer (unsupported / timeout / failed) — which is an environment finding, not "no device".
    /// </summary>
    public ReadOutcome<bool> Connection { get; }

    public bool Connected => Connection.Succeeded && Connection.Value;

    /// <summary>Null means no fastboot binary was found at all.</summary>
    public static async Task<FastbootSession?> TryOpenAsync()
    {
        string? path = BinaryLocator.FindFastboot();
        if (path == null) return null;

        var client = new FastbootClient(path);
        // No local catch any more: ProbeConnectionAsync already turns every exception into an
        // outcome, so "the probe threw" can no longer be flattened into Connected=false here.
        ReadOutcome<bool> connection = await client.ProbeConnectionAsync();
        return new FastbootSession(path, client, connection);
    }
}

internal static class DeviceVars
{
    /// <summary>
    /// D-25 (audit S28): the read-only <c>getvar</c> probe, keeping "the variable is unsupported",
    /// "the probe timed out" and "the probe failed" apart. Never throws. Callers that only need a
    /// value use <see cref="TryReadAsync"/> (which logs the reason instead of dropping it).
    /// </summary>
    public static async Task<ReadOutcome<string>> TryReadOutcomeAsync(FastbootClient client, string variable)
    {
        ProcessResult result;
        try
        {
            result = await client.QueryVariableAsync(variable);
        }
        catch (Exception ex)
        {
            return ReadOutcomes.FromException<string>(ex, "getvar " + variable);
        }

        // D-10 already established the rule this leans on: success must come from stdout, while a
        // failure token anywhere (and an "unknown variable"/"not support" wording in particular) wins.
        string stdout = string.IsNullOrEmpty(result.StandardOutput) ? result.Output ?? string.Empty : result.StandardOutput;
        switch (ReadOutcomes.ClassifyVariableReply(result.IsSuccess, stdout, result.ErrorOutput))
        {
            case ReadOutcomeKind.Unsupported:
                return ReadOutcome<string>.Unsupported(
                    "getvar " + variable + " is not answerable on this loader (" + FirstLine(result.ErrorOutput, stdout) + ")");
            case ReadOutcomeKind.Failed:
                return ReadOutcome<string>.Failed(
                    "getvar " + variable + " failed (exit " + result.ExitCode + ": " + FirstLine(result.ErrorOutput, stdout) + ")");
        }

        string? value;
        try
        {
            value = FastbootReadResponses.Variable(result, variable);
        }
        catch (InvalidOperationException)
        {
            // Some bootloaders print the value without the "name:" prefix.
            value = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0 &&
                    !l.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase) &&
                    !l.StartsWith("OKAY", StringComparison.OrdinalIgnoreCase) &&
                    !l.StartsWith("INFO", StringComparison.OrdinalIgnoreCase) &&
                    !l.StartsWith("Finished", StringComparison.OrdinalIgnoreCase));
        }

        return string.IsNullOrWhiteSpace(value)
            ? ReadOutcome<string>.Failed("getvar " + variable + ": no value in the reply (" + FirstLine(stdout, result.ErrorOutput) + ")")
            : ReadOutcome<string>.Ok(value!.Trim());
    }

    /// <summary>
    /// Value-or-null wrapper kept for the call sites that only need the answer (backup gates, battery
    /// gate). The verdict is not dropped: a non-Ok read is written to the diagnostic log with its
    /// kind and reason, so "nothing came back" is no longer the only trace.
    /// </summary>
    public static async Task<string?> TryReadAsync(FastbootClient client, string variable)
    {
        ReadOutcome<string> outcome = await TryReadOutcomeAsync(client, variable);
        if (outcome.Succeeded) return outcome.Value;
        ProcessRunner.WriteDiagnostic("DeviceVars", outcome.Describe());
        return null;
    }

    private static string FirstLine(params string?[] streams)
    {
        foreach (string? stream in streams)
        {
            if (string.IsNullOrWhiteSpace(stream)) continue;
            string? line = stream!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(text => text.Trim())
                .FirstOrDefault(text => text.Length > 0);
            if (line != null) return line;
        }

        return "no output";
    }

    public static async Task<bool> PsidReadableAsync(FastbootClient client)
    {
        try
        {
            await client.ReadPsidAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<int?> PartitionCountAsync(FastbootClient client)
    {
        try
        {
            ProcessResult result = await client.QueryVariableAsync("ptable");
            return FastbootReadResponses.Partitions(result).Count;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal static class DeviceOutcome
{
    private static readonly string[] RejectionMarkers =
    {
        "FAILED", "INVALID COMMAND", "NOT ALLOWED", "DENIED", "UNLOCK", "LOCKED", "UNKNOWN COMMAND", "NOT SUPPORTED",
    };

    public static bool LooksRejected(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        string upper = output.ToUpperInvariant();
        return RejectionMarkers.Any(marker => upper.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>3 = the device itself refused the command, 5 = local/file failure.</summary>
    public static int FailureExit(string? output) =>
        LooksRejected(output) ? ExitCodes.DeviceRejected : ExitCodes.FileOrVerify;
}

internal static class ToolProbe
{
    public static async Task<string?> VersionAsync(string executablePath)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(executablePath, new[] { "--version" }, timeoutSeconds: 5);
            string text = (result.Output ?? string.Empty).Trim();
            if (text.Length == 0) return result.IsSuccess ? "unknown" : null;
            return text.Split(new[] { '\r', '\n' })[0].Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<string>> FastbootDevicesAsync(string executablePath)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(executablePath, new[] { "devices" }, timeoutSeconds: 10);
            return result.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static async Task<IReadOnlyList<string>> AdbDevicesAsync(string executablePath)
    {
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(executablePath, new[] { "devices", "-l" }, timeoutSeconds: 10);
            return result.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd())
                .Where(line => line.Length > 0 &&
                    !line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>Bundled VCOM loader inventory (read-only inspection of loaders/).</summary>
internal static class BundledLoaders
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "loaders");

    public static IReadOnlyList<(string Chip, int Files)> Inventory()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<(string, int)>();
        try
        {
            return System.IO.Directory.GetDirectories(Directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (Path.GetFileName(path), System.IO.Directory.GetFiles(path, "*.ktl").Length))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<(string, int)>();
        }
    }

    public static IReadOnlyList<(string Stage, string File, int Address, bool PwnFlag, bool Exists)> Plan(string chip, bool useFastFlashLoader = false, string? loaderRoot = null)
    {
        if (!FirmwareUnlocker.CpuAddresses.TryGetValue(chip, out var stages)) return Array.Empty<(string, string, int, bool, bool)>();

        string chipDirectory = Path.Combine(loaderRoot ?? Directory, chip);
        return stages.Select(stage =>
        {
            string file = useFastFlashLoader && stage.Name == "FASTBOOT"
                ? "fastbootf.ktl"
                : stage.Name.ToLowerInvariant() + ".ktl";
            bool exists = File.Exists(Path.Combine(chipDirectory, file));
            return (stage.Name, file, stage.Address, stage.PwnFlag, exists);
        }).ToList();
    }

    public static IReadOnlyList<string> SupportedChips() =>
        FirmwareUnlocker.CpuAddresses.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}

internal static class TextFormat
{
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:F1} {units[unit]}");
    }

    public static string Table(IReadOnlyList<string[]> rows, params string[] headers)
    {
        var all = new List<string[]> { headers };
        all.AddRange(rows);
        int[] widths = new int[headers.Length];
        foreach (string[] row in all)
        {
            for (int i = 0; i < widths.Length && i < row.Length; i++)
                widths[i] = Math.Max(widths[i], (row[i] ?? string.Empty).Length);
        }

        var builder = new StringBuilder();
        foreach (string[] row in all)
        {
            for (int i = 0; i < widths.Length; i++)
            {
                string cell = i < row.Length ? row[i] ?? string.Empty : string.Empty;
                builder.Append(cell.PadRight(widths[i]));
                if (i < widths.Length - 1) builder.Append("  ");
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }
}
