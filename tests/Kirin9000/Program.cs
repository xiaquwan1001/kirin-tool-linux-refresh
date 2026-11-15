using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kirin_Tool;
using Kirin_Tool.Services;
using Kirin_Tool.Models;
using System.Reflection;
int count=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
void Reject(Action a,string name){try{a();}catch(InvalidOperationException){Check(true,name);return;}throw new Exception(name);}
ProcessResult Reply(string value,int code=0)=>new(){Output=value,ExitCode=code};
Check(ChipCapabilities.Allows("hisi9000","read-info")&&ChipCapabilities.Allows("hisi9000","read-sn")&&ChipCapabilities.Allows("hisi9000","read-psid"),"read-only operations allowed");
Check(ChipCapabilities.Allows("hisi9000","reboot-usb-update"),"experimental rescue entry allowed");
foreach(var operation in new[]{"unlock-fastboot","write-sn","frp-remove","unlock-bootloader","enable-downgrade","sw-testpoint-enter","sw-testpoint-exit","dump-partitions","flash-xml","full-ota","convert-oem","reboot"})
 Check(!ChipCapabilities.Allows("hisi9000",operation),operation+" blocked");
Check(ChipCapabilities.Allows("hisi980","write-sn")&&ChipCapabilities.Allows("hisi990","unlock-fastboot"),"existing chip policy retained");
Check(RescuePartitionPlan.ForChip("hisi9000").Select(x=>x.Image).SequenceEqual(new[]{"ERECOVERY","ERECOVERY_RAMDISK","ERECOVERY_VENDOR"}),"Mate40 firmware image names");
Check(RescuePartitionPlan.ForChip("hisi9000")[0].Target=="rescue_recovery","explicit experimental destination");
Check(RescuePartitionPlan.ForChip("hisi980")[0]==new RescueImageTarget("ERECOVERY_KERNEL","rescue_recovery_kernel"),"older rescue mapping retained");
var psid=FastbootReadResponses.Psid(Reply("(bootloader) SN:TEST-SN\n(bootloader) IMEI:TEST-IMEI\n(bootloader) MEID:TEST-MEID\nOKAY [0.001s]"));
Check(psid["SN"]=="TEST-SN"&&psid["IMEI"]=="TEST-IMEI","PSID explicit SN parsed separately from IMEI");
Reject(()=>FastbootReadResponses.Psid(Reply("(bootloader) SN:A\nFAILED (remote: Command not allowed)",1)),"rejected PSID cannot become successful partial SN");
Reject(()=>FastbootReadResponses.Psid(Reply("SN:A\nSN:B")),"conflicting SN rejected");
Reject(()=>FastbootReadResponses.Psid(Reply("serialno:USB-IDENTIFIER")),"USB identifier never substituted");
Reject(()=>FastbootReadResponses.Psid(Reply("OKAY\n")),"empty PSID response rejected");
Check(FastbootReadResponses.OemVersion("(bootloader) NAM-LGRP1-CHN 102.0.1.272\nOKAY")=="NAM-LGRP1-CHN 102.0.1.272","colonless NAM OEM reply accepted");
Check(FastbootReadResponses.OemVersion("(bootloader) :NOH-LGRP1-CHN 104.2.0.196\nOKAY")=="NOH-LGRP1-CHN 104.2.0.196","Kirin leading colon retained");
Check(FastbootReadResponses.OemVersion("(bootloader) value\nFAILED")=="Unknown","failed version response rejected");
System.Environment.SetEnvironmentVariable("KIRIN_TOOL_SETTINGS_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kirin-tool-test-settings")); AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
var owner=new MainWindow();
// An accidental missing gate must never invoke a real fastboot client.
foreach(var field in new[]{"_fastbootClient","_xmlFastbootClient"})
 typeof(MainWindow).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,new FastbootClient("/tmp/kirin9000-test-no-device-client"));
owner.Show();Dispatcher.UIThread.RunJobs();
var combo=owner.FindControl<ComboBox>("CpuComboBox")!;
combo.SelectedItem=combo.Items.OfType<ComboBoxItem>().Single(x=>x.Content?.ToString()?.Contains("hisi9000")==true);Dispatcher.UIThread.RunJobs();
Check(owner.FindControl<Border>("Kirin9000Notice")!.IsVisible,"experimental chip notice visible");
Check(owner.FindControl<TextBlock>("Kirin9000RescueHint")!.IsVisible,"experimental rescue mapping labelled");
Check(owner.FindControl<Expander>("UnlockBootloaderPanel")!.IsVisible,"unsupported unlock still accessible for notice");
var execute=typeof(MainWindow).GetMethod("ExecuteCommand",BindingFlags.Instance|BindingFlags.NonPublic)!;
void Notice(Action invoke,string name){invoke();Dispatcher.UIThread.RunJobs();var dialog=owner.OwnedWindows.Single();Check(dialog.Title=="Notice"&&dialog.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Notice")&&dialog.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Unsupported for kirin 9000"),name+" shows heading/body before device access");dialog.Close();Dispatcher.UIThread.RunJobs();}
foreach(var command in new[]{"unlock-fastboot","write-sn","frp-remove","enable-downgrade","sw-testpoint-enter","sw-testpoint-exit","unlock-bootloader"})
 Notice(()=>execute.Invoke(owner,[command]),command);
foreach(var handler in new[]{"DumpPartitions_Click","FlashFromXml_Click","StartFullOtaButton_Click","BtnConvertOem_Click","RebootDevice_Click"})
 Notice(()=>typeof(MainWindow).GetMethod(handler,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[new Button(),new RoutedEventArgs(Button.ClickEvent)]),handler);
// Exercise the real rescue handler with synthetic archives and a local process stub.
// No physical USB or firmware payload is used.
string fixtureDir=Path.Combine(Path.GetTempPath(),"kirin9000-rescue-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureDir);
try {
 string fake=Path.Combine(fixtureDir,"fake-fastboot"), calls=Path.Combine(fixtureDir,"calls");
 File.WriteAllText(fake,"#!/usr/bin/python3\nimport sys,json\nwith open("+System.Text.Json.JsonSerializer.Serialize(calls)+",'a') as f: f.write(json.dumps(sys.argv[1:])+'\\n')\nif sys.argv[1:]==['devices']: print('TEST fastboot')\nelif sys.argv[1:]==['getvar','product']: print('(bootloader) product: NOH-AN00')\nelif sys.argv[1:]==['oem','lock-state','info']: print('(bootloader) FB LockState: LOCKED')\nelif 'flash' in sys.argv: print('FAILED (remote: partition length get error)'); sys.exit(1)\nelse: print('OKAY')\n");
 if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
 typeof(MainWindow).GetField("_fastbootClient",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,new FastbootClient(fake));
 void Fixture(string path,params string[] names) {
  using var w=new BinaryWriter(File.Create(path)); w.Write(new byte[12]);
  foreach(string name in names) { w.Write(new byte[]{0x55,0xaa,0x5a,0xa5});w.Write(102);w.Write(0);w.Write(System.Text.Encoding.ASCII.GetBytes("HWTEST00"));w.Write(0);w.Write(4u);w.Write(new byte[32]);var bytes=new byte[32];System.Text.Encoding.ASCII.GetBytes(name).CopyTo(bytes,0);w.Write(bytes);w.Write(new byte[10]);w.Write(new byte[4]);while(w.BaseStream.Position%4!=0)w.Write((byte)0); }
 }
 void PumpUntil(Func<bool> ready,int seconds=45) {var until=DateTime.UtcNow.AddSeconds(seconds);while(!ready()){Dispatcher.UIThread.RunJobs();if(DateTime.UtcNow>until)throw new Exception("Stub rescue timed out; owned windows: ["+string.Join(", ",owner.OwnedWindows.Select(w=>w.GetType().Name+":"+w.Title))+"]");Thread.Sleep(10);}Dispatcher.UIThread.RunJobs();} // 45s: the pre-op backup runs first and may spawn dump attempts
 foreach(bool complete in new[]{false,true}) {
  File.WriteAllText(calls,"");string app=Path.Combine(fixtureDir,"UPDATE.APP");
  Fixture(app,complete?new[]{"ERECOVERY","ERECOVERY_RAMDISK","ERECOVERY_VENDOR"}:new[]{"ERECOVERY_RAMDISK","ERECOVERY_VENDOR"});
  owner.UsbUpdateFile.FilePath=app;
  var task=(Task)typeof(MainWindow).GetMethod("HandleRebootToUsbUpdate",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[])!;
  // The approval gate answers first now: the rescue write is redline + irreversible, so the dialog
  // only unlocks the approve button once the per-capability confirm phrase is typed.
  PumpUntil(()=>owner.OwnedWindows.Any());
  void ApproveIfAsked(string phrase) {
   var approval=owner.OwnedWindows.FirstOrDefault(w=>w.Title?.StartsWith("Approve write:",StringComparison.Ordinal)==true);
   if(approval==null) return;
   var box=approval.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
   if(box!=null){box.Text=phrase;Dispatcher.UIThread.RunJobs();}
   var approve=approval.GetVisualDescendants().OfType<Button>().First(b=>b.Content?.ToString()=="Approve once");
   Check(approve.IsEnabled,"redline approve button unlocks only after the confirm phrase is typed");
   approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
  }
  ApproveIfAsked("I UNDERSTAND reboot-usb-update");
  PumpUntil(()=>owner.OwnedWindows.Any(w=>w.GetType().Name=="ProcessDialogUapp"));
  var progress=(Kirin_Tool.UI.ProcessDialogUapp)owner.OwnedWindows.Single();
  try { PumpUntil(()=>progress.FindControl<Button>("CloseButton")!.IsVisible,60); }
  catch(Exception ex) { throw new Exception(ex.Message+" | status="+progress.CurrentOperationText+" | calls="+File.ReadAllText(calls).Replace("\n"," ")+" | taskDone="+task.IsCompleted); }
  var argv=File.ReadAllLines(calls).Select(line=>System.Text.Json.JsonSerializer.Deserialize<string[]>(line)!).ToArray();
  if(!complete) {Check(!argv.Any(a=>a.Contains("flash")),"missing combined ERECOVERY prevents all writes");Check(progress.CurrentOperationText.Contains("no images flashed"),"missing-image result clearly reported");}
  else {Check(argv.Where(a=>a.Contains("flash")).Select(a=>a[1]).SequenceEqual(new[]{"rescue_recovery","rescue_recovery_ramdisk","rescue_recovery_vendor"}),"real handler dispatches experimental rescue aliases");Check(progress.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text?.Contains("partition length get error")==true),"bootloader failure shown in progress rows");Check(!argv.Any(a=>a.Contains("rescue_enter_recovery")),"failed writes prevent USB Update entry command");}
  progress.Close();PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();
 }
} finally {Directory.Delete(fixtureDir,true);}
combo.SelectedItem=combo.Items.OfType<ComboBoxItem>().Single(x=>x.Content?.ToString()?.Contains("hisi980")==true);Dispatcher.UIThread.RunJobs();
Check(!owner.FindControl<Border>("Kirin9000Notice")!.IsVisible,"switching back clears experimental notice");

// ------------------------------------------------- S3: Convert OEM direct branch is a gated write
// The hisi710/710a/810/820/970/980/985/990 branch of BtnConvertOem_Click used to send
// oeminfoerase-disablewp / oeminfowrite-KTModel@… / oeminfowrite-KTVendor@… with no approval and
// no write-identity gate (audit 2026-11-15 round3 §3.1). It now asks the same redline capability
// as the flash path and sends everything through DeviceWriteOps.
{
 string oemDir=Path.Combine(Path.GetTempPath(),"kirin9000-oem-"+Guid.NewGuid().ToString("N"));
 Directory.CreateDirectory(oemDir);
 void PumpOem(Func<bool> ready,int seconds=45) {var until=DateTime.UtcNow.AddSeconds(seconds);while(!ready()){Dispatcher.UIThread.RunJobs();if(DateTime.UtcNow>until)throw new Exception("S3 oeminfo flow timed out; owned windows: ["+string.Join(", ",owner.OwnedWindows.Select(w=>w.GetType().Name+":"+w.Title))+"]");Thread.Sleep(10);}Dispatcher.UIThread.RunJobs();}
 try {
  string oemCalls=Path.Combine(oemDir,"calls"), oemStub=Path.Combine(oemDir,"fake-fastboot");
  string[] oemScript = {
   "#!/usr/bin/python3",
   "import sys,json",
   "args=sys.argv[1:]",
   "open(" + System.Text.Json.JsonSerializer.Serialize(oemCalls) + ",'a').write(json.dumps(args)+'\\n')",
   "if args==['devices']:",
   "    print('TEST fastboot')",
   "elif args[:1]==['getvar']:",
   "    print('(bootloader) product: NOH-AN00')",
   "else:",
   "    print('OKAY')",
  };
  File.WriteAllText(oemStub,string.Join((char)10,oemScript));
  if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(oemStub,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  typeof(MainWindow).GetField("_fastbootClient",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,new FastbootClient(oemStub));
  combo.SelectedItem=combo.Items.OfType<ComboBoxItem>().Single(x=>x.Content?.ToString()?.Contains("hisi990")==true);Dispatcher.UIThread.RunJobs();
  owner.FindControl<TextBox>("TxtDeviceModel")!.Text="NOH-AN00";
  owner.FindControl<TextBox>("TxtVendor")!.Text="hw-cn";
  File.WriteAllText(oemCalls,"");
  typeof(MainWindow).GetMethod("BtnConvertOem_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[new Button(),new RoutedEventArgs(Button.ClickEvent)]);
  PumpOem(()=>owner.OwnedWindows.Any(w=>w.Title?.StartsWith("Approve write:",StringComparison.Ordinal)==true));
  var oemApproval=owner.OwnedWindows.Single(w=>w.Title?.StartsWith("Approve write:",StringComparison.Ordinal)==true);
  Check(oemApproval.Title!.Contains("oeminfo-flash"),"S3: the direct oeminfo write asks the oeminfo-flash capability, not a silent command");
  var oemApprove=oemApproval.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Approve once");
  Check(!oemApprove.IsEnabled,"S3: the approve button stays locked until the confirm phrase is typed");
  var sentBeforeApproval=File.Exists(oemCalls)?File.ReadAllLines(oemCalls):Array.Empty<string>();
  Check(!sentBeforeApproval.Any(line=>line.Contains("oeminfowrite")||line.Contains("oeminfoerase")),
   "S3: no oeminfo command reaches the loader before the human approves");
  oemApproval.GetVisualDescendants().OfType<TextBox>().First().Text="I UNDERSTAND oeminfo-flash";
  Dispatcher.UIThread.RunJobs();
  Check(oemApprove.IsEnabled,"S3: typing the per-capability phrase unlocks the approve button");
  oemApprove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
  PumpOem(()=>owner.OwnedWindows.OfType<Kirin_Tool.UI.ProgressDialog>().Any());
  var oemProgress=owner.OwnedWindows.OfType<Kirin_Tool.UI.ProgressDialog>().Single();
  PumpOem(()=>oemProgress.FindControl<Button>("CloseButton")!.IsVisible);
  var oemArgv=File.ReadAllLines(oemCalls).Select(line=>System.Text.Json.JsonSerializer.Deserialize<string[]>(line)!).ToArray();
  var oemCommands=oemArgv.Where(a=>a.Length>0&&a[0]=="oem").Select(a=>a[1]).ToArray();
  Check(oemCommands.SequenceEqual(new[]{"oeminfoerase-disablewp","oeminfowrite-KTModel@NOH-AN00","oeminfowrite-KTVendor@hw-cn"}),
   "S3: the three field writes are still sent, in the same order, as 'oem <cmd>' (" + string.Join(", ",oemCommands) + ")");
  Check(oemArgv.Any(a=>a.Length>1&&a[0]=="getvar"&&a[1]=="product"),
   "S3: the write-identity gate probed the device (getvar product) before the first oem write");
  string oemStatus=oemProgress.FindControl<TextBlock>("OverallStatusTextBlock")?.Text??"";
  Check(oemStatus.Contains("Both oeminfowrite commands",StringComparison.Ordinal)&&(oemProgress.Title??"").Contains("Write Complete",StringComparison.Ordinal),
   "S3: success is still judged by the loader's OKAY (" + oemStatus.Trim() + ")");
  oemProgress.Close();Dispatcher.UIThread.RunJobs();
 } finally { try{Directory.Delete(oemDir,true);}catch{} }
}

// ------------------------------------------------- R2: closing the window cancels the operation
// The 2026-11-15 orphan: the GUI exited while a fastboot child kept running for four minutes.
// Here a stub fastboot dumps "forever"; closing the window must cancel the operation, kill the
// child process tree, and leave a cancelled message on the progress dialog.
{
 string cancelDir=Path.Combine(Path.GetTempPath(),"kirin9000-cancel-"+Guid.NewGuid().ToString("N"));
 Directory.CreateDirectory(cancelDir);
 try {
  string slow=Path.Combine(cancelDir,"slow-fastboot"), pidFile=Path.Combine(cancelDir,"child.pid");
  string[] scriptLines = {
   "#!/usr/bin/python3",
   "import os,sys,time",
   "args=sys.argv[1:]",
   "if args==['devices']:",
   "    print('TEST fastboot'); sys.exit(0)",
   "if args[:1]==['getvar']:",
   "    print('(bootloader) product: NOH-AN00'); print('OKAY'); sys.exit(0)",
   "open(" + System.Text.Json.JsonSerializer.Serialize(pidFile) + ",'w').write(str(os.getpid()))",
   "print('OKAY',flush=True)",
   "time.sleep(300)",
  };
  File.WriteAllText(slow,string.Join((char)10, scriptLines));
  if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(slow,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  bool ChildAlive(int processId) => Directory.Exists("/proc/"+processId);
  typeof(MainWindow).GetField("_fastbootFlasherService",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,new FastbootFlasherService(new FastbootClient(slow)));
  var dumpTask=(Task)typeof(MainWindow).GetMethod("DumpPartitionsProcess",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[ new List<FastbootPartition>{ new FastbootPartition("ptable") }, Path.Combine(cancelDir,"dumped") ])!;
  var until=DateTime.UtcNow.AddSeconds(30);
  while(!(File.Exists(pidFile)&&new FileInfo(pidFile).Length>0)) { Dispatcher.UIThread.RunJobs(); if(DateTime.UtcNow>until) throw new Exception("the slow dump child never started"); Thread.Sleep(20); }
  int childPid=int.Parse(File.ReadAllText(pidFile).Trim());
  var dumpProgress=owner.OwnedWindows.OfType<Kirin_Tool.UI.ProcessDialogUapp>().FirstOrDefault()??throw new Exception("no dump progress dialog");
  Check(ChildAlive(childPid),"R2: the slow dump child is running before the window is closed");
  var closeStarted=DateTime.UtcNow;
  owner.Close();
  until=DateTime.UtcNow.AddSeconds(15);
  while(!dumpTask.IsCompleted&&DateTime.UtcNow<until){Dispatcher.UIThread.RunJobs();Thread.Sleep(20);}
  Check(dumpTask.IsCompleted,"R2: closing the window finishes the running dump inside the grace period ("+(DateTime.UtcNow-closeStarted).TotalSeconds.ToString("0.0")+"s)");
  until=DateTime.UtcNow.AddSeconds(5);
  while(ChildAlive(childPid)&&DateTime.UtcNow<until) Thread.Sleep(20);
  Check(!ChildAlive(childPid),"R2: closing the window kills the fastboot child process tree (pid "+childPid+")");
  string dumpStatus=(dumpProgress.OverallStatusText??"")+" "+(dumpProgress.CurrentOperationText??"");
  Check(dumpStatus.Contains("cancel",StringComparison.OrdinalIgnoreCase),"R2: the progress dialog reports the operation as cancelled: "+dumpStatus.Trim());
  dumpTask.GetAwaiter().GetResult();
 } finally { OperationCancellation.ResetForTests(); try{Directory.Delete(cancelDir,true);}catch{} }
}

owner.Close();Console.WriteLine($"{count} Kirin9000/PSID checks passed; no device operations.");
