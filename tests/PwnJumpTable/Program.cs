// D-06 (audit H20 / H23) hardware-free assertions for the pwn jump-address table.
//
// The addresses used to be two switch statements inside VcomFlasher. A changed digit there is not
// a compile error, so this harness pins all of them twice:
//   1. against a frozen snapshot of the switch as it was before the extraction (the literals below
//      are copied from the removed switch, not from the new table - a copied-from-the-table test
//      would pass no matter what the table says);
//   2. against FirmwareUnlocker.CpuAddresses, so the pwn-capable SoC set cannot drift apart.
// Plus a source scan that the literals did not creep back into VcomFlasher.cs.
// Nothing here opens a serial port or a device.
//
// Run from the repository root:  dotnet run --project tests/PwnJumpTable -c Release

using System.Text.RegularExpressions;
using Kirin_Tool.Services;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// ---- 1. frozen snapshot of the removed switch statements ------------------------------
// (xloaderStart, returnAddr) exactly as Services/VcomFlasher.cs computed them before D-06.
var oldSwitch = new Dictionary<string, (int XloaderStart, int ReturnAddr)>(StringComparer.Ordinal)
{
    ["hisi710"] = (0x2316D, 0x49BC8),
    ["hisi710a"] = (0x23155, 0x49BC8),
    ["hisi970"] = (0x2316D, 0x4DBC8),
    ["hisi980"] = (0x2316D, 0x4DBC8),
    ["hisi810"] = (0x23155, 0x4DBC8),
    ["hisi820"] = (0x23155, 0x673c8),
    ["hisi985"] = (0x23155, 0x673c8),
    ["hisi990"] = (0x23155, 0x673c8),
};

Check(PwnJumpTable.Entries.Count == oldSwitch.Count, "the table has exactly the eight SoCs the switch handled");
foreach (var (cpu, expected) in oldSwitch)
{
    Check(PwnJumpTable.Entries.ContainsKey(cpu), "table contains " + cpu);
    PwnJumpAddresses actual = PwnJumpTable.For(cpu);
    Check(actual.XloaderStart == expected.XloaderStart, cpu + " xloaderStart is byte-identical to the old switch (0x" + expected.XloaderStart.ToString("X") + ")");
    Check(actual.ReturnAddr == expected.ReturnAddr, cpu + " returnAddr is byte-identical to the old switch (0x" + expected.ReturnAddr.ToString("X") + ")");
}

// No SoC was added on the way out: every table key came from the old switch.
foreach (string cpu in PwnJumpTable.Entries.Keys)
    Check(oldSwitch.ContainsKey(cpu), "table key " + cpu + " existed in the old switch");

// ---- 2. the unsupported-CPU path kept its exact semantics ------------------------------
bool threw = false;
string message = string.Empty;
try { _ = PwnJumpTable.For("hisi999"); }
catch (NotSupportedException ex) { threw = true; message = ex.Message; }
Check(threw, "an unsupported CPU throws instead of returning a default address");
Check(message == "Exploit requested for an unsupported CPU: hisi999.", "the refusal message is the one the switch produced");
Check(!PwnJumpTable.For("hisi970").Equals(default(PwnJumpAddresses)), "a supported CPU never resolves to the zero address");

// ---- 3. alignment with FirmwareUnlocker.CpuAddresses ---------------------------------
var pwnCapable = FirmwareUnlocker.CpuAddresses
    .Where(pair => pair.Value.Any(stage => stage.PwnFlag))
    .Select(pair => pair.Key)
    .OrderBy(name => name, StringComparer.Ordinal)
    .ToArray();
var tableKeys = PwnJumpTable.Entries.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
Check(pwnCapable.SequenceEqual(tableKeys), "the pwn jump table covers exactly the PwnFlag=true SoCs of CpuAddresses");
foreach (string cpu in tableKeys)
{
    var table = FirmwareUnlocker.CpuAddresses[cpu];
    Check(table.Any(stage => stage.Name == "XLOADER" && stage.PwnFlag && stage.Address == PwnJumpTable.BootromHeadAddress),
        cpu + ": the pwn HEAD address matches its XLOADER load address in CpuAddresses");
}

// ---- 4. the literals did not creep back into the flasher -----------------------------
string flasherPath = Path.Combine(root, "Services/VcomFlasher.cs");
Check(File.Exists(flasherPath), "Services/VcomFlasher.cs is present (source scan)");
string flasherSource = File.ReadAllText(flasherPath);
foreach (string literal in new[] { "0x2316D", "0x23155", "0x673c8", "0x4DBC8", "0x49BC8" })
    Check(!flasherSource.Contains(literal, StringComparison.OrdinalIgnoreCase),
        "the jump literal " + literal + " no longer appears in VcomFlasher.cs");
Check(!Regex.IsMatch(flasherSource, @"cpu\s*switch"), "no 'cpu switch' over jump addresses remains in VcomFlasher.cs");
Check(flasherSource.Contains("PwnJumpTable.For(", StringComparison.Ordinal), "VcomFlasher resolves the jump targets through PwnJumpTable.For");

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("PwnJumpTable ALL PASS (" + checks + " checks)");
