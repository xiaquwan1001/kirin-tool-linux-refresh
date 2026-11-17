// Licensed under the repository's Business Source License 1.1. See LICENSE.
using Avalonia.Controls;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Verdict;
using Kirin_Tool.UI;
using Kirin_Tool.Utils;
namespace Kirin_Tool;

public partial class MainWindow
{
    // Non-modal battery banner (informs) plus EnsureBatteryAllowsWriteAsync (gates): the level
    // decides both. D-16 / audit S14: the banner used to be the ONLY use of the battery reading,
    // so every write path flashed a 3%-battery device. The floor itself lives in
    // Services/Verdict/BatteryGate.cs and is shared with the CLI and MCP entry points.
    // Temporary loaders commonly answer these getvar queries with "invalid command", so every
    // failure is silent and an unreadable level never refuses a write (see BatteryGate).
    private WindowsInfoBar? _batteryBanner;
    private bool _refreshingBatteryBanner;

    /// <summary>
    /// Best-effort battery percentage via fastboot getvar. The candidate getvars, the parse and
    /// the plausibility rules all come from <see cref="BatteryGate"/> (shared with CLI/MCP); this
    /// method only supplies the fastboot query. Never throws.
    /// </summary>
    private Task<int?> TryReadBatteryPercentAsync() =>
        BatteryGate.ReadPercentAsync(QueryBatteryVariableAsync);

    /// <summary>One getvar for the battery reader; a rejected variable yields null, never a throw.</summary>
    private async Task<string?> QueryBatteryVariableAsync(string variable)
    {
        try
        {
            ProcessResult result = await _fastbootClient.QueryVariableAsync(variable);
            return result.IsSuccess ? result.Output ?? string.Empty : null;
        }
        catch
        {
            // Best effort only: an unreachable or temp-loader device must
            // not raise an error dialog from a read-only hint.
            return null;
        }
    }

    /// <summary>
    /// The GUI half of the shared pre-write battery floor (D-16): call it at the top of every
    /// write entry point, before the first partition is written. Returns false (and says why)
    /// when the device reports a level below <see cref="BatteryGate.MinimumPercent"/>; an
    /// unreadable level proceeds, so a temporary loader is not bricked into "cannot flash".
    /// </summary>
    private async Task<bool> EnsureBatteryAllowsWriteAsync(string operation)
    {
        BatteryGateVerdict verdict = BatteryGate.Evaluate(await TryReadBatteryPercentAsync());
        if (!verdict.Refuse) return true;

        await ShowMessageBox("Battery Too Low",
            $"{operation} refused: {verdict.Reason}\n\nGate: {verdict.Gate}, level: {verdict.Level}%.");
        return false;
    }

    private void EnsureBatteryBanner()
    {
        if (_batteryBanner != null) return;
        _batteryBanner = new WindowsInfoBar
        {
            Title = "Battery",
            IsClosable = false,
            IsOpen = false,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };
        if (this.FindControl<StackPanel>("DeviceInfoPage") is { } page)
            page.Children.Insert(2, _batteryBanner); // below the two page headings, above the expander
    }

    /// <summary>Refreshes the read-only battery banner. Low (&lt;50%) shows a
    /// warning; a healthy level hides it; an unreadable level shows one
    /// quiet line. Nothing here can interrupt or refuse a write.</summary>
    private async Task RefreshBatteryBannerAsync()
    {
        if (_refreshingBatteryBanner) return;
        _refreshingBatteryBanner = true;
        try
        {
            EnsureBatteryBanner();
            int? percent = await TryReadBatteryPercentAsync();
            if (_batteryBanner == null) return;
            if (percent is < 50)
            {
                _batteryBanner.Title = "Low Battery";
                _batteryBanner.Message = $"Battery {percent}% — flashing below 50% risks a mid-write power loss. Charge the device before writing.";
                _batteryBanner.IsOpen = true;
            }
            else if (percent is >= 50)
            {
                _batteryBanner.IsOpen = false;
            }
            else
            {
                _batteryBanner.Title = "Battery";
                _batteryBanner.Message = "Battery level unavailable on this loader";
                _batteryBanner.IsOpen = true;
            }
        }
        catch
        {
            // The banner is informational only; never surface errors from it.
            if (_batteryBanner != null) _batteryBanner.IsOpen = false;
        }
        finally { _refreshingBatteryBanner = false; }
    }
    // output is nullable at the call sites (a failed flash may carry no captured text);
    // null simply falls through to the generic "see the log" description.
    private static string FlashFailureDescription(string? output)
    {
        var failed = (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("FAILED", StringComparison.OrdinalIgnoreCase));
        return failed?.Trim() ?? "Flash failed; see the fastboot log.";
    }
    private async Task<bool> AllowChipOperationAsync(string operation)
    {
        if (ChipCapabilities.Allows(GetSelectedCpu(), operation)) return true;
        await _dialogService.ShowMessageBox("Notice", ChipCapabilities.UnsupportedNotice, showHeading: true);
        return false;
    }
    private async void ReadPsid_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!await AllowChipOperationAsync("read-psid")) return;
        try
        {
            if (!await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode.");
                return;
            }
            var values = await _fastbootClient.ReadPsidAsync();
            string text = string.Join("\n", values.Select(pair => $"{pair.Key}: {pair.Value}"));
            await ShowMessageBox("Device identifiers (get-psid)", text);
        }
        catch (Exception ex) { await ShowMessageBox("Read device identifiers", ex.Message); }
    }
}
