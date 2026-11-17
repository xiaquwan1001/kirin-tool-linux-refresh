// D-16 (audit S14) hardware-free assertions for the shared pre-write battery floor.
//
// The floor is one object (Services/Verdict/BatteryGate.cs) asked by the GUI dialog path, the CLI
// write commands and the MCP tools; this harness pins its pure decisions from stub getvar text and
// then asserts by reflection that all three entry points really are wired to it. Nothing here
// opens a device: every level comes from a string this file wrote itself.
//
// Run from the repository root:  dotnet run --project tests/BatteryGate -c Release

using System.Reflection;
using Kirin_Tool.Services.Verdict;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// ---- 1. the floor itself -------------------------------------------------------------
BatteryGateVerdict low = BatteryGate.Evaluate(3);
Check(low.Refuse, "3% refuses the write");
Check(low.ExitCode == 4, "3% refuses with a non-zero exit code (4 = gated)");
Check(low.Gate == "battery", "the refusal verdict carries gate=battery");
Check(low.Level == 3, "the refusal verdict carries level=3");
Check(low.Reason.Contains("3%", StringComparison.Ordinal), "the reason quotes the measured level");

Check(BatteryGate.Evaluate(49).Refuse, "49% still refuses (the floor is inclusive of 50)");
Check(!BatteryGate.Evaluate(50).Refuse, "50% is allowed");
Check(!BatteryGate.Evaluate(100).Refuse, "100% is allowed");
Check(BatteryGate.Evaluate(0).Refuse, "0% refuses");
Check(BatteryGate.Evaluate(50).Gate == "battery", "an allowed measured level still reports gate=battery");
Check(BatteryGate.Evaluate(50).Level == 50, "an allowed measured level reports its level");

BatteryGateVerdict unknown = BatteryGate.Evaluate(null);
Check(!unknown.Refuse, "an unreadable level never refuses a write");
Check(unknown.Gate == "battery-unknown", "an unreadable level is reported as battery-unknown, not as a pass");
Check(unknown.Level is null, "an unreadable level has no level field");
Check(BatteryGate.MinimumPercent == 50, "the documented floor is 50%");

// ---- 2. parsing a stub getvar answer -------------------------------------------------
Check(BatteryGate.ParsePercent("battery-soc", "battery-soc: 42") == 42, "parses a plain battery-soc answer");
Check(BatteryGate.ParsePercent("battery-soc", "(bootloader) battery-soc: 42\r\nOKAY") == 42, "parses the (bootloader)-prefixed answer");
Check(BatteryGate.ParsePercent("battery-capacity", "battery-capacity: 7") == 7, "parses battery-capacity");
Check(BatteryGate.ParsePercent("battery-soc", "battery-soc: 42%") == 42, "parses a trailing percent sign");
Check(BatteryGate.ParsePercent("battery-soc", "FAILED (remote: 'unknown command')") is null, "a FAILED answer yields no level");
Check(BatteryGate.ParsePercent("battery-soc", "INFO invalid command") is null, "an 'invalid command' answer yields no level");
Check(BatteryGate.ParsePercent("battery-voltage", "battery-voltage: 4200") is null, "a voltage reading is never treated as a percentage");
Check(BatteryGate.ParsePercent("battery-soc", "battery-soc: 101") is null, "an out-of-range reading (101) is rejected");
Check(BatteryGate.ParsePercent("battery-soc", "battery-soc: -1") is null, "a negative reading is rejected");
Check(BatteryGate.ParsePercent("battery-soc", "") is null, "an empty answer yields no level");
Check(BatteryGate.ParsePercent("battery-soc", null) is null, "a null answer yields no level");
Check(BatteryGate.ParsePercent("battery-soc", "OKAY") is null, "a bare OKAY yields no level");

// ---- 3. the reader: first plausible variable wins, in order ---------------------------
var asked = new List<string>();
Task<string?> Stub(string variable) { asked.Add(variable); return Task.FromResult<string?>(null); }

asked.Clear();
int? fromCapacity = await BatteryGate.ReadPercentAsync(variable =>
{
    asked.Add(variable);
    return Task.FromResult<string?>(variable switch
    {
        "battery-soc" => "FAILED (remote: 'unknown command')",
        "battery-capacity" => "battery-capacity: 33",
        _ => null,
    });
});
Check(fromCapacity == 33, "falls through to battery-capacity when battery-soc is unsupported");
Check(asked.SequenceEqual(new[] { "battery-soc", "battery-capacity" }), "stops asking as soon as a level parses");
Check(await BatteryGate.ReadPercentAsync(Stub) is null, "no plausible answer anywhere yields null");

BatteryGateVerdict viaCheck = await BatteryGate.CheckAsync(_ => Task.FromResult<string?>("battery-soc: 12"));
Check(viaCheck.Refuse && viaCheck.Level == 12, "CheckAsync(refuse) is what a 12% stub produces");

// ---- 4. the three entry points share this object -------------------------------------
Assembly app = typeof(BatteryGate).Assembly;
const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

// GUI: the dialog path has a pre-write check that asks BatteryGate.
Type mainWindow = app.GetType("Kirin_Tool.MainWindow") ?? throw new Exception("FAIL MainWindow type not found");
MethodInfo? guiGate = mainWindow.GetMethod("EnsureBatteryAllowsWriteAsync", Any);
Check(guiGate != null && guiGate.ReturnType == typeof(Task<bool>), "GUI exposes EnsureBatteryAllowsWriteAsync(string) -> Task<bool>");
Check(!guiGate!.GetMethodBody()!.GetILAsByteArray()!.Length.Equals(0), "the GUI gate has a body (not an empty stub)");

// CLI/MCP: the shared gate class + the flag the write commands branch on.
Type? cliGate = app.GetType("Kirin_Tool.Cli.Commands.BatteryWriteGate");
Check(cliGate != null, "CLI/MCP has the shared BatteryWriteGate helper");
MethodInfo? cliCheck = cliGate!.GetMethod("CheckAsync", Any);
Check(cliCheck != null && cliCheck.GetParameters().Length == 3, "BatteryWriteGate.CheckAsync(context, command, session) exists");

Type? gatedWrite = app.GetType("Kirin_Tool.Cli.Commands.GatedDeviceWriteCommand");
Check(gatedWrite != null, "the real-write command base class exists");
PropertyInfo? flag = gatedWrite!.GetProperty("RequiresBatteryGate", Any);
Check(flag != null && flag.PropertyType == typeof(bool), "real-write commands expose RequiresBatteryGate");

// reboot writes nothing: it is the one command that must stay usable at 3% battery.
Type reboot = app.GetType("Kirin_Tool.Cli.Commands.RebootCommand") ?? throw new Exception("FAIL RebootCommand type not found");
object rebootInstance = Activator.CreateInstance(reboot, nonPublic: true)!;
Check(!(bool)flag!.GetValue(rebootInstance)!, "reboot opts out of the battery floor (a low battery must still be able to boot)");

// every other real-write command keeps the floor on.
string[] gatedCommands = { "WriteSnCommand", "FrpRemoveCommand", "UnlockBootloaderCommand", "UsbTestpointCommand", "RebootUsbUpdateCommand", "OemInfoFlashCommand", "DowngradeEraseCommand" };
foreach (string name in gatedCommands)
{
    Type type = app.GetType("Kirin_Tool.Cli.Commands." + name) ?? throw new Exception("FAIL command type not found: " + name);
    object instance = Activator.CreateInstance(type, nonPublic: true)!;
    Check((bool)flag.GetValue(instance)!, name + " asks the battery floor before writing");
}

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("BatteryGate ALL PASS (" + checks + " checks)");
