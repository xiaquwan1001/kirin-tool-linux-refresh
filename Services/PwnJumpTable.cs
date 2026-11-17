// Licensed under the repository's Business Source License 1.1. See LICENSE.
using System;
using System.Collections.Generic;

namespace Kirin_Tool.Services;

/// <summary>
/// The two addresses the pwn (BootROM exploit) sequence jumps to for one SoC: the
/// <see cref="XloaderStart"/> value handed to the BootROM as the 4-byte DATA payload and the
/// <see cref="ReturnAddr"/> the second HEAD frame points at.
/// </summary>
public readonly record struct PwnJumpAddresses(int XloaderStart, int ReturnAddr);

/// <summary>
/// D-06 / audit H20+H23 ("brick-grade literals"): <c>xloaderStart</c> and <c>returnAddr</c> used to
/// live inside two <c>switch</c> statements in <c>VcomFlasher.UploadData</c>/<c>ExploitBootrom</c>.
/// A one-byte change there is not a compile error and not even a visible diff — it silently points
/// the exploit at the wrong address on one SoC. They are an explicit, reviewable table now, and
/// <c>tests/PwnJumpTable</c> pins every value against the old switch and against
/// <see cref="FirmwareUnlocker.CpuAddresses"/>.
///
/// The key set is exactly the CPUs <see cref="FirmwareUnlocker.CpuAddresses"/> marks as
/// <c>PwnFlag = true</c> (the pwn-capable loaders); <c>tests/PwnJumpTable</c> asserts that
/// alignment so a new pwn SoC cannot be added to one table and forgotten in the other.
/// </summary>
public static class PwnJumpTable
{
    /// <summary>
    /// The BootROM HEAD address the pwn sequence loads at. Not part of the per-SoC jump table
    /// (it is the same on every pwn SoC and matches the <c>null</c>/<c>XLOADER</c> stage address in
    /// <see cref="FirmwareUnlocker.CpuAddresses"/>); kept here so the pwn sequence has one place to
    /// read its addresses from.
    /// </summary>
    public const int BootromHeadAddress = 0x22000;

    /// <summary>
    /// Per-SoC jump targets, byte-for-byte the values the removed switch statements produced.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, PwnJumpAddresses> Entries =
        new Dictionary<string, PwnJumpAddresses>(StringComparer.Ordinal)
        {
            ["hisi710"] = new(0x2316D, 0x49BC8),
            ["hisi710a"] = new(0x23155, 0x49BC8),
            ["hisi810"] = new(0x23155, 0x4DBC8),
            ["hisi820"] = new(0x23155, 0x673c8),
            ["hisi970"] = new(0x2316D, 0x4DBC8),
            ["hisi980"] = new(0x2316D, 0x4DBC8),
            ["hisi985"] = new(0x23155, 0x673c8),
            ["hisi990"] = new(0x23155, 0x673c8),
        };

    /// <summary>
    /// The jump targets for <paramref name="cpu"/>. An unsupported CPU is a hard failure with the
    /// exact message the switch used to throw (a silent fallback here means a wrong-address jump).
    /// </summary>
    public static PwnJumpAddresses For(string cpu) =>
        Entries.TryGetValue(cpu, out PwnJumpAddresses addresses)
            ? addresses
            : throw new NotSupportedException($"Exploit requested for an unsupported CPU: {cpu}.");
}
