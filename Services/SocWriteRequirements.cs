// Licensed under the repository's Business Source License 1.1. See LICENSE.
namespace Kirin_Tool.Services;

/// <summary>
/// D-26 (audit H93 / H09): the single source of truth for "what does this SoC need from the
/// operator's hands". Before this file the same SoC name lists existed four times - in
/// <see cref="FirmwareUnlocker"/> (twice), in the GUI's oeminfo branch and in the CLI - so a
/// one-character edit in one copy silently changed one entry point and not the others.
///
/// Two questions are answered here and nowhere else:
///  * <see cref="RequiresCableReplug"/>: the null -> XLOADER handoff needs a physical replug;
///  * <see cref="RequiresWpOff"/>: oeminfo write protection must be erased before the
///    <c>oeminfowrite-*</c> fields can be written.
///
/// The lists themselves are data, not protocol: nothing here touches a wire command, a delay or
/// a retry.
/// </summary>
public static class SocWriteRequirements
{
    /// <summary>
    /// hisi970 and hisi710/710a also load the null stage, but their handoff does not need the
    /// cable; only these five SoCs do (unchanged since the GUI shipped this list).
    /// </summary>
    private static readonly string[] CableReplugList = { "hisi980", "hisi810", "hisi820", "hisi985", "hisi990" };

    /// <summary>These SoCs are told to unplug from <b>both</b> sides; hisi980 only with the fastflash loader.</summary>
    private static readonly string[] BothSidesList = { "hisi810", "hisi820", "hisi985", "hisi990" };

    /// <summary>oeminfo write protection is erased unconditionally on these.</summary>
    private static readonly string[] WriteProtectionList = { "hisi810", "hisi820", "hisi985", "hisi990" };

    /// <summary>hisi980 only needs the write-protection erase on the fastflash-loader path.</summary>
    private const string WriteProtectionFastFlashSoc = "hisi980";

    /// <summary>Read-only view of the replug list (tests and docs use it; callers must not mutate).</summary>
    public static IReadOnlySet<string> CableReplugSocs { get; } =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(CableReplugList, StringComparer.Ordinal);

    /// <summary>Read-only view of the unconditional write-protection list.</summary>
    public static IReadOnlySet<string> WriteProtectionSocs { get; } =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(WriteProtectionList, StringComparer.Ordinal);

    /// <summary>
    /// True when the null -> XLOADER handoff on this SoC needs the operator to unplug the cable.
    /// Every entry point (GUI service, CLI/MCP) must ask this, never repeat the list.
    /// </summary>
    public static bool RequiresCableReplug(string? soc) =>
        soc != null && CableReplugSocs.Contains(soc);

    /// <summary>
    /// True when the cable prompt must ask for an unplug from <b>both</b> sides instead of the
    /// computer side only. hisi980 depends on the loader flavour, hence the flag.
    /// </summary>
    public static bool ReplugAtBothSides(string? soc, bool useFastFlashLoader = false) =>
        soc != null && (BothSidesList.Contains(soc, StringComparer.Ordinal) ||
                        (useFastFlashLoader && string.Equals(soc, WriteProtectionFastFlashSoc, StringComparison.Ordinal)));

    /// <summary>
    /// True when <c>oeminfoerase-disablewp</c> must be sent before the oeminfo field writes.
    /// hisi980 asks for it only on the fastflash-loader path (the historical GUI rule).
    /// </summary>
    public static bool RequiresWpOff(string? soc, bool useFastFlashLoader = false) =>
        soc != null && (WriteProtectionSocs.Contains(soc) ||
                        (useFastFlashLoader && string.Equals(soc, WriteProtectionFastFlashSoc, StringComparison.Ordinal)));
}
