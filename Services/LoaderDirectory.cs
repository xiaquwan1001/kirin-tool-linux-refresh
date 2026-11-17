// Licensed under the repository's Business Source License 1.1. See LICENSE.
namespace Kirin_Tool.Services;

/// <summary>Where a resolved loader root came from; printed by doctor/plan so nobody has to guess.</summary>
public enum LoaderDirectorySource
{
    /// <summary><c>--loader-dir &lt;dir&gt;</c> on the CLI (highest priority).</summary>
    Argument,
    /// <summary><c>KIRIN_LOADERS_DIR</c> in the environment.</summary>
    Environment,
    /// <summary><c>&lt;program directory&gt;/loaders</c> (the bundled layout).</summary>
    Default,
}

/// <summary>One resolved loader root, plus the evidence for it.</summary>
public sealed record LoaderDirectoryResolution(string Root, LoaderDirectorySource Source, string? RawValue)
{
    /// <summary>Stable token for JSON/logs: <c>--loader-dir</c> / <c>KIRIN_LOADERS_DIR</c> / <c>default</c>.</summary>
    public string SourceName => Source switch
    {
        LoaderDirectorySource.Argument => LoaderDirectory.ArgumentSourceName,
        LoaderDirectorySource.Environment => LoaderDirectory.EnvironmentSourceName,
        _ => LoaderDirectory.DefaultSourceName,
    };

    /// <summary>The per-chip directory (<c>&lt;root&gt;/&lt;cpu&gt;</c>, D-19's frozen layout).</summary>
    public string ChipDirectory(string chip) => Path.Combine(Root, chip);
}

/// <summary>
/// D-19 (audit H06 / H50 / H51): loader-directory resolution in one place.
///
/// The layout itself is a frozen face and does not move here: the root is still
/// <c>&lt;program directory&gt;/loaders</c> and the per-chip directory is still
/// <c>&lt;root&gt;/&lt;cpu&gt;</c> with <c>&lt;stage&gt;.ktl</c> (and <c>fastbootf.ktl</c>) inside it.
///
/// What is new is that the root can also come from the environment - so a package that installs
/// the loader set elsewhere (or a user with several copies) does not have to repeat
/// <c>--loader-dir</c> on every call - and that the resolution reports <b>which</b> of the three
/// sources won, so "Loader directory for &lt;cpu&gt; not found" can be traced to a specific input
/// instead of being read as a permission problem.
///
/// Priority: <c>--loader-dir</c> &gt; <c>KIRIN_LOADERS_DIR</c> &gt; default.
/// </summary>
public static class LoaderDirectory
{
    /// <summary>Environment variable read when the CLI has no <c>--loader-dir</c>.</summary>
    public const string EnvironmentVariable = "KIRIN_LOADERS_DIR";

    public const string ArgumentSourceName = "--loader-dir";
    public const string EnvironmentSourceName = EnvironmentVariable;
    public const string DefaultSourceName = "default";

    /// <summary>The bundled layout: <c>&lt;program directory&gt;/loaders</c>.</summary>
    public static string DefaultRoot(string baseDirectory) => Path.Combine(baseDirectory, "loaders");

    /// <summary>
    /// Resolves the loader root. Blank values are not a source (an empty <c>--loader-dir</c> must not
    /// silently mean "the current directory"), and the winner is always named.
    /// </summary>
    public static LoaderDirectoryResolution Resolve(string? argument, string? environment, string baseDirectory,
        string? workingDirectory = null)
    {
        string cwd = workingDirectory ?? Directory.GetCurrentDirectory();

        if (IsSet(argument))
            return new(Path.GetFullPath(argument!, cwd), LoaderDirectorySource.Argument, argument);
        if (IsSet(environment))
            return new(Path.GetFullPath(environment!, cwd), LoaderDirectorySource.Environment, environment);
        return new(DefaultRoot(baseDirectory), LoaderDirectorySource.Default, null);
    }

    /// <summary>Same priority chain, reading <see cref="EnvironmentVariable"/> from the process.</summary>
    public static LoaderDirectoryResolution ResolveFromEnvironment(string? argument, string baseDirectory,
        string? workingDirectory = null) =>
        Resolve(argument, System.Environment.GetEnvironmentVariable(EnvironmentVariable), baseDirectory, workingDirectory);

    /// <summary>
    /// The loader file name of a stage - the frozen naming rule in one place:
    /// <c>&lt;stage&gt;.ktl</c> in lower case, except the fastflash-loader flavour of FASTBOOT.
    /// </summary>
    public static string StageFileName(string stage, bool useFastFlashLoader = false) =>
        useFastFlashLoader && string.Equals(stage, "FASTBOOT", StringComparison.Ordinal)
            ? "fastbootf.ktl"
            : stage.ToLowerInvariant() + ".ktl";

    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);
}
