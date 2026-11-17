// D-14 (audit BB-13 / BB-16, GUI side) hardware-free assertions for the dload sentinel.
//
// Two things are pinned here:
//   1. the sentinel rule itself (pure: path + entry names, plus a real temp-directory run),
//      including the destructive case that motivated the debt - a directory in the cwd that is
//      NOT ours must survive untouched;
//   2. that every write path now goes through it: the GUI builds a private temp directory instead
//      of the cwd-relative "dload" literal, and OpLog.CleanDirectory (the recursive delete) has a
//      single call site, the sentinel-gated cleaner.
// No device, no serial port, no USB: everything is a temp directory this harness creates.
//
// Run from the repository root:  dotnet run --project tests/DloadSentinel -c Release

using Kirin_Tool.Services.USBUpdate;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// ---- 1. the rule (pure) --------------------------------------------------------------
string scratch = Path.Combine(Path.GetTempPath(), "kirin-dload-sentinel-" + Guid.NewGuid().ToString("n"));

Check(DloadSentinel.Evaluate(scratch, directoryExists: true, Array.Empty<string>()).MayDelete,
    "an empty directory may be deleted (nothing can be lost)");

string[] ourFiles = { "list.txt", "XLOADER.img", "XLOADER.img.header", "boot.img", "boot.img.header", "unlockcode", "XLOADER_BAK.img", "XLOADER_HEADER_BAK.img" };
DloadCleanupVerdict ours = DloadSentinel.Evaluate(scratch, true, ourFiles);
Check(ours.MayDelete, "a directory holding only the files this workflow writes may be deleted");
Check(ours.Unexpected.Count == 0, "an allowed directory names no unexpected entries");

DloadCleanupVerdict foreign = DloadSentinel.Evaluate(scratch, true, new[] { "list.txt", "XLOADER.img", "family-photos.jpg" });
Check(!foreign.MayDelete, "one foreign entry is enough to refuse the delete");
Check(foreign.Unexpected.SequenceEqual(new[] { "family-photos.jpg" }), "the refusal names the foreign entry");
Check(foreign.DirectoryExists, "the verdict records that the directory exists");

DloadCleanupVerdict noMarker = DloadSentinel.Evaluate(scratch, true, new[] { "boot.img", "boot.img.header" });
Check(!noMarker.MayDelete, "whitelist-shaped names without a workflow marker are not identifiable as ours");

Check(!DloadSentinel.Evaluate(null, true, Array.Empty<string>()).MayDelete, "no directory is never deleted");
Check(!DloadSentinel.Evaluate("", true, Array.Empty<string>()).MayDelete, "an empty path is never deleted");
Check(!DloadSentinel.Evaluate(scratch, directoryExists: false, Array.Empty<string>()).MayDelete, "a missing directory is not 'deleted'");

DloadCleanupVerdict filesystemRoot = DloadSentinel.Evaluate("/", true, Array.Empty<string>());
Check(!filesystemRoot.MayDelete, "a filesystem root is never deleted");
Check(filesystemRoot.Reason.Contains("root", StringComparison.OrdinalIgnoreCase), "the root refusal says why");

string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
if (!string.IsNullOrEmpty(home))
    Check(!DloadSentinel.Evaluate(home, true, ourFiles).MayDelete, "the user's home directory is never deleted, even when it looks like ours");

Check(DloadSentinel.IsOwnEntry("list.txt"), "list.txt is a workflow file");
Check(DloadSentinel.IsOwnEntry("XLOADER.img"), "XLOADER.img is a workflow file");
Check(DloadSentinel.IsOwnEntry("super.img.header"), "a partition image header is a workflow file");
Check(!DloadSentinel.IsOwnEntry(".git"), "a dotfile is not a workflow file");
Check(!DloadSentinel.IsOwnEntry("list.txt.bak"), "a look-alike name is not a workflow file");
Check(!DloadSentinel.IsOwnEntry("../evil.img"), "a traversal name is not a workflow file");
Check(!DloadSentinel.IsOwnEntry(null), "a null entry is not a workflow file");

// ---- 2. the real filesystem: refuse survives, allow deletes ---------------------------
string foreignDir = Path.Combine(Path.GetTempPath(), "kirin-dload-foreign-" + Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(foreignDir);
try
{
    File.WriteAllText(Path.Combine(foreignDir, "list.txt"), "previous run");
    File.WriteAllText(Path.Combine(foreignDir, "XLOADER.img"), "previous run");
    File.WriteAllText(Path.Combine(foreignDir, "family-photos.jpg"), "not ours");

    DloadCleanupVerdict refused = DloadSentinel.EvaluateDirectory(foreignDir);
    Check(!refused.MayDelete, "EvaluateDirectory refuses a directory with a foreign file");

    DloadCleanupVerdict cleaned = DloadDirectoryCleaner.Clean(foreignDir, log: null);
    Check(!cleaned.MayDelete, "the cleaner refuses what the sentinel refuses");
    Check(Directory.Exists(foreignDir), "the refused directory still exists");
    Check(File.Exists(Path.Combine(foreignDir, "family-photos.jpg")), "the foreign file was NOT deleted (this is the debt)");

    // nested directories are foreign entries too: no recursive delete of an unknown tree
    Directory.CreateDirectory(Path.Combine(foreignDir, "subdir"));
    Check(!DloadSentinel.EvaluateDirectory(foreignDir).MayDelete, "a subdirectory is a foreign entry");

    // now make it ours: the same directory becomes deletable once the foreign content is gone.
    Directory.Delete(Path.Combine(foreignDir, "subdir"), true);
    File.Delete(Path.Combine(foreignDir, "family-photos.jpg"));
    DloadCleanupVerdict ok = DloadSentinel.EvaluateDirectory(foreignDir);
    Check(ok.MayDelete, "the same directory is deletable once it holds only workflow files");
    Check(DloadDirectoryCleaner.Clean(foreignDir, log: null).MayDelete, "the cleaner deletes a recognised directory");
    Check(!Directory.Exists(foreignDir), "the recognised directory was actually removed");
}
finally
{
    try { if (Directory.Exists(foreignDir)) Directory.Delete(foreignDir, true); } catch (IOException) { }
}

// missing directory: the cleaner reports and does nothing (no exception).
Check(!DloadDirectoryCleaner.Clean(scratch, log: null).MayDelete, "cleaning a non-existent directory refuses instead of throwing");

// ---- 3. every write path goes through the sentinel -----------------------------------
string guiPath = Path.Combine(root, "MainWindow.axaml.cs");
string guiSource = File.ReadAllText(guiPath);
string guiCode = string.Join("\n", guiSource.Split('\n').Where(line =>
{
    string trimmed = line.TrimStart();
    return !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("///", StringComparison.Ordinal);
}));
Check(!guiCode.Contains("\"dload\"", StringComparison.Ordinal), "the GUI no longer passes a cwd-relative \"dload\" literal");
Check(guiCode.Contains("PrivateWorkDirectory.Create(\"kirin-tool-dload\")", StringComparison.Ordinal),
    "the GUI builds its scratch directory with PrivateWorkDirectory (same rule as the CLI)");
Check(guiCode.Split("PrivateWorkDirectory.Create(").Length == 2, "the GUI creates exactly one scratch directory, shared by both services");

string testpointSource = File.ReadAllText(Path.Combine(root, "Services/USBUpdate/SoftwareTestpointService.cs"));
Check(!testpointSource.Contains("Directory.Delete(", StringComparison.Ordinal), "SoftwareTestpointService no longer deletes a directory itself");
Check(testpointSource.Split("CleanScratchDirectory();").Length == 3, "both SoftwareTestpointService cleanups call the one gated helper");
Check(testpointSource.Contains("DloadDirectoryCleaner.Clean(", StringComparison.Ordinal), "that helper is the sentinel-gated cleaner");

string flasherSource = File.ReadAllText(Path.Combine(root, "Services/USBUpdate/USBUpdateFlasherService.cs"));
Check(!flasherSource.Contains("Directory.Delete(", StringComparison.Ordinal), "USBUpdateFlasherService no longer deletes a directory itself");
Check(flasherSource.Contains("DloadDirectoryCleaner.Clean(", StringComparison.Ordinal), "the USB Update flash cleanup goes through the sentinel-gated cleaner");

// The recursive delete lives in OpLog.CleanDirectory; it must have exactly one caller now.
var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .ToList();
var deleteCallers = sources
    .Where(path => !path.EndsWith(Path.DirectorySeparatorChar + "DloadDirectoryCleaner.cs", StringComparison.Ordinal))
    .Where(path => !path.EndsWith(Path.DirectorySeparatorChar + "OpLog.cs", StringComparison.Ordinal))
    .Where(path => File.ReadAllText(path).Contains("OpLog.CleanDirectory(", StringComparison.Ordinal))
    .Select(path => Path.GetFileName(path))
    .ToList();
Check(deleteCallers.Count == 0, "OpLog.CleanDirectory has no direct caller outside the sentinel gate (" + string.Join(", ", deleteCallers) + ")");

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("DloadSentinel ALL PASS (" + checks + " checks)");
