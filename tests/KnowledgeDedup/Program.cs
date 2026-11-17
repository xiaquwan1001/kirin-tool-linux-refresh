// D-27 (audit H49 / H74 / S42 / S43) hardware-free assertions that three pieces of
// UPDATE.APP/OEMINFO knowledge exist exactly once.
//
// Before this harness there were two implementations of each:
//   * OEMINFO version probe: Services/OemInfoEditor.DetectOemVersion and
//     Cli/Commands/WriteCommands.TryDetectOemInfoVersion (same magic, same 0x400 step);
//   * UPDATE.APP alias table: Utils/UpdateApp.GetPartitionName and
//     Cli/Commands/OtaFlashRunner.NameAliases;
//   * UPDATE.APP entry marker 55 AA 5A A5: Utils/UpdateApp.ParseFile and
//     Services/USBUpdate/USBUpdateApp (twice).
// The merged implementation lives in Utils/UpdateAppFormat.cs (marker + aliases) and
// Services/OemInfoEditor.DetectVersion (probe). This test pins
//   (1) the merged semantics against a frozen copy of the OLD rules (not against the new
//       constants - a copied-from-the-new-code test would pass no matter what),
//   (2) that the old copies are gone, by scanning the sources.
// No device, no USB, no package on disk is opened here.
//
// Run from the repository root:  dotnet run --project tests/KnowledgeDedup -c Release

using Kirin_Tool.Services;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }
string Source(string relative) => File.ReadAllText(Path.Combine(root, relative));

// ---------------------------------------------------------------- 1. UPDATE.APP alias table
// Frozen copy of the two old switches, typed out from the removed code.
var oldParserAliases = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["hisiufs_gpt"] = "ptable",
    ["efi"] = "ptable",
    ["ufsfw"] = "ufs_fw",
    ["erecovery_ramdis"] = "erecovery_ramdisk",
    ["recovery_ramdis"] = "recovery_ramdisk",
    ["vbmeta_hw_produc"] = "vbmeta_hw_product",
};
Check(UpdateAppFormat.PartitionAliases.Count == oldParserAliases.Count,
    "the one alias table has exactly the six aliases the two copies had");
foreach (var (key, expected) in oldParserAliases)
{
    Check(UpdateAppFormat.PartitionAliases.TryGetValue(key, out string? value) && value == expected,
        "alias " + key + " -> " + expected + " preserved");
}

// Parser semantics (Utils/UpdateApp.GetPartitionName): blank -> UNKNOWN, trim + lower + alias.
Check(UpdateAppFormat.PartitionName("  HISIUFS_GPT ") == "ptable", "parser rule: entry name is trimmed, lower-cased and aliased");
Check(UpdateAppFormat.PartitionName("SYSTEM") == "system", "parser rule: unmapped names come back lower-cased");
Check(UpdateAppFormat.PartitionName("   ") == "UNKNOWN", "parser rule: a blank name is UNKNOWN (not an empty string)");
Check(UpdateAppFormat.PartitionName(null) == "UNKNOWN", "parser rule: a null name is UNKNOWN");
Check(UpdateAppFormat.PartitionName("efi") == "ptable", "parser rule: the static efi entry maps to ptable");

// Planner semantics (Cli/Commands/OtaFlashRunner): alias applied, unmapped names pass through
// lower-cased, and nothing is invented for a blank name (that used to be the caller's problem).
Check(UpdateAppFormat.LoaderPartitionName("HISIUFS_GPT") == "ptable", "planner rule: an archive name resolves through the same table");
Check(UpdateAppFormat.LoaderPartitionName("VBMETA_HW_PRODUC") == "vbmeta_hw_product", "planner rule: the long vbmeta alias resolves");
Check(UpdateAppFormat.LoaderPartitionName("  System ") == "system", "planner rule: unmapped names are trimmed and lower-cased");
Check(UpdateAppFormat.LoaderPartitionName("") == "", "planner rule: a blank plan entry stays blank (no invented UNKNOWN)");

// ---------------------------------------------------------------- 2. entry marker
Check(UpdateAppFormat.IsHeaderMagic(new byte[] { 0x55, 0xAA, 0x5A, 0xA5 }), "the marker 55 AA 5A A5 is recognised");
Check(UpdateAppFormat.IsHeaderMagic(new byte[] { 0xFF, 0x55, 0xAA, 0x5A, 0xA5 }.AsSpan(0, 4)) == false,
    "a marker that does not start at offset 0 of the span is not a header");
Check(!UpdateAppFormat.IsHeaderMagic(new byte[] { 0x55, 0xAA, 0x5A }), "three bytes are not a header (truncated tail)");
Check(!UpdateAppFormat.IsHeaderMagic(new byte[] { 0x55, 0xAA, 0x5A, 0xA4 }), "a one-bit-different marker is not a header");
Check(!UpdateAppFormat.IsHeaderMagic(ReadOnlySpan<byte>.Empty), "an empty span is not a header");
Check(UpdateAppFormat.HeaderMagicLength == 4, "the marker is four bytes");

// ---------------------------------------------------------------- 3. OEMINFO version probe
byte[] OemImage(int blocks, int versionBlock, int version)
{
    var data = new byte[blocks * 0x400];
    if (versionBlock >= 0)
    {
        System.Text.Encoding.ASCII.GetBytes("OEM_INFO").CopyTo(data, versionBlock * 0x400);
        BitConverter.GetBytes(version).CopyTo(data, versionBlock * 0x400 + 8);
    }
    return data;
}

Check(OemInfoEditor.DetectVersion(OemImage(1, 0, 6)) == 6, "probe: version 6 in block 0");
Check(OemInfoEditor.DetectVersion(OemImage(3, 2, 9)) == 9, "probe: the 0x400 block step finds a header in block 2");
Check(OemInfoEditor.DetectVersion(OemImage(2, -1, 0)) is null, "probe: no magic -> null (not a bogus version)");
Check(OemInfoEditor.DetectVersion(null) is null, "probe: null data -> null");
Check(OemInfoEditor.DetectVersion(Array.Empty<byte>()) is null, "probe: empty data -> null");
// A header whose 12-byte prefix does not fit in the buffer has no version to read.
{
    var truncated = new byte[0x400 + 8];
    System.Text.Encoding.ASCII.GetBytes("OEM_INFO").CopyTo(truncated, 0x400);
    Check(OemInfoEditor.DetectVersion(truncated) is null, "probe: a truncated trailing header is not a match");
}
// The instance path (what the converter uses) reports the same number as the static probe.
{
    byte[] image = OemImage(2, 1, 8);
    var editor = new OemInfoEditor(image);
    Check(editor.DetectedVersion == 8 && editor.DetectedVersion == OemInfoEditor.DetectVersion(image),
        "the converter's DetectedVersion and the shared probe agree");
}
try
{
    _ = new OemInfoEditor(OemImage(1, -1, 0));
    Check(false, "an image without OEM_INFO magic is still refused by the editor");
}
catch (InvalidDataException)
{
    Check(true, "an image without OEM_INFO magic is still refused by the editor");
}

// ---------------------------------------------------------------- 4. only one implementation
string[] sourceFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    .ToArray();

List<string> FilesContaining(string needle, string[] allowed, Func<string, bool>? scope = null) =>
    sourceFiles
        .Where(path => scope == null || scope(path.Replace('\\', '/')))
        .Where(path => File.ReadAllText(path).Contains(needle, StringComparison.Ordinal))
        .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
        .Where(relative => !allowed.Contains(relative, StringComparer.Ordinal))
        .OrderBy(relative => relative, StringComparer.Ordinal)
        .ToList();

// 4a. the marker bytes may only appear in the one format file
foreach (string marker in new[] { "0x55", "0x5A, 0xA5", "0xA5" })
{
    List<string> stray = FilesContaining(marker, new[] { "Utils/UpdateAppFormat.cs" });
    Check(stray.Count == 0, "entry-marker literal " + marker + " appears nowhere but Utils/UpdateAppFormat.cs (found: " + string.Join(", ", stray) + ")");
}

// 4b. the alias source names may only live in the one table
foreach (string alias in new[] { "hisiufs_gpt", "erecovery_ramdis", "vbmeta_hw_produc" })
{
    List<string> stray = FilesContaining(alias, new[] { "Utils/UpdateAppFormat.cs" });
    Check(stray.Count == 0, "alias key " + alias + " appears nowhere but Utils/UpdateAppFormat.cs (found: " + string.Join(", ", stray) + ")");
}

// 4c. the OEMINFO probe (magic string / 0x400 block step) lives in OemInfoEditor only.
// "0x400" is also the VCOM frame payload cap (Services/VcomFlasher.cs) — a different fact — so the
// block-step scan is scoped to the CLI, which is where the second copy lived.
{
    List<string> strayMagic = FilesContaining("OEM_INFO", new[] { "Services/OemInfoEditor.cs" });
    Check(strayMagic.Count == 0,
        "the OEMINFO magic appears nowhere but Services/OemInfoEditor.cs (found: " + string.Join(", ", strayMagic) + ")");

    List<string> strayStep = FilesContaining("0x400",
        new[] { "Services/OemInfoEditor.cs", "Services/VcomFlasher.cs" },
        path => path.StartsWith("Cli/", StringComparison.Ordinal));
    Check(strayStep.Count == 0,
        "the 0x400 block step appears in no CLI source (found: " + string.Join(", ", strayStep) + ")");

    Check(!Source("Cli/Commands/WriteCommands.cs").Contains("OEM_INFO", StringComparison.Ordinal),
        "the CLI's own OEM_INFO literal is gone");
}

// 4d. the call sites really call the shared code (a green scan with no callers would be worthless)
Check(Source("Cli/Commands/WriteCommands.cs").Contains("OemInfoEditor.DetectVersion(", StringComparison.Ordinal),
    "the CLI plans read their OEMINFO version through OemInfoEditor.DetectVersion");
Check(Source("Cli/Commands/OtaFlashRunner.cs").Contains("UpdateAppFormat.LoaderPartitionName(", StringComparison.Ordinal),
    "the CLI plan resolves partition names through UpdateAppFormat.LoaderPartitionName");
Check(!Source("Cli/Commands/OtaFlashRunner.cs").Contains("NameAliases", StringComparison.Ordinal),
    "the planner's private alias dictionary is gone");
Check(Source("Utils/UpdateApp.cs").Contains("UpdateAppFormat.PartitionName(", StringComparison.Ordinal) &&
      Source("Utils/UpdateApp.cs").Contains("UpdateAppFormat.IsHeaderMagic(", StringComparison.Ordinal),
    "the strict parser uses the shared name rule and marker");
Check(Source("Services/USBUpdate/USBUpdateApp.cs").Contains("UpdateAppFormat.IsHeaderMagic(", StringComparison.Ordinal),
    "the USB-update extractor uses the shared marker");

Console.WriteLine(checks + " knowledge-dedup checks passed; no hardware accessed.");
