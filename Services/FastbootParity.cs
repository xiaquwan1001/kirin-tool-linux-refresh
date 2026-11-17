// Licensed to the repository's Business Source License 1.1. See LICENSE.
using System;
using System.IO;

namespace Kirin_Tool.Services;

/// <summary>
/// D-07 / audit P11+P12+P13+H32: which <c>fastboot</c> is this really?
///
/// Two different programs answer to the name `fastboot`:
/// * the client this tool ships (AOSP android-tools + the vendor `huawei_vendor` patch, see
///   `fastboot-modern/build.sh`), which speaks the Huawei temporary loader's `getvar`/`oem`
///   commands, and
/// * the distro/Google `platform-tools` client installed at `/usr/bin/fastboot`, whose
///   `getvar partition-size` and `oem` handling fails against those loaders
///   (`packaging/build-deb.sh` documents exactly this).
///
/// Falling back from the first to the second used to be completely silent: the only symptom was
/// "every read fails", which reads like a cable/connection problem. This classifier turns the
/// binary's provenance into a token a human, the CLI JSON and `doctor` can all agree on.
///
/// The judgement is deliberately conservative: only the tool's own bundled directory *proves*
/// Huawei lineage. A recognizable platform-tools location is attributed to Google. Everything
/// else (a `KIRIN_FASTBOOT` override, an unusual install prefix) is `unknown`: not proven
/// Huawei, therefore not "parity ok" — but also not accused of being platform-tools.
/// </summary>
public static class FastbootParity
{
    /// <summary>The bundled Huawei-capable client (same directory as the shipped binaries).</summary>
    public const string Huawei = "huawei";

    /// <summary>A platform-tools (AOSP/Google) client: no Huawei vendor patch.</summary>
    public const string Google = "google";

    /// <summary>Provenance could not be established from the path or the version line.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Classify one resolved `fastboot` path. `versionLine` is the first line of
    /// `fastboot --version` when the caller already has it (it can add the platform-tools marker
    /// that some vendor builds print); it is optional.
    /// </summary>
    public static string Classify(string? executablePath, string? versionLine = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return Unknown;

        string full;
        try
        {
            full = Path.GetFullPath(executablePath!.Trim());
        }
        catch (Exception)
        {
            return Unknown;
        }

        if (IsBundledDirectory(Path.GetDirectoryName(full))) return Huawei;
        if (IsPlatformToolsDirectory(Path.GetDirectoryName(full))) return Google;
        if (LooksLikePlatformToolsVersion(versionLine)) return Google;
        return Unknown;
    }

    /// <summary>True when the path is the bundled client, i.e. the lineage the tool can vouch for.</summary>
    public static bool IsHuawei(string? executablePath, string? versionLine = null) =>
        string.Equals(Classify(executablePath, versionLine), Huawei, StringComparison.Ordinal);

    /// <summary>
    /// One-line explanation for the `doctor` per-check detail and the diagnostic warning: what the
    /// token means for this run, in the words a user reading "all reads failed" needs.
    /// </summary>
    public static string Describe(string parity) => parity switch
    {
        Huawei => "bundled Huawei-capable client",
        Google => "platform-tools client: Huawei temporary loaders are known to fail getvar/oem with it",
        _ => "lineage unverified: not the bundled client; reads against Huawei temporary loaders may fail",
    };

    private static bool IsBundledDirectory(string? directory)
    {
        if (directory == null) return false;
        string bundled;
        try
        {
            bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fastboot"));
        }
        catch (Exception)
        {
            return false;
        }

        return string.Equals(TrimDirectory(directory), TrimDirectory(bundled), StringComparison.Ordinal);
    }

    /// <summary>
    /// Directories a distro/Google install puts the AOSP client in. `platform-tools` in the path
    /// covers the SDK layout (`.../platform-tools/fastboot`) and unpacked release archives.
    /// </summary>
    private static bool IsPlatformToolsDirectory(string? directory)
    {
        if (directory == null) return false;
        string normalized = TrimDirectory(directory).Replace('\\', '/');
        if (normalized.Contains("/platform-tools", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (string systemDirectory in new[] { "/usr/bin", "/usr/local/bin", "/bin", "/opt/android-sdk" })
        {
            if (string.Equals(normalized, systemDirectory, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>`fastboot version 35.0.2-...` alone is not proof (the bundled build prints the same
    /// shape), so only the explicit platform-tools marker counts here.</summary>
    private static bool LooksLikePlatformToolsVersion(string? versionLine) =>
        versionLine != null && versionLine.Contains("platform-tools", StringComparison.OrdinalIgnoreCase);

    private static string TrimDirectory(string directory) => Path.TrimEndingDirectorySeparator(directory);
}
