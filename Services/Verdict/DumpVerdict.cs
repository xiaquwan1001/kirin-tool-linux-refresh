namespace Kirin_Tool.Services.Verdict;

/// <summary>How one partition dump was judged.</summary>
public sealed record DumpVerdictResult(
    bool Ok,
    string Verdict,
    string Reason,
    long FileBytes,
    long? ExpectedBytes,
    bool SizeChecked);

/// <summary>
/// Decides whether a partition dump really produced the partition (debt D-09 / audit F09, S22).
///
/// The old rule was "the file exists and is longer than 0 bytes". A loader that answers the
/// dump verb, writes 512 bytes and dies then looks exactly like a completed 96 MiB oeminfo
/// backup — and that file gets written back later. The rule below requires:
///
///  * a non-empty file, <b>and</b>
///  * no failure token in the loader reply (token rules from <see cref="CommandOutput"/>), <b>and</b>
///  * when the partition size is known: the file is not truncated
///    (<c>length &lt; declared</c> is a failure; <c>length &gt; declared</c> is accepted but named,
///    because a loader may pad to its own block size).
///
/// An unknown declared size (loader answered nothing usable) is reported as
/// <see cref="DumpVerdictResult.SizeChecked"/> = false, never as a silent pass of the size half.
/// </summary>
public static class DumpVerdict
{
    public const string OkVerdict = "file-written";
    public const string FileMissing = "file-missing";
    public const string Truncated = "file-truncated";
    public const string Padded = "file-padded";
    public const string FailureOutput = "loader-reported-failure";

    public static DumpVerdictResult Evaluate(long fileBytes, long? expectedBytes, string? stdout, string? stderr)
    {
        if (fileBytes <= 0)
        {
            return new DumpVerdictResult(false, FileMissing,
                "no dump file was written (0 bytes on disk)", fileBytes, expectedBytes, expectedBytes.HasValue);
        }

        if (CommandOutput.HasFailure(stderr) || CommandOutput.HasFailure(stdout))
        {
            return new DumpVerdictResult(false, FailureOutput,
                "the loader printed a failure token next to the dump", fileBytes, expectedBytes, expectedBytes.HasValue);
        }

        if (expectedBytes is > 0 and var expected && fileBytes < expected)
        {
            return new DumpVerdictResult(false, Truncated,
                $"dump is truncated: {fileBytes} of {expected} byte(s) declared by the partition table",
                fileBytes, expectedBytes, true);
        }

        if (expectedBytes is > 0 and var expected2 && fileBytes > expected2)
        {
            return new DumpVerdictResult(true, Padded,
                $"dump is {fileBytes} byte(s) but the partition declares {expected2}; accepted as padded",
                fileBytes, expectedBytes, true);
        }

        return new DumpVerdictResult(true, OkVerdict,
            expectedBytes.HasValue
                ? $"dump matches the declared size ({fileBytes} byte(s))"
                : $"dump wrote {fileBytes} byte(s) (declared size unknown, size half not checked)",
            fileBytes, expectedBytes, expectedBytes.HasValue);
    }

    /// <summary>
    /// Parses the value of <c>getvar partition-size:&lt;name&gt;</c>. fastboot answers
    /// <c>(bootloader) partition-size:oeminfo: 0x6000000</c> or a bare <c>0x6000000</c>; anything
    /// else (empty, "no such partition", a decimal alias of an unknown unit) yields null so the
    /// caller records "size unknown" instead of inventing a number.
    /// </summary>
    public static long? ParseDeclaredSize(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        foreach (string raw in reply!.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (CommandOutput.HasFailure(line)) return null;

            // Drop the "(bootloader) name:" prefix when present, then keep the last token that
            // looks like a hex size.
            int colon = line.LastIndexOf(':');
            string tail = colon >= 0 ? line[(colon + 1)..].Trim() : line;
            string[] tokens = tail.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string token = tokens[i];
                if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(token[2..], System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out long hex) && hex >= 0)
                        return hex;
                    continue;
                }

                if (long.TryParse(token, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long dec) && dec > 0)
                    return dec;
            }
        }

        return null;
    }
}
