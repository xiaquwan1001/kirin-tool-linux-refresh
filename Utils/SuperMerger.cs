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

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Kirin_Tool.Services.Verdict;

namespace Kirin_Tool.Utils
{
    public static class SuperMerger
    {
        public static async Task MergeSuperImages(string path1, string path2, string outputPath, Action<double>? progressCallback = null)
        {
            await Task.Run(() => PerformMerge(path1, path2, outputPath, progressCallback));
        }

        private static void PerformMerge(string path1, string path2, string outputPath, Action<double>? progressCallback)
        {
            var len1 = new FileInfo(path1).Length;
            var len2 = new FileInfo(path2).Length;

            string largePath = path1, smallPath = path2;
            long largeLen = len1, smallLen = len2;

            if (len2 > len1)
            {
                largePath = path2;
                smallPath = path1;
                largeLen = len2;
                smallLen = len1;
            }

            long totalSize = largeLen + smallLen;
            const int bufferSize = 4 * 1024 * 1024;
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            try
            {
                long lastChunkOffset = 0;
                SparseHeader hLarge, hSmall;

                using (var fs = new FileStream(largePath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    hLarge = ReadStruct<SparseHeader>(reader);
                    if (hLarge.Magic != SparseConstants.SPARSE_HEADER_MAGIC) 
                        throw new Exception($"File {Path.GetFileName(largePath)} is not a valid sparse image.");
                    ValidateHeader(hLarge, largePath, largeLen);

                    var largeChunks = new List<SparseChunkFact>(hLarge.TotalChunks > 4096 ? 4096 : (int)hLarge.TotalChunks);
                    for (int i = 0; i < hLarge.TotalChunks; i++)
                    {
                        lastChunkOffset = fs.Position;
                        var chunk = ReadStruct<ChunkHeader>(reader);
                        // TotalSize >= 12 and the payload has to be inside the file: a corrupt
                        // TotalSize used to produce a negative Seek (bare IOException) or an
                        // endless walk over a huge declared chunk count (audit §1.2).
                        if (chunk.TotalSize < SparseConstants.CHUNK_HEADER_SIZE)
                            throw new InvalidDataException(
                                $"Sparse chunk {i} of {Path.GetFileName(largePath)} declares TotalSize={chunk.TotalSize} (< 12).");
                        long payload = chunk.TotalSize - SparseConstants.CHUNK_HEADER_SIZE;
                        if (fs.Position + payload > fs.Length)
                            throw new InvalidDataException(
                                $"Sparse chunk {i} of {Path.GetFileName(largePath)} runs past the end of the file.");
                        largeChunks.Add(new SparseChunkFact(chunk.ChunkType, chunk.ChunkSize, chunk.TotalSize));
                        fs.Seek(payload, SeekOrigin.Current);
                    }

                    // M2 (§1.2): the declared block count must equal what the chunks describe.
                    // A header that disagrees with its own chunks describes an image that cannot
                    // exist, and this walk is the only place that has both facts.
                    EnforceBlockSum(largePath, hLarge, largeChunks);
                }

                using (var src = new FileStream(largePath, FileMode.Open, FileAccess.Read))
                using (var dst = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    long remaining = lastChunkOffset;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(bufferSize, remaining);
                        int read = src.Read(buffer, 0, toRead);
                        if (read == 0) break;
                        dst.Write(buffer, 0, read);
                        remaining -= read;
                        progressCallback?.Invoke((double)dst.Position * 100 / totalSize);
                    }
                }

                long newDataOffset = 0;
                using (var fs = new FileStream(smallPath, FileMode.Open, FileAccess.Read))
                using (var reader = new BinaryReader(fs))
                {
                    hSmall = ReadStruct<SparseHeader>(reader);
                    if (hSmall.Magic != SparseConstants.SPARSE_HEADER_MAGIC) 
                        throw new Exception($"File {Path.GetFileName(smallPath)} is not a valid sparse image.");
                    ValidateHeader(hSmall, smallPath, smallLen);
                    EnforceBlockSum(smallPath, hSmall, ReadChunkFacts(smallPath, hSmall));

                    var firstChunk = ReadStruct<ChunkHeader>(reader);
                    if (firstChunk.TotalSize < SparseConstants.CHUNK_HEADER_SIZE)
                        throw new InvalidDataException(
                            $"The first sparse chunk of {Path.GetFileName(smallPath)} declares TotalSize={firstChunk.TotalSize} (< 12).");
                    newDataOffset = fs.Position + (firstChunk.TotalSize - SparseConstants.CHUNK_HEADER_SIZE);
                    if (newDataOffset > smallLen)
                        throw new InvalidDataException(
                            $"The first sparse chunk of {Path.GetFileName(smallPath)} runs past the end of the file.");
                    if (newDataOffset < 0)
                        throw new InvalidDataException("The first sparse chunk offset is negative.");
                }

                using (var src = new FileStream(smallPath, FileMode.Open, FileAccess.Read))
                using (var dst = new FileStream(outputPath, FileMode.Append, FileAccess.Write))
                {
                    src.Seek(newDataOffset, SeekOrigin.Begin);
                    int read;
                    while ((read = src.Read(buffer, 0, bufferSize)) > 0)
                    {
                        dst.Write(buffer, 0, read);
                        progressCallback?.Invoke((double)dst.Position * 100 / totalSize);
                    }
                }

                // Validated as >= 1 by ValidateHeader, so the subtraction cannot underflow into
                // 0xFFFFFFFF (a "total_chunks=4294967295" image was still flashed into super, §1.2).
                uint newTotalChunks = checked((uint)((long)hLarge.TotalChunks - 1 + hSmall.TotalChunks - 1));
                using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Write))
                using (var writer = new BinaryWriter(fs))
                {
                    fs.Seek(0x0C, SeekOrigin.Begin);
                    writer.Write(4096); 
                    
                    fs.Seek(0x14, SeekOrigin.Begin);
                    writer.Write(newTotalChunks);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Reads every chunk header of a sparse image (seek only — no payload is read) into the
        /// facts the block accounting needs. Geometry errors are reported with the same wording the
        /// merge loop uses, so a caller cannot get a weaker verdict from this pass.
        /// </summary>
        private static List<SparseChunkFact> ReadChunkFacts(string path, SparseHeader header)
        {
            string name = Path.GetFileName(path);
            var facts = new List<SparseChunkFact>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);
            fs.Seek(header.FileHeaderSize, SeekOrigin.Begin);
            for (int i = 0; i < header.TotalChunks; i++)
            {
                var chunk = ReadStruct<ChunkHeader>(reader);
                if (chunk.TotalSize < SparseConstants.CHUNK_HEADER_SIZE)
                    throw new InvalidDataException(
                        $"Sparse chunk {i} of {name} declares TotalSize={chunk.TotalSize} (< 12).");
                long payload = chunk.TotalSize - SparseConstants.CHUNK_HEADER_SIZE;
                if (fs.Position + payload > fs.Length)
                    throw new InvalidDataException($"Sparse chunk {i} of {name} runs past the end of the file.");
                facts.Add(new SparseChunkFact(chunk.ChunkType, chunk.ChunkSize, chunk.TotalSize));
                fs.Seek(payload, SeekOrigin.Current);
            }
            return facts;
        }

        /// <summary>
        /// M2 (audit 2026-11-15 round5 §1.2): `TotalBlocks` is a claim about the blocks the image
        /// expands to. Nothing compared it with the chunks, so a header whose block count disagreed
        /// with its own chunk list was merged and could be flashed. The verdict comes from
        /// <see cref="SparseBlockSum"/> (`verified`/`failed`); a failed one refuses the input.
        /// </summary>
        private static void EnforceBlockSum(string path, SparseHeader header, List<SparseChunkFact> chunks)
        {
            SparseBlockSumVerdict verdict = SparseBlockSum.Evaluate(header.TotalBlocks, header.BlockSize, chunks);
            if (verdict.Consistent) return;

            throw new InvalidDataException(
                $"Sparse header of {Path.GetFileName(path)}: block-sum verdict {verdict.Token} — {verdict.Reason}.");
        }

        /// <summary>
        /// Rejects a header whose geometry cannot describe a real file: the sparse version/sizes
        /// are fixed, and a chunk count larger than the file is a hang (the merge loop walks every
        /// declared chunk) as well as a sign of a corrupt image (audit 2026-11-15 round3 §1.2).
        /// </summary>
        private static void ValidateHeader(SparseHeader header, string path, long fileLength)
        {
            string name = Path.GetFileName(path);
            if (header.FileHeaderSize != 28 || header.ChunkHeaderSize != SparseConstants.CHUNK_HEADER_SIZE)
                throw new InvalidDataException(
                    $"Sparse header of {name} declares FileHeaderSize={header.FileHeaderSize}/ChunkHeaderSize={header.ChunkHeaderSize}; expected 28/12.");
            if (header.BlockSize == 0)
                throw new InvalidDataException($"Sparse header of {name} declares BlockSize=0.");
            if (header.TotalChunks == 0)
                throw new InvalidDataException($"Sparse header of {name} declares 0 chunks.");
            long maxChunks = (fileLength - header.FileHeaderSize) / SparseConstants.CHUNK_HEADER_SIZE;
            if (header.TotalChunks > maxChunks)
                throw new InvalidDataException(
                    $"Sparse header of {name} declares {header.TotalChunks} chunks but the file can hold at most {maxChunks}.");
        }

        private static T ReadStruct<T>(BinaryReader reader) where T : struct
        {
            int required = Marshal.SizeOf<T>();
            byte[] bytes = reader.ReadBytes(required);
            if (bytes.Length < required)
            {
                throw new EndOfStreamException(
                    $"Short read while parsing {typeof(T).Name}: expected {required} bytes, got {bytes.Length}.");
            }
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }
    }
}
