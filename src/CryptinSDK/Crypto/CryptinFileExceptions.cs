// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Crypto;

/// <summary>Base type for everything the file reader rejects.</summary>
public class CryptinFileException : Exception
{
    public CryptinFileException(string message) : base(message) { }
    public CryptinFileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Not a Crypt-in file, truncated, an unsupported version, or an unknown flag
/// bit — anything wrong with the container before decryption is attempted.
/// </summary>
public sealed class InvalidCryptinFileException : CryptinFileException
{
    public InvalidCryptinFileException(string message) : base(message) { }
}

/// <summary>
/// §5 step 7 — the header names a different dongle than the one attached.
/// Deliberately distinct from <see cref="CorruptedCryptinFileException"/>:
/// collapsing the two loses the only signal that separates "wrong dongle" from
/// "damaged file", and those need opposite advice to the user.
/// </summary>
public sealed class WrongDongleException : CryptinFileException
{
    public WrongDongleException(byte[] headerPublicKey)
        : base($"encrypted with a different dongle ({Convert.ToHexString(headerPublicKey)[..16].ToLowerInvariant()})")
        => HeaderPublicKey = headerPublicKey;

    /// <summary>The K_identity public key stored in the header. Covered by the
    /// AAD only in the legacy derivation: in files written by the dongle it is not
    /// authenticated, so this is a routing verdict, not an integrity one (§1).</summary>
    public byte[] HeaderPublicKey { get; }
}

/// <summary>§5 step 9 — the AEAD tag did not verify.</summary>
public sealed class CorruptedCryptinFileException : CryptinFileException
{
    public CorruptedCryptinFileException(string message, Exception inner)
        : base(message, inner) { }

    /// <summary>For a tag failure reported by the dongle (ERR_TAG_MISMATCH),
    /// where there is no underlying .NET exception to wrap — the AEAD check
    /// happened on the chip.</summary>
    public CorruptedCryptinFileException(string message) : base(message) { }
}
