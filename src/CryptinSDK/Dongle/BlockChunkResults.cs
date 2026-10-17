// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Dongle;

/// <summary>
/// One encrypted chunk back from the dongle (§4.7).
/// </summary>
/// <param name="Ciphertext">Same length as the plaintext that went in — GCM is
/// a stream cipher underneath, so nothing is padded.</param>
/// <param name="Salt">Only on chunk 0. Minted by the dongle, not the host, and
/// destined for the .crin header.</param>
/// <param name="Nonce">Only on chunk 0, same reasoning.</param>
public record EncryptChunkResult(byte[] Ciphertext, byte[]? Salt, byte[]? Nonce);

/// <summary>One decrypted chunk back from the dongle (§4.7).</summary>
public record DecryptChunkResult(byte[] Plaintext);
