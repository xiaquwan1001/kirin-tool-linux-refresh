using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kirin_Tool;
using Kirin_Tool.Services;
using System.Reflection;

int checks=0;
void Check(bool value,string name) { if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);checks++; }
string root=Path.Combine(Path.GetTempPath(),"kirin-udev-test-"+Guid.NewGuid().ToString("N"));
string? oldFlag=Environment.GetEnvironmentVariable("KIRIN_APPIMAGE");
Directory.CreateDirectory(root);
try {
 string high=Path.Combine(root,"etc"),low=Path.Combine(root,"lib");Directory.CreateDirectory(high);Directory.CreateDirectory(low);
 string[] directories=[high,low];

 // ---- D-22: the session half is injected, so these rules-only assertions do not depend on
 // whether the machine running the harness has a desktop session, ssh, or nothing at all.
 UdevSessionContext desktop=UdevRuleNotice.ClassifySession(true,false,new[]{"uucp"},new[]{"uucp"});
 UdevSessionContext headlessOutOfGroup=UdevRuleNotice.ClassifySession(false,false,new[]{"wheel"},new[]{"uucp"});
 UdevSessionContext headlessInGroup=UdevRuleNotice.ClassifySession(false,false,new[]{"uucp"},new[]{"uucp"});
 UdevSessionContext sshOutOfGroup=UdevRuleNotice.ClassifySession(true,true,new[]{"wheel"},new[]{"uucp"});
 // The packaged text carries explanatory comments; the assertions below need the ACTION lines.
 string[] ruleLines=UdevRuleNotice.DesktopRules.Split('\n').Where(l=>l.StartsWith("ACTION==")).ToArray();

 void Rule(string text)=>File.WriteAllText(Path.Combine(low,"51-kirin.rules"),text);
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"missing rules need notice");
 Rule(UdevRuleNotice.DesktopRules);
 Check(UdevRuleNotice.Inspect(directories,desktop).Complete,"both Huawei desktop rules recognized");
 Rule("# "+UdevRuleNotice.DesktopRules.Replace("\n","\n# "));
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"commented rules do not hide notice");
 Rule(ruleLines[0]);
 Check(UdevRuleNotice.Inspect(directories,desktop) is {Usb:true,Serial:false},"USB alone does not cover VCOM");
 Rule(ruleLines[1]);
 Check(UdevRuleNotice.Inspect(directories,desktop) is {Usb:false,Serial:true},"VCOM alone does not cover USB");
 Rule(UdevRuleNotice.DesktopRules.Replace("12d1","18d1"));
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"other vendors do not cover Huawei");
 Rule(UdevRuleNotice.DesktopRules.Replace(", TAG+=\"uaccess\"",""));
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"selectors without permission grant do not hide notice");
 Rule(UdevRuleNotice.DesktopRules.Replace("0660","0666").Replace(", TAG+=\"uaccess\"",""));
 Check(UdevRuleNotice.Inspect(directories,desktop).Complete,"equivalent world read/write rules recognized");
 Rule(UdevRuleNotice.DesktopRules.Replace("TAG+=\"uaccess\"","TAG+=\"uaccess\", TAG-=\"uaccess\""));
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"removed access tag does not hide notice");
 Rule(UdevRuleNotice.DesktopRules.Replace("0660","0666").Replace("TAG+=\"uaccess\"","MODE=\"0000\""));
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"later mode override does not hide notice");
 Rule(UdevRuleNotice.DesktopRules.Replace("MODE=","ATTR{idProduct}==\"107e\", MODE="));
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"restricted product does not establish all mode permissions");
 Rule(UdevRuleNotice.DesktopRules.Replace(", MODE=",", \\\nMODE="));
 Check(UdevRuleNotice.Inspect(directories,desktop).Complete,"continued rules recognized");
 Rule(UdevRuleNotice.DesktopRules);File.WriteAllText(Path.Combine(high,"51-kirin.rules"),"# overridden");
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"etc filename override prevents stale lower match");
 File.Delete(Path.Combine(high,"51-kirin.rules"));File.CreateSymbolicLink(Path.Combine(high,"51-kirin.rules"),"/dev/null");
 Check(!UdevRuleNotice.Inspect(directories,desktop).Complete,"dev null mask disables lower rule");
 File.Delete(Path.Combine(high,"51-kirin.rules"));
 File.Delete(Path.Combine(low,"51-kirin.rules"));
 File.WriteAllText(Path.Combine(low,"usb.rules"),ruleLines[0]);
 File.WriteAllText(Path.Combine(high,"tty.rules"),ruleLines[1]);
 Check(UdevRuleNotice.Inspect(directories,desktop).Complete,"coverage combines files without requiring a fixed filename");

 // ---- D-22 part 2: rule text + session context -------------------------------------
 Rule(UdevRuleNotice.DesktopRules);
 Check(UdevRuleNotice.Inspect(directories,desktop).Verdict=="rules+session","desktop session names the rules+session verdict");
 File.WriteAllText(Path.Combine(high,"70-other-vendor.rules"),"ACTION==\"add\", SUBSYSTEM==\"usb\", ATTR{idVendor}==\"18d1\", MODE=\"0660\", GROUP=\"camera\", TAG+=\"uaccess\""+'\n');
 Check(!UdevRuleNotice.Inspect(directories,desktop).Session!.RuleGroups.Contains("camera"),"groups are only collected from rules that cover Huawei 12d1");
 Check(UdevRuleNotice.Inspect(directories,desktop).Session!.RuleGroups.SequenceEqual(new[]{"uucp"}),"the Huawei rule contributes exactly its own group");
 File.Delete(Path.Combine(high,"70-other-vendor.rules"));
 Check(UdevRuleNotice.Inspect(directories,headlessInGroup).Complete,"a group member can use the rule without a logind session");
 Check(UdevRuleNotice.Inspect(directories,headlessInGroup).Verdict=="rules+session","group membership alone is a complete answer");
 UdevRuleCoverage headless=UdevRuleNotice.Inspect(directories,headlessOutOfGroup);
 Check(headless.RulesPresent,"the rule text is still recognized as present");
 Check(!headless.SessionAccess && !headless.Complete,"rules present + headless session + not in the group is NOT complete");
 Check(headless.Verdict=="rules-present-session-unsupported","the verdict names the session half");
 Check(headless.Hints.Any(h=>h.Contains("usermod -aG uucp")),"the hint gives the exact group command");
 Check(headless.Hints.Any(h=>h.Contains("uaccess")),"the hint explains why uaccess did not help");
 UdevRuleCoverage remote=UdevRuleNotice.Inspect(directories,sshOutOfGroup);
 Check(!remote.Complete,"an ssh session without the group is not complete");
 Check(remote.Session is {Kind:"remote"},"the ssh session is reported as remote");
 Rule(UdevRuleNotice.DesktopRules.Replace("0660","0666").Replace(", TAG+=\"uaccess\"",""));
 var worldRule=UdevRuleNotice.Inspect(directories,headlessOutOfGroup); Check(worldRule.Complete,"world-writable rules need no group and no session (verdict="+worldRule.Verdict+" usb="+worldRule.Usb+" serial="+worldRule.Serial+" world="+worldRule.WorldAccessible+")");
 Check(UdevRuleNotice.Inspect(directories,headlessOutOfGroup).WorldAccessible,"world-writable is reported as such");
 Rule(UdevRuleNotice.DesktopRules);

 // ---- the rule text is the packaged file, not a second copy -------------------------
 string packagedPath=Path.Combine(root,"..","..","packaging","51-kirin-tool-fastboot.rules");
 string repoPackaged=Path.Combine(Directory.GetCurrentDirectory(),"packaging","51-kirin-tool-fastboot.rules");
 string packaged=File.ReadAllText(File.Exists(repoPackaged)?repoPackaged:packagedPath).TrimEnd('\n');
 Check(UdevRuleNotice.DesktopRules==packaged,"the printed rules are byte-identical to packaging/51-kirin-tool-fastboot.rules");
 Check(!UdevRuleNotice.DesktopRules.Contains("adbusers") && !UdevRuleNotice.DesktopRules.Contains("plugdev"),"no distro-specific group split remains in the rule text");
 Check(UdevRuleNotice.DesktopRules.Split('\n').Where(l=>l.StartsWith("ACTION==")).All(l=>l.Contains("GROUP=\"uucp\"")),"both packaged rules grant the same group");
 string deb=File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(),"packaging","build-deb.sh"));
 Check(!deb.Contains("s/GROUP="),"build-deb.sh no longer rewrites the group");

 // ---- session/context probing is pure and injectable -------------------------------
 var parsed=UdevRuleNotice.ParseGroupNames("Name:\tx\nGroups:\t27 1000 1001 \n","root:x:0:\nwheel:x:1000:u\nusers:x:1001:\nsudo:x:27:u\n");
 Check(parsed.SequenceEqual(new[]{"sudo","wheel","users"}),"group ids are mapped to names in order: "+string.Join(",",parsed));
 Check(!UdevRuleNotice.ParseGroupNames("Name:\tx\n","wheel:x:1000:").Any(),"a process status without Groups: yields no groups");
 Check(!UdevRuleNotice.ParseGroupNames("Groups:\t4242 \n","wheel:x:1000:").Any(),"an unnameable group id is skipped, not guessed");
 var probed=UdevRuleNotice.ProbeSession(new[]{"uucp"},key=>key switch{"XDG_SESSION_ID"=>"3","XDG_SESSION_TYPE"=>"wayland",_=>null},path=>path=="/proc/self/status"?"Groups:\t1000 \n":path=="/etc/group"?"wheel:x:1000:\nuucp:x:14:\n":null,path=>true);
 Check(probed.HasLogindSession && !probed.IsRemote && !probed.ProbeFailed,"an XDG wayland session is recognized as a logind desktop session");
 Check(UdevRuleNotice.ProbeSession(new[]{"uucp"},key=>key switch{"SSH_CONNECTION"=>"1.2.3.4 5 6",_=>"3"},path=>null,path=>false) is {IsRemote:true,HasLogindSession:false},"SSH_CONNECTION makes the session remote, not desktop");
 var failed=UdevRuleNotice.ProbeSession(new[]{"uucp"},key=>throw new System.IO.IOException("boom"),path=>null,path=>false);
 Check(failed.ProbeFailed,"a failing probe is reported, not treated as 'no groups needed'");
 Check(!new UdevRuleCoverage(true,true,false,failed).Complete,"an unknown/failed session probe does not claim completeness");

 File.CreateSymbolicLink(Path.Combine(high,"broken.rules"),Path.Combine(root,"absent"));
 Check(UdevRuleNotice.Inspect(directories,desktop) is {ReadError:true,Complete:false},"unreadable effective rule cannot silently hide notice");
 Check(UdevRuleNotice.ApplyCommands.Contains(UdevRuleNotice.DesktopRules) && !UdevRuleNotice.ApplyCommands.Contains("/tmp/"),"copy script includes rules and survives closing AppImage");
 File.WriteAllText(Path.Combine(root,"commands.sh"),UdevRuleNotice.ApplyCommands);
 using(var syntax=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash"){ArgumentList={"-n",Path.Combine(root,"commands.sh")}})!) {syntax.WaitForExit();Check(syntax.ExitCode==0,"copyable heredoc shell syntax valid; never executed");}
 Environment.SetEnvironmentVariable("KIRIN_APPIMAGE",null);
 System.Environment.SetEnvironmentVariable("KIRIN_TOOL_SETTINGS_DIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kirin-tool-test-settings")); AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
 var window=new MainWindow();window.Show();Dispatcher.UIThread.RunJobs();
 var render=typeof(MainWindow).GetMethod("RenderUdevNotice",BindingFlags.Instance|BindingFlags.NonPublic)!;
 var notice=window.FindControl<Border>("UdevNotice")!;
 render.Invoke(window,[new UdevRuleCoverage(false,false,false)]);
 Check(!notice.IsVisible,"installed app does not show AppImage notice");
 Environment.SetEnvironmentVariable("KIRIN_APPIMAGE","1");
 render.Invoke(window,[new UdevRuleCoverage(false,false,false)]);Dispatcher.UIThread.RunJobs();
 Check(notice.IsVisible && window.FindControl<TextBox>("UdevCommands")!.Text==UdevRuleNotice.ApplyCommands,"AppImage missing rules shows readable command box");
 window.FindControl<Button>("CopyUdevCommandsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 Dispatcher.UIThread.RunJobs();
 Check(window.FindControl<TextBlock>("UdevCopyStatus")!.Text=="Commands copied","actual copy button updates status");
 Check(window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult()==UdevRuleNotice.ApplyCommands,"clipboard receives complete commands");
 render.Invoke(window,[new UdevRuleCoverage(true,true,false,desktop)]);
 Check(!notice.IsVisible,"existing complete rules hide AppImage notice");
 render.Invoke(window,[new UdevRuleCoverage(true,true,false,headlessOutOfGroup)]);Dispatcher.UIThread.RunJobs();
 Check(notice.IsVisible,"rules present but unusable session shows the notice (D-22)");
 Check(window.FindControl<TextBlock>("UdevNoticeDescription")!.Text!.Contains("cannot use them yet"),"the banner states the session half, not 'rules missing'");
 render.Invoke(window,[new UdevRuleCoverage(true,true,true)]);
 Check(notice.IsVisible && window.FindControl<TextBlock>("UdevNoticeDescription")!.Text!.Contains("could not be read"),"read failure explains notice");
 string? screenshot=Environment.GetEnvironmentVariable("KIRIN_UDEV_SCREENSHOT");
 if(screenshot!=null){render.Invoke(window,[new UdevRuleCoverage(false,false,false)]);Dispatcher.UIThread.RunJobs();Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();window.CaptureRenderedFrame()?.Save(screenshot);}
 window.Close();
 Console.WriteLine($"{checks} udev notice checks passed; no host rule writes or USB operations.");
} finally {Environment.SetEnvironmentVariable("KIRIN_APPIMAGE",oldFlag);Directory.Delete(root,true);}
