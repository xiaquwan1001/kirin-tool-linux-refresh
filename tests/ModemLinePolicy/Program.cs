// D-18 (audit P02 / H15) hardware-free assertions for the Linux modem-line decision.
//
// Before D-18 VcomFlasher.Connect asked one question - "is the kernel driver literally option?" -
// and used "request RTS/DTR" for every other answer, including "the probe found no driver". The
// decision now lives in Services/ModemLinePolicy.cs and records its own evidence.
//
// Nothing here opens a serial port: the fake sysfs tree below is directories and symlinks only.
//
// Run from the repository root:  dotnet run --project tests/ModemLinePolicy -c Release

using System.Text.RegularExpressions;
using Kirin_Tool.Services;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// ---- 1. the escapes keep their exact meaning -----------------------------------------
ModemLineDecision forcedOn = ModemLinePolicy.Decide(true, false, true, DriverProbeResult.Of("cdc_acm"));
Check(forcedOn.BulkOnly && forcedOn.Origin == ModemLinePolicy.OriginForced,
    "VcomFlasherOptions.BulkOnly=true still forces bulk-only framing");
Check(forcedOn.Verified, "an explicit caller decision counts as verified (nothing was guessed)");

ModemLineDecision forcedOff = ModemLinePolicy.Decide(true, false, false, DriverProbeResult.Of("option"));
Check(!forcedOff.BulkOnly && forcedOff.Origin == ModemLinePolicy.OriginForced,
    "VcomFlasherOptions.BulkOnly=false still forces the modem-line path even on the option driver");

ModemLineDecision windows = ModemLinePolicy.Decide(false, false, null, DriverProbeResult.NotApplicable);
Check(!windows.BulkOnly && windows.Origin == ModemLinePolicy.OriginNonLinux,
    "non-Linux keeps the historical Handshake.RequestToSend + DTR settings");

ModemLineDecision explicitPort = ModemLinePolicy.Decide(true, true, null, DriverProbeResult.Unavailable("no sysfs entry for a pty"));
Check(explicitPort.BulkOnly && explicitPort.Origin == ModemLinePolicy.OriginExplicitPort,
    "an explicit --port is still treated as a no-modem-line device (pty/fake)");

// ---- 2. the decision is now evidence-based, and says so ------------------------------
ModemLineDecision option = ModemLinePolicy.Decide(true, false, null, DriverProbeResult.Of("option"));
Check(option.BulkOnly && option.Origin == ModemLinePolicy.OriginDriverOption,
    "driver=option clears RTS/DTR (the captured behaviour)");
Check(option.Verified, "driver=option is verified evidence, not an assumption");

ModemLineDecision cdc = ModemLinePolicy.Decide(true, false, null, DriverProbeResult.Of("cdc_acm"));
Check(cdc.BulkOnly, "a non-option driver still clears RTS/DTR first (the bulk endpoint needs no modem lines)");
Check(cdc.Origin == ModemLinePolicy.OriginCapabilityProbe && !cdc.Verified,
    "a non-option driver is reported as an unverified capability assumption, not as option evidence");
Check(cdc.Reason.Contains("cdc_acm", StringComparison.Ordinal), "the recorded reason names the driver it saw");
Check(cdc.Describe().Contains("verified=no", StringComparison.Ordinal), "Describe() states that the path is unverified");

ModemLineDecision unknown = ModemLinePolicy.Decide(true, false, null, DriverProbeResult.Unavailable("sysfs unreadable"));
Check(unknown.BulkOnly && unknown.Origin == ModemLinePolicy.OriginCapabilityProbe && !unknown.Verified,
    "an unavailable probe no longer falls back to requesting modem lines silently");
Check(unknown.Reason.Contains("sysfs unreadable", StringComparison.Ordinal), "the probe failure is carried into the reason");
Check(unknown.Describe().Contains("bulk-only", StringComparison.Ordinal), "Describe() names the mode that will run");

ModemLineDecision notAttempted = ModemLinePolicy.Decide(true, false, null, DriverProbeResult.NotApplicable);
Check(notAttempted.BulkOnly && !notAttempted.Verified && notAttempted.Reason.Contains("not Linux", StringComparison.Ordinal),
    "a probe that could not run is reported as unverified evidence, never as the option driver");

// Every decision explains itself in one line (the log/doctor contract).
foreach (ModemLineDecision decision in new[] { forcedOn, forcedOff, windows, explicitPort, option, cdc, unknown, notAttempted })
{
    Check(decision.Reason.Length > 0, "every decision carries a reason");
    Check(decision.Describe().Contains("origin=" + decision.Origin, StringComparison.Ordinal),
        "Describe() exposes the origin token " + decision.Origin);
}

// ---- 3. the sysfs probe (fake tree, no device) --------------------------------------
string dir = Path.Combine(Path.GetTempPath(), "kirin-modemlines-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string ttyClassRoot = Path.Combine(dir, "class", "tty");
    string usbInterface = Path.Combine(dir, "devices", "usb", "1-1", "1-1:1.0");
    string tty = Path.Combine(usbInterface, "tty", "ttyUSB0");
    Directory.CreateDirectory(tty);
    Directory.CreateDirectory(ttyClassRoot);
    Directory.CreateSymbolicLink(Path.Combine(ttyClassRoot, "ttyUSB0"), tty);
    Directory.CreateSymbolicLink(Path.Combine(tty, "device"), usbInterface);

    Check(LinuxSerialPortFinder.GetPortDriverName("/dev/ttyUSB0", ttyClassRoot) == null,
        "a tty without a driver link resolves to null, not to a guessed driver name");

    string driverDir = Path.Combine(dir, "bus", "usb", "drivers", "option");
    Directory.CreateDirectory(driverDir);
    Directory.CreateSymbolicLink(Path.Combine(usbInterface, "driver"), driverDir);
    Check(LinuxSerialPortFinder.GetPortDriverName("/dev/ttyUSB0", ttyClassRoot) == "option",
        "the sysfs walk finds the driver link of the physical interface");

    Check(ModemLinePolicy.ProbeDriver("/dev/ttyUSB0", ttyClassRoot: ttyClassRoot) is { Known: true, Attempted: true, Driver: "option" },
        "ProbeDriver reads the same driver through the same rule");
    Check(!(ModemLinePolicy.ProbeDriver("", null) is { Known: true }), "an empty port name is not probed");
    Check(ModemLinePolicy.ProbeDriver("/dev/ttyUSB0", _ => null) is { Attempted: true, Driver: null, Known: false },
        "an injected probe that finds nothing is reported as attempted-but-unknown");
    Check(ModemLinePolicy.ProbeDriver("/dev/ttyUSB0", _ => throw new IOException("boom")) is { Error: not null, Known: false },
        "a throwing probe is recorded as an error, never swallowed into 'option'");
}
finally { Directory.Delete(dir, true); }

// ---- 4. the flasher asks the policy, and logs the answer ----------------------------
string Read(string relative)
{
    string path = Path.Combine(root, relative);
    Check(File.Exists(path), relative + " is present (source scan)");
    return File.ReadAllText(path);
}

string flasher = Read("Services/VcomFlasher.cs");
Check(flasher.Contains("ModemLinePolicy.Decide(", StringComparison.Ordinal), "VcomFlasher asks ModemLinePolicy for the mode");
Check(flasher.Contains("ModemLinePolicy.ProbeDriver(", StringComparison.Ordinal), "VcomFlasher probes the driver through the policy");
Check(!flasher.Contains("IsPortUsingDriver", StringComparison.Ordinal),
    "the 'driver name == option' shortcut is gone from VcomFlasher");
Check(flasher.Contains("\"port.modem-lines\"", StringComparison.Ordinal) || flasher.Contains("port.modem-lines", StringComparison.Ordinal),
    "the chosen path is written to the session log");
Check(flasher.Contains("modemLinesOrigin", StringComparison.Ordinal) && flasher.Contains("modemLinesVerified", StringComparison.Ordinal),
    "the port.open event carries the origin and whether the path is verified");
Check(flasher.Contains("ModemLineDecision? ModemLines", StringComparison.Ordinal),
    "the flasher exposes the decision it took");
Check(flasher.Contains("OriginCapabilityProbe", StringComparison.Ordinal),
    "the unverified clearing attempt keeps an explicit fallback branch");

string doctor = Read("Cli/Commands/ReadCommands.cs");
Check(doctor.Contains("vcom modem lines:", StringComparison.Ordinal) && doctor.Contains("modemLines = new", StringComparison.Ordinal),
    "doctor reports the modem-line path (text and JSON)");

string cli = Read("Cli/Commands/VcomUnlockCommand.cs");
Check(cli.Contains("modemLines = new", StringComparison.Ordinal) && cli.Contains("modemLines.Describe()", StringComparison.Ordinal),
    "the vcom-unlock plan carries the modem-line decision (JSON and text)");

Console.WriteLine();
Console.WriteLine("checks=" + checks);
Console.WriteLine("ModemLinePolicy ALL PASS (" + checks + " checks)");
