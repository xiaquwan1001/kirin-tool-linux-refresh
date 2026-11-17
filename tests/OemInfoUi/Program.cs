using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kirin_Tool;

System.Environment.SetEnvironmentVariable("KIRIN_TOOL_SETTINGS_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kirin-tool-test-settings")); AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Exception? unhandled = null;
Dispatcher.UIThread.UnhandledException += (_, e) => { unhandled=e.Exception; e.Handled=true; };
var owner=new MainWindow();owner.Show();
var model=owner.FindControl<TextBox>("TxtDeviceModel")!;
var vendor=owner.FindControl<TextBox>("TxtVendor")!;
var file=owner.FindControl<TextBox>("TxtOemInfoFile")!;
var convert=owner.FindControl<Button>("BtnConvertOem")!;
// The click handler is async: the validation dialog is created on a later
// continuation, which CI's slower runners do not always reach with a single
// RunJobs(). Pump until a window is owned (or 2 s pass) so the check is about
// behaviour, not scheduling luck.
void PumpForDialog() {
    for (int i=0;i<200 && owner.OwnedWindows.Count==0;i++) {
        Dispatcher.UIThread.RunJobs();
        if (owner.OwnedWindows.Count==0) System.Threading.Thread.Sleep(10);
    }
    Dispatcher.UIThread.RunJobs();
}
void Test(string name,string? m,string? v,string? f,string expected) {
    model.Text=m;vendor.Text=v;file.Text=f;unhandled=null;
    convert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    PumpForDialog();
    if(unhandled!=null)throw new Exception(name+": UI exception",unhandled);
    var dialog=owner.OwnedWindows.SingleOrDefault();
    if(dialog?.Title!="Input Error" || !dialog.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==expected))
        throw new Exception(name+": expected input validation dialog");
    dialog.Close();Dispatcher.UIThread.RunJobs();
    Console.WriteLine("PASS "+name);
}
// Untouched Avalonia TextBoxes have null text. All paths below end before device I/O.
Test("untouched empty form",null,null,null,"Device Model and Vendor/Country cannot be empty.");
Test("missing vendor","TEST-MODEL",null,null,"Device Model and Vendor/Country cannot be empty.");
Test("whitespace model","   ","test/test",null,"Device Model and Vendor/Country cannot be empty.");
// Force the file-based CPU route by selecting the first supported combo item (hisi65x_a).
owner.FindControl<ComboBox>("CpuComboBox")!.SelectedIndex=1;
Test("missing file with valid fields","TEST-MODEL","test/test",null,"Please select a valid OEMInfo file.");
Test("empty file text","TEST-MODEL","test/test","","Please select a valid OEMInfo file.");
owner.Close();
Console.WriteLine("5 OEMInfo UI checks passed; headless, no device access.");
