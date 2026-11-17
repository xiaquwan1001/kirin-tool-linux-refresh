// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace Kirin_Tool.Utils
{
    /// <summary>
    /// D-27 (H49/H74/S42/S43): the UPDATE.APP container knowledge that used to exist in two
    /// places each, and the one place it is allowed to live now.
    ///
    ///  - <see cref="IsHeaderMagic"/>: the 55 AA 5A A5 entry marker. Both parsers
    ///    (<see cref="UpdateApp"/>, the strict offline extractor, and
    ///    <c>Services/USBUpdate/USBUpdateApp</c>, the extractor that actually writes image files
    ///    for the USB-update flow) scan for it byte by byte.
    ///  - <see cref="PartitionAliases"/> / <see cref="PartitionName"/> / <see cref="LoaderPartitionName"/>:
    ///    the entry-name → fastboot-partition-name table. The archive carries the GPT blob as
    ///    <c>HISIUFS_GPT</c> while the loader exposes it as <c>ptable</c>, and the loader matches
    ///    names case-sensitively in lower case. <c>Cli/Commands/OtaFlashRunner</c> used to keep a
    ///    copy of the same table hard-coded next to the parser's copy.
    ///
    /// Neither member changes behaviour: the table and the magic are byte-for-byte the values the
    /// two implementations already used, only stated once.
    /// </summary>
    public static class UpdateAppFormat
    {
        /// <summary>First byte of the UPDATE.APP entry marker (0x55).</summary>
        public const byte MagicByte0 = 0x55;

        /// <summary>Second byte of the entry marker (0xAA).</summary>
        public const byte MagicByte1 = 0xAA;

        /// <summary>Third byte of the entry marker (0x5A).</summary>
        public const byte MagicByte2 = 0x5A;

        /// <summary>Fourth byte of the entry marker (0xA5).</summary>
        public const byte MagicByte3 = 0xA5;

        /// <summary>Length of the entry marker in bytes.</summary>
        public const int HeaderMagicLength = 4;

        /// <summary>Name reported for an entry whose name field is blank.</summary>
        public const string UnknownPartitionName = "UNKNOWN";

        /// <summary>
        /// The one UPDATE.APP-entry-name → fastboot partition name alias table.
        /// Keys are compared case-insensitively; values are the loader's own lower-case names.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> PartitionAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hisiufs_gpt"] = "ptable",
                ["efi"] = "ptable",
                ["ufsfw"] = "ufs_fw",
                ["erecovery_ramdis"] = "erecovery_ramdisk",
                ["recovery_ramdis"] = "recovery_ramdisk",
                ["vbmeta_hw_produc"] = "vbmeta_hw_product",
            };

        /// <summary>
        /// True when <paramref name="bytes"/> starts with the 55 AA 5A A5 entry marker.
        /// Shorter spans are simply not a header (a truncated tail of a package is not a marker).
        /// </summary>
        public static bool IsHeaderMagic(ReadOnlySpan<byte> bytes) =>
            bytes.Length >= HeaderMagicLength &&
            bytes[0] == MagicByte0 && bytes[1] == MagicByte1 &&
            bytes[2] == MagicByte2 && bytes[3] == MagicByte3;

        /// <summary>
        /// <see cref="UpdateApp"/>'s rule: a blank name is <see cref="UnknownPartitionName"/>,
        /// anything else is trimmed, lower-cased and alias-resolved. Used verbatim by both the
        /// strict parser and the USB-update extractor so they cannot disagree about a name.
        /// </summary>
        public static string PartitionName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return UnknownPartitionName;

            string name = rawName!.ToLowerInvariant().Trim();
            return PartitionAliases.TryGetValue(name, out string? alias) ? alias : name;
        }

        /// <summary>
        /// <c>OtaFlashRunner</c>'s rule: the plan already carries the archive's entry name, so the
        /// alias is applied without inventing <see cref="UnknownPartitionName"/>; an unmapped name
        /// is returned lower-cased and trimmed, exactly as before.
        /// </summary>
        public static string LoaderPartitionName(string planned)
        {
            string lower = (planned ?? string.Empty).Trim().ToLowerInvariant();
            return PartitionAliases.TryGetValue(lower, out string? alias) ? alias : lower;
        }
    }
}
