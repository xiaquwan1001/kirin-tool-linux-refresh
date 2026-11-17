// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System;
using System.Collections.Generic;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// One chunk as far as the block accounting is concerned: its type, the block count it declares
/// (only meaningful for fill/dontcare) and its on-disk size (raw chunks expand to
/// <c>TotalSize - 12</c> bytes of payload).
/// </summary>
public readonly record struct SparseChunkFact(ushort Type, uint DeclaredBlocks, uint TotalSize);

/// <summary>
/// The result of checking a sparse header's <c>TotalBlocks</c> against the blocks its own chunks
/// describe. <see cref="Token"/> is the machine value (`verified` / `failed`) so a caller can log
/// or report it verbatim instead of re-deriving it from a bool.
/// </summary>
public sealed record SparseBlockSumVerdict(bool Consistent, string Token, uint DeclaredBlocks, long AccountedBlocks, string Reason)
{
    public bool Failed => !Consistent;
}

/// <summary>
/// M2 (audit 2026-11-15 round5 §1.2): <c>SparseHeader.TotalBlocks</c> is a claim about how many
/// <c>BlockSize</c> blocks the image expands to, and nothing used to check it. A header whose
/// <c>TotalBlocks</c> is smaller (or larger) than the blocks its chunks account for describes an
/// image that cannot exist; merging and flashing such a super image writes geometry the device
/// never agreed to. The rule is the one libsparse applies when it reads an image back: the chunk
/// accounting must equal the declared block count exactly, otherwise the image is refused
/// (verdict <c>failed</c>).
///
/// This lives outside <see cref="SuperMerger"/> so the rule can be exercised without a multi-GB
/// file — the merge itself only walks the chunks and feeds this.
/// </summary>
public static class SparseBlockSum
{
    public static SparseBlockSumVerdict Evaluate(uint declaredBlocks, uint blockSize, IEnumerable<SparseChunkFact> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        long accounted = 0;
        int index = 0;
        foreach (SparseChunkFact chunk in chunks)
        {
            long blocks;
            switch (chunk.Type)
            {
                case SparseConstants.CHUNK_TYPE_RAW:
                {
                    long payload = (long)chunk.TotalSize - SparseConstants.CHUNK_HEADER_SIZE;
                    if (payload < 0)
                        return Failed(declaredBlocks, accounted,
                            "chunk " + index + " declares TotalSize=" + chunk.TotalSize + " (< " + SparseConstants.CHUNK_HEADER_SIZE + ")");
                    if (blockSize == 0)
                        return Failed(declaredBlocks, accounted, "BlockSize=0 makes the block accounting meaningless");
                    if (payload % blockSize != 0)
                        return Failed(declaredBlocks, accounted,
                            "raw chunk " + index + " carries " + payload + " bytes, which is not a multiple of BlockSize=" + blockSize);
                    blocks = payload / blockSize;
                    break;
                }

                case SparseConstants.CHUNK_TYPE_FILL:
                case SparseConstants.CHUNK_TYPE_DONT_CARE:
                    blocks = chunk.DeclaredBlocks;
                    break;

                case SparseConstants.CHUNK_TYPE_CRC32:
                    // 4 payload bytes of checksum, no data blocks.
                    blocks = 0;
                    break;

                default:
                    return Failed(declaredBlocks, accounted,
                        "chunk " + index + " has unknown type 0x" + chunk.Type.ToString("x4"));
            }

            accounted += blocks;
            index++;
        }

        if (accounted != declaredBlocks)
            return Failed(declaredBlocks, accounted,
                "the header declares TotalBlocks=" + declaredBlocks + " but the chunks account for " + accounted + " block(s)");

        return new SparseBlockSumVerdict(true, "verified", declaredBlocks, accounted,
            "chunk block count matches TotalBlocks=" + declaredBlocks);
    }

    private static SparseBlockSumVerdict Failed(uint declaredBlocks, long accounted, string reason) =>
        new(false, "failed", declaredBlocks, accounted, reason);
}
