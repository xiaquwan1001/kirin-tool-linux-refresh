// Headless checks for the GUI QoL features: persisted settings and the
// light/dark theme. No window is ever shown on a real display.
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kirin_Tool;
using Kirin_Tool.Services;

// Screenshot output: never a machine-specific path (this test runs in CI too).
// Override with KIRIN_GUIQOL_SHOTS when you want the PNGs in a known folder.
string ShotDirectory = Environment.GetEnvironmentVariable("KIRIN_GUIQOL_SHOTS")
    ?? Path.Combine(Path.GetTempPath(), "kirin-guiqol-shots");

AppBuilder.Configure<App>().WithInterFont().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

Exception? unhandled = null;
Dispatcher.UIThread.UnhandledException += (_, e) => { unhandled = e.Exception; e.Handled = true; };

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS " + message);
}

static string NewConfigDirectory(string name)
{
    var directory = Path.Combine(Path.GetTempPath(), "kirin-guiqol-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(directory);
    return directory;
}

// ---------------------------------------------------------------- settings
var configA = NewConfigDirectory("roundtrip");
UserSettings.Initialize(configA);
Check(UserSettings.Theme == "light", "default theme is light");
UserSettings.CpuModel = "hisi990";
UserSettings.Theme = "dark";
UserSettings.RememberDirectory(configA);
UserSettings.BasePtablePath = "/tmp/base_ptable.app";
UserSettings.BaseUpdatePath = "/tmp/base_update.app";
UserSettings.CustPtablePath = "/tmp/cust_ptable.app";
UserSettings.CustUpdatePath = "/tmp/cust_update.app";
UserSettings.PreloadPtablePath = "/tmp/preload_ptable.app";
UserSettings.PreloadUpdatePath = "/tmp/preload_update.app";
UserSettings.SwTpUpdatePath = "/tmp/swtp.app";
UserSettings.UsbUpdatePath = "/tmp/usb.app";
UserSettings.FastbootConfigXml = "/tmp/flash.xml";
UserSettings.ExtractorSourcePath = "/tmp/update.app";
UserSettings.ExtractorDestinationPath = configA;
UserSettings.OemInfoFile = "/tmp/oeminfo.img";
UserSettings.DeviceModel = "VOG-AL00";
UserSettings.Vendor = "all/cn";

Check(File.Exists(UserSettings.SettingsFilePath), "settings.json written");
var json = File.ReadAllText(UserSettings.SettingsFilePath);
using (var document = JsonDocument.Parse(json))
{
    var root = document.RootElement;
    Check(root.TryGetProperty("cpu_model", out _) && root.TryGetProperty("theme", out _) &&
          root.TryGetProperty("last_directory", out _) && root.TryGetProperty("base_ptable_path", out _) &&
          root.TryGetProperty("extractor_destination_path", out _) && root.TryGetProperty("oem_info_file", out _),
        "settings.json uses snake_case keys");
}

Check(!File.Exists(UserSettings.SettingsFilePath + ".tmp"), "atomic write leaves no .tmp file");

UserSettings.Initialize(configA); // reload from disk
Check(UserSettings.CpuModel == "hisi990", "cpu_model survives a reload");
Check(UserSettings.Theme == "dark", "theme survives a reload");
Check(UserSettings.DeviceModel == "VOG-AL00" && UserSettings.Vendor == "all/cn", "OEMInfo text fields survive a reload");
Check(UserSettings.PreloadUpdatePath == "/tmp/preload_update.app", "full OTA path survives a reload");
Check(UserSettings.ExtractorDestinationPath == configA, "extractor destination survives a reload");
Check(UserSettings.LastDirectory == configA, "valid last_directory is returned");
Check(UserSettings.ExistingFile("/tmp/does-not-exist.app") is null, "missing files are dropped when restoring");
Check(UserSettings.ExistingDirectory("/tmp/definitely-not-here") is null, "missing directories are dropped when restoring");

// ----------------------------------------------------------- corrupt file
var configB = NewConfigDirectory("corrupt");
File.WriteAllText(Path.Combine(configB, "settings.json"), "{oops");
UserSettings.Initialize(configB);
Check(UserSettings.Theme == "light", "corrupt settings.json falls back to defaults");
Check(UserSettings.CpuModel is null, "corrupt settings.json yields no cpu_model");
Check(File.Exists(Path.Combine(configB, "settings.json.bad")), "corrupt settings.json is quarantined as .bad");
UserSettings.Theme = "dark";
Check(File.Exists(Path.Combine(configB, "settings.json")), "settings are rewritten after a corrupt load");

// ------------------------------------------------------------------ theme
var app = Application.Current ?? throw new Exception("no application");
app.RequestedThemeVariant = ThemeVariant.Dark;
Check(app.RequestedThemeVariant == ThemeVariant.Dark, "RequestedThemeVariant reads back dark");
Check(app.ActualThemeVariant == ThemeVariant.Dark, "ActualThemeVariant switches to dark");
app.RequestedThemeVariant = ThemeVariant.Light;
Check(app.ActualThemeVariant == ThemeVariant.Light, "ActualThemeVariant switches back to light");

var lightText = PaletteBrush(app, "AppTextBrush", ThemeVariant.Light);
var darkText = PaletteBrush(app, "AppTextBrush", ThemeVariant.Dark);
var lightCard = PaletteBrush(app, "AppCardBrush", ThemeVariant.Light);
var darkCard = PaletteBrush(app, "AppCardBrush", ThemeVariant.Dark);
Check(lightText.Color != darkText.Color && lightCard.Color != darkCard.Color, "light and dark palettes differ");
Check(Contrast(lightText.Color, lightCard.Color) > 3 && Contrast(darkText.Color, darkCard.Color) > 3,
    "text/panel contrast stays readable in both palettes");

// -------------------------------------------------- main window rendering
Directory.CreateDirectory(ShotDirectory);
var configC = NewConfigDirectory("window");
UserSettings.Initialize(configC);

var shots = new List<(string Name, ThemeVariant Variant, string Path)>
{
    ("light", ThemeVariant.Light, Path.Combine(ShotDirectory, "main-light.png")),
    ("dark", ThemeVariant.Dark, Path.Combine(ShotDirectory, "main-dark.png"))
};

foreach (var (name, variant, path) in shots)
{
    app.RequestedThemeVariant = variant;
    unhandled = null;
    var window = new MainWindow();
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    Check(unhandled is null, name + " theme: MainWindow shows without UI exceptions");

    var frame = window.CaptureRenderedFrame() ?? throw new Exception(name + ": capture returned no frame");
    frame.Save(path);
    Check(File.Exists(path) && new FileInfo(path).Length > 0, name + " theme: rendered frame saved to " + path);

    var (mean, low, high) = Luminance(frame);
    if (variant == ThemeVariant.Dark)
    {
        Check(mean < 0.45, $"dark theme frame is dark (mean luminance {mean:0.00})");
    }
    else
    {
        Check(mean > 0.55, $"light theme frame is light (mean luminance {mean:0.00})");
    }

    // A window whose text and background collapsed into one colour would show a
    // tiny luminance spread; both frames must keep real contrast.
    Check(high - low > 0.6, $"{name} theme keeps a rendered luminance range ({low:0.00}..{high:0.00})");

    var windowText = (window.Foreground as ISolidColorBrush)?.Color ?? Colors.Black;
    var windowBackground = (window.Background as ISolidColorBrush)?.Color ?? Colors.White;
    Check(Contrast(windowText, windowBackground) > 4.5,
        $"{name} theme: window text {windowText} stays readable on {windowBackground}");
    window.Close();
    Dispatcher.UIThread.RunJobs();
}

// ------------------------------------------- theme switcher in the caption
app.RequestedThemeVariant = ThemeVariant.Light;
var themed = new MainWindow();
themed.Show();
Dispatcher.UIThread.RunJobs();
// The selector lives in the caption UserControl, so it is not in the window's name scope.
var selector = themed.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(combo => combo.Name == "ThemeSelector")
               ?? throw new Exception("ThemeSelector is missing from the visual tree");
Check(selector.ItemCount == 3, "theme selector offers light/dark/system");
selector.SelectedIndex = 1; // dark
Dispatcher.UIThread.RunJobs();
Check(app.RequestedThemeVariant == ThemeVariant.Dark, "picking Dark in the caption switches the app variant");
Check(UserSettings.Theme == "dark", "picking Dark in the caption persists the theme");
selector.SelectedIndex = 0; // light
Dispatcher.UIThread.RunJobs();
Check(app.RequestedThemeVariant == ThemeVariant.Light && UserSettings.Theme == "light", "picking Light switches back and persists");
themed.Close();
Dispatcher.UIThread.RunJobs();

// D-05 (2026-11-17): a skipped partition is not a written one. The GUI used to increment its
// success counter for skips, so "Successfully flashed all N partitions!" was printed even when
// nothing was written.
{
    var allSkipped = Kirin_Tool.Services.Verdict.FlashTally.Describe(0, 3, 3);
    Check(!allSkipped.AllSuccessful, "D-05: all-skipped is not a success");
    Check(!allSkipped.Message.Contains("Successfully flashed all"), "D-05: all-skipped never claims 'flashed all'");
    Check(allSkipped.Message.Contains("3 skipped") && allSkipped.AttemptedCount == 0,
        "D-05: the message names the skipped count and the denominator excludes skips");

    var mixed = Kirin_Tool.Services.Verdict.FlashTally.Describe(2, 1, 3);
    Check(mixed.AllSuccessful && mixed.AttemptedCount == 2 && mixed.FailedCount == 0,
        "D-05: two written + one skipped is 'all attempted succeeded'");
    Check(mixed.Message.Contains("2/2") && mixed.Message.Contains("1 skipped"),
        "D-05: mixed message says 'Flashed 2/2 partitions; 1 skipped (not written).'");

    var partial = Kirin_Tool.Services.Verdict.FlashTally.Describe(1, 1, 3);
    Check(!partial.AllSuccessful && partial.FailedCount == 1,
        "D-05: one written + one failed + one skipped is not a success");

    var clean = Kirin_Tool.Services.Verdict.FlashTally.Describe(3, 0, 3);
    Check(clean.AllSuccessful && clean.Message == "Successfully flashed all 3 partitions!",
        "D-05: the no-skip wording is unchanged");

    var nothing = Kirin_Tool.Services.Verdict.FlashTally.Describe(0, 0, 0);
    Check(!nothing.AllSuccessful, "D-05: an empty plan is not a success");
    Console.WriteLine("PASS D-05 skipped partitions are reported separately and never counted as successes");
}

// D-23 (a): the pre-op backup is an explicit, reversible option whose default did not change.
{
    Check(Kirin_Tool.Services.Verdict.PreOpBackupChoice.DefaultEnabled &&
          Kirin_Tool.Services.Verdict.PreOpBackupChoice.ShouldRun(true),
        "D-23a: the pre-op backup still runs by default");
    Check(!Kirin_Tool.Services.Verdict.PreOpBackupChoice.ShouldRun(false),
        "D-23a: turning the option off really skips the backup");

    string disabledNotice = Kirin_Tool.Services.Verdict.PreOpBackupChoice.DisabledNotice("gui-full-ota");
    Check(disabledNotice.Contains(Kirin_Tool.Services.Verdict.PreOpBackupChoice.DisabledToken, StringComparison.Ordinal) &&
          disabledNotice.Contains("restored", StringComparison.Ordinal) &&
          disabledNotice.Contains("gui-full-ota", StringComparison.Ordinal),
        "D-23a: the notice names the token, the consequence and the write path");
    Check(Kirin_Tool.Services.Verdict.PreOpBackupChoice.DecisionRecord("gui-full-ota", false)
            .Contains(Kirin_Tool.Services.Verdict.PreOpBackupChoice.DisabledToken, StringComparison.Ordinal),
        "D-23a: the session record says the backup was turned off");

    var preOpConfig = NewConfigDirectory("preop-backup");
    UserSettings.Initialize(preOpConfig);
    Check(UserSettings.PreOpBackup, "D-23a: a fresh settings file keeps the backup on");
    File.WriteAllText(Path.Combine(preOpConfig, "settings.json"), "{\"theme\":\"dark\"}");
    UserSettings.Initialize(preOpConfig);
    Check(UserSettings.PreOpBackup && UserSettings.Theme == "dark",
        "D-23a: an existing settings.json without the key keeps the backup on");
    UserSettings.PreOpBackup = false;
    UserSettings.Initialize(preOpConfig);
    Check(!UserSettings.PreOpBackup, "D-23a: turning it off survives a reload");
    Check(File.ReadAllText(UserSettings.SettingsFilePath).Contains("pre_op_backup", StringComparison.Ordinal),
        "D-23a: the option is stored under a snake_case key");
    UserSettings.PreOpBackup = true;

    string logRoot = Path.Combine(Path.GetTempPath(), "kirin-guiqol-log-" + Guid.NewGuid().ToString("N")[..8]);
    Environment.SetEnvironmentVariable("KIRIN_LOG_DIR", logRoot);
    Kirin_Tool.Services.Diagnostics.OperationLogger.ResetSessionForTests();
    unhandled = null;
    var preOpWindow = new MainWindow();
    preOpWindow.Show();
    Dispatcher.UIThread.RunJobs();
    var preOpBox = preOpWindow.FindControl<CheckBox>("PreOpBackupCheckBox")
        ?? throw new Exception("D-23a: the pre-op backup checkbox is missing from the Full OTA panel");
    var preOpWarning = preOpWindow.FindControl<TextBlock>("PreOpBackupDisabledNotice")
        ?? throw new Exception("D-23a: the pre-op backup warning is missing");
    Check(preOpBox.IsChecked == true, "D-23a: the option is visible and checked by default");
    Check(!preOpWarning.IsVisible, "D-23a: no warning while the backup is on");

    preOpBox.IsChecked = false;
    Dispatcher.UIThread.RunJobs();
    Check(!UserSettings.PreOpBackup && preOpWarning.IsVisible,
        "D-23a: unchecking it persists the choice and raises the on-screen warning");

    // The four write paths ask the same question; with the option off the backup returns the
    // Skipped result before any fastboot/serial access (the injected clients answer nothing).
    var runBackup = typeof(MainWindow).GetMethod("RunPreOpBackupAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new Exception("D-23a: RunPreOpBackupAsync is missing");
    Task backupTask = (Task)runBackup.Invoke(preOpWindow, new object?[] { new[] { "boot" }, "gui-test", null })!;
    backupTask.GetAwaiter().GetResult();
    var skipped = (Kirin_Tool.Services.PreOpBackupResult?)backupTask.GetType().GetProperty("Result")!.GetValue(backupTask);
    Check(skipped != null && !skipped.Attempted && skipped.Error == "disabled",
        "D-23a: with the option off the backup is skipped explicitly (Attempted=false, Error=disabled)");

    preOpBox.IsChecked = true;
    Dispatcher.UIThread.RunJobs();
    Check(UserSettings.PreOpBackup && !preOpWarning.IsVisible, "D-23a: re-checking it restores the safe default");
    preOpWindow.Close();
    Dispatcher.UIThread.RunJobs();
    Check(unhandled is null, "D-23a: the option added no UI exception");
    Console.WriteLine("PASS D-23a pre-op backup is a visible option with an unchanged safe default");
}

// D-23 (b): the conclusion is a reviewable record, not only a progress line.
{
    Check(Kirin_Tool.Services.Verdict.OperationConclusionRecord.VerdictOf(Kirin_Tool.Services.Verdict.OperationOutcome.Verified) == "verified" &&
          Kirin_Tool.Services.Verdict.OperationConclusionRecord.VerdictOf(Kirin_Tool.Services.Verdict.OperationOutcome.Partial) == "partial" &&
          Kirin_Tool.Services.Verdict.OperationConclusionRecord.VerdictOf(Kirin_Tool.Services.Verdict.OperationOutcome.Failed) == "failed" &&
          Kirin_Tool.Services.Verdict.OperationConclusionRecord.VerdictOf(Kirin_Tool.Services.Verdict.OperationOutcome.Cancelled) == "cancelled" &&
          Kirin_Tool.Services.Verdict.OperationConclusionRecord.VerdictOf(Kirin_Tool.Services.Verdict.OperationOutcome.TimedOut) == "timed-out",
        "D-23b: each outcome has its own verdict word");
    Check(Kirin_Tool.Services.Verdict.OperationConclusionRecord.IsSuccess(Kirin_Tool.Services.Verdict.OperationOutcome.Verified) &&
          !Kirin_Tool.Services.Verdict.OperationConclusionRecord.IsSuccess(Kirin_Tool.Services.Verdict.OperationOutcome.Partial),
        "D-23b: a partial flash is never recorded as a success");
    Check(Kirin_Tool.Services.Verdict.OperationConclusionRecord.FromSuccess(false) == Kirin_Tool.Services.Verdict.OperationOutcome.Failed &&
          Kirin_Tool.Services.Verdict.OperationConclusionRecord.FromSuccess(true) == Kirin_Tool.Services.Verdict.OperationOutcome.Verified,
        "D-23b: legacy bool conclusions map onto the same enum");

    string logRoot = Path.Combine(Path.GetTempPath(), "kirin-guiqol-log-" + Guid.NewGuid().ToString("N")[..8]);
    Environment.SetEnvironmentVariable("KIRIN_LOG_DIR", logRoot);
    Kirin_Tool.Services.Diagnostics.OperationLogger.ResetSessionForTests();

    // ASCII only: the JSONL writer escapes non-ASCII, so a dash in the fixture would make the
    // Contains() below tire-kick the encoder instead of the record.
    const string message = "OTA flash PARTIAL - 3/4 partitions verified";
    string? sessionDirectory = Kirin_Tool.Services.Verdict.OperationConclusionRecord.Record(
        "gui-full-ota", Kirin_Tool.Services.Verdict.OperationOutcome.Partial, message);
    Check(sessionDirectory != null && Directory.Exists(sessionDirectory),
        "D-23b: the conclusion is written into the existing sessions/ store (no new storage)");
    string jsonlPath = Path.Combine(sessionDirectory!, "session.jsonl");
    Check(File.Exists(jsonlPath), "D-23b: session.jsonl exists");
    string jsonl = File.ReadAllText(jsonlPath);
    Check(jsonl.Contains("gui.conclusion", StringComparison.Ordinal) &&
          jsonl.Contains("gui:partial", StringComparison.Ordinal) &&
          jsonl.Contains(message, StringComparison.Ordinal),
        "D-23b: the recorded line carries the step, the verdict source and the message");

    var listed = Kirin_Tool.Services.Diagnostics.LogSessionStore.List(logRoot);
    Check(listed.Any(descriptor => Path.GetFullPath(descriptor.Directory) == Path.GetFullPath(sessionDirectory!)),
        "D-23b: logs list sees the GUI conclusion (the record is reviewable)");

    // A broken log root must degrade to "no record", never to a crash after a finished write.
    Environment.SetEnvironmentVariable("KIRIN_LOG_DIR", jsonlPath);
    Kirin_Tool.Services.Diagnostics.OperationLogger.ResetSessionForTests();
    Check(Kirin_Tool.Services.Verdict.OperationConclusionRecord.Record(
            "gui-full-ota", Kirin_Tool.Services.Verdict.OperationOutcome.Failed, "boom") is null,
        "D-23b: an unwritable log root yields no record instead of breaking the operation");
    Environment.SetEnvironmentVariable("KIRIN_LOG_DIR", null);
    Kirin_Tool.Services.Diagnostics.OperationLogger.ResetSessionForTests();
    Console.WriteLine("PASS D-23b GUI conclusions land in the shared operation session");
}

// D-23 (c): a timeout is its own conclusion, never "the user said no".
{
    Check(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.FromAnswer(true, true) == Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.Approved,
        "D-23c: an answered yes is Approved");
    Check(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.FromAnswer(true, false) == Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.Declined,
        "D-23c: an answered no is Declined");
    Check(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.FromAnswer(false, false) == Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.TimedOut &&
          Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.FromAnswer(false, true) == Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.TimedOut,
        "D-23c: an unanswered confirmation is TimedOut, whatever the answer task would have said");
    Check(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.AllowsWrite(Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.Approved) &&
          !Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.AllowsWrite(Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.Declined) &&
          !Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.AllowsWrite(Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.TimedOut),
        "D-23c: only an explicit approval lets the write continue");

    string[] names = { "boot", "vbmeta" };
    string declined = Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.Describe(Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.Declined, names);
    string timedOut = Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.Describe(Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.TimedOut, names);
    string approved = Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.Describe(Kirin_Tool.Services.Verdict.RedlineDeltaOutcome.Approved, names);
    Check(declined.Contains("DECLINED", StringComparison.Ordinal) &&
          declined.Contains(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.DeclinedToken, StringComparison.Ordinal),
        "D-23c: a refusal says DECLINED and carries its own token");
    Check(timedOut.Contains("TIMEOUT", StringComparison.Ordinal) &&
          timedOut.Contains(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.TimedOutToken, StringComparison.Ordinal) &&
          timedOut.Contains("NOT a user refusal", StringComparison.Ordinal),
        "D-23c: a timeout says TIMEOUT and states that it is not a refusal");
    Check(!declined.Contains("TIMEOUT", StringComparison.Ordinal) && declined != timedOut,
        "D-23c: the two conclusions are textually distinct");
    Check(approved.Contains(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.ApprovedToken, StringComparison.Ordinal),
        "D-23c: an approval carries its own token");
    Check(timedOut.Contains("boot", StringComparison.Ordinal) && timedOut.Contains("vbmeta", StringComparison.Ordinal),
        "D-23c: the conclusion names the partitions that were never confirmed");
    Check(Kirin_Tool.Services.Verdict.RedlineDeltaVerdict.Timeout == TimeSpan.FromMinutes(10),
        "D-23c: the bounded wait is still the historical ten minutes");

    // The GUI funnel must actually use the three-state mapping (a green pure function nobody calls
    // would leave the silent-cancel bug in place).
    string mainWindow = File.ReadAllText(Path.Combine(RepositoryRoot(), "MainWindow.axaml.cs"));
    Check(mainWindow.Contains("RedlineDeltaVerdict.FromAnswer(", StringComparison.Ordinal) &&
          mainWindow.Contains("RedlineDeltaVerdict.Describe(", StringComparison.Ordinal),
        "D-23c: ConfirmRedlineDeltaOnBackground returns the three-state outcome via the shared rule");
    Check(mainWindow.Contains("OperationOutcome.TimedOut", StringComparison.Ordinal),
        "D-23c: the timeout conclusion is recorded as timed-out, not as a cancellation/refusal");
    Console.WriteLine("PASS D-23c redline re-confirmation distinguishes timeout from refusal");
}

Console.WriteLine("8 GUI QoL checks passed; headless, no device access.");
return 0;

static ISolidColorBrush PaletteBrush(Application application, string key, ThemeVariant variant)
{
    return application.FindResource(variant, key) as ISolidColorBrush
           ?? throw new Exception($"theme dictionary is missing {key} for {variant}");
}

// Reads the rendered frame and reports mean/2%-98% luminance spread.
static (double Mean, double Low, double High) Luminance(WriteableBitmap frame)
{
    using var locked = frame.Lock();
    var stride = locked.RowBytes;
    var height = locked.Size.Height;
    var bytes = new byte[stride * height];
    Marshal.Copy(locked.Address, bytes, 0, bytes.Length);

    var samples = new List<double>(capacity: 4096);
    double total = 0;
    long count = 0;
    // Sample a coarse grid: enough for statistics without a multi-megabyte pass.
    for (var y = 0; y < height; y += 3)
    {
        for (var x = 0; x < locked.Size.Width; x += 3)
        {
            var offset = y * stride + x * 4;
            if (offset + 2 >= bytes.Length) continue;
            var value = (bytes[offset] + bytes[offset + 1] + bytes[offset + 2]) / 3.0 / 255.0;
            total += value;
            count++;
            if (samples.Count < 4096) samples.Add(value);
        }
    }

    samples.Sort();
    return (count == 0 ? 0 : total / count, samples[0], samples[^1]);
}

static double Contrast(Color foreground, Color background)
{
    var a = RelativeLuminance(foreground);
    var b = RelativeLuminance(background);
    var lighter = Math.Max(a, b);
    var darker = Math.Min(a, b);
    return (lighter + 0.05) / (darker + 0.05);
}

static double RelativeLuminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

static double Channel(byte value)
{
    var v = value / 255.0;
    return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}

// Source assertions (D-23c) need the repository root, not the test's output directory.
static string RepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Kirin-Tool.csproj")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new Exception("repository root not found from " + AppContext.BaseDirectory);
}
