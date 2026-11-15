// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Kirin_Tool.Models;

namespace Kirin_Tool.Services;

/// <summary>How much the write path knows about the device it is about to write.</summary>
public enum IdentityReading
{
    /// <summary>A <c>product:</c> line was read and it is a real model string.</summary>
    Read,
    /// <summary>No usable evidence (probe threw, non-zero exit, empty output): the gate cannot judge.</summary>
    Unreadable,
    /// <summary>The loader answered, but the answer is not a model name (the 2026-11-15 failure).</summary>
    Invalid,
}

/// <summary>Result of the pre-write identity probe. <see cref="Refused"/> is the only blocking state.</summary>
public sealed record DeviceIdentity(IdentityReading Status, string? Product, string? Serial, string? Reason)
{
    /// <summary>True when the write must not be sent: the loader answered something that is not an identity.</summary>
    public bool Refused => Status == IdentityReading.Invalid;

    /// <summary>Positive hint only — never used to reject a model, see <see cref="DeviceIdentityGate"/>.</summary>
    public bool LooksLikeKirinFamily => DeviceIdentityGate.LooksLikeKirinFamily(Product);

    /// <summary>One human line for op logs and refusals.</summary>
    public string Describe() => Status switch
    {
        IdentityReading.Read => "product=" + Product + (LooksLikeKirinFamily ? " (kirin family)" : " (unrecognised family, allowed)"),
        IdentityReading.Unreadable => "unreadable (" + Reason + "); write path continues",
        _ => "INVALID (" + Reason + ")",
    };
}

/// <summary>
/// K6: the pre-write identity gate (audit 2026-11-15 §4).
///
/// Before this gate the GUI rescue flow only *recorded* <c>getvar product</c>/<c>serialno</c> in the
/// pre-op manifest. When the loader answered the probe with a bare <c>OKAY</c> (which the recorder
/// stored verbatim as the "product"), nothing objected and the rescue write plan still ran — on the
/// other phone that was plugged in. The gate turns that evidence into a decision:
///
/// * the loader answered with text but no <c>product:</c> line, or with a token that is obviously
///   not a model name (<c>OKAY</c>, <c>FAILED</c>, <c>unknown</c>, …) → <b>refuse</b>;
/// * the probe could not run / exited non-zero / printed nothing → <b>unreadable</b>, allowed. A
///   tool that is not talking to a bootloader at all (no device, stub binary, timeout) must not be
///   turned into a write refusal, and no chip family is rejected by name (hisi980/hisi990/hisi9000
///   model strings keep working).
///
/// The family markers below are therefore a *hint* ("this looks like the expected family"), never a
/// whitelist: an unknown but plausible model string is allowed and recorded as such.
/// </summary>
public static class DeviceIdentityGate
{
    /// <summary>The op/verdict source recorded when the gate refuses a write.</summary>
    public const string VerdictRefused = "device-identity";

    /// <summary>
    /// The op/verdict source recorded when the gate refuses a *read* that writes host files
    /// (`oem dump-emmc/dump-storage`, `fetch`). A read refusal is not a write refusal: the
    /// device was not touched, so the two verdicts must stay distinguishable in the log.
    /// </summary>
    public const string VerdictRefusedRead = "device-identity-read";

    /// <summary>Model prefixes shipped by Huawei for Kirin phones (hint only, never a whitelist).</summary>
    private static readonly string[] KirinFamilyMarkers =
    {
        "els-", "elsp-", "ana-", "noh-", "nam-", "jsn-", "tas-", "lio-", "bla-",
        "ele-", "lya-", "mha-", "vog-", "nop-", "nla-",
    };

    /// <summary>Answers a loader gives instead of an identity (case-insensitive, exact token).</summary>
    private static readonly string[] IllegalTokens =
    {
        "okay", "ok", "failed", "unknown", "null", "none", "error", "invalid", "n/a", "na", "...",
        "finished.", "finished",
    };

    /// <summary>Pull <c>&lt;variable&gt;: value</c> out of a getvar answer, ignoring <c>(bootloader)</c> and OKAY lines.</summary>
    public static string? ParseValue(string? output, string variable)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(variable)) return null;
        string prefix = variable.Trim() + ":";
        foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.StartsWith("(bootloader)", StringComparison.Ordinal))
                line = line[12..].TrimStart();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string value = line[prefix.Length..].Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    public static string? ParseProduct(string? output) => ParseValue(output, "product");

    public static bool LooksLikeKirinFamily(string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return false;
        string value = product.Trim();
        if (value.Contains("kirin", StringComparison.OrdinalIgnoreCase)) return true;
        return KirinFamilyMarkers.Any(marker => value.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the string cannot be a device model name.</summary>
    public static bool IsIllegal(string? product)
    {
        if (string.IsNullOrWhiteSpace(product)) return true;
        string value = product.Trim();
        if (IllegalTokens.Contains(value.ToLowerInvariant())) return true;
        if (value.Contains("unknown", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains("failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Contains("okay", StringComparison.OrdinalIgnoreCase)) return true;
        // A model name is one printable token: no spaces, no control characters, bounded length.
        if (value.Length > 64) return true;
        if (value.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch))) return true;
        return false;
    }

    /// <summary>
    /// Judge one probe: <paramref name="read"/> is the raw <c>getvar product</c> process result
    /// (null when it could not run) and <paramref name="product"/> the parsed value.
    /// </summary>
    public static DeviceIdentity Evaluate(ProcessResult? read, string? product, string? serial = null)
    {
        if (read == null)
            return new DeviceIdentity(IdentityReading.Unreadable, null, serial, "the identity probe did not run");

        if (!read.IsSuccess)
            return new DeviceIdentity(IdentityReading.Unreadable, null, serial,
                "getvar product exited " + read.ExitCode);

        string output = read.Output ?? string.Empty;
        if (string.IsNullOrWhiteSpace(output))
            return new DeviceIdentity(IdentityReading.Unreadable, null, serial,
                "getvar product printed nothing");

        string? value = product ?? ParseProduct(output);
        if (value == null)
        {
            // The 2026-11-15 shape: the loader printed something, but not a product line.
            return new DeviceIdentity(IdentityReading.Invalid, null, serial,
                "getvar product returned no 'product:' line (answer: " + OneLine(output) + ")");
        }

        if (IsIllegal(value))
            return new DeviceIdentity(IdentityReading.Invalid, value, serial,
                "getvar product answered '" + OneLine(value) + "', which is not a model name");

        return new DeviceIdentity(IdentityReading.Read, value, serial, null);
    }

    /// <summary>
    /// The gate refused a write. A dedicated type (rather than a bare InvalidOperationException) so the
    /// CLI/GUI faces can turn it into a verdict instead of crashing on a loader that talks nonsense.
    /// </summary>
    public sealed class DeviceIdentityRefusedException : InvalidOperationException
    {
        public DeviceIdentityRefusedException(string message) : base(message) { }
    }

    /// <summary>
    /// Judge a raw <c>getvar product</c> text (as the pre-op backup recorder stores it) without a
    /// <see cref="ProcessResult"/>: non-empty text counts as "the loader answered".
    /// </summary>
    public static DeviceIdentity EvaluateText(string? output, string? serial = null)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new DeviceIdentity(IdentityReading.Unreadable, null, serial, "the probe returned nothing");
        var read = new ProcessResult { ExitCode = 0, Output = output };
        return Evaluate(read, ParseProduct(output), serial);
    }

    private static string OneLine(string text)
    {
        string one = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()));
        return one.Length <= 120 ? one : one[..117] + "...";
    }
}
