// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

/*
 * Copyright (c) 2026 Kethily Daniel & NDXCode. All rights reserved.
 * 
 * Use of this software is governed by the Business Source License included 
 * in the LICENSE file and at www.mariadb.com/bsl11.
 * 
 * Change Date: Four years from the date each version of the Licensed Work 
 * is first publicly distributed.
 * 
 * On the Change Date, in accordance with the Business Source License, 
 * use of this software will be governed by the GNU General Public License v3.0 
 * or later (GPL-3.0-or-later).
 * 
 * Contact Information: https://kirintool.cfd
 */

using Kirin_Tool.Utils;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Kirin_Tool.Services
{
    /// <summary>
    /// How <see cref="VcomFlasher"/> treats the one-byte answer the BootROM returns for a
    /// command frame. The flasher itself has no default of its own: the entry point decides.
    /// The GUI has always used <c>Lenient</c> (read a byte, do not validate its value, never
    /// fail on silence); the CLI and MCP entry points default to <c>Strict</c> since v2.4.15.
    /// </summary>
    public enum VcomAckMode
    {
        /// <summary>Read the ACK and fail unless it is <c>0xAA</c>.</summary>
        Strict,
        /// <summary>Read the ACK, record it, never fail (GUI behaviour).</summary>
        Lenient,
        /// <summary>Do not wait for an ACK at all (blind send, like <c>expectAck: false</c>).</summary>
        Off,
    }

    /// <summary>One frame as it went out on the wire (plus the ACK it produced, if any).</summary>
    public sealed record VcomFrameInfo(string Kind, int Bytes, int? Address, int? Sequence, byte? Ack, string AckStatus);

    /// <summary>
    /// Injection points for the CLI/MCP path and for offline pty tests. Every property keeps
    /// the GUI behaviour when left at its default (auto-detect 12d1:3609, lenient ACK, 10 s).
    /// </summary>
    public sealed class VcomFlasherOptions
    {
        /// <summary>Explicit tty/COM path; skips the 12d1:3609 enumeration.</summary>
        public string? PortName { get; init; }
        /// <summary>
        /// Force the bulk-only framing (no modem-line signalling). Default: explicit port → true,
        /// auto-detect → the D-18 capability probe in <see cref="ModemLinePolicy"/>.
        /// </summary>
        public bool? BulkOnly { get; init; }
        /// <summary>
        /// D-18: driver-name probe used by the Linux modem-line decision. Left null it reads sysfs
        /// (<see cref="Kirin_Tool.Utils.LinuxSerialPortFinder.GetPortDriverName"/>); tests inject a
        /// fake. A throwing probe is recorded, never fatal.
        /// </summary>
        public Func<string, string?>? DriverProbe { get; init; }
        /// <summary>
        /// ACK policy. The class default stays <c>Lenient</c> because the GUI builds these
        /// options without setting it (the GUI must keep its historical "read it, never fail
        /// on it" behaviour). The <b>CLI and MCP entry points default to <c>Strict</c></b>:
        /// they pass an explicit value from <c>--ack-mode</c> (whose CLI/MCP default is
        /// <c>strict</c>), so the difference lives in the entry layer, not here.
        /// See docs/ack-default.md.
        /// </summary>
        public VcomAckMode AckMode { get; init; } = VcomAckMode.Lenient;
        public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(10);
        /// <summary>Per-frame trace sink (CLI writes these to stderr).</summary>
        public Action<VcomFrameInfo>? FrameObserver { get; init; }
        /// <summary>Session audit log shared with the GUI (see <see cref="VcomSessionLogger"/>).</summary>
        public VcomSessionLogger? Logger { get; init; }
    }

    public class VcomFlasher : IDisposable
    {
        private const int IDT_VID = 0x12D1;
        private const int IDT_PID = 0x3609;
        private const int MAX_DATA_LEN = 0x400;

        // Null until Connect() succeeds; every reader already null-checks via `?.`
        // (IsOpen, Dispose), so `?` just makes the existing invariant explicit.
        private SerialPort? _serialPort;
        private readonly VcomFlasherOptions _options;
        private long _bytesSinceHead;

        public VcomFlasher(VcomFlasherOptions? options = null)
        {
            _options = options ?? new VcomFlasherOptions();
        }

        /// <summary>True once a port handle is open (used by the CLI to report link state).</summary>
        public bool IsOpen => _serialPort?.IsOpen == true;

        public string? OpenPortName { get; private set; }

        /// <summary>
        /// D-18: which modem-line path this flasher actually took, and on what evidence. Null until
        /// a <see cref="Connect"/> succeeded; callers (CLI plan, doctor, session log) report it so
        /// "connected, then the first frame dies" is never again indistinguishable from a cable
        /// problem.
        /// </summary>
        public ModemLineDecision? ModemLines { get; private set; }

        public int FramesSent { get; private set; }
        public int AckExpected { get; private set; }
        public int AckOk { get; private set; }
        public int AckNonAck { get; private set; }
        public int AckTimeouts { get; private set; }

        public void Connect()
        {
            string? portName = _options.PortName ?? FindIdtDevicePort();
            if (string.IsNullOrEmpty(portName))
            {
                throw new IOException("No device in VCOM mode (HUAWEI USB COM 1.0) was found.");
            }

            // An explicit --port may be a pty or a tty the user picked by hand; a missing
            // path is a "no device" condition, not a crash (SerialPort.Open would throw an
            // IOError with a less useful message).
            if (_options.PortName != null && OperatingSystem.IsLinux() && !File.Exists(portName))
            {
                throw new IOException($"VCOM port not found: {portName}");
            }

            // Linux option maps DTR to CDC SET_CONTROL_LINE_STATE. The captured
            // 12d1:3609 BootROM stalls that request (EPIPE), before any packet is
            // sent. This USB bulk endpoint needs no UART modem-line signalling.
            //
            // D-18 (audit P02 / H15): the decision used to be "is the driver literally
            // option?" with modem lines for every other answer - including "the probe found
            // no driver at all". It now comes from ModemLinePolicy, which records *why* it
            // chose a path (driver evidence / capability assumption / forced / explicit port)
            // so the log and doctor can show which one ran.
            DriverProbeResult driverProbe = ModemLinePolicy.ProbeDriver(portName, _options.DriverProbe);
            ModemLineDecision decision = ModemLinePolicy.Decide(
                OperatingSystem.IsLinux(), _options.PortName != null, _options.BulkOnly, driverProbe);
            // Close any previously opened instance first so a repeated
            // Connect() (e.g. after replug) is idempotent instead of throwing
            // "access denied" on a still-held port handle.
            Dispose();

            SerialPort serialPort = CreatePort(portName, decision.BulkOnly);
            try
            {
                serialPort.Open();
            }
            catch (IOException ex) when (decision.Origin == ModemLinePolicy.OriginCapabilityProbe)
            {
                // The clearing path was an unverified (or unavailable) capability assumption.
                // Falling back to the historical modem-line settings changes no wire byte, and
                // the fallback is recorded instead of being guessed from the driver name.
                serialPort.Dispose();
                decision = decision with
                {
                    Mode = ModemLineMode.ModemLines,
                    Origin = "probe-fallback",
                    Reason = "clearing RTS/DTR could not be confirmed to open the port (" + ex.Message
                             + "); fell back to the historical Handshake.RequestToSend + DTR settings",
                };
                _options.Logger?.Event("port.modem-lines",
                    ("port", portName), ("mode", "modem-lines"), ("origin", decision.Origin),
                    ("verified", false), ("reason", decision.Reason), ("error", ex.Message));
                serialPort = CreatePort(portName, bulkOnly: false);
                try
                {
                    serialPort.Open();
                }
                catch
                {
                    serialPort.Dispose();
                    throw;
                }
            }
            catch
            {
                // Do not leak the half-open port instance on failure.
                serialPort.Dispose();
                throw;
            }
            _serialPort = serialPort;
            OpenPortName = portName;
            ModemLines = decision;
            _options.Logger?.Event("port.open",
                ("port", portName),
                ("explicit", _options.PortName != null),
                ("bulkOnly", decision.BulkOnly),
                ("handshake", decision.BulkOnly ? "none" : "rtscts"),
                ("modemLines", decision.BulkOnly ? "bulk-only" : "rtscts"),
                ("modemLinesOrigin", decision.Origin),
                ("modemLinesVerified", decision.Verified),
                ("driver", driverProbe.Driver),
                ("driverKnown", driverProbe.Known),
                ("probeAttempted", driverProbe.Attempted),
                ("probeError", driverProbe.Error),
                ("modemLinesReason", decision.Reason),
                ("ackMode", _options.AckMode.ToString().ToLowerInvariant()),
                ("ackTimeoutMs", (int)_options.AckTimeout.TotalMilliseconds));
        }

        /// <summary>115200 8N1 with either the historical modem-line settings or none at all.</summary>
        private static SerialPort CreatePort(string portName, bool bulkOnly) => new(portName, 115200)
        {
            Handshake = bulkOnly ? Handshake.None : Handshake.RequestToSend,
            DtrEnable = !bulkOnly,
            ReadTimeout = 10000,
            WriteTimeout = 10000,
        };

        public async Task ReconnectAfterCableAsync()
        {
            // The replug destroys Linux's tty handle. Reopen the same VID/PID
            // after it enumerates; do not send another start frame or loader.
            Dispose();
            _options.Logger?.Event("port.reconnect", ("port", _options.PortName), ("waitMs", 15000));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (_options.PortName != null)
                {
                    // An explicit port cannot be matched by VID/PID, so reopen the same path;
                    // a pty or a fixed tty path is the caller's own responsibility.
                    try
                    {
                        Connect();
                        return;
                    }
                    catch (IOException)
                    {
                        // keep polling until the window closes
                    }
                }
                else if (FindIdtDevicePort() != null)
                {
                    Connect();
                    return;
                }
                await Task.Delay(250);
            }
            throw new IOException("COM 1.0 did not reappear after reconnecting the cable. No next-stage loader was sent.");
        }

        public async Task SendStartFrame()
        {
            var startCmd = new byte[] { 0xFE, 0x00, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x02, 0x01, 0x1D, 0x0F };
            await WriteAndVerify(startCmd, false, "START");
            await Task.Delay(50);
        }

        public async Task UploadData(byte[] data, int address, (bool shouldHeadResend, string Cpu) exploitInfo, IProgress<(long sent, long total)> progress)
        {
            await WriteAndVerify(BuildHeadCmd(address, data.Length), true, "HEAD", address);

            int seq = 0;
            long offset = 0;
            while (offset < data.Length)
            {
                int chunkSize = (int)Math.Min(MAX_DATA_LEN, data.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(data, offset, chunk, 0, chunkSize);
                seq++;
                await WriteAndVerify(BuildDataCmd(seq, chunk), true, "DATA", address, seq);
                offset += chunkSize;
                progress?.Report((offset, data.Length));
            }

            if (exploitInfo.shouldHeadResend)
            {
                // D-06: the pwn jump targets are an explicit table (Services/PwnJumpTable.cs), not
                // inlined switch literals — the same lookup, the same exception on an unknown CPU.
                PwnJumpAddresses jump = PwnJumpTable.For(exploitInfo.Cpu);
                await ExploitBootrom(jump);
            }
            else
            {
                await WriteAndVerify(BuildTailCmd(seq + 1), false, "TAIL", address, seq + 1);
            }
            await Task.Delay(500);
        }

        private async Task ExploitBootrom(PwnJumpAddresses jump)
        {
            // The frame order, addresses, delays and payload bytes are the pre-D-06 ones; only the
            // source of the two literals changed (they come from PwnJumpTable now).
            int xloaderStartAddr = jump.XloaderStart;
            int returnAddr = jump.ReturnAddr;

            await WriteAndVerify(BuildHeadCmd(PwnJumpTable.BootromHeadAddress, 4), true, "HEAD", PwnJumpTable.BootromHeadAddress);
            await Task.Delay(100);

            await WriteAndVerify(BuildHeadCmd(returnAddr, 4), false, "HEAD", returnAddr);

            // Invariant: WriteAndVerify/Ack paths run only after Connect() assigned _serialPort;
            // null-forgiving keeps the pre-existing NRE-on-misuse instead of hiding it.
            if (_serialPort!.BytesToRead > 0)
            {
                byte[] rsp = new byte[_serialPort.BytesToRead];
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                try
                {
                    _ = await _serialPort.BaseStream.ReadAsync(rsp.AsMemory(), cts.Token);
                }
                catch (OperationCanceledException) { }
            }

            byte[] payloadBytes = BitConverter.GetBytes(xloaderStartAddr);
            if (!BitConverter.IsLittleEndian) Array.Reverse(payloadBytes);
            await WriteAndVerify(BuildDataCmd(1, payloadBytes), true, "DATA", PwnJumpTable.BootromHeadAddress, 1);
            await Task.Delay(10);

            await WriteAndVerify(BuildTailCmd(2), false, "TAIL", PwnJumpTable.BootromHeadAddress, 2);
            await Task.Delay(100);
        }

        /// <summary>
        /// Writes one frame and, when the frame expects it, reads the BootROM's single ACK byte.
        /// The policy (strict / lenient / off) lives in <see cref="VcomFlasherOptions.AckMode"/>:
        /// strict fails on silence or on a value other than 0xAA, lenient records and continues
        /// (the GUI behaviour), off never reads.
        /// </summary>
        private async Task WriteAndVerify(byte[] command, bool expectAck = true, string kind = "FRAME", int? address = null, int? sequence = null)
        {
            // Same invariant as above: only reachable with an open port.
            await _serialPort!.BaseStream.WriteAsync(command, 0, command.Length);
            await _serialPort.BaseStream.FlushAsync();
            FramesSent++;
            if (kind == "HEAD") _bytesSinceHead = 0;
            if (kind == "DATA") _bytesSinceHead += Math.Max(0, command.Length - 5); // 3 header + 2 CRC bytes
            _options.Logger?.Event("frame.write",
                ("type", kind),
                ("addr", address is { } addr ? "0x" + addr.ToString("X8") : null),
                ("len", command[0] == 0xFE ? DeclaredLength(command) : Math.Max(0, command.Length - 5)),
                ("seq", sequence),
                ("bytes", command.Length),
                ("cumulativeBytes", kind == "DATA" ? _bytesSinceHead : null),
                ("expectAck", expectAck && _options.AckMode != VcomAckMode.Off));

            if (!expectAck || _options.AckMode == VcomAckMode.Off)
            {
                Observe(kind, command.Length, address, sequence, null, _options.AckMode == VcomAckMode.Off ? "skipped" : "not-expected");
                return;
            }

            AckExpected++;
            using var cts = new CancellationTokenSource(_options.AckTimeout);
            var ackBuffer = new byte[1];
            int bytesRead;
            try
            {
                bytesRead = await _serialPort.BaseStream.ReadAsync(ackBuffer, 0, 1, cts.Token);
            }
            catch (OperationCanceledException)
            {
                bytesRead = 0;
            }
            catch (IOException) when (_options.AckMode != VcomAckMode.Strict)
            {
                // A closed/hot-unplugged tty during a blind README-grade flow is not fatal in
                // the lenient mode; strict mode still rethrows below via the null value check.
                bytesRead = 0;
            }

            if (bytesRead == 0)
            {
                AckTimeouts++;
                _options.Logger?.Event("frame.ack", ("type", kind), ("result", "timeout"),
                    ("waitedMs", (int)_options.AckTimeout.TotalMilliseconds), ("fatal", _options.AckMode == VcomAckMode.Strict));
                Observe(kind, command.Length, address, sequence, null, "timeout");
                if (_options.AckMode == VcomAckMode.Strict)
                    throw new IOException($"No ACK from device for {kind} frame (waited {_options.AckTimeout.TotalSeconds:0.###}s).");
                return;
            }

            byte ack = ackBuffer[0];
            bool isAck = ack == 0xAA;
            if (isAck) AckOk++; else AckNonAck++;
            _options.Logger?.Event("frame.ack", ("type", kind), ("result", isAck ? "ok" : "bad"),
                ("raw", ack), ("fatal", !isAck && _options.AckMode == VcomAckMode.Strict));
            Observe(kind, command.Length, address, sequence, ack, isAck ? "ok" : "bad");
            if (!isAck && _options.AckMode == VcomAckMode.Strict)
                throw new IOException($"Unexpected ACK 0x{ack:X2} for {kind} frame (expected 0xAA).");
        }

        private void Observe(string kind, int bytes, int? address, int? sequence, byte? ack, string status) =>
            _options.FrameObserver?.Invoke(new VcomFrameInfo(kind, bytes, address, sequence, ack, status));

        /// <summary>Payload length declared in a 0xFE command frame (big endian, bytes 4..7).</summary>
        private static int DeclaredLength(byte[] command) =>
            command.Length >= 8 ? (command[4] << 24) | (command[5] << 16) | (command[6] << 8) | command[7] : 0;

        private byte[] BuildHeadCmd(int address, int length)
        {
            var cmd = new byte[12]; cmd[0] = 0xFE; cmd[1] = 0x00; cmd[2] = 0xFF; cmd[3] = 0x01;
            Array.Copy(BitConverter.GetBytes(length).Reverse().ToArray(), 0, cmd, 4, 4);
            Array.Copy(BitConverter.GetBytes(address).Reverse().ToArray(), 0, cmd, 8, 4);
            ushort crc = Crc16.CrcHqx(cmd);
            var fullCmd = new byte[14]; Array.Copy(cmd, fullCmd, 12);
            Array.Copy(BitConverter.GetBytes(crc).Reverse().ToArray(), 0, fullCmd, 12, 2);
            return fullCmd;
        }

        private byte[] BuildDataCmd(int seq, byte[] data)
        {
            var cmd = new byte[3 + data.Length]; cmd[0] = 0xDA; cmd[1] = (byte)(seq & 0xFF); cmd[2] = (byte)(~seq & 0xFF);
            Array.Copy(data, 0, cmd, 3, data.Length);
            ushort crc = Crc16.CrcHqx(cmd);
            var fullCmd = new byte[cmd.Length + 2]; Array.Copy(cmd, fullCmd, cmd.Length);
            Array.Copy(BitConverter.GetBytes(crc).Reverse().ToArray(), 0, fullCmd, cmd.Length, 2);
            return fullCmd;
        }

        private byte[] BuildTailCmd(int seq)
        {
            var cmd = new byte[3]; cmd[0] = 0xED; cmd[1] = (byte)(seq & 0xFF); cmd[2] = (byte)(~seq & 0xFF);
            ushort crc = Crc16.CrcHqx(cmd);
            var fullCmd = new byte[5]; Array.Copy(cmd, fullCmd, 3);
            Array.Copy(BitConverter.GetBytes(crc).Reverse().ToArray(), 0, fullCmd, 3, 2);
            return fullCmd;
        }

        // Returns null on platforms with neither Linux nor Windows USB-serial support;
        // Connect() turns that into the "No device in VCOM mode" IOException.
        private string? FindIdtDevicePort()
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxSerialPortFinder.FindPort(IDT_VID, IDT_PID);
            }

            if (OperatingSystem.IsWindows())
            {
                return FindWindowsPortByVidPid(IDT_VID, IDT_PID);
            }

            return null;
        }

        [SupportedOSPlatform("windows")]
        // Returns null when no matching COM device is enumerated; FindIdtDevicePort's caller
        // turns that into "No device in VCOM mode".
        private static string? FindWindowsPortByVidPid(int vid, int pid)
        {
            // R3 (audit 2026-11-15 §4): same rule as the Linux finder (K7). The enumeration and
            // the physical-device grouping live in WindowsSerialPortFinder; one device with several
            // COM interfaces still returns its first port, two physical devices throw
            // AmbiguousSerialDeviceException instead of guessing "the first COM port".
            return WindowsSerialPortFinder.FindPort(vid, pid);
        }

        public void Dispose()
        {
            if (_serialPort != null)
            {
                string? port = OpenPortName;
                _options.Logger?.Event("port.close", ("port", port), ("framesSent", FramesSent),
                    ("ackOk", AckOk), ("ackNonAck", AckNonAck), ("ackTimeouts", AckTimeouts));
            }
            _serialPort?.Close();
            _serialPort?.Dispose();
            _serialPort = null;
        }
    }
}
