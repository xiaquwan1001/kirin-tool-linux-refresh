// D-19 (audit H06 / H50 / H51) hardware-free assertions for loader-directory resolution.
//
// The rule used to be "CLI --loader-dir, otherwise <program directory>/loaders", decided in two
// places (CommandSupport.BundledLoaders and FirmwareUnlocker.UnlockFastboot) with the file naming
// repeated a third and fourth time. It is now one pure function in Services/LoaderDirectory.cs:
// priority --loader-dir > KIRIN_LOADERS_DIR > default, and the winner is printable.
//
// The layout and the file names are a FROZEN FACE: <root>/<cpu>/<stage>.ktl (+ fastbootf.ktl).
// This harness pins them so a "cleanup" cannot rename them.
//
// Run from the repository root:  dotnet run --project tests/LoaderDirectory -c Release

using System.Text.RegularExpressions;
using Kirin_Tool.Services;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

string work = Path.Combine(Path.GetTempPath(), "kirin-loaderdir-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string baseDir = Path.Combine(work, "program");
    Directory.CreateDirectory(baseDir);

    // ---- 1. priority chain -------------------------------------------------------------
    LoaderDirectoryResolution fromArgument = LoaderDirectory.Resolve("/opt/loaders", "/env/loaders", baseDir, work);
    Check(fromArgument.Root == "/opt/loaders" && fromArgument.Source == LoaderDirectorySource.Argument,
        "--loader-dir wins over KIRIN_LOADERS_DIR");
    Check(fromArgument.SourceName == "--loader-dir", "the argument source prints as --loader-dir");

    LoaderDirectoryResolution fromEnvironment = LoaderDirectory.Resolve(null, "/env/loaders", baseDir, work);
    Check(fromEnvironment.Root == "/env/loaders" && fromEnvironment.Source == LoaderDirectorySource.Environment,
        "KIRIN_LOADERS_DIR wins over the default");
    Check(fromEnvironment.SourceName == LoaderDirectory.EnvironmentVariable && fromEnvironment.SourceName == "KIRIN_LOADERS_DIR",
        "the environment source prints as KIRIN_LOADERS_DIR");

    LoaderDirectoryResolution fromDefault = LoaderDirectory.Resolve(null, null, baseDir, work);
    Check(fromDefault.Root == Path.Combine(baseDir, "loaders") && fromDefault.Source == LoaderDirectorySource.Default,
        "with neither input the root is <program dir>/loaders (bundled layout unchanged)");
    Check(fromDefault.SourceName == "default", "the default source prints as default");

    // ---- 2. blank inputs are not a source ----------------------------------------------
    foreach (string? blank in new[] { null, "", "   " })
    {
        Check(LoaderDirectory.Resolve(blank, "/env/loaders", baseDir, work).Source == LoaderDirectorySource.Environment,
            "an empty --loader-dir ('" + (blank ?? "null") + "') does not shadow KIRIN_LOADERS_DIR");
        Check(LoaderDirectory.Resolve(blank, blank, baseDir, work).Source == LoaderDirectorySource.Default,
            "blank inputs fall through to the default ('" + (blank ?? "null") + "')");
    }

    // ---- 3. relative values resolve against the caller's directory ----------------------
    LoaderDirectoryResolution relative = LoaderDirectory.Resolve("kingroot-loaders", null, baseDir, work);
    Check(relative.Root == Path.Combine(work, "kingroot-loaders"),
        "a relative --loader-dir resolves against the CLI working directory, not the process cwd");
    Check(relative.RawValue == "kingroot-loaders", "the raw value is kept for the log/JSON");

    // ---- 4. the frozen layout and file naming ------------------------------------------
    Check(fromDefault.ChipDirectory("hisi970") == Path.Combine(fromDefault.Root, "hisi970"),
        "the per-chip directory is still <root>/<cpu>");
    Check(new LoaderDirectoryResolution("/x", LoaderDirectorySource.Default, null).ChipDirectory("hisi65x_a")
            == Path.Combine("/x", "hisi65x_a"),
        "the layout accepts the historical hisi65x_a chip name");

    var expectedNames = new Dictionary<string, (string Normal, string FastFlash)>(StringComparer.Ordinal)
    {
        ["XLOADER"] = ("xloader.ktl", "xloader.ktl"),
        ["null"] = ("null.ktl", "null.ktl"),
        ["UCE"] = ("uce.ktl", "uce.ktl"),
        ["FASTBOOT"] = ("fastboot.ktl", "fastbootf.ktl"),
        ["USBLOADER"] = ("usbloader.ktl", "usbloader.ktl"),
        ["BL2"] = ("bl2.ktl", "bl2.ktl"),
    };
    foreach (var (stage, names) in expectedNames)
    {
        Check(LoaderDirectory.StageFileName(stage) == names.Normal,
            "StageFileName(" + stage + ") is the frozen " + names.Normal);
        Check(LoaderDirectory.StageFileName(stage, useFastFlashLoader: true) == names.FastFlash,
            "StageFileName(" + stage + ", fastflash) is the frozen " + names.FastFlash);
    }
    Check(LoaderDirectory.StageFileName("fastboot") == "fastboot.ktl", "the fastflash flavour only applies to FASTBOOT");

    // Every stage named by the address table resolves to <stage>.ktl (no chip invents a name).
    foreach (var (chip, stages) in FirmwareUnlocker.CpuAddresses)
        foreach (var stage in stages)
            Check(LoaderDirectory.StageFileName(stage.Name) == stage.Name.ToLowerInvariant() + ".ktl",
                chip + "/" + stage.Name + " keeps the <stage>.ktl rule");

    // ---- 5. one owner: no consumer re-implements the chain or the naming ---------------
    string Read(string relativePath)
    {
        string path = Path.Combine(root, relativePath);
        Check(File.Exists(path), relativePath + " is present (source scan)");
        return File.ReadAllText(path);
    }

    string support = Read("Cli/CommandSupport.cs");
    Check(support.Contains("LoaderDirectory.ResolveFromEnvironment(null, AppContext.BaseDirectory)", StringComparison.Ordinal),
        "BundledLoaders.Directory resolves through LoaderDirectory (env honoured)");
    Check(support.Contains("LoaderDirectory.StageFileName(", StringComparison.Ordinal),
        "BundledLoaders.Plan uses the single naming rule");
    Check(!Regex.IsMatch(support, @"Path\.Combine\(AppContext\.BaseDirectory, ""loaders""\)"),
        "the hard-coded <base>/loaders path is gone from CommandSupport");

    string unlocker = Read("Services/FirmwareUnlocker.cs");
    Check(unlocker.Contains("LoaderDirectory.ResolveFromEnvironment(", StringComparison.Ordinal),
        "the GUI/service default honours KIRIN_LOADERS_DIR too");
    Check(unlocker.Contains("LoaderDirectory.StageFileName(", StringComparison.Ordinal),
        "FirmwareUnlocker uses the single naming rule");
    Check(!unlocker.Contains("fastbootf.ktl", StringComparison.Ordinal),
        "the fastbootf.ktl literal no longer lives in FirmwareUnlocker");

    string precheck = Read("Services/Verdict/LoaderPrecheck.cs");
    Check(precheck.Contains("LoaderDirectory.StageFileName(", StringComparison.Ordinal),
        "the pre-flight uses the same naming rule (a missing file cannot be reported under another name)");

    string cli = Read("Cli/Commands/VcomUnlockCommand.cs");
    Check(cli.Contains("BundledLoaders.Resolve(args.Value(\"loader-dir\"), context.WorkingDirectory)", StringComparison.Ordinal),
        "vcom-unlock resolves the root through the one chain");
    Check(cli.Contains("loaderDirectorySource", StringComparison.Ordinal),
        "the vcom-unlock plan/JSON reports where the loader directory came from");
    Check(cli.Contains("KIRIN_LOADERS_DIR", StringComparison.Ordinal),
        "the CLI documents the KIRIN_LOADERS_DIR fallback in --help/params");

    string doctor = Read("Cli/Commands/ReadCommands.cs");
    Check(doctor.Contains("source = loaderResolution.SourceName", StringComparison.Ordinal),
        "doctor --json carries loaders.source");
    Check(doctor.Contains("(source: {loaderResolution.SourceName})", StringComparison.Ordinal),
        "the human doctor line names the source as well");

    // ---- 6. the env var actually reaches the resolver ---------------------------------
    string? old = Environment.GetEnvironmentVariable(LoaderDirectory.EnvironmentVariable);
    try
    {
        string envRoot = Path.Combine(work, "env-loaders");
        Environment.SetEnvironmentVariable(LoaderDirectory.EnvironmentVariable, envRoot);
        Check(LoaderDirectory.ResolveFromEnvironment(null, baseDir).Root == envRoot,
            "KIRIN_LOADERS_DIR is read from the process environment");
        Check(LoaderDirectory.ResolveFromEnvironment("/opt/x", baseDir).Root == "/opt/x",
            "--loader-dir still wins when the environment variable is set");
        Environment.SetEnvironmentVariable(LoaderDirectory.EnvironmentVariable, "   ");
        Check(LoaderDirectory.ResolveFromEnvironment(null, baseDir).Source == LoaderDirectorySource.Default,
            "a whitespace-only KIRIN_LOADERS_DIR is not a source");
    }
    finally { Environment.SetEnvironmentVariable(LoaderDirectory.EnvironmentVariable, old); }
}
finally { Directory.Delete(work, true); }

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("LoaderDirectory ALL PASS (" + checks + " checks)");
