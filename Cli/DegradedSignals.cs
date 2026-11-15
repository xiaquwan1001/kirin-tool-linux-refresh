using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Kirin_Tool.Cli;

/// <summary>
/// R4 (audit 2026-11-15 §4): the fastboot fallback becomes a field of the result, not just a line
/// in the diagnostics log.
///
/// `FastbootClient` already knew when it fell back to a non-bundled `fastboot` (the Google
/// platform-tools binary at `/usr/bin/fastboot`), but it only wrote a warning to
/// `kirintool_log_*.log`. The CLI (and therefore MCP, which returns the CLI's JSON verbatim) now
/// says so in the machine-readable result: a top-level `degraded` array plus `fastboot.fallback`
/// (or `fastbootFallback` for commands that have no `fastboot` object).
///
/// Additive only: nothing that existed changes, nulls are still omitted, and the keys are added by
/// the single JSON writer (<see cref="CliContext.WriteJson"/>) so every command and the MCP tool
/// result carry the same signal.
/// </summary>
internal static class DegradedSignals
{
    /// <summary>The token reported in `degraded` when the bundled Huawei client was not the one used.</summary>
    public const string SystemFastbootFallback = "system-fastboot-fallback";

    /// <summary>
    /// True when `fastbootPath` is not the tool's own bundled client (the Huawei fastboot that
    /// understands `oem dump-emmc`). The system `/usr/bin/fastboot`, a PATH lookup and the
    /// `KIRIN_FASTBOOT` override all fall into this class: they are not the client this line ships.
    /// </summary>
    public static bool IsSystemFastbootFallback(string? fastbootPath)
    {
        if (string.IsNullOrWhiteSpace(fastbootPath)) return false;

        string full;
        try { full = Path.GetFullPath(fastbootPath); }
        catch (Exception) { return false; }

        string bundledDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fastboot"));
        string? directory = Path.GetDirectoryName(full);
        if (directory == null) return false;
        return !string.Equals(Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(bundledDirectory), StringComparison.Ordinal);
    }

    /// <summary>Every degradation the current process can prove, in a stable order.</summary>
    public static IReadOnlyList<string> Tokens()
    {
        string? fastbootPath = BinaryLocator.FindFastboot();
        if (IsSystemFastbootFallback(fastbootPath)) return new[] { SystemFastbootFallback };
        return Array.Empty<string>();
    }

    /// <summary>
    /// Add the fields to one JSON result object. Existing keys are never overwritten; a command
    /// that already declares a `fastboot` object gets `fallback` inside it, otherwise the flag is
    /// a top-level `fastbootFallback` (both are additions).
    /// </summary>
    public static void Apply(JsonObject result)
    {
        string? fastbootPath = BinaryLocator.FindFastboot();
        bool fallback = IsSystemFastbootFallback(fastbootPath);
        IReadOnlyList<string> tokens = fallback ? new[] { SystemFastbootFallback } : Array.Empty<string>();

        if (result["degraded"] is not JsonArray degraded)
        {
            degraded = new JsonArray();
            result["degraded"] = degraded;
        }
        foreach (string token in tokens)
        {
            bool present = false;
            foreach (JsonNode? entry in degraded)
            {
                if (string.Equals(entry?.GetValue<string>(), token, StringComparison.Ordinal)) { present = true; break; }
            }
            if (!present) degraded.Add(token);
        }

        if (!fallback) return;

        if (result["fastboot"] is JsonObject fastboot)
        {
            fastboot["fallback"] = true;
            if (fastboot["path"] == null) fastboot["path"] = fastbootPath;
            return;
        }

        result["fastbootFallback"] = true;
        if (result["fastbootPath"] == null) result["fastbootPath"] = fastbootPath;
    }
}
