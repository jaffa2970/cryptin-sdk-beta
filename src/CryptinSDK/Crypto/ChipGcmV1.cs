// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CryptinSDK.Crypto;

/// <summary>
/// CRYPTIN_FILE_FORMAT.md §2.1 — version 1 as the chip actually wrote it
/// (findings J4/K4). Read-only: nothing writes this form any more, and the chip
/// itself answers ERR_DEPRECATED to a v1 encrypt since format v2.
///
/// The chip's AES-GCM restarted at a 16-byte boundary on every chunk of
/// CMD_ENCRYPT_BLOCK: CTR jumps to the next counter block after a partial one
/// (leftover keystream discarded), and GHASH runs over each chunk of ciphertext
/// zero-padded on its own, then the length block with the true total length,
/// AAD empty. With the product's cuts — 27, then 55 — every chip-written v1 file
/// longer than 27 bytes is in this form, and standard AES-GCM rejects it.
///
/// This is what lets a file from the beta be opened with file_secret alone, the
/// recovery-without-dongle case. The cuts are a historical fact of the files
/// already written, not a protocol parameter: they are spelled out here rather
/// than taken from HidConstants, so a future change to the transfer sizes cannot
/// silently stop old files from opening.
///
/// The tag is verified over the ciphertext before a single byte of plaintext is
/// produced, and compared in fixed time.
/// </summary>
internal static class ChipGcmV1
{
    /// <summary>Opening chunk of a v1 transfer, as written by every beta app.</summary>
    internal const int FirstCut = 27;

    /// <summary>Every following chunk.</summary>
    internal const int NextCut = 55;

    private const int Block = 16;

    /// <returns>false on a tag mismatch; plaintext is then empty.</returns>
    internal static bool TryDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
                                    ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
                                    out byte[] plaintext)
    {
        plaintext = [];
        if (nonce.Length != 12 || tag.Length != Block)
            return false;

        using var aes = Aes.Create();
        aes.Key = key.ToArray();

        Span<byte> j0 = stackalloc byte[Block];
        nonce.CopyTo(j0);
        j0[15] = 1;

        Span<byte> buf = stackalloc byte[Block];
        Span<byte> ks = stackalloc byte[Block];
        buf.Clear();
        aes.EncryptEcb(buf, ks, PaddingMode.None);                  // H = E_K(0^128)
        var h = BinaryPrimitives.ReadUInt128BigEndian(ks);

        // Tag first, over the ciphertext exactly as the chip chunked it.
        UInt128 y = 0;
        foreach (var (start, length) in Cuts(ciphertext.Length))
        {
            for (var b = start; b < start + length; b += Block)
            {
                buf.Clear();
                ciphertext.Slice(b, Math.Min(Block, start + length - b)).CopyTo(buf);
                y = GfMul(y ^ BinaryPrimitives.ReadUInt128BigEndian(buf), h);
            }
        }
        y = GfMul(y ^ (UInt128)((ulong)ciphertext.Length * 8), h);  // len(A)=0 || len(C)

        aes.EncryptEcb(j0, ks, PaddingMode.None);
        BinaryPrimitives.WriteUInt128BigEndian(buf, y);
        for (var i = 0; i < Block; i++) buf[i] ^= ks[i];
        if (!CryptographicOperations.FixedTimeEquals(buf, tag))
            return false;

        // Then CTR, restarting on a block boundary at every chunk.
        var output = new byte[ciphertext.Length];
        Span<byte> counter = stackalloc byte[Block];
        j0.CopyTo(counter);
        foreach (var (start, length) in Cuts(ciphertext.Length))
        {
            for (var b = start; b < start + length; b += Block)
            {
                var c = BinaryPrimitives.ReadUInt32BigEndian(counter[12..]);
                BinaryPrimitives.WriteUInt32BigEndian(counter[12..], unchecked(c + 1));   // inc32
                aes.EncryptEcb(counter, ks, PaddingMode.None);
                var n = Math.Min(Block, start + length - b);
                for (var i = 0; i < n; i++) output[b + i] = (byte)(ciphertext[b + i] ^ ks[i]);
            }
        }
        CryptographicOperations.ZeroMemory(ks);
        plaintext = output;
        return true;
    }

    private static IEnumerable<(int Start, int Length)> Cuts(int total)
    {
        var first = Math.Min(FirstCut, total);
        yield return (0, first);
        for (var off = first; off < total; off += NextCut)
            yield return (off, Math.Min(NextCut, total - off));
    }

    /// <summary>
    /// GF(2^128) product in GCM bit order (NIST SP 800-38D, algorithm 1),
    /// branch-free on the operands.
    /// </summary>
    private static UInt128 GfMul(UInt128 x, UInt128 y)
    {
        var r = (UInt128)0xE1 << 120;
        UInt128 z = 0, v = y;
        for (var i = 127; i >= 0; i--)
        {
            var xBit = (UInt128)0 - ((x >> i) & 1);                // all ones if set
            z ^= v & xBit;
            var lsb = (UInt128)0 - (v & 1);
            v = (v >> 1) ^ (r & lsb);
        }
        return z;
    }
}
