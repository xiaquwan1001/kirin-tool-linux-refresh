// Licensed to the repository's Business Source License 1.1. See LICENSE.
using System;

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// M4 (audit 2026-11-15 round3 §1.2 / round5 §7): a gzip payload carries no declared output size,
/// so a few megabytes of input can expand to hundreds of gigabytes and fill the staging disk or a
/// tmpfs. <see cref="Limit"/> is the one bound both the CLI decompressor and its assertions use:
/// 32x the compressed length with a 512 MiB floor, additionally capped by the free space of the
/// filesystem the output would land on. It is a pure function on purpose — the previous version
/// lived inline in the CLI and could not be asserted without writing half a gigabyte.
/// </summary>
public static class GzipExtractionLimit
{
    /// <summary>Bytes; the uncompressed output of one package payload may not exceed this floor.</summary>
    public const long FloorBytes = 512L << 20;

    /// <summary>Multiplier applied to the compressed length.</summary>
    public const long Factor = 32;

    /// <summary>
    /// The bound: <c>min(free space, max(512 MiB, 32 x compressed))</c>. A negative or unknown
    /// free space (some filesystems report -1) means "use the size bound alone" rather than
    /// "no limit": the size bound must always exist.
    /// </summary>
    public static long Limit(long compressedLength, long freeSpace)
    {
        long sizeBound = Math.Max(FloorBytes, compressedLength > 0 ? compressedLength * Factor : 0);
        return freeSpace > 0 ? Math.Min(freeSpace, sizeBound) : sizeBound;
    }
}
