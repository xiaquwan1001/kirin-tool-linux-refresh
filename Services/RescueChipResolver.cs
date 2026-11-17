// Licensed to the repository's Business Source License 1.1. See LICENSE.
using System;

namespace Kirin_Tool.Services;

/// <summary>Where a rescue target list came from. A device answer is a fact; a dropdown is a guess.</summary>
public enum RescueChipSource
{
    /// <summary>`getvar chipset` answered a platform this build knows the rescue mapping for.</summary>
    Device,

    /// <summary>The device said nothing usable, so the CPU dropdown decided. Must be confirmed by hand.</summary>
    ManualSelection,
}

/// <param name="Chip">The chip token handed to <see cref="RescuePartitionPlan.ForChip"/>.</param>
/// <param name="Source">Device fact or manual selection.</param>
/// <param name="DeviceReport">The raw `getvar chipset` answer, kept for the confirmation text and the log.</param>
public sealed record RescueChipResolution(string Chip, RescueChipSource Source, string? DeviceReport)
{
    /// <summary>The provenance label a surface shows next to the target list.</summary>
    public string SourceLabel => Source == RescueChipSource.Device
        ? "device platform (" + Chip + ")"
        : "manual CPU selection (" + Chip + ")";

    /// <summary>Human-readable device answer for dialogs: null/empty becomes an explicit "no answer".</summary>
    public string DeviceReportLabel => string.IsNullOrWhiteSpace(DeviceReport) ? "no answer" : "'" + DeviceReport!.Trim() + "'";
}

/// <summary>
/// D-15 / audit BB-19: the USB-Update rescue partition list (`ERECOVERY*` → `rescue_recovery*`) used to be
/// chosen by `RescuePartitionPlan.ForChip(GetSelectedCpu())`, i.e. by a host-side dropdown. The dropdown
/// reset on every restart and had no relation to the phone in fastboot mode, so flashing the rescue
/// images of the wrong chip family was one forgotten click away.
///
/// The device is asked first (`getvar chipset`). Only when it cannot name a platform this build knows does
/// the dropdown decide — and then it is recorded as a manual selection so the GUI can insist on an
/// explicit confirmation and show the provenance of the target list.
/// </summary>
public static class RescueChipResolver
{
    public static RescueChipResolution Resolve(string? deviceChipsetReport, string? selectedCpu)
    {
        string reported = Normalize(deviceChipsetReport);
        if (ChipCapabilities.IsKnownChip(reported))
            return new RescueChipResolution(reported, RescueChipSource.Device, deviceChipsetReport);

        // No usable device answer: fall back to the dropdown, but never pretend it was a device fact.
        return new RescueChipResolution(Normalize(selectedCpu), RescueChipSource.ManualSelection, deviceChipsetReport);
    }

    /// <summary>
    /// Accepts both an already-parsed value (`hisi9000`) and a raw loader line
    /// (`(bootloader) chipset: hisi9000`), so a caller cannot make the resolver blind by passing the
    /// unparsed answer. Anything unparsable becomes empty, which is a manual selection, never a guess.
    /// </summary>
    private static string Normalize(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? string.Empty : value!.Trim();
        if (text.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase))
            text = text["(bootloader)".Length..].Trim();
        int colon = text.LastIndexOf(':');
        if (colon >= 0 && colon < text.Length - 1) text = text[(colon + 1)..].Trim();
        return text.ToLowerInvariant();
    }
}
