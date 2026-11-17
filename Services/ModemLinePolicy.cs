// Licensed under the repository's Business Source License 1.1. See LICENSE.
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services;

/// <summary>How the flasher drives the modem lines of the VCOM tty.</summary>
public enum ModemLineMode
{
    /// <summary>The historical Windows behaviour: <c>Handshake.RequestToSend</c> + <c>DtrEnable</c>.</summary>
    ModemLines,
    /// <summary>No modem-line signalling at all (no RTS/DTR request): the BootROM's bulk endpoint needs none.</summary>
    BulkOnly,
}

/// <summary>
/// What the driver probe could prove about the tty. <see cref="Known"/> is false when sysfs had no
/// <c>driver</c> link (or the probe was not run at all), which is exactly the case the old
/// "driver name == option" rule silently turned into "use modem lines".
/// </summary>
public sealed record DriverProbeResult(string? Driver, bool Attempted, string? Error)
{
    /// <summary>A driver name was actually read.</summary>
    public bool Known => Attempted && Driver != null && Error == null;

    public static DriverProbeResult Of(string? driver) => new(driver, true, null);

    /// <summary>The probe ran but found nothing / could not run.</summary>
    public static DriverProbeResult Unavailable(string? error = null) => new(null, false, error);

    /// <summary>Non-Linux platforms: no sysfs probe exists, and none is needed.</summary>
    public static DriverProbeResult NotApplicable { get; } = new(null, false, "not Linux");
}

/// <summary>
/// D-18 (audit P02 / H15): the Linux modem-line decision, as a pure value.
///
/// Before this file <c>VcomFlasher.Connect</c> asked exactly one question - "is the kernel driver
/// literally <c>option</c>?" - and answered "use RTS/DTR" for every other answer, including
/// "the probe found no driver at all". A BootROM behind <c>cdc_acm</c> (or any renamed driver)
/// therefore had <c>SET_CONTROL_LINE_STATE</c> requested, which the 12d1:3609 endpoint stalls: the
/// symptom is "connected, then the first frame dies", and it used to be indistinguishable from a
/// cable/device problem.
///
/// The decision is now evidence-based and it says which path it took:
///  * caller forced <c>BulkOnly</c> (<c>--bulk-only</c>, or an explicit port) wins - the escapes
///    stay exactly as they were;
///  * Linux: RTS/DTR are cleared first (the <c>option</c>-driver principle). When the probe proves
///    <c>option</c> this is verified evidence; when it proves another driver, or proves nothing,
///    the same no-modem-line path is still taken but reported as <b>unverified</b>, and
///    <see cref="VcomFlasher"/> retries once with the historical settings if the open fails;
///  * non-Linux keeps the historical settings untouched.
///
/// Nothing here opens a port: the wire behaviour is decided by <see cref="ModemLineMode"/> and
/// nothing else.
/// </summary>
public sealed record ModemLineDecision(ModemLineMode Mode, string Origin, string Reason, DriverProbeResult Probe)
{
    public bool BulkOnly => Mode == ModemLineMode.BulkOnly;

    /// <summary>True when the clearing path rests on a positively identified driver.</summary>
    public bool Verified => Origin == ModemLinePolicy.OriginDriverOption || Origin == ModemLinePolicy.OriginForced
                            || Origin == ModemLinePolicy.OriginExplicitPort;

    /// <summary>One line an operator/doctor/log can read without guessing.</summary>
    public string Describe() =>
        (BulkOnly ? "bulk-only (RTS/DTR cleared" : "modem lines requested (Handshake.RequestToSend + DTR")
        + "; origin=" + Origin
        + "; verified=" + (Verified ? "yes" : "no")
        + "; driver=" + (Probe.Driver ?? (Probe.Attempted ? "none" : "probe-unavailable"))
        + "): " + Reason;
}

public static class ModemLinePolicy
{
    /// <summary>The kernel driver the captured 12d1:3609 BootROM uses on Linux.</summary>
    public const string OptionDriverName = "option";

    public const string OriginForced = "forced";
    public const string OriginNonLinux = "non-linux";
    public const string OriginExplicitPort = "explicit-port";
    public const string OriginDriverOption = "driver-option";
    public const string OriginCapabilityProbe = "capability-probe";

    /// <summary>
    /// Chooses the modem-line mode. <paramref name="forcedBulkOnly"/> is the caller's explicit
    /// answer (<c>VcomFlasherOptions.BulkOnly</c> / <c>--bulk-only</c>); <c>null</c> means "decide
    /// from the evidence".
    /// </summary>
    public static ModemLineDecision Decide(bool isLinux, bool explicitPort, bool? forcedBulkOnly, DriverProbeResult probe)
    {
        if (forcedBulkOnly is { } forced)
        {
            return new(forced ? ModemLineMode.BulkOnly : ModemLineMode.ModemLines, OriginForced,
                "forced by the caller (VcomFlasherOptions.BulkOnly / --bulk-only); no probe is consulted", probe);
        }

        if (!isLinux)
        {
            return new(ModemLineMode.ModemLines, OriginNonLinux,
                "non-Linux: the historical Handshake.RequestToSend + DTR settings are kept unchanged", probe);
        }

        if (explicitPort)
        {
            return new(ModemLineMode.BulkOnly, OriginExplicitPort,
                "explicit port: a pty or a hand-picked tty has no modem lines to signal", probe);
        }

        if (string.Equals(probe.Driver, OptionDriverName, StringComparison.Ordinal))
        {
            return new(ModemLineMode.BulkOnly, OriginDriverOption,
                "driver=option: the captured 12d1:3609 BootROM stalls SET_CONTROL_LINE_STATE (EPIPE) before any frame", probe);
        }

        if (probe.Known)
        {
            return new(ModemLineMode.BulkOnly, OriginCapabilityProbe,
                "driver=" + probe.Driver + " is not the option driver: no evidence that SET_CONTROL_LINE_STATE is accepted, "
                + "so RTS/DTR are cleared first (unverified capability assumption; the open falls back to modem lines and logs it)", probe);
        }

        string why = probe.Error != null ? "driver probe failed (" + probe.Error + ")"
            : probe.Attempted ? "no driver link in sysfs" : "driver probe not attempted";
        return new(ModemLineMode.BulkOnly, OriginCapabilityProbe,
            why + ": RTS/DTR clearing is attempted first as a no-op for the bulk endpoint "
            + "(unverified; the open falls back to modem lines and logs it)", probe);
    }

    /// <summary>
    /// Reads the tty's kernel driver name. <paramref name="injected"/> lets tests (and any future
    /// non-sysfs platform) supply the name; a throwing probe is reported, never swallowed.
    /// </summary>
    public static DriverProbeResult ProbeDriver(string? portName, Func<string, string?>? injected = null,
        string ttyClassRoot = "/sys/class/tty")
    {
        if (portName is not { Length: > 0 }) return DriverProbeResult.Unavailable("no port name yet");
        if (!OperatingSystem.IsLinux()) return DriverProbeResult.NotApplicable;

        try
        {
            string? driver = injected != null
                ? injected(portName)
                : LinuxSerialPortFinder.GetPortDriverName(portName, ttyClassRoot);
            return DriverProbeResult.Of(driver);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DriverProbeResult.Unavailable(ex.GetType().Name + ": " + ex.Message);
        }
    }
}
