// Licensed under the repository's Business Source License 1.1. See LICENSE.
using System.Reflection;
using System.Text.RegularExpressions;

namespace Kirin_Tool.Services;

/// <summary>
/// D-22 (audit P09 / P08): the current session's side of the permission question.
///
/// <c>TAG+="uaccess"</c> only works for a user with an active logind seat (a local desktop
/// session). Over ssh, in a container or in CI the tag does nothing, so a rule that is "present
/// and correct" can still leave <c>open()</c> failing. Group membership (the rule's
/// <c>GROUP=</c>) is the part that works everywhere - but only if the user is actually in it.
/// </summary>
public sealed record UdevSessionContext(
    bool HasLogindSession,
    bool IsRemote,
    IReadOnlyList<string> UserGroups,
    IReadOnlyList<string> RuleGroups,
    bool ProbeFailed = false)
{
    /// <summary>Every group the installed rule names is one the current user belongs to.</summary>
    public bool GroupsCovered =>
        RuleGroups.Count > 0 && RuleGroups.All(group => UserGroups.Contains(group, StringComparer.Ordinal));

    /// <summary><c>TAG+="uaccess"</c> can work here: an active local desktop session.</summary>
    public bool DesktopUaccess => HasLogindSession && !IsRemote;

    /// <summary>The session itself can reach the device nodes.</summary>
    public bool Covered => GroupsCovered || DesktopUaccess;

    /// <summary>Stable token for JSON/logs.</summary>
    public string Kind => DesktopUaccess ? "desktop" : IsRemote ? "remote" : HasLogindSession ? "logind" : "headless";

    /// <summary>One line for the CLI text output.</summary>
    public string Describe() =>
        "groups=" + (UserGroups.Count == 0 ? "(none read)" : string.Join(",", UserGroups))
        + " ruleGroups=" + (RuleGroups.Count == 0 ? "(none)" : string.Join(",", RuleGroups))
        + " session=" + Kind
        + " uaccessUsable=" + (DesktopUaccess ? "yes" : "no")
        + (ProbeFailed ? " (probe failed)" : string.Empty);

    public static UdevSessionContext Unknown { get; } = new(false, false, Array.Empty<string>(), Array.Empty<string>(), ProbeFailed: true);
}

/// <summary>
/// Two-part answer to "can this host talk to a Huawei device without root?":
/// <see cref="RulesPresent"/> (does an installed rule grant access) and
/// <see cref="SessionAccess"/> (can the session that asks actually use that grant).
/// <see cref="Complete"/> is the conjunction - the previous single boolean claimed "rules are
/// complete" from static text alone and then reported success on a session that could not use the
/// rule at all.
/// </summary>
public record UdevRuleCoverage(bool Usb, bool Serial, bool ReadError, UdevSessionContext? Session = null, bool WorldAccessible = false)
{
    /// <summary>The rule text grants 12d1 access to both the USB device and the tty node.</summary>
    public bool RulesPresent => Usb && Serial && !ReadError;

    /// <summary>The session can use the grant (group membership, desktop uaccess, or world mode).</summary>
    public bool SessionAccess => WorldAccessible || Session == null || Session.Covered;

    public bool Complete => RulesPresent && SessionAccess;

    /// <summary><c>rules+session</c> / <c>rules-present-session-unsupported</c> / <c>rules-missing</c> / <c>rules-unreadable</c>.</summary>
    public string Verdict => !RulesPresent
        ? (ReadError ? "rules-unreadable" : "rules-missing")
        : SessionAccess ? "rules+session" : "rules-present-session-unsupported";

    /// <summary>Actionable next steps; empty exactly when <see cref="Complete"/> is true.</summary>
    public IReadOnlyList<string> Hints
    {
        get
        {
            if (Complete) return Array.Empty<string>();
            var hints = new List<string>();
            if (ReadError)
                hints.Add("some rule directories could not be read: re-run the check with the permissions that can read /etc/udev/rules.d");
            if (!RulesPresent && !ReadError)
                hints.Add("no Huawei (12d1) rule for both the USB device and the tty node: install the bundled 51-kirin-tool-fastboot.rules (the commands below write it)");
            if (RulesPresent && !SessionAccess && Session != null)
            {
                hints.Add("the rules are installed but this session cannot use them (" + Session.Kind
                          + "): TAG+=\"uaccess\" needs an active local desktop session, ssh/container/CI sessions get nothing from it");
                string groups = Session.RuleGroups.Count == 0 ? "uucp" : string.Join(",", Session.RuleGroups);
                hints.Add("give the account the group the rule names: sudo usermod -aG " + groups
                          + " $USER  (then log out and back in, or reconnect the cable)");
            }
            return hints;
        }
    }
}

/// <summary>Read-only check of the Huawei (12d1) vendor-wide permission rules.
/// This does not run udevadm, access USB, or assert the current device's permissions.</summary>
public static class UdevRuleNotice
{
    public static bool IsAppImage => OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("KIRIN_APPIMAGE") == "1";
    public static readonly string[] RuleDirectories =
        ["/etc/udev/rules.d", "/run/udev/rules.d", "/usr/local/lib/udev/rules.d", "/usr/lib/udev/rules.d", "/lib/udev/rules.d"];

    /// <summary>Embedded copy of <c>packaging/51-kirin-tool-fastboot.rules</c> (the packaged text).</summary>
    internal const string EmbeddedResourceName = "KirinTool.51-kirin-tool-fastboot.rules";

    /// <summary>
    /// D-22: the rule text is no longer a second, hand-maintained copy. It is the very file the
    /// packages install, embedded at build time, so what the GUI/CLI tells the user and what
    /// udev reads can only differ if the build is stale - and tests/UdevNotice pins that too.
    /// </summary>
    public static string DesktopRules { get; } = LoadPackagedRules();

    public static string ApplyCommands =>
        "sudo install -d -m 0755 /etc/udev/rules.d\n" +
        "sudo tee /etc/udev/rules.d/51-kirin-tool-fastboot.rules >/dev/null <<'KIRIN_UDEV_RULES'\n" +
        DesktopRules + "\nKIRIN_UDEV_RULES\nsudo udevadm control --reload-rules";

    private static readonly Regex Assignments = new(
        "(?<key>[A-Za-z_]+(?:\\{[^}]+\\})?)\\s*(?<op>==|!=|\\+=|:=|=|-=)\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.CultureInvariant);

    private static string LoadPackagedRules()
    {
        Assembly assembly = typeof(UdevRuleNotice).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                "the packaged udev rule text (" + EmbeddedResourceName + ") is missing from the assembly; "
                + "Kirin-Tool.csproj must embed packaging/51-kirin-tool-fastboot.rules");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd('\n');
    }

    /// <summary>
    /// Rule text + session context. <paramref name="session"/> defaults to a probe of this process
    /// (<see cref="ProbeSession"/>); tests inject a fake context instead of a fake udev.
    /// </summary>
    public static UdevRuleCoverage Inspect(IEnumerable<string>? directories = null, UdevSessionContext? session = null)
    {
        bool usb = false, serial = false, error = false, worldUsb = false, worldSerial = false;
        var ruleGroups = new List<string>();
        var effective = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string directory in directories ?? RuleDirectories)
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*.rules"))
                    effective.TryAdd(Path.GetFileName(file), file); // Earlier directories override by filename.
            }
            catch (IOException) { error = true; }
            catch (UnauthorizedAccessException) { error = true; }
        }
        foreach (var file in effective.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            try
            {
                var info = new FileInfo(file.Value);
                if (info.LinkTarget != null && info.ResolveLinkTarget(true)?.FullName == "/dev/null") continue;
                foreach (string line in File.ReadAllText(file.Value).Replace("\\\r\n", "").Replace("\\\n", "").Split('\n'))
                {
                    string rule = StripComment(line);
                    var terms = Assignments.Matches(rule).Select(m =>
                        (Key: m.Groups["key"].Value, Op: m.Groups["op"].Value, Value: m.Groups["value"].Value)).ToArray();
                    foreach (string group in terms.Where(t => t.Key == "GROUP" && (t.Op == "=" || t.Op == ":=")).Select(t => t.Value))
                        if (group.Length > 0 && !ruleGroups.Contains(group, StringComparer.Ordinal)) ruleGroups.Add(group);
                    foreach (string subsystem in new[] { "usb", "tty" })
                    {
                        bool vendor = terms.Any(t => t.Op == "==" && (t.Key == "ATTR{idVendor}" || t.Key == "ATTRS{idVendor}") && t.Value == "12d1");
                        bool target = terms.Any(t => t.Key == "SUBSYSTEM" && t.Op == "==" && t.Value == subsystem);
                        bool uaccess = false, worldReadWrite = false, tagFinal = false, modeFinal = false;
                        foreach (var term in terms)
                        {
                            if (term.Key == "TAG" && !tagFinal)
                            {
                                if (term.Op == "=" || term.Op == ":=") uaccess = term.Value == "uaccess";
                                else if (term.Value == "uaccess" && term.Op == "+=") uaccess = true;
                                else if (term.Value == "uaccess" && term.Op == "-=") uaccess = false;
                                tagFinal = term.Op == ":=";
                            }
                            if (term.Key == "MODE" && !modeFinal && (term.Op == "=" || term.Op == ":="))
                            {
                                worldReadWrite = Regex.IsMatch(term.Value, "^[0-7]{3,4}$") && (Convert.ToInt32(term.Value, 8) & 6) == 6;
                                modeFinal = term.Op == ":=";
                            }
                        }
                        bool accessible = uaccess || worldReadWrite;
                        // Product/interface/kernel selectors cannot establish coverage for all modes.
                        bool restricted = terms.Where(t => t.Op == "==" || t.Op == "!=").Any(t =>
                            t.Op == "!=" || !(t.Key == "SUBSYSTEM" || t.Key == "ATTR{idVendor}" || t.Key == "ATTRS{idVendor}" ||
                            (t.Key == "ACTION" && t.Value.Split('|').Contains("add")) ||
                            (t.Key == "ENV{DEVTYPE}" && subsystem == "usb" && t.Value == "usb_device") ||
                            (t.Key == "SUBSYSTEMS" && subsystem == "tty" && t.Value == "usb")));
                        if (vendor && target && accessible && !restricted)
                        {
                            // Grants accumulate: one installed rule may be a uaccess rule and
                            // another a world-writable rule for the same subsystem; the session only
                            // needs *some* rule that works for it.
                            if (subsystem == "usb") { usb = true; worldUsb |= worldReadWrite && !uaccess; }
                            else { serial = true; worldSerial |= worldReadWrite && !uaccess; }
                        }
                    }
                }
            }
            catch (IOException) { error = true; }
            catch (UnauthorizedAccessException) { error = true; }
        }
        UdevSessionContext context = session ?? ProbeSession(ruleGroups);
        return new(usb, serial, error, context, worldUsb && worldSerial);
    }

    // ---- session probe -----------------------------------------------------------------

    /// <summary>No logind seat outside a local desktop session; ssh/container sessions get no uaccess.</summary>
    public static UdevSessionContext ClassifySession(bool hasLogindSession, bool isRemote,
        IEnumerable<string> userGroups, IEnumerable<string> ruleGroups, bool probeFailed = false) =>
        new(hasLogindSession, isRemote,
            userGroups.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ruleGroups.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            probeFailed);

    /// <summary>
    /// Reads the session facts: <c>XDG_SESSION_ID</c> (surface seat) and <c>SSH_CONNECTION</c>/
    /// <c>SSH_TTY</c> (remote), plus the current user's groups from <c>/proc/self/status</c> and
    /// <c>/etc/group</c>. Every read is injectable so the harness needs neither udev nor a session.
    /// </summary>
    public static UdevSessionContext ProbeSession(IReadOnlyList<string> ruleGroups,
        Func<string, string?>? getEnvironment = null, Func<string, string?>? readFile = null,
        Func<string, bool>? fileExists = null)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        readFile ??= ReadTextFileOrNull;
        fileExists ??= File.Exists;
        try
        {
            string? sessionId = getEnvironment("XDG_SESSION_ID");
            string? sessionType = getEnvironment("XDG_SESSION_TYPE");
            bool remote = !string.IsNullOrEmpty(getEnvironment("SSH_CONNECTION")) || !string.IsNullOrEmpty(getEnvironment("SSH_TTY"));
            bool hasLogind = !remote && !string.IsNullOrEmpty(sessionId)
                && (sessionType is null || sessionType is "x11" or "wayland" or "mir" or "tty");
            var groups = ParseGroupNames(readFile("/proc/self/status") ?? string.Empty,
                fileExists("/etc/group") ? readFile("/etc/group") ?? string.Empty : string.Empty);
            return ClassifySession(hasLogind, remote, groups, ruleGroups);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ClassifySession(false, false, Array.Empty<string>(), ruleGroups, probeFailed: true);
        }
    }

    /// <summary>
    /// Turns <c>/proc/self/status</c> (<c>Groups: 27 1000 …</c>) into group <b>names</b> using
    /// <c>/etc/group</c>. A numeric id that has no name is skipped: an unnameable group cannot be
    /// compared with the rule's <c>GROUP=</c> text.
    /// </summary>
    public static IReadOnlyList<string> ParseGroupNames(string procStatus, string etcGroup)
    {
        var ids = new List<string>();
        foreach (string line in (procStatus ?? string.Empty).Split('\n'))
        {
            if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;
            // /proc/self/status separates the ids with tabs; split on any whitespace.
            ids.AddRange(line["Groups:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            break;
        }

        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (etcGroup ?? string.Empty).Split('\n'))
        {
            string[] fields = line.Split(':');
            if (fields.Length < 3 || fields[0].Length == 0) continue;
            byId.TryAdd(fields[2], fields[0]);
        }

        var names = new List<string>();
        foreach (string id in ids)
            if (byId.TryGetValue(id, out string? name) && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        return names;
    }

    private static string? ReadTextFileOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            if (line[i] == '#' && !quoted) return line[..i];
        }
        return line;
    }
}
