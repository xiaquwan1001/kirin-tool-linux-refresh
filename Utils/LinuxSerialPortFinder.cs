// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;

namespace Kirin_Tool.Utils
{
    /// <summary>
    /// Locates USB serial ports by their parent USB device rather than choosing
    /// an arbitrary tty device. This is the Linux equivalent of the Windows WMI
    /// VID/PID lookup used by the original application.
    /// </summary>
    public static class LinuxSerialPortFinder
    {
        /// <summary>
        /// K7: 候选端口按 USB 物理设备（最近的带 idVendor/idProduct 的 sysfs 祖先）归并后再选。
        ///
        /// 上游取「第一个 VID/PID 匹配的端口」，多台同型号设备同时插着时就是猜一台：把 loader
        /// 打到别人机器上的风险和 2026-11-15 的 fastboot 事故同源。现在：
        /// * 命中 1 台物理设备（哪怕它有 ttyUSB0..3 四个接口）→ 仍返回第一个端口，行为不变；
        /// * 命中 &gt;1 台 → 抛 <see cref="AmbiguousSerialDeviceException"/>，要求显式 --port 或拔掉其它设备。
        ///
        /// <paramref name="ttyClassRoot"/>/<paramref name="ports"/> 只用于离线测试（假 sysfs 树）。
        /// </summary>
        public static string? FindPort(int vendorId, int? productId = null, Func<string, bool>? descriptorMatches = null,
            Func<DirectoryInfo, bool>? interfaceMatches = null, string ttyClassRoot = "/sys/class/tty",
            IEnumerable<string>? ports = null)
        {
            IReadOnlyList<(string Port, string UsbDevice)> candidates =
                MatchingPorts(vendorId, productId, descriptorMatches, interfaceMatches, ttyClassRoot, ports);

            // One physical device, several ttys (the option driver exposes 4 interfaces): same as before.
            var devices = candidates.Select(candidate => candidate.UsbDevice)
                .Distinct(StringComparer.Ordinal).ToList();
            if (devices.Count > 1)
            {
                string inventory = string.Join(", ", candidates
                    .GroupBy(candidate => candidate.UsbDevice, StringComparer.Ordinal)
                    .Select(group => group.Key + " (" + string.Join("/", group.Select(candidate => Path.GetFileName(candidate.Port))) + ")"));
                throw new AmbiguousSerialDeviceException(
                    devices.Count + " devices with VID " + vendorId.ToString("X4")
                    + (productId.HasValue ? "/PID " + productId.Value.ToString("X4") : string.Empty)
                    + " are plugged in: " + inventory
                    + ". Refusing to pick one automatically; specify the port explicitly (--port) or unplug the other device(s).");
            }

            return candidates.Count == 0 ? null : candidates[0].Port;
        }

        /// <summary>All ports matching the identity/filters, each tagged with its physical USB device.</summary>
        private static IReadOnlyList<(string Port, string UsbDevice)> MatchingPorts(int vendorId, int? productId,
            Func<string, bool>? descriptorMatches, Func<DirectoryInfo, bool>? interfaceMatches,
            string ttyClassRoot, IEnumerable<string>? ports)
        {
            var candidates = new List<(string Port, string UsbDevice)>();
            if (!OperatingSystem.IsLinux())
            {
                return candidates;
            }

            foreach (string port in (ports ?? SerialPort.GetPortNames()).OrderBy(port => port, StringComparer.Ordinal))
            {
                if (!IsUsbSerialPort(port) ||
                    !TryGetUsbDevice(port, out DirectoryInfo? usbDevice, out DirectoryInfo? serialDevice, ttyClassRoot))
                {
                    continue;
                }

                // Invariant: TryGetUsbDevice assigns usbDevice != null exactly when it returns true,
                // so `!` here never hits a null dereference.
                if (!MatchesUsbIdentity(usbDevice!, vendorId, productId))
                {
                    continue;
                }

                string descriptor = GetUsbDescriptor(serialDevice);
                if ((descriptorMatches == null && interfaceMatches == null) ||
                    (descriptorMatches?.Invoke(descriptor) ?? false) ||
                    (interfaceMatches?.Invoke(serialDevice!) ?? false))
                {
                    candidates.Add((port, usbDevice!.FullName));
                }
            }

            return candidates;
        }

        /// <summary>
        /// Huawei hw_cdcacm.inf maps subclass 13/protocol 23 to DBAdapter
        /// Reserved Interface. Read the nearest USB interface, never a sibling.
        /// This only recognizes a port already provided by a kernel driver.
        /// </summary>
        public static bool IsHuaweiUsbUpdateInterface(DirectoryInfo serialDevice)
        {
            for (DirectoryInfo? directory = serialDevice; directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "idVendor")))
                    return false;
                if (File.Exists(Path.Combine(directory.FullName, "bInterfaceClass")))
                    return string.Equals(ReadAttribute(directory, "bInterfaceClass"), "ff", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(ReadAttribute(directory, "bInterfaceSubClass"), "13", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(ReadAttribute(directory, "bInterfaceProtocol"), "23", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static bool IsUsbSerialPort(string port)
        {
            return port.StartsWith("/dev/ttyUSB", StringComparison.Ordinal) ||
                   port.StartsWith("/dev/ttyACM", StringComparison.Ordinal) ||
                   port.StartsWith("/dev/ttyGS", StringComparison.Ordinal);
        }

        private static bool TryGetUsbDevice(string port, out DirectoryInfo? usbDevice, out DirectoryInfo? serialDevice,
            string ttyClassRoot = "/sys/class/tty")
        {
            serialDevice = ResolveSerialDevice(port, ttyClassRoot);
            for (DirectoryInfo? directory = serialDevice; directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "idVendor")) &&
                    File.Exists(Path.Combine(directory.FullName, "idProduct")))
                {
                    usbDevice = directory;
                    return true;
                }
            }

            usbDevice = null;
            return false;
        }

        public static DirectoryInfo? ResolveSerialDevice(string port, string ttyClassRoot = "/sys/class/tty")
        { // note: may return null when the device link cannot be resolved
            // Resolve the class symlink FIRST. The relative device link is
            // relative to its real parent, not /sys/class/tty/<name>.
            var entry = new DirectoryInfo(Path.Combine(ttyClassRoot, Path.GetFileName(port)));
            var realEntry = entry.ResolveLinkTarget(true) as DirectoryInfo ?? entry;
            return new DirectoryInfo(Path.Combine(realEntry.FullName, "device"))
                .ResolveLinkTarget(true) as DirectoryInfo;
        }

        public static bool IsPortUsingDriver(string port, string driverName, string ttyClassRoot = "/sys/class/tty")
        {
            for (var directory = ResolveSerialDevice(port, ttyClassRoot); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "idVendor"))) break;
                var driver = new DirectoryInfo(Path.Combine(directory.FullName, "driver"));
                if (driver.Exists && driver.ResolveLinkTarget(true)?.Name == driverName) return true;
            }
            return false;
        }

        /// <summary>
        /// D-18: the resolved kernel driver name of a tty (the nearest `driver` link below the USB
        /// device), or null when there is no such link. Read-only: it only walks sysfs, never opens
        /// the tty, so probing cannot disturb a running transfer.
        /// </summary>
        public static string? GetPortDriverName(string port, string ttyClassRoot = "/sys/class/tty")
        {
            for (var directory = ResolveSerialDevice(port, ttyClassRoot); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "idVendor"))) break;
                var driver = new DirectoryInfo(Path.Combine(directory.FullName, "driver"));
                if (!driver.Exists) continue;
                string? name = driver.ResolveLinkTarget(true)?.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return null;
        }

        private static bool MatchesUsbIdentity(DirectoryInfo usbDevice, int vendorId, int? productId)
        {
            string? vendor = ReadAttribute(usbDevice, "idVendor");
            string? product = ReadAttribute(usbDevice, "idProduct");

            return string.Equals(vendor, $"{vendorId:X4}", StringComparison.OrdinalIgnoreCase) &&
                   (!productId.HasValue ||
                    string.Equals(product, $"{productId.Value:X4}", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetUsbDescriptor(DirectoryInfo? usbDevice)
        {
            var values = new List<string>();
            for (DirectoryInfo? directory = usbDevice; directory != null; directory = directory.Parent)
            {
                foreach (string attribute in new[] { "manufacturer", "product", "interface" })
                {
                    string? value = ReadAttribute(directory, attribute);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value);
                    }
                }
            }

            return string.Join(" ", values);
        }

        private static string? ReadAttribute(DirectoryInfo directory, string attribute)
        {
            try
            {
                string path = Path.Combine(directory.FullName, attribute);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// K7: more than one physical USB device matches the VID/PID (or descriptor) filter. The caller
    /// must not guess — the same rule the fastboot side enforces when <c>fastboot devices</c> shows
    /// more than one serial (see <see cref="ProcessRunner"/>).
    /// </summary>
    public sealed class AmbiguousSerialDeviceException : IOException
    {
        public AmbiguousSerialDeviceException(string message) : base(message) { }
    }
}
