// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace CryptinSDK.Seed;

/// <summary>
/// BLAKE2b (RFC 7693), keyed, one shot. .NET has no BLAKE2, and the dongle
/// derives file_secret with it (Monocypher <c>crypto_blake2b_keyed</c>,
/// firmware keystore.cpp), so recovering files from the 24 words alone needs
/// this on the host.
///
/// Written from RFC 7693 §3, not ported. Checked against the RFC's own vector
/// and the reference keyed KAT in the CLI self-test, and against the chip:
/// file_secret from α's words equals what the chip returns.
/// </summary>
public static class Blake2b
{
    private static readonly ulong[] Iv =
    [
        0x6A09E667F3BCC908, 0xBB67AE8584CAA73B, 0x3C6EF372FE94F82B, 0xA54FF53A5F1D36F1,
        0x510E527FADE682D1, 0x9B05688C2B3E6C1F, 0x1F83D9ABFB41BD6B, 0x5BE0CD19137E2179,
    ];

    private static readonly byte[][] Sigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
    ];

    /// <summary>BLAKE2b with an optional key (0..64 bytes), output 1..64 bytes.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> message, ReadOnlySpan<byte> key, int outputLength)
    {
        if (outputLength is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(outputLength));
        if (key.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(key));

        var h = (ulong[])Iv.Clone();
        h[0] ^= 0x01010000UL ^ ((ulong)key.Length << 8) ^ (ulong)outputLength;

        // A key is processed as a first block of its own, zero-padded (§3.3).
        var data = new byte[(key.Length > 0 ? 128 : 0) + message.Length];
        key.CopyTo(data);
        message.CopyTo(data.AsSpan(key.Length > 0 ? 128 : 0));

        var block = new byte[128];
        var m = new ulong[16];
        var v = new ulong[16];
        UInt128 counter = 0;
        var offset = 0;
        try
        {
            // Every full block except the last is compressed as non-final.
            while (data.Length - offset > 128)
            {
                counter += 128;
                Compress(h, data.AsSpan(offset, 128), counter, last: false, m, v);
                offset += 128;
            }
            Array.Clear(block);
            data.AsSpan(offset).CopyTo(block);
            counter += (ulong)(data.Length - offset);
            Compress(h, block, counter, last: true, m, v);

            var full = new byte[64];
            for (var i = 0; i < 8; i++)
                BinaryPrimitives.WriteUInt64LittleEndian(full.AsSpan(i * 8), h[i]);
            var output = full[..outputLength];
            CryptographicOperations.ZeroMemory(full);
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            CryptographicOperations.ZeroMemory(block);
            Array.Clear(m);
            Array.Clear(v);
            Array.Clear(h);
        }
    }

    private static void Compress(ulong[] h, ReadOnlySpan<byte> block, UInt128 t, bool last,
                                 ulong[] m, ulong[] v)
    {
        for (var i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        for (var i = 0; i < 8; i++)
        {
            v[i] = h[i];
            v[i + 8] = Iv[i];
        }
        v[12] ^= (ulong)t;
        v[13] ^= (ulong)(t >> 64);
        if (last) v[14] = ~v[14];

        for (var r = 0; r < 12; r++)
        {
            var s = Sigma[r % 10];
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }
        for (var i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(ulong[] v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
