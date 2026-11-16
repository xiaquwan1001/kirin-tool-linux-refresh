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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Kirin_Tool.Services
{
    public class OemInfoEditor
    {
        private const int BlockSize = 0x400;
        private const int DataPayloadOffsetInBlock = 0x200;
        private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("OEM_INFO");

        private readonly byte[] _binaryData;
        private readonly int _detectedVersion;

        /// <summary>The OEMINFO block version found in the image (for verdict text).</summary>
        public int DetectedVersion => _detectedVersion;

        private static readonly Dictionary<int, (List<int> ModelIds, int RegionId)> TargetIdsMap = new Dictionary<int, (List<int>, int)>
        {
            { 6, (new List<int>{0x5b, 0x61}, 0x12) },
            { 8, (new List<int>{0x5ee, 0x5ef}, 0x5de) },
            { 9, (new List<int>{0x301, 0x300}, 0x2f0) }
        };

        public OemInfoEditor(byte[] oeminfoData)
        {
            _binaryData = oeminfoData;
            _detectedVersion = DetectOemVersion();
            if (_detectedVersion == -1)
            {
                throw new InvalidDataException("Could not detect a supported OEMINFO version.");
            }
        }

        private int DetectOemVersion()
        {
            for (int offset = 0; offset < _binaryData.Length; offset += BlockSize)
            {
                if (offset + 12 > _binaryData.Length) break;

                if (_binaryData.Skip(offset).Take(8).SequenceEqual(MagicBytes))
                {
                    return System.BitConverter.ToInt32(_binaryData, offset + 8);
                }
            }
            return -1;
        }

        /// <summary>Longest accepted model/vendor value (a GPT name is 36 chars; 64 is generous).</summary>
        public const int MaxFieldLength = 64;

        /// <summary>
        /// D-11 / audit F05: the counts come back with the bytes, so a conversion that matched
        /// <b>no</b> entry can be reported as a failure instead of a silent "success" whose
        /// output is byte-identical to the input.
        /// </summary>
        public sealed record EditOutcome(byte[] Data, int ModelEntriesRewritten, int RegionEntriesRewritten)
        {
            public bool AnyRewritten => ModelEntriesRewritten > 0 || RegionEntriesRewritten > 0;
        }

        public EditOutcome EditEntriesDetailed(string model, string regionVendor)
        {
            // Both values land inside one 1024-byte block. Validate up front so a too-long value
            // cannot be written over the next block's magic/id header (audit §1.2).
            ValidateField(model, nameof(model));
            ValidateField(regionVendor, nameof(regionVendor));

            if (!TargetIdsMap.ContainsKey(_detectedVersion))
                throw new System.NotSupportedException($"OEMINFO version '{_detectedVersion}' is not supported for modification.");

            var targets = TargetIdsMap[_detectedVersion];
            var modifiedData = (byte[])_binaryData.Clone();
            int modelHits = 0;
            int regionHits = 0;

            for (int offset = 0; offset < modifiedData.Length; offset += BlockSize)
            {
                if (offset + 16 > modifiedData.Length) break;
                if (!modifiedData.Skip(offset).Take(8).SequenceEqual(MagicBytes)) continue;

                int entryVersion = System.BitConverter.ToInt32(modifiedData, offset + 8);
                int entryId = System.BitConverter.ToInt32(modifiedData, offset + 12);

                if (entryVersion == _detectedVersion)
                {
                    if (targets.ModelIds.Contains(entryId))
                    {
                        UpdateEntry(modifiedData, offset, model);
                        modelHits++;
                    }
                    else if (targets.RegionId == entryId)
                    {
                        UpdateEntry(modifiedData, offset, regionVendor);
                        regionHits++;
                    }
                }
            }
            return new EditOutcome(modifiedData, modelHits, regionHits);
        }

        public byte[] EditEntries(string model, string regionVendor) =>
            EditEntriesDetailed(model, regionVendor).Data;

        private static void ValidateField(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("OEMINFO " + name + " cannot be empty.");
            if (value!.Length > MaxFieldLength)
                throw new InvalidDataException(
                    "OEMINFO " + name + " is " + value.Length + " characters; the limit is " + MaxFieldLength + ".");
            foreach (char c in value)
            {
                if (char.IsControl(c))
                    throw new InvalidDataException("OEMINFO " + name + " contains a control character.");
            }
        }

        private void UpdateEntry(byte[] data, int blockOffset, string newString)
        {
            var newBytes = Encoding.UTF8.GetBytes(newString);
            int newLen = newBytes.Length;

            // Bounds: the length prefix, the payload and the padding all have to stay inside this
            // block *and* inside the array. The old code had no check at all: a value longer than
            // the 512-byte payload silently overwrote the next block's OEM_INFO header, and the
            // resulting image was then flashed into the oeminfo partition (audit §1.2).
            int maxPayloadSize = BlockSize - DataPayloadOffsetInBlock;
            if (newLen > maxPayloadSize)
                throw new InvalidDataException(
                    "OEMINFO value is " + newLen + " bytes; one block holds at most " + maxPayloadSize + ".");
            if (blockOffset < 0 || blockOffset + BlockSize > data.Length)
                throw new InvalidDataException(
                    "OEMINFO block at offset " + blockOffset + " does not fit in the " + data.Length + "-byte image.");

            int dataStartOffset = blockOffset + DataPayloadOffsetInBlock;
            if (dataStartOffset + newLen > data.Length)
                throw new InvalidDataException("OEMINFO payload would be written past the end of the image.");

            System.Buffer.BlockCopy(System.BitConverter.GetBytes(newLen), 0, data, blockOffset + 20, 4);
            System.Buffer.BlockCopy(newBytes, 0, data, dataStartOffset, newLen);

            int paddingStart = dataStartOffset + newLen;
            int blockEnd = System.Math.Min(blockOffset + BlockSize, data.Length);
            for (int i = paddingStart; i < blockEnd; i++)
            {
                data[i] = 0xFF;
            }
        }
    }
}