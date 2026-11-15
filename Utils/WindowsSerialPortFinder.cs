// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace Kirin_Tool.Utils;

/// <summary>
/// One Windows PnP entity that looks like a serial port. <see cref="DeviceId"/> is the WMI
/// <c>PNPDeviceID</c>; <paramref name="ParentDeviceId"/> is optional because `Win32_PnPEntity`
/// does not expose a parent column, but an injected enumerator (tests, other transports) may.
/// </summary>
public sealed record WindowsSerialDevice(string? Port, string? DeviceId, string? ParentDeviceId, string? Name);

/// <summary>
/// R3 (audit 2026-11-15 §4): the Windows half of the K7 port-uniqueness rule.
///
/// The upstream Windows lookup took the first PnP entity whose name contained "COM", i.e. it
/// guessed one device out of however many were plugged in — the same shape as the Linux
/// "first ttyUSB" bug that K7 fixed. This finder groups the COM candidates by their *physical*
/// USB device first:
///
/// * one physical device with several COM interfaces (a Huawei composite device) still counts as
///   one device and keeps returning its first port — GUI behaviour unchanged;
/// * more than one physical device throws <see cref="AmbiguousSerialDeviceException"/> (the type
///   K7 introduced for Linux), naming both devices and telling the operator to pass `--port`.
///
/// The enumeration is an injectable seam (`FindPort`'s third argument, exposed as
/// <see cref="WindowsEnumerator"/>) because Windows cannot be
/// exercised on this host; the grouping rules themselves are hardware-free and unit tested.
/// </summary>
public static class WindowsSerialPortFinder
{
    /// <summary>Injection point: (vendorId, productId) → every PnP entity matching that VID/PID.</summary>
    public static Func<int, int, IReadOnlyList<WindowsSerialDevice>> WindowsEnumerator
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return None;
            return EnumerateWindows;
        }
    }

    /// <summary>
    /// The COM port of the one matching physical device, or null when nothing matched.
    /// Throws <see cref="AmbiguousSerialDeviceException"/> when more than one physical device has it.
    /// </summary>
    public static string? FindPort(int vendorId, int productId,
        Func<int, int, IReadOnlyList<WindowsSerialDevice>>? enumerate = null)
    {
        // The WMI enumerator is Windows-only; on any other host the finder simply matches nothing
        // (VcomFlasher only calls it under OperatingSystem.IsWindows(), this keeps the analyzer honest).
        Func<int, int, IReadOnlyList<WindowsSerialDevice>> defaultEnumerator =
            OperatingSystem.IsWindows() ? EnumerateWindows : None;
        IReadOnlyList<WindowsSerialDevice> candidates = (enumerate ?? defaultEnumerator)(vendorId, productId)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Port))
            .ToList();

        var devices = candidates
            .Select(candidate => PhysicalDeviceKey(candidate.DeviceId, candidate.ParentDeviceId) ?? "(unknown device)")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (devices.Count > 1)
        {
            string inventory = string.Join(", ", candidates
                .GroupBy(candidate => PhysicalDeviceKey(candidate.DeviceId, candidate.ParentDeviceId) ?? "(unknown device)",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key + " (" + string.Join("/", group.Select(candidate => candidate.Port)) + ")"));
            throw new AmbiguousSerialDeviceException(
                devices.Count + " devices with VID " + vendorId.ToString("X4") + "/PID " + productId.ToString("X4")
                + " are plugged in: " + inventory
                + ". Refusing to pick one COM port automatically; specify the port explicitly (--port) or unplug the other device(s).");
        }

        return candidates.Count == 0 ? null : candidates[0].Port;
    }

    /// <summary>
    /// The physical USB device behind a PnP id: the composite interface suffix (`&MI_xx`) and the
    /// per-interface instance (the trailing backslash part) are not part of it. `ParentDeviceId`,
    /// when the enumerator knows it, is authoritative.
    /// </summary>
    public static string? PhysicalDeviceKey(string? deviceId, string? parentDeviceId = null)
    {
        if (!string.IsNullOrWhiteSpace(parentDeviceId)) return parentDeviceId.Trim();
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        string id = deviceId.Trim();
        int slash = id.LastIndexOf('\\');
        if (slash <= 0) return id;

        // The device (not interface) part: a composite child carries "&MI_xx" before the instance.
        string prefix = id[..slash];
        int interfaceMarker = prefix.IndexOf("&MI_", StringComparison.OrdinalIgnoreCase);
        if (interfaceMarker > 0) prefix = prefix[..interfaceMarker];

        // A USB child instance ends with "&<interface index>&<...>": that tail differs per interface
        // of the same phone, while the leading field ("6&1a2b3c4d") is derived from the parent path
        // and therefore is what distinguishes two identically named phones.
        string instance = id[(slash + 1)..];
        string[] parts = instance.Split('&');
        if (parts.Length >= 3) instance = parts[0] + "&" + parts[1];

        return prefix + "\\" + instance;
    }

    /// <summary>Pull `COM12` out of a PnP name like `HUAWEI Mobile Connect - 3G PC UI Interface (COM12)`.</summary>
    public static string? PortFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        int open = name.LastIndexOf('(');
        if (open < 0) return null;
        int close = name.IndexOf(')', open + 1);
        if (close < 0) return null;

        string inner = name[(open + 1)..close].Trim();
        if (inner.Length <= 3 || !inner.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (char ch in inner[3..])
            if (!char.IsAsciiDigit(ch)) return null;
        return inner;
    }

    private static IReadOnlyList<WindowsSerialDevice> None(int vendorId, int productId) =>
        Array.Empty<WindowsSerialDevice>();

    /// <summary>Default enumerator: the same WMI query the upstream lookup used, plus the device id.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WindowsSerialDevice> EnumerateWindows(int vendorId, int productId)
    {
        string pattern = $"VID_{vendorId:X4}&PID_{productId:X4}";
        using var searcher = new ManagementObjectSearcher(
            $"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%{pattern}%'");

        var devices = new List<WindowsSerialDevice>();
        foreach (ManagementBaseObject device in searcher.Get())
        {
            string? name = device["Name"]?.ToString();
            string? port = PortFromName(name);
            if (port == null) continue;
            devices.Add(new WindowsSerialDevice(port, device["DeviceID"]?.ToString(), null, name));
        }
        return devices;
    }
}
