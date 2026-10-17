// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Text;

namespace CryptinSDK.Seed;

/// <summary>
/// What the dongle derives from its 32-byte entropy, reproduced on the host for
/// the one case that needs it: opening files from the 24 words, without the
/// dongle. Mirrors firmware <c>Keystore::deriveFromEntropy</c> (keystore.cpp).
///
/// Holding the words is holding everything; this adds no power, it only lets the
/// owner use what the paper already gives them.
/// </summary>
public static class SeedDerivation
{
    /// <summary>firmware <c>FILE_SECRET_CONTEXT</c>.</summary>
    public const string FileSecretContext = "cryptin-file-secret-v1";

    /// <summary>
    /// file_secret = BLAKE2b-256 keyed with the entropy over the context string
    /// (Monocypher <c>crypto_blake2b_keyed(out, 32, entropy, 32, ctx, len)</c>).
    /// The input keying material of every file key (CRYPTIN_FILE_FORMAT.md §1).
    /// </summary>
    public static byte[] FileSecret(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != 32)
            throw new ArgumentException($"32 bytes of entropy are needed, there are {entropy.Length}",
                nameof(entropy));
        return Blake2b.Hash(Encoding.ASCII.GetBytes(FileSecretContext), entropy, 32);
    }
}
