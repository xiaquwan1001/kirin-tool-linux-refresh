// Hardware-free assertions for debt D-25 (audit S23/S25/S28/S31/S33): "failed", "unsupported"
// and "timeout" must be distinguishable, at every read site the audit named.
//
// Everything here is a pure classifier or a real-but-local file (temp dir, temp file): no adb, no
// fastboot, no USB, no device.
//
// Run from the repository root:  dotnet run --project tests/CommandOutcomes -c Release

using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Verdict;
using Kirin_Tool.Utils;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// ---------------------------------------------------------------- 1. the outcome type itself
{
    var ok = ReadOutcome<string>.Ok("ELS-AN00");
    Check(ok.Succeeded && ok.Value == "ELS-AN00" && ok.Describe() == "ok", "Ok carries the value and describes as ok");

    var unsupported = ReadOutcome<string>.Unsupported("getvar foo is not answerable");
    Check(unsupported.IsUnsupported && !unsupported.Succeeded && unsupported.Value == null,
        "Unsupported is its own kind and carries no value");
    Check(unsupported.Describe().StartsWith("unsupported: ", StringComparison.Ordinal) && unsupported.Describe().Contains("not answerable"),
        "Unsupported describes its reason (a null used to carry none)");

    var timeout = ReadOutcome<string>.Timeout("getvar foo timed out: 300s");
    var failed = ReadOutcome<string>.Failed("exit 1");
    Check(timeout.IsTimeout && failed.IsFailed && !timeout.IsFailed && !failed.IsTimeout,
        "Timeout and Failed are different kinds (they were the same null)");

    ReadOutcome<string>[] all = { ok, unsupported, timeout, failed };
    Check(all.Select(outcome => outcome.Describe()).Distinct().Count() == 4, "all four kinds describe differently");

    ReadOutcome<bool> carried = timeout.CastFailure<bool>();
    Check(carried.IsTimeout && carried.Reason == timeout.Reason, "CastFailure keeps kind and reason for a different payload");
}

// ---------------------------------------------------------------- 2. exception mapping
{
    var timedOut = ReadOutcomes.FromException<string>(new TimeoutException("Process timed out after 300 seconds"), "getvar product");
    Check(timedOut.IsTimeout && timedOut.Describe().Contains("getvar product") && timedOut.Describe().Contains("timed out"),
        "TimeoutException maps to Timeout with the command name in the reason");

    var cancelled = ReadOutcomes.FromException<string>(new OperationCanceledException("window closed"), "getvar product");
    Check(cancelled.IsFailed && cancelled.Describe().Contains("cancelled"),
        "a host cancel is Failed-with-reason, not a timeout and not a value");

    var broken = ReadOutcomes.FromException<string>(new IOException("no such executable"), "getvar product");
    Check(broken.IsFailed && broken.Describe().Contains("IOException") && broken.Describe().Contains("no such executable"),
        "any other exception is Failed and keeps the exception type + message");
}

// ---------------------------------------------------------------- 3. getvar reply classification
{
    Check(ReadOutcomes.ClassifyVariableReply(true, "(bootloader) product: ELS-AN00\nOKAY", "") == ReadOutcomeKind.Ok,
        "an answered getvar is Ok");
    Check(ReadOutcomes.ClassifyVariableReply(false, "", "FAILED (remote: 'unknown variable')") == ReadOutcomeKind.Unsupported,
        "an unknown variable is Unsupported, not a failure of the tool");
    Check(ReadOutcomes.ClassifyVariableReply(false, "FAILED (remote: 'not support')", "") == ReadOutcomeKind.Unsupported,
        "'not support' is Unsupported even though it also reads as a failure token");
    Check(ReadOutcomes.ClassifyVariableReply(false, "invalid command", "") == ReadOutcomeKind.Unsupported,
        "'invalid command' is Unsupported");
    Check(ReadOutcomes.ClassifyVariableReply(false, "", "FAILED (remote: 'flash write failure')") == ReadOutcomeKind.Failed,
        "a real failure reply is Failed, not Unsupported");
    Check(ReadOutcomes.ClassifyVariableReply(false, "something odd", "") == ReadOutcomeKind.Failed,
        "a non-zero exit with no token is Failed");
    Check(ReadOutcomes.ClassifyVariableReply(true, "OKAY", "fastboot: error: could not open device") == ReadOutcomeKind.Failed,
        "a failure token on stderr wins over an OKAY on stdout (D-10 rule kept)");
    Check(!ReadOutcomes.LooksUnsupported("(bootloader) product: ELS-AN00"), "a normal answer is not read as unsupported");
}

// ---------------------------------------------------------------- 4. the connection probe (S25)
{
    ProcessResult Devices(string stdout, int exit = 0, string stderr = "") =>
        new() { ExitCode = exit, Output = stdout + stderr, StandardOutput = stdout, ErrorOutput = stderr, Arguments = "devices" };

    var device = ConnectionProbe.Classify(Devices("TEST\tfastboot\n"), null, null);
    Check(device.Succeeded && device.Value, "a listed device is Ok(true) — the old bool is preserved exactly here");

    var none = ConnectionProbe.Classify(Devices(""), null, null);
    Check(none.Succeeded && !none.Value, "an empty device table is Ok(false): the probe ran and found nothing");

    var other = ConnectionProbe.Classify(Devices("OTHER\tfastboot\n"), null, null);
    Check(other.Succeeded && other.Value,
        "without a pin any fastboot device counts (upstream semantics kept, and still Ok not Failed)");

    var pinned = ConnectionProbe.Classify(Devices("TEST\tfastboot\n"), null, "ELS123");
    Check(pinned.Succeeded && !pinned.Value, "a pinned serial that is not listed is Ok(false) (K7 rule kept)");
    Check(ConnectionProbe.Classify(Devices("ELS123\tfastboot\n"), null, "ELS123").Value,
        "the pinned serial is recognised");

    var broken = ConnectionProbe.Classify(Devices("", 1, "fastboot: error: could not open /dev/bus/usb/001/002"), null, null);
    Check(broken.IsFailed, "a non-zero 'fastboot devices' is Failed, not 'no device'");
    Check(broken.Describe().Contains("could not open"), "the failed probe keeps the tool's own words");
    Check(ConnectionProbe.StatusToken(broken.Kind) == "probe-failed", "the failed probe has its own status token");

    var unsupported = ConnectionProbe.Classify(Devices("FAILED (remote: unknown command)", 1), null, null);
    Check(unsupported.IsUnsupported && ConnectionProbe.StatusToken(unsupported.Kind) == "probe-unsupported",
        "a client without 'devices' is Unsupported with its own token");

    var timedOut = ConnectionProbe.Classify(null, new TimeoutException("Process timed out after 300 seconds"), null);
    Check(timedOut.IsTimeout && ConnectionProbe.StatusToken(timedOut.Kind) == "probe-timeout",
        "a probe timeout is Timeout with its own token");

    Check(ConnectionProbe.Classify(null, null, null).IsFailed, "no result and no exception is still Failed, never silence");
    Check(ConnectionProbe.StatusToken(ReadOutcomeKind.Ok) == "ok", "the Ok token is unchanged");
    Check(ConnectionProbe.ParseDevices("A\tfastboot\nB\tfastboot\n", null), "multi-line tables parse");
    Check(!ConnectionProbe.ParseDevices("A\tfastboot\r\n", "B"), "the pinned check is per column, not substring");
}

// ---------------------------------------------------------------- 5. diagnostic log (S23)
{
    var order = new List<string>();
    var written = DiagnosticLogWriter.TryWrite(
        new[] { "/exe/log", "/state/log" }, "kirintool_log_x.log", "body",
        path => { order.Add(path); if (path.StartsWith("/exe", StringComparison.Ordinal)) throw new IOException("read-only"); });
    Check(written.Succeeded && written.Value == Path.Combine("/state/log", "kirintool_log_x.log"),
        "a read-only primary directory falls through to the XDG directory and says where it wrote");
    Check(order.Count == 2 && order[0].StartsWith("/exe", StringComparison.Ordinal),
        "candidates are tried in order (executable directory first)");

    var lost = DiagnosticLogWriter.TryWrite(
        new[] { "/a", "/b" }, "log.txt", "body",
        path => throw new UnauthorizedAccessException("denied " + path));
    Check(lost.IsFailed, "when every candidate fails the outcome is Failed, not a dropped write");
    Check(lost.Describe().Contains("/a") && lost.Describe().Contains("/b") && lost.Describe().Contains("UnauthorizedAccessException"),
        "the failure names every candidate and why each one failed");

    Check(DiagnosticLogWriter.TryWrite(Array.Empty<string>(), "log.txt", "body", _ => { }).IsFailed,
        "no candidate at all is Failed, not a silent success");

    // The real writer: it must run, and it must record its own outcome.
    ProcessRunner.WriteDiagnostic("CommandOutcomes", "self-test entry");
    Check(ProcessRunner.LastDiagnosticWrite.Succeeded,
        "WriteDiagnostic records where the entry went (LastDiagnosticWrite): " + ProcessRunner.LastDiagnosticWrite.Describe());
}

// ---------------------------------------------------------------- 6. sysfs attribute reads (S31)
{
    string temp = Path.Combine(Path.GetTempPath(), "kirin-commandoutcomes-sysfs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        string attribute = Path.Combine(temp, "idVendor");
        File.WriteAllText(attribute, "  12d1\n");
        var read = LinuxSerialPortFinder.TryReadAttribute(attribute);
        Check(read.Succeeded && read.Value == "12d1", "an existing sysfs attribute is Ok and trimmed");

        var absent = LinuxSerialPortFinder.TryReadAttribute(Path.Combine(temp, "idProduct"));
        Check(absent.IsUnsupported && absent.Describe().Contains("absent"),
            "a missing attribute is Unsupported ('this device does not have it'), not a read failure");

        File.WriteAllText(Path.Combine(temp, "empty"), "   \n");
        Check(LinuxSerialPortFinder.TryReadAttribute(Path.Combine(temp, "empty")).IsUnsupported,
            "an empty attribute is Unsupported (no value), not Ok with an empty string");

        var failed = LinuxSerialPortFinder.TryReadAttribute(attribute, _ => throw new UnauthorizedAccessException("EACCES"));
        Check(failed.IsFailed && failed.Describe().Contains("UnauthorizedAccessException"),
            "a read that throws is Failed with the reason — the exact case that used to look like 'port not found'");
    }
    finally
    {
        try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// ---------------------------------------------------------------- 7. settings recovery (S33)
{
    string configDir = Path.Combine(Path.GetTempPath(), "kirin-commandoutcomes-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(configDir);
    try
    {
        string settingsPath = Path.Combine(configDir, "settings.json");

        // 7a. no file: defaults, and the outcome says so by naming the path.
        UserSettings.Initialize(configDir);
        Check(UserSettings.LastLoadOutcome.Succeeded && UserSettings.LastLoadOutcome.Describe() == "ok",
            "a missing settings file is Ok(defaults), not a failure");
        Check(UserSettings.LastLoadOutcome.Value != null && UserSettings.LastLoadOutcome.Value!.Contains("absent"),
            "the missing-file outcome says the file was absent");

        // 7b. valid file: Ok with the path.
        File.WriteAllText(settingsPath, "{\"theme\": \"dark\"}");
        UserSettings.Reload();
        Check(UserSettings.Theme == UserSettings.ThemeDark && UserSettings.LastLoadOutcome.Succeeded,
            "a valid settings file loads and reports Ok");
        Check(UserSettings.LastLoadOutcome.Value == settingsPath, "the Ok outcome names the file it read");

        // 7c. corrupt file: the quarantine still happens (unchanged behaviour) but the reason survives.
        File.WriteAllText(settingsPath, "{ this is not json");
        UserSettings.Reload();
        string bad = Path.Combine(configDir, UserSettings.CorruptFileName);
        Check(File.Exists(bad), "a corrupt settings file is still moved to settings.json.bad (behaviour unchanged)");
        Check(UserSettings.LastLoadOutcome.IsFailed, "the corrupt settings file is reported as Failed, not silently replaced");
        Check(UserSettings.LastLoadOutcome.Describe().Contains("could not be read") &&
              UserSettings.LastLoadOutcome.Describe().Contains("moved to") &&
              UserSettings.LastLoadOutcome.Describe().Contains("defaults"),
            "the reason names the read failure, the quarantine target and the fallback: " + UserSettings.LastLoadOutcome.Describe());
        Check(UserSettings.Theme == UserSettings.ThemeLight, "defaults are used after the quarantine (unchanged behaviour)");

        // 7d. the write side is reported too, so "my settings did not stick" has an answer.
        UserSettings.Update(data => data.CpuModel = "kirin9000");
        Check(UserSettings.LastSaveOutcome.Succeeded && UserSettings.LastSaveOutcome.Value == settingsPath,
            "a successful save reports Ok with the path");
    }
    finally
    {
        try { Directory.Delete(configDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// ---------------------------------------------------------------- 8. no call site went back to a bare bool/null
{
    string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
    string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

    string fastboot = Read("Services/FastbootClient.cs");
    Check(fastboot.Contains("ProbeConnectionAsync") && fastboot.Contains("ReadOutcome<bool>"),
        "FastbootClient exposes the probe verdict, not only the bool");
    Check(fastboot.Contains("ConnectionProbe.Classify(result, null, DeviceSerial.Resolve())"),
        "the probe goes through the pure classifier (so it cannot silently return false again)");

    string commandSupport = Read("Cli/CommandSupport.cs");
    Check(commandSupport.Contains("TryReadOutcomeAsync") && commandSupport.Contains("ReadOutcomes.ClassifyVariableReply"),
        "DeviceVars keeps the getvar verdict");
    Check(!commandSupport.Contains("catch (Exception)\n        {\n            connected = false;"),
        "FastbootSession no longer flattens a probe exception into Connected=false");

    string readCommands = Read("Cli/Commands/ReadCommands.cs");
    Check(readCommands.Contains("ConnectionProbe.StatusToken"),
        "info/doctor print the probe token instead of a single 'not-connected'");
    Check(readCommands.Contains("\"probe-failed\" or \"probe-timeout\" => ExitCodes.Usage"),
        "info exits 1 (not 2/no-device) when the probe itself could not run");

    string processRunner = Read("Utils/ProcessRunner.cs");
    Check(processRunner.Contains("DiagnosticLogWriter.TryWrite") && !processRunner.Contains("catch {}\n        }\n    }\n}"),
        "the diagnostic log write is recorded instead of ending in a blanket catch");

    string serialFinder = Read("Utils/LinuxSerialPortFinder.cs");
    Check(serialFinder.Contains("TryReadAttribute") && serialFinder.Contains("ReadOutcome<string>"),
        "sysfs attribute reads return an outcome");
}

Console.WriteLine("PASS " + checks + " checks");
return 0;
