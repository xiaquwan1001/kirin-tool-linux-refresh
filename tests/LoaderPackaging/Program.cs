// D-20 (audit P15) hardware-free assertions for the packaged loader set.
//
// packaging/build-deb.sh and packaging/AppRun both assume loaders/ sits next to the binary; the
// publish path only produced it as a side effect of a csproj Content glob, and nothing verified
// it. A tree without loaders/ packages fine and fails later with "Loader directory for <cpu> not
// found", which is read as a host permission problem.
//
// This harness runs packaging/check-loaders.sh against fake trees (directories and file names
// only) and source-scans the four packaging entry points. No dotnet publish, no USB, no udev.
//
// Run from the repository root:  dotnet run --project tests/LoaderPackaging -c Release

using System.Diagnostics;
using Kirin_Tool.Services;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

string work = Path.Combine(Path.GetTempPath(), "kirin-loaderpack-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string script = Path.Combine(root, "packaging/check-loaders.sh");
    Check(File.Exists(script), "packaging/check-loaders.sh exists");

    // The script must never need a device, root or a network: syntax check only, then behaviour.
    (int syntaxCode, _, _) = Run("bash", "-n", script);
    Check(syntaxCode == 0, "check-loaders.sh has valid shell syntax");

    // ---- fake asset tree ---------------------------------------------------------------
    string source = Path.Combine(work, "loaders");
    foreach (string chip in new[] { "hisi970", "hisi980" })
    {
        Directory.CreateDirectory(Path.Combine(source, chip));
        foreach (string stage in new[] { "null", "xloader", "fastboot" })
            File.WriteAllBytes(Path.Combine(source, chip, stage + ".ktl"), new byte[] { 1, 2, 3 });
    }
    Directory.CreateDirectory(Path.Combine(source, "empty-chip")); // no .ktl: not an asset

    string GoodTree()
    {
        string publish = Path.Combine(work, "publish-" + Guid.NewGuid().ToString("N")[..8]);
        CopyLoaders(source, Path.Combine(publish, "loaders"));
        return publish;
    }

    // ---- the happy path ----------------------------------------------------------------
    string good = GoodTree();
    (int code, string stdout, string stderr) = Run("bash", script, good, source);
    Check(code == 0, "a complete publish tree passes (" + stderr.Trim() + ")");
    Check(stdout.Contains("loaders ok: 2 chips, 6 .ktl files", StringComparison.Ordinal),
        "the success line counts chips and .ktl files: " + stdout.Trim());

    // ---- a tree without loaders/ fails loudly, with the consequence spelled out ---------
    string noLoaders = Path.Combine(work, "publish-empty");
    Directory.CreateDirectory(noLoaders);
    (code, stdout, stderr) = Run("bash", script, noLoaders, source);
    Check(code == 1, "a publish tree without loaders/ fails the check");
    Check(stderr.Contains("missing loader directory in the publish tree: loaders/hisi970", StringComparison.Ordinal),
        "the failure names the missing chip directory");
    Check(stderr.Contains("Loader directory for <cpu> not found", StringComparison.Ordinal),
        "the failure states the runtime symptom it prevents");
    Check(stderr.Contains("NOT a host permission problem", StringComparison.Ordinal),
        "the failure says it is a packaging bug, not a permission problem");
    Check(stderr.Contains("packaging/publish.sh", StringComparison.Ordinal), "the failure names the fix");

    // ---- a chip directory or a single stage file missing is caught ----------------------
    string missingChip = GoodTree();
    Directory.Delete(Path.Combine(missingChip, "loaders", "hisi980"), true);
    (code, _, stderr) = Run("bash", script, missingChip, source);
    Check(code == 1 && stderr.Contains("loaders/hisi980", StringComparison.Ordinal),
        "a missing chip directory is reported by name");

    string missingFile = GoodTree();
    File.Delete(Path.Combine(missingFile, "loaders", "hisi970", "xloader.ktl"));
    (code, _, stderr) = Run("bash", script, missingFile, source);
    Check(code == 1 && stderr.Contains("loaders/hisi970/xloader.ktl", StringComparison.Ordinal),
        "a missing stage file is reported by name");

    // ---- empty chips in the source are not assets (a half-checked-out tree) ------------
    Check(!stdout.Contains("empty-chip", StringComparison.Ordinal), "a source chip without .ktl is not required");

    // ---- an unusable source tree is a different, louder failure ------------------------
    string emptySource = Path.Combine(work, "empty-source");
    Directory.CreateDirectory(emptySource);
    (code, _, stderr) = Run("bash", script, good, emptySource);
    Check(code == 2 && stderr.Contains("no <chip>/<stage>.ktl files", StringComparison.Ordinal),
        "a source tree with no loaders is refused as a misconfiguration (exit 2)");

    (code, _, stderr) = Run("bash", script, good, Path.Combine(work, "does-not-exist"));
    Check(code == 2 && stderr.Contains("loader source tree not found", StringComparison.Ordinal),
        "a missing source tree is refused as a misconfiguration (exit 2)");

    (code, _, stderr) = Run("bash", script);
    Check(code == 2 && stderr.Contains("usage:", StringComparison.Ordinal), "no argument prints usage and exits 2");

    // ---- the real asset tree is complete (no device needed) ----------------------------
    string realSource = Path.Combine(root, "loaders");
    Check(Directory.Exists(realSource), "the repository's loaders/ tree exists");
    string realPublish = Path.Combine(work, "publish-real");
    CopyLoaders(realSource, Path.Combine(realPublish, "loaders"));
    (code, stdout, _) = Run("bash", script, realPublish, realSource);
    Check(code == 0, "the repository asset tree satisfies its own check");
    Check(stdout.Contains("18 chips", StringComparison.Ordinal), "the repository ships 18 chip directories: " + stdout.Trim());

    // Every chip the address table knows has assets (otherwise a supported SoC cannot unlock).
    foreach (string chip in FirmwareUnlocker.CpuAddresses.Keys)
        Check(Directory.Exists(Path.Combine(realSource, chip)), "loaders/ covers the supported SoC " + chip);

    // ---- the four packaging entry points agree ----------------------------------------
    string Read(string relative)
    {
        string path = Path.Combine(root, relative);
        Check(File.Exists(path), relative + " is present (source scan)");
        return File.ReadAllText(path);
    }

    string publish = Read("packaging/publish.sh");
    Check(publish.Contains("check-loaders.sh", StringComparison.Ordinal), "publish.sh validates the loader set");
    Check(publish.IndexOf("check-loaders.sh", StringComparison.Ordinal) < publish.IndexOf("licenses", StringComparison.Ordinal),
        "publish.sh validates before it finishes the tree (fail before the slow tail)");

    string deb = Read("packaging/build-deb.sh");
    Check(deb.Contains("check-loaders.sh", StringComparison.Ordinal), "build-deb.sh asserts the loader set");
    Check(deb.IndexOf("check-loaders.sh", StringComparison.Ordinal) < deb.IndexOf("cp -a \"$PUBLISH/.\"", StringComparison.Ordinal),
        "build-deb.sh asserts before it copies the payload");

    string appRun = Read("packaging/AppRun");
    Check(appRun.Contains("loaders/", StringComparison.Ordinal) && appRun.Contains("packaging/publish.sh", StringComparison.Ordinal),
        "AppRun's assumption names the producer it relies on");

    string pkgbuild = Read("packaging/PKGBUILD");
    Check(pkgbuild.Contains("publish-out/loaders", StringComparison.Ordinal), "PKGBUILD check() requires the loader directory");

    // ---- the app still reads <app>/loaders/<cpu> (the frozen layout) -------------------
    Check(LoaderDirectory.DefaultRoot("/opt/app") == Path.Combine("/opt/app", "loaders"),
        "the packaged layout the check enforces is still <app>/loaders");
    Check(new LoaderDirectoryResolution("/opt/app/loaders", LoaderDirectorySource.Default, null).ChipDirectory("hisi990")
            == Path.Combine("/opt/app/loaders", "hisi990"),
        "the check's loaders/<chip> matches what the resolver builds");
}
finally { Directory.Delete(work, true); }

static (int Code, string Stdout, string Stderr) Run(string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
}

static void CopyLoaders(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (string chip in Directory.GetDirectories(from))
    {
        string target = Path.Combine(to, Path.GetFileName(chip));
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(chip, "*.ktl"))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
}

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("LoaderPackaging ALL PASS (" + checks + " checks)");
