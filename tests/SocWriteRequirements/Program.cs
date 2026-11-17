// D-26 (audit H93 / H09) hardware-free assertions for the SoC requirement lists.
//
// The "needs a cable replug" and "needs oeminfo write protection erased" lists used to exist in
// four places: Services/FirmwareUnlocker.cs (twice), MainWindow.axaml.cs (the oeminfo branch) and
// Cli/Commands/VcomUnlockCommand.cs. They now live in Services/SocWriteRequirements.cs only.
//
// This harness pins them twice:
//   1. against a frozen snapshot of the literals as they were before the extraction (the literals
//      below are copied from the removed code, not read from the new table - a
//      copied-from-the-table test would pass no matter what the table says);
//   2. against a source scan, so a second literal list cannot creep back into any consumer.
// Nothing here opens a serial port, a fastboot process or a device.
//
// Run from the repository root:  dotnet run --project tests/SocWriteRequirements -c Release

using System.Text.RegularExpressions;
using Kirin_Tool.Services;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// ---- 1. frozen snapshot of the removed literals ---------------------------------------
// FirmwareUnlocker.cs and VcomUnlockCommand.cs both had exactly this replug list.
string[] oldReplug = { "hisi980", "hisi810", "hisi820", "hisi985", "hisi990" };
// MainWindow.axaml.cs' shouldDisableWp was: (hisi980 && fastflash) || hisi810 || hisi820 || hisi985 || hisi990.
string[] oldWpOff = { "hisi810", "hisi820", "hisi985", "hisi990" };
// The "unplug from BOTH SIDES" prompt covered these four, plus hisi980 on the fastflash loader.
string[] oldBothSides = { "hisi810", "hisi820", "hisi985", "hisi990" };

Check(SocWriteRequirements.CableReplugSocs.OrderBy(x => x, StringComparer.Ordinal)
        .SequenceEqual(oldReplug.OrderBy(x => x, StringComparer.Ordinal)),
    "the replug SoC set is byte-identical to the four removed literals");
Check(SocWriteRequirements.WriteProtectionSocs.OrderBy(x => x, StringComparer.Ordinal)
        .SequenceEqual(oldWpOff.OrderBy(x => x, StringComparer.Ordinal)),
    "the unconditional write-protection SoC set is byte-identical to the removed literal");
Check(SocWriteRequirements.CableReplugSocs.Count == oldReplug.Length, "no replug SoC was added on the way out");
Check(SocWriteRequirements.WriteProtectionSocs.Count == oldWpOff.Length, "no write-protection SoC was added on the way out");

// The sets are read-only views: a caller must not be able to add a SoC at runtime.
Check(SocWriteRequirements.CableReplugSocs is not ISet<string> addable || addable.IsReadOnly,
    "CableReplugSocs is not a mutable set");
Check(SocWriteRequirements.WriteProtectionSocs is not ISet<string> writable || writable.IsReadOnly,
    "WriteProtectionSocs is not a mutable set");

// ---- 2. the three predicates, SoC by SoC ----------------------------------------------
foreach (string soc in FirmwareUnlocker.CpuAddresses.Keys.OrderBy(x => x, StringComparer.Ordinal))
{
    Check(SocWriteRequirements.RequiresCableReplug(soc) == oldReplug.Contains(soc, StringComparer.Ordinal),
        "RequiresCableReplug(" + soc + ") matches the removed list");
    Check(SocWriteRequirements.RequiresWpOff(soc) == oldWpOff.Contains(soc, StringComparer.Ordinal),
        "RequiresWpOff(" + soc + ") matches the removed GUI list (no fastflash loader)");
    Check(SocWriteRequirements.ReplugAtBothSides(soc) == oldBothSides.Contains(soc, StringComparer.Ordinal),
        "ReplugAtBothSides(" + soc + ") matches the removed prompt rule (no fastflash loader)");
}

// hisi980 is the only SoC whose requirement depends on the loader flavour.
Check(SocWriteRequirements.RequiresWpOff("hisi980", useFastFlashLoader: false) == false,
    "hisi980 keeps the write protection with the normal loader (historical GUI rule)");
Check(SocWriteRequirements.RequiresWpOff("hisi980", useFastFlashLoader: true),
    "hisi980 erases the write protection on the fastflash-loader path");
Check(SocWriteRequirements.ReplugAtBothSides("hisi980") == false,
    "hisi980 asks for the computer-side unplug with the normal loader");
Check(SocWriteRequirements.ReplugAtBothSides("hisi980", useFastFlashLoader: true),
    "hisi980 asks for the both-sides unplug on the fastflash-loader path");
Check(SocWriteRequirements.RequiresCableReplug("hisi980"), "hisi980 needs the replug on both loader paths");

// The fastflash flag only ever changes hisi980's answer.
foreach (string soc in FirmwareUnlocker.CpuAddresses.Keys)
{
    if (soc == "hisi980") continue;
    Check(SocWriteRequirements.RequiresWpOff(soc, true) == SocWriteRequirements.RequiresWpOff(soc, false),
        "the fastflash flag does not change RequiresWpOff(" + soc + ")");
}

// Unknown / null SoCs are refused by name, not guessed.
foreach (string? unknown in new[] { null, "", "hisi999", "HISI980" })
{
    Check(!SocWriteRequirements.RequiresCableReplug(unknown), "unknown SoC '" + (unknown ?? "null") + "' is not a replug SoC");
    Check(!SocWriteRequirements.RequiresWpOff(unknown), "unknown SoC '" + (unknown ?? "null") + "' is not a write-protection SoC");
    Check(!SocWriteRequirements.RequiresWpOff(unknown, true), "unknown SoC '" + (unknown ?? "null") + "' stays a no on the fastflash path too");
    Check(!SocWriteRequirements.ReplugAtBothSides(unknown), "unknown SoC '" + (unknown ?? "null") + "' gets the computer-side prompt");
}

// ---- 3. no consumer keeps its own copy ------------------------------------------------
string Read(string relative)
{
    string path = Path.Combine(root, relative);
    Check(File.Exists(path), relative + " is present (source scan)");
    return File.ReadAllText(path);
}

string unlocker = Read("Services/FirmwareUnlocker.cs");
Check(unlocker.Contains("SocWriteRequirements.RequiresCableReplug(", StringComparison.Ordinal),
    "FirmwareUnlocker asks SocWriteRequirements for the replug rule");
Check(unlocker.Contains("SocWriteRequirements.ReplugAtBothSides(", StringComparison.Ordinal),
    "FirmwareUnlocker asks SocWriteRequirements for the prompt rule");
// The address table legitimately names every SoC; what must not come back is the replug/wp
// decision itself, i.e. a chained comparison against the replug list.
Check(!Regex.IsMatch(unlocker, @"cpu == ""hisi(980|810|820|985|990)"""),
    "FirmwareUnlocker.cs no longer compares the CPU against an inline SoC list");
Check(Regex.Matches(unlocker, @"RequiresCableReplug\(").Count == 2,
    "FirmwareUnlocker uses the replug rule exactly at the two former literal sites");

string cli = Read("Cli/Commands/VcomUnlockCommand.cs");
Check(cli.Contains("SocWriteRequirements.RequiresCableReplug(", StringComparison.Ordinal),
    "the CLI asks SocWriteRequirements for the replug rule");
Check(!cli.Contains("ReplugAfterNullCpus", StringComparison.Ordinal),
    "the CLI's private ReplugAfterNullCpus copy is gone");
Check(!Regex.IsMatch(cli, @"""hisi(980|810|820|985|990)"""),
    "the CLI carries no SoC literal list of its own");

string gui = Read("MainWindow.axaml.cs");
Check(gui.Contains("SocWriteRequirements.RequiresWpOff(", StringComparison.Ordinal),
    "the GUI's oeminfo branch asks SocWriteRequirements for the write-protection rule");
Check(!Regex.IsMatch(gui, @"shouldDisableWp\s*=\s*\(\s*selectedCpu"),
    "the GUI no longer builds shouldDisableWp from an inline SoC list");
// Scoped to shouldDisableWp on purpose: MainWindow keeps its separate `useDirectCommand` list
// (which oeminfo-capable SoCs have a direct oeminfowrite path) - that is a different question and
// not part of D-26.
Check(!Regex.IsMatch(gui, @"shouldDisableWp\s*=\s*[\s\S]{0,400}?selectedCpu =="),
    "the removed shouldDisableWp literal chain did not creep back");

// The single owner itself must stay a table, never a switch with per-SoC branches.
string owner = Read("Services/SocWriteRequirements.cs");
Check(!Regex.IsMatch(owner, @"soc\s*switch"), "SocWriteRequirements has no per-SoC switch statement");
foreach (string soc in oldReplug)
    Check(owner.Contains('"' + soc + '"', StringComparison.Ordinal), "SocWriteRequirements names " + soc + " literally");

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("SocWriteRequirements ALL PASS (" + checks + " checks)");
