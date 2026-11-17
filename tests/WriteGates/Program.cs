// Hardware-free gate checks for the write commands: even a fully acknowledged plan
// (--yes / --json) must exit 4 with applied=false, and flash-xml must refuse a
// package with missing images (exit 5) before the redline gate is even reached.
//
// Run from the repository root:  dotnet run --project tests/WriteGates -c Release
//
// Unlike tests/FlashPolicy a missing CLI binary is a hard failure here: this
// harness is meaningless without it and CI always builds KirinTool.Cli first.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// Newest build wins: a stale Release binary from an older checkout would assert old behaviour.
string? cli = new[] {
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli.dll"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli.dll"),
}.Where(File.Exists).OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault()
  ?? throw new Exception(
    "FAIL cli binary not found; build it first: dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release");

// Extra environment handed to the CLI process: the stub "fastboot" (KIRIN_FASTBOOT) and the
// behaviour the stub should imitate (STUB_MODE). Set per case, cleared after it.
var stubEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);

(int Code, string Out, string Err) Run(params string[] argv)
{
    bool dll = cli.EndsWith(".dll", StringComparison.Ordinal);
    var psi = new ProcessStartInfo(dll ? "dotnet" : cli)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = root,
    };
    if (dll) psi.ArgumentList.Add(cli);
    foreach (string a in argv) psi.ArgumentList.Add(a);
    foreach (var pair in stubEnvironment) psi.Environment[pair.Key] = pair.Value;
    using Process process = Process.Start(psi)!;
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
}

// Assert the shared "confirmed but nothing written" invariant of the gated write
// commands: exit 4, applied=false, and a machine-readable gate string that says
// the write path did not run ("plan-only" before --yes, "confirmed" after it —
// the write path itself is not implemented in this build).
void CheckGatedPlan(string json, string label)
{
    using JsonDocument doc = JsonDocument.Parse(json);
    Check(doc.RootElement.GetProperty("exitCode").GetInt32() == 4, label + ": exit 4");
    Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, label + ": applied=false");
    string gate = doc.RootElement.GetProperty("gate").GetString() ?? "";
    Check(gate is "plan-only" or "confirmed", label + ": gate is a no-write gate (got '" + gate + "')");
}

string work = Path.Combine(Path.GetTempPath(), "kirin-writegates-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    // ---- 1. vcom-unlock: plan-only without --yes, and a dry run that never touches the port ----
    // The command itself is a REAL write now (CliWriteKind.RealWrite), so the gate is asserted
    // with --dry-run: exit 4, gate=plan-only, and no device access. That keeps this harness
    // hardware-free even when a real VCOM device happens to be attached.
    {
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--json");
        Check(code == 4, "vcom-unlock without --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "vcom-unlock");

        (code, stdout, stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--dry-run", "--json");
        Check(code == 4, "vcom-unlock --dry-run exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "vcom-unlock --dry-run");
    }

    // ---- 2. oeminfo: acknowledged rewrite of a synthetic OEMINFO image ----
    // One 0x400 block: "OEM_INFO" magic, version 6, model entry id 0x5b (a target id
    // of OemInfoEditor's version-6 map) so the local in-memory conversion succeeds.
    {
        byte[] oeminfo = new byte[0x400];
        Encoding.ASCII.GetBytes("OEM_INFO").CopyTo(oeminfo, 0);
        BitConverter.GetBytes(6).CopyTo(oeminfo, 8);
        BitConverter.GetBytes(0x5b).CopyTo(oeminfo, 12);
        byte[] model = Encoding.ASCII.GetBytes("ELS-AN00");
        BitConverter.GetBytes(model.Length).CopyTo(oeminfo, 20);
        model.CopyTo(oeminfo, 0x200);
        string input = Path.Combine(work, "oeminfo.img");
        File.WriteAllBytes(input, oeminfo);

        (int code, string stdout, string stderr) = Run("oeminfo", "--model", "ELS-TN00", "--region", "cn",
            "--input", input, "--yes", "--json");
        Check(code == 4, "oeminfo --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "oeminfo");
    }

    // ---- 3. downgrade-enable: acknowledged version-field erase plan ----
    {
        (int code, string stdout, string stderr) = Run("downgrade-enable", "--yes", "--json");
        Check(code == 4, "downgrade-enable --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "downgrade-enable");
    }

    // ---- 4. flash-xml: missing images abort (exit 5) before the redline gate ----
    // Both images are missing on purpose and one identifier is redline: the run must
    // fail with the missing-image refusal, never reach the redline refusal (exit 4).
    {
        string xmlPath = Path.Combine(work, "flashing.xml");
        File.WriteAllText(xmlPath,
            "<flashing>\n  <fastbootimage>\n" +
            "    <image name=\"boot\" identifier=\"boot\">BOOT.img</image>\n" +
            "    <image name=\"oeminfo\" identifier=\"oeminfo\">OEMINFO.img</image>\n" +
            "  </fastbootimage>\n</flashing>\n");

        (int code, string stdout, string stderr) = Run("flash-xml", xmlPath, "--json");
        Check(code == 5, "flash-xml with missing images exits 5 (got " + code + ")");
        Check(stderr.Contains("missing"), "flash-xml refusal names the missing images: " + stderr.Trim());
        Check(!stderr.Contains("redline"), "flash-xml fails on missing images before the redline gate");

        using JsonDocument doc = JsonDocument.Parse(stdout);
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "flash-xml: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "refused", "flash-xml: gate=refused");
        Check(doc.RootElement.GetProperty("reason").GetString()!.Contains("missing"), "flash-xml: refusal reason names the missing images");
        Check(doc.RootElement.GetProperty("exitCode").GetInt32() == 5, "flash-xml: JSON exitCode matches the process exit code");
        Check(doc.RootElement.GetProperty("missingImages").GetArrayLength() == 2, "flash-xml: both missing images reported");
    }
    // ---- 5. oeminfo-flash: the device write half of oeminfo needs the redline gate ----
    // The CLI `oeminfo` command stays plan-only (exit 4); writing the converted image is a
    // separate capability (D-01) and is refused without the redline override + typed phrase.
    {
        string image = Path.Combine(work, "converted-oeminfo.img");
        File.WriteAllBytes(image, new byte[0x400]);

        (int code, string stdout, string stderr) = Run("oeminfo-flash", "--file", image, "--yes", "--json");
        Check(code == 4, "oeminfo-flash without --allow-redline exits 4 (got " + code + "): " + stderr.Trim());
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "oeminfo-flash refusal: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "refused", "oeminfo-flash refusal: gate=refused");
        Check(doc.RootElement.GetProperty("reason").GetString()!.Contains("redline"), "oeminfo-flash refusal: reason names the redline rule");

        (code, stdout, stderr) = Run("oeminfo-flash", "--file", image, "--yes", "--allow-redline", "--json");
        Check(code == 4, "oeminfo-flash with a non-interactive stdin still exits 4 (got " + code + ")");
        Check(stderr.Contains("confirm phrase"), "oeminfo-flash refuses because the typed phrase cannot be given here");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

// ---------------------------------------------------------------- 5. D-02: the JSON verdict
// The stub "fastboot" answers what the case needs, so the assertions are about the code under
// test and not about a physical device: a failing loader must never produce applied:true.
string stubDir = Path.Combine(Path.GetTempPath(), "kirin-writegates-stub-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stubDir);
string stub = Path.Combine(stubDir, "stub-fastboot");
File.WriteAllText(stub, """
#!/usr/bin/env python3
import os, pathlib, sys
mode = os.environ.get("STUB_MODE", "ok")
state = pathlib.Path(os.path.abspath(__file__)).with_suffix(".state")
args = sys.argv[1:]
log = os.environ.get("STUB_LOG")
if log:
    with open(log, "a") as handle:
        handle.write(" ".join(args) + "\n")
def answer(text, code=0):
    print(text)
    sys.exit(code)
if args[:1] == ["devices"]:
    answer("TEST fastboot")
if mode == "silent-device" and args[:1] in (["getvar"], ["oem"]):
    # D-08: connected, but every probe is rejected — the "connected but knows nothing" shape.
    answer("FAILED (remote: command not allowed)", 1)
if args[:1] == ["reboot"]:
    answer("FAILED (remote: reboot not allowed)", 1) if mode == "reboot-fail" else answer("OKAY")
if args[:1] == ["flash"]:
    answer("FAILED (remote: flash write failure)", 1) if mode in ("flash-fail", "flash-all-fail") else answer("OKAY")
if args[:1] == ["oem"] and len(args) > 1 and args[1].startswith("oeminfoerase-"):
    answer("FAILED (remote: erase not allowed)", 1) if mode == "erase-fail" else answer("OKAY")
if args[:1] == ["getvar"] and len(args) > 1:
    variable = args[1]
    if variable == "product":
        # K6/R1 identity gate: a real loader answers '(bootloader) product: <model>'.
        # 'bad-product' imitates the 2026-11-15 loader, which answered the probe with a bare OKAY;
        # 'silent-product' imitates a probe that could not run (unreadable, therefore allowed).
        if mode == "bad-product":
            answer("OKAY")
        if mode == "silent-product":
            answer("")
        answer("(bootloader) product: ELS-AN00\nOKAY")
    if variable.startswith("nve:SN@"):
        if mode == "write-fail":
            answer("FAILED (remote: variable write not allowed)", 1)
        state.write_text(variable.split("@", 1)[1])
        answer("OKAY")
    if variable == "nve:SN":
        if mode == "readback-fail":
            answer("FAILED (remote: unknown variable)", 1)
        if mode == "readback-mismatch":
            answer("(bootloader) nve:SN:OLDSERIAL7\nOKAY")
        answer("(bootloader) nve:SN:" + (state.read_text() if state.exists() else "NONE") + "\nOKAY")
answer("OKAY")
""");
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
stubEnvironment["KIRIN_FASTBOOT"] = stub;

// ---- 5a. reboot: the device refuses the command ----
{
    stubEnvironment["STUB_MODE"] = "reboot-fail";
    (int code, string stdout, string stderr) = Run("reboot", "--yes", "--json");
    Check(code == 3, "reboot against a refusing loader exits 3 (got " + code + "): " + stderr.Trim());
    using JsonDocument doc = JsonDocument.Parse(stdout);
    Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "reboot failure: applied=false (no hardcoded success)");
    Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "reboot failure: gate=failed");
    Check(doc.RootElement.GetProperty("reason").GetString()!.Contains("FAILED"), "reboot failure: reason quotes the loader answer");
    Check(doc.RootElement.GetProperty("exitCode").GetInt32() == code, "reboot failure: JSON exitCode matches the process exit code");
}

// ---- 5b. reboot: the device accepts the command ----
{
    stubEnvironment["STUB_MODE"] = "ok";
    (int code, string stdout, string stderr) = Run("reboot", "--yes", "--json");
    Check(code == 0, "reboot against an accepting loader exits 0 (got " + code + "): " + stderr.Trim());
    using JsonDocument doc = JsonDocument.Parse(stdout);
    Check(doc.RootElement.GetProperty("applied").GetBoolean(), "reboot success: applied=true");
    Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "reboot success: gate=applied");
    Check(!doc.RootElement.TryGetProperty("reason", out JsonElement reason) || reason.ValueKind == JsonValueKind.Null,
        "reboot success: no reason");
}

// ---------------------------------------------------------------- 6. D-03: write-sn read-back
// The command is driven in-process because a redline write needs the confirm phrase typed on an
// interactive stdin: the harness installs an approving gate instead of faking a terminal. The
// device is still the stub above, so nothing here touches real hardware.
{
    var core = typeof(Kirin_Tool.Services.FastbootClient).Assembly;
    Type runnerType = core.GetType("Kirin_Tool.Cli.CliRunner")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliRunner is gone");
    var table = ((System.Collections.IEnumerable)runnerType
        .GetProperty("Commands", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
        .GetValue(null)!).Cast<object>().ToList();
    object command = table.Single(candidate =>
        (string)candidate.GetType().GetProperty("Name")!.GetValue(candidate)! == "write-sn");
    MethodInfo execute = command.GetType().GetMethod("ExecuteAsync")
        ?? throw new Exception("FAIL WriteSnCommand.ExecuteAsync is gone");
    Type parserType = core.GetType("Kirin_Tool.Cli.ArgParser")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.ArgParser is gone");
    MethodInfo parse = parserType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
    Type contextType = core.GetType("Kirin_Tool.Cli.CliContext")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliContext is gone");
    ConstructorInfo contextCtor = contextType.GetConstructors()
        .Single(ctor => ctor.GetParameters().Length == 5);

    async Task<(int Code, JsonDocument Json)> RunWriteSn(string mode, string value)
    {
        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        try
        {
            // In-process, so the stub reads its behaviour from this process' environment.
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", stub);
            Environment.SetEnvironmentVariable("STUB_MODE", mode);
            object parsed = parse.Invoke(null, new object[]
            {
                new[] { "--value", value, "--yes", "--allow-redline", "--json" },
                new[] { "value" },
                new[] { "yes", "allow-redline", "json" },
            })!;
            var stdout = new StringWriter();
            object context = contextCtor.Invoke(new object[] { stdout, new StringWriter(), true, true, root });
            var call = (Task<int>)execute.Invoke(command, new object[] { parsed, context, CancellationToken.None })!;
            int code = await call;
            return (code, JsonDocument.Parse(stdout.ToString()));
        }
        finally
        {
            ApprovalGates.ResetToFailClosed();
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", null);
            Environment.SetEnvironmentVariable("STUB_MODE", null);
        }
    }

    // 6a. the write command itself fails
    {
        (int code, JsonDocument doc) = await RunWriteSn("write-fail", "NEWSERIAL9");
        Check(code == 3, "write-sn with a failed write exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "write-sn failed write: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "write-sn failed write: gate=failed");
        Check(doc.RootElement.GetProperty("verified").GetBoolean() == false, "write-sn failed write: verified=false");
        Check(doc.RootElement.GetProperty("reason").GetString()!.StartsWith("command_failed", StringComparison.Ordinal),
            "write-sn failed write: reason=command_failed");
    }

    // 6b. the write is accepted but the device still reads the old value
    {
        (int code, JsonDocument doc) = await RunWriteSn("readback-mismatch", "NEWSERIAL9");
        Check(code == 3, "write-sn with a read-back mismatch exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "write-sn mismatch: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "write-sn mismatch: gate=failed");
        Check(doc.RootElement.GetProperty("verified").GetBoolean() == false, "write-sn mismatch: verified=false");
        Check(doc.RootElement.GetProperty("reason").GetString() == "readback_mismatch:OLDSERIAL7",
            "write-sn mismatch: reason names the value the device kept (got '" + doc.RootElement.GetProperty("reason").GetString() + "')");
        Check(doc.RootElement.GetProperty("readBack").GetString() == "OLDSERIAL7", "write-sn mismatch: readBack is reported");
        Check(doc.RootElement.GetProperty("expected").GetString() == "NEWSERIAL9", "write-sn mismatch: expected is reported");
    }

    // 6c. the read-back itself is unavailable: that is not a match
    {
        (int code, JsonDocument doc) = await RunWriteSn("readback-fail", "NEWSERIAL9");
        Check(code == 3, "write-sn with an unavailable read-back exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("verified").GetBoolean() == false, "write-sn unavailable read-back: verified=false");
        Check(doc.RootElement.GetProperty("reason").GetString() == "readback_unavailable",
            "write-sn unavailable read-back: reason=readback_unavailable");
    }

    // 6d. identical read-back only
    {
        (int code, JsonDocument doc) = await RunWriteSn("ok", "NEWSERIAL9");
        Check(code == 0, "write-sn with an identical read-back exits 0 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean(), "write-sn verified: applied=true");
        Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "write-sn verified: gate=applied");
        Check(doc.RootElement.GetProperty("verified").GetBoolean(), "write-sn verified: verified=true");
        Check(doc.RootElement.GetProperty("readBack").GetString() == "NEWSERIAL9", "write-sn verified: readBack equals the written value");
    }

    // 6e. K6: the loader answers getvar product with a bare OKAY (the 2026-11-15 shape).
    // Nothing may be written, and the failure must name the identity, not the command.
    {
        (int code, JsonDocument doc) = await RunWriteSn("bad-product", "NEWSERIAL9");
        Check(code == 3, "write-sn with an unidentified device exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "unidentified device: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "unidentified device: gate=failed");
        Check(doc.RootElement.GetProperty("reason").GetString() == "device_identity_refused",
            "unidentified device: reason token is device_identity_refused (got '" + doc.RootElement.GetProperty("reason").GetString() + "')");
        Check(doc.RootElement.GetProperty("result").GetProperty("error").GetString()!.Contains("did not identify itself"),
            "unidentified device: the detail says the device did not identify itself");
    }
}

// ---------------------------------------------------------------- 6f. K6 pre-write identity gate (unit half)
// The verdict is derived from the loader answer alone, so the rules are asserted without a process:
// this is the unit-test half of the gate (the end-to-end half is 6e above).
{
    Kirin_Tool.Models.ProcessResult Answer(string output, int code = 0) => new() { ExitCode = code, Output = output };

    var read = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer("(bootloader) product: ELS-AN00\nOKAY"), null);
    Check(read.Status == Kirin_Tool.Services.IdentityReading.Read && !read.Refused && read.LooksLikeKirinFamily,
        "identity gate accepts a real Kirin 990 model");
    var noh = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer("(bootloader) product: NOH-AN00\nOKAY"), null);
    Check(!noh.Refused && noh.LooksLikeKirinFamily, "identity gate accepts a Kirin 9000 model");
    var other = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer("product: SM-G991B\nOKAY"), null);
    Check(!other.Refused && !other.LooksLikeKirinFamily,
        "an unknown but plausible model is allowed (the gate is not a chip whitelist)");
    var bareOkay = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer("OKAY"), null);
    Check(bareOkay.Refused && bareOkay.Status == Kirin_Tool.Services.IdentityReading.Invalid,
        "a bare OKAY answer is not an identity: the write is refused (2026-11-15)");
    var failedToken = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer("product: FAILED (remote: 'unknown')"), null);
    Check(failedToken.Refused, "a FAILED/unknown token as product is refused");
    var failedProbe = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer("no device", 7), null);
    Check(!failedProbe.Refused && failedProbe.Status == Kirin_Tool.Services.IdentityReading.Unreadable,
        "a probe that could not run does not block the write");
    var silent = Kirin_Tool.Services.DeviceIdentityGate.Evaluate(Answer(""), null);
    Check(!silent.Refused, "an empty probe answer is unreadable, not a refusal");
    Check(Kirin_Tool.Services.DeviceIdentityGate.ParseProduct("(bootloader) product: ANA-AN00\nOKAY") == "ANA-AN00",
        "the product line is parsed with or without the (bootloader) prefix");
}
// ---------------------------------------------------------------- 7. D-13: downgrade-erase
// Enable Downgrade used to erase four oeminfo version fields straight from the GUI with no approval
// and while the registry called it PlanOnly. Two things are pinned here: the erase command reports the
// loader's answer honestly (a refused erase is never applied), and the write service refuses a ticket
// that was minted for another capability.
{
    var core = typeof(Kirin_Tool.Services.FastbootClient).Assembly;
    Type runnerType = core.GetType("Kirin_Tool.Cli.CliRunner")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliRunner is gone");
    var table = ((System.Collections.IEnumerable)runnerType
        .GetProperty("Commands", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
        .GetValue(null)!).Cast<object>().ToList();
    object command = table.Single(candidate =>
        (string)candidate.GetType().GetProperty("Name")!.GetValue(candidate)! == "downgrade-erase");
    MethodInfo execute = command.GetType().GetMethod("ExecuteAsync")
        ?? throw new Exception("FAIL DowngradeEraseCommand.ExecuteAsync is gone");
    Type parserType = core.GetType("Kirin_Tool.Cli.ArgParser")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.ArgParser is gone");
    MethodInfo parse = parserType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
    Type contextType = core.GetType("Kirin_Tool.Cli.CliContext")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliContext is gone");
    ConstructorInfo contextCtor = contextType.GetConstructors()
        .Single(ctor => ctor.GetParameters().Length == 5);

    // The erase is redline + irreversible, so its approval needs the typed phrase: the in-process
    // gate below types it exactly like a human would. Nothing here is reachable from the product.
    async Task<(int Code, JsonDocument Json)> RunDowngradeErase(string mode)
    {
        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        try
        {
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", stub);
            Environment.SetEnvironmentVariable("STUB_MODE", mode);
            object parsed = parse.Invoke(null, new object[]
            {
                new[] { "--yes", "--allow-redline", "--json" },
                Array.Empty<string>(),
                new[] { "yes", "allow-redline", "json" },
            })!;
            var stdout = new StringWriter();
            object context = contextCtor.Invoke(new object[] { stdout, new StringWriter(), true, true, root });
            var call = (Task<int>)execute.Invoke(command, new object[] { parsed, context, CancellationToken.None })!;
            int code = await call;
            return (code, JsonDocument.Parse(stdout.ToString()));
        }
        finally
        {
            ApprovalGates.ResetToFailClosed();
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", null);
            Environment.SetEnvironmentVariable("STUB_MODE", null);
        }
    }

    // 7a. the loader refuses every version-field erase
    {
        (int code, JsonDocument doc) = await RunDowngradeErase("erase-fail");
        Check(code == 3, "downgrade-erase against a refusing loader exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "downgrade-erase failure: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "downgrade-erase failure: gate=failed");
        JsonElement failureResult = doc.RootElement.GetProperty("result");
        Check(failureResult.GetProperty("successfulSteps").GetInt32() == 0, "downgrade-erase failure: 0/4 steps succeeded");
        Check(failureResult.GetProperty("verdictSource").GetString() == "step-outcomes:0/4-okay",
            "downgrade-erase failure: verdictSource names the rule the verdict came from");
    }

    // 7b. all four erases answer OKAY
    {
        (int code, JsonDocument doc) = await RunDowngradeErase("ok");
        Check(code == 0, "downgrade-erase against an accepting loader exits 0 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean(), "downgrade-erase success: applied=true");
        Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "downgrade-erase success: gate=applied");
        Check(doc.RootElement.GetProperty("result").GetProperty("successfulSteps").GetInt32() == 4,
            "downgrade-erase success: 4/4 steps succeeded");
    }

    // 7c. no ticket, or a ticket minted for another capability, never reaches the erase
    {
        var ops = new Kirin_Tool.Services.WriteOperations.DeviceWriteOps(new Kirin_Tool.Services.FastbootClient(stub));
        bool noTicket = false;
        try { await ops.EnableDowngradeAsync(null!); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("downgrade-erase", StringComparison.Ordinal)) { noTicket = true; }
        Check(noTicket, "DeviceWriteOps.EnableDowngradeAsync: without an approved ticket it throws instead of erasing");

        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        try
        {
            ApprovalAdmission admission = await ApprovalGuard.AdmitAsync(
                ApprovalRequests.For("reboot", argv: new[] { "fastboot", "reboot" }, targets: Array.Empty<string>(),
                    sessionId: "downgrade-ticket-" + Guid.NewGuid().ToString("n")), CancellationToken.None);
            bool wrongTicket = false;
            try { await ops.EnableDowngradeAsync(admission.RequireTicket()); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("cannot authorize", StringComparison.Ordinal)) { wrongTicket = true; }
            Check(wrongTicket, "DeviceWriteOps.EnableDowngradeAsync: a 'reboot' ticket cannot authorize the erase");
        }
        finally { ApprovalGates.ResetToFailClosed(); }
    }
}

// ---------------------------------------------------------------- 9. explicit -s <SN>
// With a pinned serial the spawned command line itself must carry it: the environment variable alone
// keeps fastboot from picking another phone, but the op log (argv) would not say which one was hit.
{
    string pinnedStub = Path.Combine(stubDir, "pinned-fastboot");
    File.WriteAllText(pinnedStub, "#!/usr/bin/env python3\nimport sys, json\nprint(json.dumps(sys.argv[1:]))\n");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pinnedStub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    string logRoot = Path.Combine(stubDir, "oplog");
    Directory.CreateDirectory(logRoot);
    string? oldLogRoot = Environment.GetEnvironmentVariable("KIRIN_LOG_DIR");
    Environment.SetEnvironmentVariable("KIRIN_LOG_DIR", logRoot);
    Environment.SetEnvironmentVariable("KIRIN_SERIAL", "ELS0123456789");
    try
    {
        var flash = await Kirin_Tool.Utils.ProcessRunner.RunAsync(pinnedStub, new[] { "flash", "boot", "/tmp/boot.img" }, op: "fastboot.flash");
        string[] flashArgv = JsonSerializer.Deserialize<string[]>(flash.Output.Trim())!;
        Check(flashArgv.SequenceEqual(new[] { "-s", "ELS0123456789", "flash", "boot", "/tmp/boot.img" }),
            "a pinned serial is written on the command line (-s before the subcommand)");

        var listing = await Kirin_Tool.Utils.ProcessRunner.RunAsync(pinnedStub, "devices", op: "fastboot.devices");
        string[] listArgv = JsonSerializer.Deserialize<string[]>(listing.Output.Trim())!;
        Check(listArgv.SequenceEqual(new[] { "devices" }), "device listing stays unpinned so every device is visible");

        var derive = await Kirin_Tool.Utils.ProcessRunner.RunAsync(pinnedStub, new[] { "getvar", "product", "--quiet" });
        string[] deriveArgv = JsonSerializer.Deserialize<string[]>(derive.Output.Trim())!;
        Check(deriveArgv[0] == "-s" && deriveArgv.Contains("--quiet"), "the pinned serial and the caller's argv both reach the child");
        var deriveOp = typeof(Kirin_Tool.Utils.ProcessRunner)
            .GetMethod("DeriveOp", BindingFlags.NonPublic | BindingFlags.Static)!;
        string opName = (string)deriveOp.Invoke(null, new object[] { "fastboot", new[] { "-s", "ELS0123456789", "getvar", "product" } })!;
        Check(opName == "fastboot.getvar", "the op name still comes from the subcommand, never from the injected serial (got " + opName + ")");
    }
    finally
    {
        Environment.SetEnvironmentVariable("KIRIN_SERIAL", null);
        Environment.SetEnvironmentVariable("KIRIN_LOG_DIR", oldLogRoot);
    }
}

// ---------------------------------------------------------------- 8. K7 VCOM port uniqueness
// A fake sysfs tree (same technique as tests/StableParity) stands in for the kernel: one physical
// device exposing four option interfaces must still count as ONE device, while two physical devices
// must be refused instead of silently picking "the first port".
{
    string sys = Path.Combine(Path.GetTempPath(), "kirin-port-unique-" + Guid.NewGuid().ToString("N"));
    string classTty = Path.Combine(sys, "class", "tty");
    Directory.CreateDirectory(classTty);
    string AddDevice(string usbName, params string[] ttys)
    {
        string usb = Path.Combine(sys, "devices", "usb", usbName);
        Directory.CreateDirectory(usb);
        File.WriteAllText(Path.Combine(usb, "idVendor"), "12d1");
        File.WriteAllText(Path.Combine(usb, "idProduct"), "3609");
        foreach (string tty in ttys)
        {
            string iface = Path.Combine(usb, usbName + ":1.0");
            string ttyDir = Path.Combine(iface, "tty", tty);
            Directory.CreateDirectory(ttyDir);
            Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), "../../..");
            Directory.CreateSymbolicLink(Path.Combine(classTty, tty),
                Path.Combine("..", "..", "devices", "usb", usbName, usbName + ":1.0", "tty", tty));
        }
        return usb;
    }

    foreach (string tty in new[] { "ttyUSB0", "ttyUSB1", "ttyUSB2", "ttyUSB3" }) AddDevice("2-1", tty);
    string[] oneDevice = { "/dev/ttyUSB0", "/dev/ttyUSB1", "/dev/ttyUSB2", "/dev/ttyUSB3" };
    Check(Kirin_Tool.Utils.LinuxSerialPortFinder.FindPort(0x12d1, 0x3609, ttyClassRoot: classTty, ports: oneDevice) == "/dev/ttyUSB0",
        "four ttys of one physical device still resolve to one port (GUI behaviour unchanged)");
    Check(Kirin_Tool.Utils.LinuxSerialPortFinder.FindPort(0x12d1, 0x9999, ttyClassRoot: classTty, ports: oneDevice) == null,
        "a different product id matches no port (unchanged filter)");

    AddDevice("2-2", "ttyUSB4");
    string[] twoDevices = oneDevice.Concat(new[] { "/dev/ttyUSB4" }).ToArray();
    bool refused = false;
    try
    {
        Kirin_Tool.Utils.LinuxSerialPortFinder.FindPort(0x12d1, 0x3609, ttyClassRoot: classTty, ports: twoDevices);
    }
    catch (Kirin_Tool.Utils.AmbiguousSerialDeviceException ex)
    {
        refused = ex.Message.Contains("--port") && ex.Message.Contains("ttyUSB0") && ex.Message.Contains("ttyUSB4");
    }
    Check(refused, "two physical devices with the same VID/PID are refused with both named and --port suggested");
    try { Directory.Delete(sys, recursive: true); } catch { }
}

// ---------------------------------------------------------------- 10. R1 read-side identity gate
// `oem dump-emmc`/`oem dump-storage`/`fetch` write host files and have already been sent to the
// wrong phone (2026-11-15). They are judged by the same getvar product probe as a write, but only
// an *invalid* answer blocks them: a probe that could not run stays unreadable and allowed.
{
    string callLog = Path.Combine(stubDir, "calls.log");
    Environment.SetEnvironmentVariable("STUB_LOG", callLog);
    try
    {
        // 10a. the loader answers a bare OKAY: no dump command may be launched.
        Environment.SetEnvironmentVariable("STUB_MODE", "bad-product");
        var refusedClient = new Kirin_Tool.Services.FastbootClient(stub);
        File.Delete(callLog);
        bool dumpRefused = false;
        try { await refusedClient.OemCommandAsync("dump-emmc ptable \"" + Path.Combine(stubDir, "ptable.img") + "\""); }
        catch (Kirin_Tool.Services.DeviceIdentityGate.DeviceIdentityRefusedException) { dumpRefused = true; }
        Check(dumpRefused, "dump-emmc against an unidentified loader is refused");
        string[] dumpCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        Check(!dumpCalls.Any(line => line.Contains("dump-emmc")),
            "an invalid identity sends zero dump commands (calls: " + string.Join(" | ", dumpCalls) + ")");
        Check(dumpCalls.Any(line => line.Contains("getvar") && line.Contains("product")),
            "the read gate still asks the device who it is before deciding");

        // 10b. the raw `fetch` path is gated by the same verdict.
        File.Delete(callLog);
        bool fetchRefused = false;
        try { await refusedClient.CommandAsync("fetch ptable \"" + Path.Combine(stubDir, "ptable2.img") + "\""); }
        catch (Kirin_Tool.Services.DeviceIdentityGate.DeviceIdentityRefusedException) { fetchRefused = true; }
        Check(fetchRefused, "fetch against an unidentified loader is refused");
        string[] fetchCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        Check(!fetchCalls.Any(line => line.Contains("fetch")),
            "an invalid identity sends zero fetch commands (calls: " + string.Join(" | ", fetchCalls) + ")");

        // 10c. a probe that could not run is unreadable, not a refusal: the dump proceeds (stub).
        Environment.SetEnvironmentVariable("STUB_MODE", "silent-product");
        var silentClient = new Kirin_Tool.Services.FastbootClient(stub);
        File.Delete(callLog);
        string silentOutput = await silentClient.OemCommandAsync("dump-emmc ptable \"" + Path.Combine(stubDir, "silent.img") + "\"");
        string[] silentCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        Check(silentCalls.Any(line => line.Contains("dump-emmc")),
            "an unreadable probe does not block the dump (no device/stub scenario, calls: " + string.Join(" | ", silentCalls) + ")");
        Check(silentOutput.Trim().Length > 0, "the unreadable-probe dump still reports the loader answer");

        // 10d. a real product name keeps the dump allowed.
        Environment.SetEnvironmentVariable("STUB_MODE", "ok");
        var goodClient = new Kirin_Tool.Services.FastbootClient(stub);
        File.Delete(callLog);
        await goodClient.OemCommandAsync("dump-storage nvm \"" + Path.Combine(stubDir, "nvm.img") + "\"");
        string[] goodCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        Check(goodCalls.Any(line => line.Contains("dump-storage")), "a named device still dumps (dump-storage allowed)");

        // 10e. argv list overload (audit round5 §10): the same command as tokens. The child must
        // receive exactly the argv the quoted string form produced (a host path with a space stays
        // one argument), a leading "oem" token must not be doubled, and the read gate must judge the
        // same token list the child gets.
        Environment.SetEnvironmentVariable("STUB_MODE", "ok");
        var overloadClient = new FastbootClient(stub);
        string spacedPath = Path.Combine(stubDir, "list form.img");
        File.Delete(callLog);
        await overloadClient.OemCommandAsync(new[] { "dump-emmc", "ptable", spacedPath });
        string[] listCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        File.Delete(callLog);
        await overloadClient.OemCommandAsync("dump-emmc ptable \"" + spacedPath + "\"");
        string[] stringCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        // Only the oem invocation is compared: the two clients have their own identity cache, so the
        // number of getvar probes may differ, but the command that reaches the child must not.
        string[] listOemCalls = listCalls.Where(line => line.StartsWith("oem ", StringComparison.Ordinal)).ToArray();
        string[] stringOemCalls = stringCalls.Where(line => line.StartsWith("oem ", StringComparison.Ordinal)).ToArray();
        Check(listOemCalls.Length == 1 && listOemCalls.SequenceEqual(stringOemCalls),
            "the argv list overload sends exactly the same child argv as the quoted single-string form ("
            + string.Join(" | ", listOemCalls) + " vs " + string.Join(" | ", stringOemCalls) + ")");
        Check(listCalls.Any(line => line.Contains("dump-emmc") && line.Contains("list form.img")),
            "a host path containing a space stays one argument in the list form");

        File.Delete(callLog);
        await overloadClient.OemCommandAsync(new[] { "oem", "dump-storage", "nvm", spacedPath + "2" });
        string[] prefixedCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        Check(prefixedCalls.Any(line => line.StartsWith("oem dump-storage", StringComparison.Ordinal)),
            "a leading 'oem' token is accepted and not doubled (" + string.Join(" | ", prefixedCalls) + ")");
        Check(!prefixedCalls.Any(line => line.Contains("oem oem", StringComparison.Ordinal)),
            "the child argv never carries a doubled 'oem' prefix");

        bool emptyRefused = false;
        try { await overloadClient.OemCommandAsync(Array.Empty<string>()); } catch (ArgumentException) { emptyRefused = true; }
        Check(emptyRefused, "an empty argv list is a usage error, not a bare 'fastboot oem'");
        bool bareOemRefused = false;
        try { await overloadClient.OemCommandAsync(new[] { "oem" }); } catch (ArgumentException) { bareOemRefused = true; }
        Check(bareOemRefused, "a list that is only 'oem' is a usage error, not a bare 'fastboot oem'");

        // The read gate is applied to the list form on the tokens the child will get.
        Environment.SetEnvironmentVariable("STUB_MODE", "bad-product");
        var overloadRefusedClient = new FastbootClient(stub);
        File.Delete(callLog);
        bool overloadRefused = false;
        try { await overloadRefusedClient.OemCommandAsync(new[] { "dump-emmc", "ptable", spacedPath }); }
        catch (Kirin_Tool.Services.DeviceIdentityGate.DeviceIdentityRefusedException) { overloadRefused = true; }
        Check(overloadRefused, "the argv list form is gated by the same read identity probe");
        string[] overloadRefusedCalls = File.Exists(callLog) ? File.ReadAllLines(callLog) : Array.Empty<string>();
        Check(!overloadRefusedCalls.Any(line => line.Contains("dump-emmc")),
            "an invalid identity sends zero dump commands through the list form");
    }
    finally
    {
        Environment.SetEnvironmentVariable("STUB_LOG", null);
        Environment.SetEnvironmentVariable("STUB_MODE", null);
    }
}

// ---------------------------------------------------------------- 11. R3 Windows port uniqueness
// The Windows half of K7. Windows cannot be exercised on this host, so the PnP enumeration is an
// injectable seam and the fake data below is the whole test: one physical device exposing two COM
// interfaces is ONE device, two physical devices are refused instead of taking "the first COM".
{
    Kirin_Tool.Utils.WindowsSerialDevice Device(string port, string deviceId, string? parent = null) =>
        new(port, deviceId, parent, "HUAWEI Mobile Connect (" + port + ")");

    // 11a. one phone, two COM interfaces (same composite USB device) -> still one port, as before.
    var onePhone = new Func<int, int, IReadOnlyList<Kirin_Tool.Utils.WindowsSerialDevice>>((_, _) => new[]
    {
        Device("COM3", @"USB\VID_12D1&PID_3609&MI_00\6&1a2b3c4d&0&0000"),
        Device("COM5", @"USB\VID_12D1&PID_3609&MI_02\6&1a2b3c4d&0&0002"),
    });
    Check(Kirin_Tool.Utils.WindowsSerialPortFinder.FindPort(0x12d1, 0x3609, onePhone) == "COM3",
        "two COM interfaces of one physical Windows device still resolve to one port (GUI behaviour unchanged)");

    // 11b. the same rule when the enumerator knows the parent device (WMI-agnostic seam).
    var withParents = new Func<int, int, IReadOnlyList<Kirin_Tool.Utils.WindowsSerialDevice>>((_, _) => new[]
    {
        Device("COM3", @"USB\VID_12D1&PID_3609&MI_00\6&1a2b3c4d&0&0000", @"USB\VID_12D1&PID_3609\5&1111&0&1"),
        Device("COM5", @"USB\VID_12D1&PID_3609&MI_02\6&1a2b3c4d&0&0002", @"USB\VID_12D1&PID_3609\5&1111&0&1"),
    });
    Check(Kirin_Tool.Utils.WindowsSerialPortFinder.FindPort(0x12d1, 0x3609, withParents) == "COM3",
        "a shared parent device id also counts as one device");

    // 11c. two physical phones with the same VID/PID -> refuse, naming both and --port.
    var twoPhones = new Func<int, int, IReadOnlyList<Kirin_Tool.Utils.WindowsSerialDevice>>((_, _) => new[]
    {
        Device("COM3", @"USB\VID_12D1&PID_3609&MI_00\6&1a2b3c4d&0&0000"),
        Device("COM7", @"USB\VID_12D1&PID_3609&MI_00\7&9f8e7d6c&0&0000"),
    });
    bool windowsRefused = false;
    try
    {
        Kirin_Tool.Utils.WindowsSerialPortFinder.FindPort(0x12d1, 0x3609, twoPhones);
    }
    catch (Kirin_Tool.Utils.AmbiguousSerialDeviceException ex)
    {
        windowsRefused = ex.Message.Contains("--port") && ex.Message.Contains("COM3") && ex.Message.Contains("COM7");
    }
    Check(windowsRefused, "two physical Windows devices with the same VID/PID are refused with both COM ports named and --port suggested");

    // 11d. no match / no port in the name -> null, not a guess.
    Check(Kirin_Tool.Utils.WindowsSerialPortFinder.FindPort(0x12d1, 0x3609,
            (_, _) => Array.Empty<Kirin_Tool.Utils.WindowsSerialDevice>()) == null,
        "no matching Windows device is null (unchanged: caller reports 'No device in VCOM mode')");
    Check(Kirin_Tool.Utils.WindowsSerialPortFinder.PortFromName("HUAWEI Mobile Connect (COM12)") == "COM12" &&
          Kirin_Tool.Utils.WindowsSerialPortFinder.PortFromName("USB Serial Device") == null,
        "the COM number is parsed from the PnP name, and a name without one is not a port");
}

// ---------------------------------------------------------------- 12. R4 fastboot fallback in the result
// A non-bundled fastboot (`/usr/bin/fastboot` from platform-tools, a PATH hit, or an explicit
// override) used to live only in kirintool_log_*.log. It is now a field of every JSON result
// (and therefore of the MCP tool result, which is that text verbatim).
{
    stubEnvironment["KIRIN_FASTBOOT"] = stub;
    stubEnvironment["STUB_MODE"] = "ok";
    try
    {
        (int code, string stdout, string stderr) = Run("doctor", "--json");
        // D-07: the stub is not the bundled Huawei client. "found && runs" is no longer reported as a
        // healthy environment: exit 6 (degraded, see Cli/ExitCodes.Degraded) and ok:false.
        Check(code == 6, "doctor against a non-bundled override fastboot exits 6/degraded (got " + code + "): " + stderr.Trim());
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement doctorRoot = doc.RootElement;
        Check(doctorRoot.GetProperty("ok").GetBoolean() == false,
            "doctor --json ok is false for a non-Huawei-parity fastboot (was unconditionally true before D-07)");
        Check(doctorRoot.GetProperty("fastboot").GetProperty("parity").GetString() == "unknown",
            "doctor --json reports fastboot.parity=unknown for an unverifiable override path");
        JsonElement parityCheck = doctorRoot.GetProperty("checks").EnumerateArray()
            .First(entry => entry.GetProperty("name").GetString() == "fastboot-parity");
        Check(parityCheck.GetProperty("ok").GetBoolean() == false,
            "the per-check table carries fastboot-parity with ok=false, next to fastboot-found/fastboot-runs");
        Check(parityCheck.GetProperty("detail").GetString()!.Contains("unknown"),
            "the fastboot-parity detail names the lineage token");
        Check(doctorRoot.GetProperty("degraded").EnumerateArray().Any(entry => entry.GetString() == "system-fastboot-fallback"),
            "doctor --json reports degraded:[system-fastboot-fallback]");
        Check(doctorRoot.GetProperty("fastboot").GetProperty("fallback").GetBoolean(),
            "doctor --json carries fastboot.fallback=true next to the existing found/path fields");
        Check(doctorRoot.GetProperty("fastboot").GetProperty("path").GetString() == stub,
            "doctor --json keeps the existing fastboot.path field unchanged");

        (code, stdout, stderr) = Run("reboot", "--yes", "--json");
        Check(code == 0, "reboot against the stub exits 0 (got " + code + ")");
        using JsonDocument writeDoc = JsonDocument.Parse(stdout);
        Check(writeDoc.RootElement.GetProperty("applied").GetBoolean(),
            "the write command keeps its existing applied verdict");
        Check(writeDoc.RootElement.GetProperty("degraded").EnumerateArray()
                .Any(entry => entry.GetString() == "system-fastboot-fallback"),
            "a write command's JSON carries the same degraded token (no per-command code)");
        Check(writeDoc.RootElement.GetProperty("fastbootFallback").GetBoolean(),
            "a write command names the fallback explicitly (fastbootFallback=true)");

        // The classifier itself: the bundled layout is not a degradation.
        var core = typeof(Kirin_Tool.Services.FastbootClient).Assembly;
        Type signals = core.GetType("Kirin_Tool.Cli.DegradedSignals")
            ?? throw new Exception("FAIL Kirin_Tool.Cli.DegradedSignals is gone");
        // D-07: the lineage classifier itself (path provenance is the only thing it can prove).
        Type parityType = core.GetType("Kirin_Tool.Services.FastbootParity")
            ?? throw new Exception("FAIL Kirin_Tool.Services.FastbootParity is gone");
        var classifyParity = parityType.GetMethod("Classify", BindingFlags.Public | BindingFlags.Static)!;
        string ParityOf(string? path, string? version = null) =>
            (string)classifyParity.Invoke(null, new object?[] { path, version })!;
        Check(ParityOf("/usr/bin/fastboot") == "google", "/usr/bin/fastboot is classified google (distro platform-tools)");
        Check(ParityOf("/opt/android-sdk/platform-tools/fastboot") == "google",
            "an SDK platform-tools layout is classified google");
        Check(ParityOf(Path.Combine(AppContext.BaseDirectory, "fastboot", "fastboot")) == "huawei",
            "the bundled directory is the only path that proves huawei lineage");
        Check(ParityOf("") == "unknown" && ParityOf(null) == "unknown", "an absent fastboot has no lineage (unknown)");
        Check(ParityOf(stub) == "unknown", "an arbitrary override path is unknown, not silently assumed Huawei");
        Check(ParityOf("/tmp/fastboot", "fastboot version 35.0.2 (platform-tools)") == "google",
            "a version line that says platform-tools is google even from an odd path");
        Check(new Kirin_Tool.Services.FastbootClient("/usr/bin/fastboot").Parity == "google",
            "FastbootClient exposes the lineage of the binary every one of its commands will run");

        var classify = signals.GetMethod("IsSystemFastbootFallback", BindingFlags.Public | BindingFlags.Static)!;
        Check((bool)classify.Invoke(null, new object[] { "/usr/bin/fastboot" })!,
            "/usr/bin/fastboot is classified as the system fallback");
        Check(!(bool)classify.Invoke(null, new object[] { Path.Combine(AppContext.BaseDirectory, "fastboot", "fastboot") })!,
            "the bundled fastboot directory is not a degradation");
        Check(!(bool)classify.Invoke(null, new object[] { "" })!, "an absent fastboot is not a fallback token");
    }
    finally
    {
        stubEnvironment.Clear();
    }
}

// ---------------------------------------------------------------- 13. D-08: connected vs useful
// "connected" and "the loader answered something" used to be the same verdict: a device that
// accepted the connection and rejected every getvar produced connected:true + exit 0, exactly like
// a healthy device. They are now two judgements (device.status / device.informationallyUseful) and
// two exit codes (0 vs 6).
{
    stubEnvironment["KIRIN_FASTBOOT"] = stub;
    stubEnvironment["STUB_MODE"] = "silent-device";
    try
    {
        (int code, string stdout, string stderr) = Run("info", "--json");
        Check(code == 6, "info against a connecting but silent loader exits 6/degraded (got " + code + "): " + stderr.Trim());
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement device = doc.RootElement.GetProperty("device");
        Check(device.GetProperty("connected").GetBoolean(), "info still reports connected:true for a silent loader (no lie about the link)");
        Check(device.GetProperty("status").GetString() == "connected-but-empty",
            "info distinguishes 'connected-but-empty' from 'ok' in device.status");
        Check(device.GetProperty("informationallyUseful").GetBoolean() == false,
            "info says the device is not informationally useful");
        Check(device.GetProperty("variables").EnumerateObject().Any() == false,
            "info's variables map is empty (the loader rejected every getvar)");

        (code, stdout, stderr) = Run("doctor", "--json");
        Check(code == 6, "doctor against the same silent loader exits 6/degraded (got " + code + "): " + stderr.Trim());
        using JsonDocument doctorDoc = JsonDocument.Parse(stdout);
        JsonElement doctorRoot = doctorDoc.RootElement;
        Check(doctorRoot.GetProperty("status").GetString() == "connected-but-empty",
            "doctor carries the same device-level status token");
        Check(doctorRoot.GetProperty("ok").GetBoolean() == false,
            "doctor ok is false when the device connects and answers nothing");
        Check(doctorRoot.GetProperty("device").GetProperty("informationallyUseful").GetBoolean() == false,
            "doctor device.informationallyUseful is false");
        Check(doctorRoot.GetProperty("checks").EnumerateArray()
                .First(entry => entry.GetProperty("name").GetString() == "fastboot-device")
                .GetProperty("ok").GetBoolean(),
            "the per-check table keeps the two judgements apart: fastboot-device is ok (it IS connected) while psid/ptable are not");

        // Contrast: a loader that answers product is useful, and only the lineage keeps doctor off 0.
        stubEnvironment["STUB_MODE"] = "ok";
        (code, stdout, stderr) = Run("info", "--json");
        Check(code == 0, "info against an answering loader still exits 0 (got " + code + "): " + stderr.Trim());
        using JsonDocument healthyDoc = JsonDocument.Parse(stdout);
        Check(healthyDoc.RootElement.GetProperty("device").GetProperty("status").GetString() == "ok",
            "info device.status is ok when a variable is readable");
        Check(healthyDoc.RootElement.GetProperty("device").GetProperty("informationallyUseful").GetBoolean(),
            "info device.informationallyUseful is true when a variable is readable");
        Check(healthyDoc.RootElement.GetProperty("device").GetProperty("variables").GetProperty("product").GetString() == "ELS-AN00",
            "the readable variable is still reported unchanged");

        (code, stdout, _) = Run("doctor", "--json");
        using JsonDocument healthyDoctorDoc = JsonDocument.Parse(stdout);
        Check(healthyDoctorDoc.RootElement.GetProperty("device").GetProperty("informationallyUseful").GetBoolean(),
            "doctor agrees the answering device is informationally useful");
        Check(healthyDoctorDoc.RootElement.GetProperty("status").GetString() == "degraded-fastboot-parity",
            "with a usable device the remaining status is the lineage one (the stub is not the bundled client)");
        Check(code == 6, "doctor still exits 6 for the lineage reason, not because of the device (got " + code + ")");
    }
    finally
    {
        stubEnvironment.Clear();
        stubEnvironment["KIRIN_FASTBOOT"] = stub;
    }
}

stubEnvironment.Clear();
try { Directory.Delete(stubDir, true); } catch { }

// ---------------------------------------------------------------- M2/M3/M5: artifact hardening
// These three parsers all take a file the user did not necessarily create, and all three feed a
// device write: a bad super image is flashed into 'super', a mis-written OEMINFO image into
// 'oeminfo', a flashing xml decides which host files are read. Refuse the bad input instead of
// producing a half-valid artifact (audit 2026-11-15 round3 §1.2 / §1.3).
{
    byte[] Sparse(uint totalChunks, ushort fileHeaderSize, ushort chunkHeaderSize, uint blockSize, params uint[] chunkTotalSizes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0xed26ff3au); writer.Write((ushort)1); writer.Write((ushort)0);
        writer.Write(fileHeaderSize); writer.Write(chunkHeaderSize); writer.Write(blockSize);
        writer.Write(0u); writer.Write(totalChunks); writer.Write(0u);
        foreach (uint totalSize in chunkTotalSizes)
        {
            writer.Write(0xcac1u); writer.Write((ushort)0); writer.Write(0u); writer.Write(totalSize);
        }
        return stream.ToArray();
    }

    string spoil = Path.Combine(Path.GetTempPath(), "kirin-sparse-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(spoil);
    try
    {
        byte[] good = Sparse(1, 28, 12, 4096, 12u);
        File.WriteAllBytes(Path.Combine(spoil, "good"), good);
        var failures = new (string Label, byte[] Bad)[]
        {
            ("M2: FileHeaderSize != 28 is refused", Sparse(1, 27, 12, 4096, 12u)),
            ("M2: ChunkHeaderSize != 12 is refused", Sparse(1, 28, 16, 4096, 12u)),
            ("M2: BlockSize == 0 is refused", Sparse(1, 28, 12, 0, 12u)),
            ("M2: total_chunks == 0 is refused (it used to produce 0xFFFFFFFF)", Sparse(0, 28, 12, 4096)),
            ("M2: total_chunks beyond the file is refused", Sparse(0xFFFFFFFF, 28, 12, 4096, 12u)),
            ("M2: a chunk TotalSize < 12 is refused", Sparse(1, 28, 12, 4096, 8u)),
            ("M2: a chunk that runs past EOF is refused", Sparse(1, 28, 12, 4096, 4096u)),
        };
        foreach ((string label, byte[] bad) in failures)
        {
            string badPath = Path.Combine(spoil, "bad");
            File.WriteAllBytes(badPath, bad);
            bool refused = false;
            var started = DateTime.UtcNow;
            try { await SuperMerger.MergeSuperImages(Path.Combine(spoil, "good"), badPath, Path.Combine(spoil, "merged")); }
            catch (Exception) { refused = true; }
            Check(refused, label);
            Check((DateTime.UtcNow - started).TotalSeconds < 5,
                "M2: the refusal is immediate, not a walk over the declared chunk count (" + (DateTime.UtcNow - started).TotalSeconds.ToString("0.00") + "s)");
        }

        // M2 (r7-b4, §1.2 续): the header's TotalBlocks is a claim about how many blocks the image
        // expands to, and the chunks have to add up to it. This is the same rule libsparse applies
        // when it reads an image back; the verdict is a pure function so it is checked both ways.
        byte[] SparseDeclared(uint totalBlocks, uint blockSize, params (ushort Type, uint Blocks, uint TotalSize)[] chunks)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(0xed26ff3au); writer.Write((ushort)1); writer.Write((ushort)0);
            writer.Write((ushort)28); writer.Write((ushort)12); writer.Write(blockSize);
            writer.Write(totalBlocks); writer.Write((uint)chunks.Length); writer.Write(0u);
            foreach (var chunk in chunks)
            {
                writer.Write(chunk.Type); writer.Write((ushort)0); writer.Write(chunk.Blocks); writer.Write(chunk.TotalSize);
                // Payload bytes (zeroed) so the geometry checks that run before the block-sum
                // accounting see a file whose chunks really are inside it.
                writer.Write(new byte[Math.Max(0, (int)chunk.TotalSize - 12)]);
            }
            return stream.ToArray();
        }

        var blockSumFailures = new (string Label, byte[] Bad)[]
        {
            ("M2: TotalBlocks smaller than what the chunks describe is refused",
                SparseDeclared(0, 4096, ((ushort)0xcac1, 0u, 12u + 4096u))),
            ("M2: TotalBlocks larger than what the chunks describe is refused",
                SparseDeclared(99, 4096, ((ushort)0xcac1, 0u, 12u + 4096u))),
            ("M2: a raw chunk whose payload is not a multiple of BlockSize is refused",
                SparseDeclared(0, 4096, ((ushort)0xcac1, 0u, 12u + 100u))),
        };
        foreach ((string label, byte[] bad) in blockSumFailures)
        {
            string badPath = Path.Combine(spoil, "bad-blocksum");
            File.WriteAllBytes(badPath, bad);
            bool refused = false;
            try { await SuperMerger.MergeSuperImages(Path.Combine(spoil, "good"), badPath, Path.Combine(spoil, "merged-blocksum")); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, label);
        }

        // A header that adds up still merges: the refusal must not tax well-formed images. Both
        // sides are built by SparseDeclared (12-byte chunk headers, payload present), because the
        // older local Sparse() helper above writes 14-byte chunk headers - malformed on purpose for
        // the geometry cases, but not something a valid image should look like.
        string consistentA = Path.Combine(spoil, "consistent-a");
        File.WriteAllBytes(consistentA, SparseDeclared(3, 4096,
            ((ushort)0xcac1, 0u, 12u + 4096u), ((ushort)0xcac3, 2u, 12u), ((ushort)0xcac4, 0u, 16u)));
        string consistentB = Path.Combine(spoil, "consistent-b");
        File.WriteAllBytes(consistentB, SparseDeclared(2, 4096, ((ushort)0xcac1, 0u, 12u + 8192u)));
        string consistentOut = Path.Combine(spoil, "merged-consistent");
        await SuperMerger.MergeSuperImages(consistentA, consistentB, consistentOut);
        Check(File.Exists(consistentOut) && new FileInfo(consistentOut).Length > 0,
            "M2: an image whose TotalBlocks equals its chunk accounting still merges");

        // The verdict itself (machine token `failed` / `verified`), device-free.
        var tooFew = Kirin_Tool.Services.Verdict.SparseBlockSum.Evaluate(2, 4096, new[]
        {
            new Kirin_Tool.Services.Verdict.SparseChunkFact(0xcac1, 0, 12 + 4096),
        });
        Check(tooFew.Token == "failed" && !tooFew.Consistent && tooFew.AccountedBlocks == 1,
            "M2: TotalBlocks=2 with one raw 4096-byte chunk is verdict=failed (accounted 1)");
        var addsUp = Kirin_Tool.Services.Verdict.SparseBlockSum.Evaluate(3, 4096, new[]
        {
            new Kirin_Tool.Services.Verdict.SparseChunkFact(0xcac1, 0, 12 + 4096),
            new Kirin_Tool.Services.Verdict.SparseChunkFact(0xcac3, 2, 12),
            new Kirin_Tool.Services.Verdict.SparseChunkFact(0xcac4, 0, 16),
        });
        Check(addsUp.Token == "verified" && addsUp.AccountedBlocks == 3,
            "M2: raw(1) + dontcare(2) + crc32(0) is verdict=verified for TotalBlocks=3");
        var unknownChunk = Kirin_Tool.Services.Verdict.SparseBlockSum.Evaluate(0, 4096, new[]
        {
            new Kirin_Tool.Services.Verdict.SparseChunkFact(0x1234, 0, 12),
        });
        Check(unknownChunk.Token == "failed", "M2: an unknown chunk type is verdict=failed");
    }
    finally { try { Directory.Delete(spoil, true); } catch { } }

    // M3: OEMINFO model/vendor bounds. Block = 0x400, payload at 0x200.
    byte[] OemInfoImage()
    {
        var data = new byte[2 * 0x400];
        for (int block = 0; block < 2; block++)
        {
            Encoding.ASCII.GetBytes("OEM_INFO").CopyTo(data, block * 0x400);
            BitConverter.GetBytes(6).CopyTo(data, block * 0x400 + 8);
            BitConverter.GetBytes(block == 0 ? 0x5b : 0x12).CopyTo(data, block * 0x400 + 12);
        }
        return data;
    }

    var editor = new Kirin_Tool.Services.OemInfoEditor(OemInfoImage());
    byte[] edited = editor.EditEntries("NOH-AN00", "hw-cn");
    Check(BitConverter.ToInt32(edited, 20) == 8 && Encoding.ASCII.GetString(edited, 0x200, 8) == "NOH-AN00",
        "M3: a normal model is written with its length prefix into the first block");
    Check(BitConverter.ToInt32(edited, 0x400 + 20) == 5 && Encoding.ASCII.GetString(edited, 0x400 + 0x200, 5) == "hw-cn",
        "M3: a normal vendor value lands in its own block");
    Check(edited[^1] == 0xFF, "M3: the block padding is still filled with 0xFF");

    foreach ((string value, string label) in new[]
    {
        (new string('A', 65), "M3: a 65-character model is refused"),
        ("", "M3: an empty model is refused"),
        ("bad\u0001model", "M3: a control character in the model is refused"),
    })
    {
        bool refused = false;
        try { editor.EditEntries(value, "hw-cn"); } catch (InvalidDataException) { refused = true; }
        Check(refused, label);
    }
    bool vendorRefused = false;
    try { editor.EditEntries("NOH-AN00", new string('B', 65)); } catch (InvalidDataException) { vendorRefused = true; }
    Check(vendorRefused, "M3: a 65-character vendor value is refused before anything is written");

    // The block-level guard itself: a payload longer than the 512-byte block field must throw
    // instead of overwriting the next block's OEM_INFO header.
    var updateEntry = typeof(Kirin_Tool.Services.OemInfoEditor).GetMethod("UpdateEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
    byte[] before = OemInfoImage();
    bool overflowRefused = false;
    try { updateEntry.Invoke(editor, new object[] { before, 0, new string('C', 0x201) }); }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { overflowRefused = true; }
    Check(overflowRefused, "M3: a payload larger than one block's payload area is refused");
    Check(Enumerable.Range(0, 8).All(i => before[0x400 + i] == (byte)"OEM_INFO"[i]),
        "M3: the next block's magic is untouched after the refused write");

    // M5: flashing xml — no DTD, and image paths stay inside the package directory.
    string xmlDir = Path.Combine(Path.GetTempPath(), "kirin-flashxml-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(xmlDir, "fastbootimage"));
    try
    {
        File.WriteAllBytes(Path.Combine(xmlDir, "fastbootimage", "BOOT.img"), new byte[] { 1, 2, 3 });
        var flashing = new FastbootFlasherService(new FastbootClient(Path.Combine(xmlDir, "unused-fastboot")));

        string okXml = Path.Combine(xmlDir, "ok.xml");
        File.WriteAllText(okXml, "<configurations><configuration><fastbootimage><image name=\"BOOT\" identifier=\"boot\">fastbootimage/BOOT.img</image></fastbootimage></configuration></configurations>");
        var parsed = flashing.ParseFlashingXml(okXml);
        Check(parsed.Count == 1 && parsed[0].DumpPath == Path.Combine(xmlDir, "fastbootimage", "BOOT.img"),
            "M5: a relative image inside the package still resolves as before");

        string escapeXml = Path.Combine(xmlDir, "escape.xml");
        File.WriteAllText(escapeXml, "<configurations><configuration><fastbootimage><image name=\"X\" identifier=\"boot\">../../etc/shadow</image></fastbootimage></configuration></configurations>");
        bool escapeRefused = false;
        try { flashing.ParseFlashingXml(escapeXml); } catch (InvalidDataException) { escapeRefused = true; }
        Check(escapeRefused, "M5: an image path escaping the package directory is refused");

        string absoluteXml = Path.Combine(xmlDir, "absolute.xml");
        File.WriteAllText(absoluteXml, "<configurations><configuration><fastbootimage><image name=\"X\" identifier=\"boot\">/etc/shadow</image></fastbootimage></configuration></configurations>");
        bool absoluteRefused = false;
        try { flashing.ParseFlashingXml(absoluteXml); } catch (InvalidDataException) { absoluteRefused = true; }
        Check(absoluteRefused, "M5: an absolute image path outside the package directory is refused");

        string dtdXml = Path.Combine(xmlDir, "dtd.xml");
        File.WriteAllText(dtdXml, "<!DOCTYPE configurations [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><configurations><configuration><fastbootimage><image name=\"X\" identifier=\"boot\">&xxe;</image></fastbootimage></configuration></configurations>");
        bool dtdRefused = false;
        try { flashing.ParseFlashingXml(dtdXml); } catch (Exception) { dtdRefused = true; }
        Check(dtdRefused, "M5: a flashing xml with a DTD/entity is refused instead of resolving it");
    }
    finally { try { Directory.Delete(xmlDir, true); } catch { } }
}

// ---------------------------------------------------------------- 13. D-12 usb-update-flash
// BB-09: the GUI could burn a whole package over the USB-update channel; the CLI/MCP could not.
// These checks pin the new face without a device: the registry row, the fact that both faces call
// the *same* service method, that the gate refuses before the transfer starts for every missing
// acknowledgement, and that the three-state verdict maps onto the shared write report.
{
    var row = Kirin_Tool.Services.Capabilities.CapabilityRegistry.ById("usb-update-flash");
    Check(row != null, "D-12: capability 'usb-update-flash' is registered");
    Check(row!.CliCommand == "usb-update-flash" && row.McpTool == "usb_update_flash"
            && row.GuiHandler == "StartFullOtaButton_Click",
        "D-12: GUI + CLI + MCP faces are declared on the registry row");
    Check(row.WriteKind == Kirin_Tool.Services.Capabilities.CapabilityWriteKind.Write && row.NeedsApproval,
        "D-12: it is a gated write capability (not read-only, not plan-only)");
    Check(row.ServiceMethod == "USBUpdateFlasherService.FlashPartitions",
        "D-12: the registry names the service method (got '" + row.ServiceMethod + "')");
    Check(row.Reversibility == Kirin_Tool.Services.Approval.ApprovalReversibility.Irreversible,
        "D-12: the transfer is declared irreversible (a full-package burn cannot be undone)");

    // Same service method from both faces: this is the parity claim, checked on the sources so it
    // cannot drift into "the CLI re-implements the unpack loop".
    string guiSource = File.ReadAllText(Path.Combine(root, "MainWindow.axaml.cs"));
    Check(guiSource.Contains("_usbUpdateFlasherService.FlashPartitions(", StringComparison.Ordinal),
        "D-12: the GUI's USB Update path calls USBUpdateFlasherService.FlashPartitions");
    string cliSource = File.ReadAllText(Path.Combine(root, "Cli/Commands/UsbUpdateFlashCommand.cs"));
    Check(cliSource.Contains("service.FlashPartitions(", StringComparison.Ordinal),
        "D-12: the CLI command calls the same USBUpdateFlasherService.FlashPartitions");

    // The gate stops the run before any transport is opened: each missing acknowledgement gets its
    // own refusal and the service's own log lines never appear.
    string d12Dir = Path.Combine(Path.GetTempPath(), "kirin-d12-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(d12Dir);
    try
    {
        string package = Path.Combine(d12Dir, "Base_UPDATE.APP");
        File.WriteAllBytes(package, Array.Empty<byte>());

        (int code, string stdout, string stderr) = Run("usb-update-flash", package, "--json");
        Check(code == 4, "D-12: usb-update-flash without --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        using (JsonDocument doc = JsonDocument.Parse(stdout))
        {
            Check(doc.RootElement.GetProperty("gate").GetString() == "refused"
                    && doc.RootElement.GetProperty("applied").GetBoolean() == false,
                "D-12: the refusal is applied=false, gate=refused");
        }
        Check(!stderr.Contains("usb-update:", StringComparison.Ordinal),
            "D-12: the transfer never starts when the gate refuses (no service log lines)");

        (code, stdout, stderr) = Run("usb-update-flash", package, "--yes", "--json");
        Check(code == 4 && stderr.Contains("allow-redline", StringComparison.Ordinal),
            "D-12: --yes alone is refused: an irreversible transfer also needs --allow-redline (stderr: " + stderr.Trim() + ")");

        (code, stdout, stderr) = Run("usb-update-flash", package, "--yes", "--allow-redline", "--json");
        Check(code == 4 && stderr.Contains("I UNDERSTAND usb-update-flash", StringComparison.Ordinal),
            "D-12: --yes --allow-redline on a non-interactive stdin is refused with the exact phrase (stderr: " + stderr.Trim() + ")");
        Check(!stderr.Contains("usb-update:", StringComparison.Ordinal),
            "D-12: neither acknowledgement level reaches the transfer");

        // UsbUpdateVerdict -> WriteReport (docs/usb-update-verdict.md). Device-free: the mapping is
        // the rule, the verdict is a plain record.
        MethodInfo mapVerdict = typeof(FastbootClient).Assembly
            .GetType("Kirin_Tool.Cli.Commands.UsbUpdateFlashCommand")!
            .GetMethod("MapVerdict", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

        Kirin_Tool.Services.USBUpdate.UsbUpdateVerdict Verdict(Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome outcome) =>
            new(outcome,
                new[] { "boot" },
                outcome == Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome.Verified ? new[] { "boot" } : Array.Empty<string>(),
                Array.Empty<Kirin_Tool.Services.USBUpdate.UsbUpdateSkip>(),
                outcome == Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome.Failed ? new[] { "boot" } : Array.Empty<string>(),
                outcome != Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome.Failed,
                "test verdict",
                Array.Empty<Kirin_Tool.Services.USBUpdate.UsbUpdateEvent>());

        (bool Applied, string Gate, int Exit) Report(Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome outcome)
        {
            object report = mapVerdict.Invoke(null, new object[] { Verdict(outcome), new { test = true } })!;
            Type type = report.GetType();
            return ((bool)type.GetProperty("Applied")!.GetValue(report)!,
                    (string)type.GetProperty("Gate")!.GetValue(report)!,
                    (int)type.GetProperty("ExitCode")!.GetValue(report)!);
        }

        var verified = Report(Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome.Verified);
        Check(verified == (true, "applied", 0), "D-12: a verified transfer maps to applied/exit 0 (got " + verified + ")");
        var partial = Report(Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome.Partial);
        Check(partial == (true, "partial", 3), "D-12: a partial transfer maps to partial/exit 3 (got " + partial + ")");
        var failed = Report(Kirin_Tool.Services.USBUpdate.UsbUpdateOutcome.Failed);
        Check(failed == (false, "failed", 3), "D-12: a failed transfer maps to failed/exit 3 (got " + failed + ")");
    }
    finally { try { Directory.Delete(d12Dir, true); } catch { } }
}

Console.WriteLine(checks + " write gate checks passed.");

/// <summary>
/// Test prompt: approves every request and, when the request is a redline one, types the exact
/// per-capability phrase a human would have to type. Nothing here is reachable from the product.
/// </summary>
sealed class ApproveEveryRequest : IApprovalPrompt
{
    public Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken) =>
        Task.FromResult(new ApprovalPromptAnswer(
            ApprovalPromptOutcome.Approved,
            model.RequiresTypedConfirm ? model.ConfirmPhrase : null));
}
