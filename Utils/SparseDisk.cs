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
using System.Runtime.InteropServices;

namespace Kirin_Tool.Utils
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SparseHeader
    {
        public uint Magic;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ushort FileHeaderSize;
        public ushort ChunkHeaderSize;
        public uint BlockSize;
        public uint TotalBlocks;
        public uint TotalChunks;
        public uint ImageChecksum;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChunkHeader
    {
        public ushort ChunkType;
        public ushort Reserved1;
        public uint ChunkSize;
        public uint TotalSize;
    }

    public static class SparseConstants
    {
        public const uint SPARSE_HEADER_MAGIC = 0xed26ff3a;
        public const ushort CHUNK_HEADER_SIZE = 12;

        // Chunk types (AOSP sparse_format.h). The block accounting below is decided by these:
        // a raw chunk expands to its payload / BlockSize, fill and dontcare expand to ChunkSize
        // blocks, and a crc32 chunk (4 payload bytes, no data) expands to nothing.
        public const ushort CHUNK_TYPE_RAW = 0xcac1;
        public const ushort CHUNK_TYPE_FILL = 0xcac2;
        public const ushort CHUNK_TYPE_DONT_CARE = 0xcac3;
        public const ushort CHUNK_TYPE_CRC32 = 0xcac4;
    }
}
