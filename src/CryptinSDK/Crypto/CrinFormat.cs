// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Crypto;

/// <summary>The two legacy containers. They share their whole cryptographic core
/// and differ only in the header layout — see CRYPTIN_FILE_FORMAT.md §1.</summary>
public enum CryptinFileFormat
{
    /// <summary>C# container, 66-byte header. The format new code writes.</summary>
    Crin,

    /// <summary>Python container, 69-byte header. Read-only for new code.</summary>
    Cryptin,
}

/// <summary>
/// The container: magics, offsets, header parsing and building.
/// Every constant below is transcribed from CRYPTIN_FILE_FORMAT.md, which is the
/// authority on the format — not KriptC#/CryptIn/Crypto/CrinFormat.cs and not
/// tools/cryptin_crypt.py. Where this file and the document disagree, the
/// document wins and this file is the bug.
/// </summary>
public static class CrinFormat
{
    // --- §2 .crin ---------------------------------------------------------
    /// <summary>ASCII "CRIN".</summary>
    public static ReadOnlySpan<byte> CrinMagic => "CRIN"u8;

    // --- §3 .cryptin ------------------------------------------------------
    /// <summary>ASCII "CRYPTIN1".</summary>
    public static ReadOnlySpan<byte> CryptinMagic => "CRYPTIN1"u8;

    /// <summary>Version 1: .crin and .cryptin before 2026-10-09 (§1). On a
    /// chip-written .crin the multi-chunk GCM is not standard (§2).</summary>
    public const byte Version1 = 1;

    /// <summary>Version 2, .crin only (2026-10-09): standard AES-GCM, info
    /// "cryptin-file-key-v2", the whole 66-byte header as AAD (§2a).</summary>
    public const byte Version2 = 2;

    /// <summary>Sizes shared by both containers (§1).</summary>
    public const int PublicKeyLength = 32;
    public const int SaltLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;

    /// <summary>Bit 0 of the .crin flags byte: the payload is a ZIP of a folder.
    /// No other bit is defined, and unknown bits must be rejected rather than
    /// ignored, so a future flag cannot be read as "plain file" (§2).</summary>
    public const byte FlagFolder = 0x01;

    /// <summary>Every flag bit that has a meaning today.</summary>
    public const byte KnownFlags = FlagFolder;

    /// <summary>Layout of .crin — header 66 bytes. AAD = bytes [0, 66) in the
    /// legacy derivation, empty in the dongle one (CRYPTIN_FILE_FORMAT.md §1).</summary>
    public static readonly CryptinLayout Crin = new(
        format: CryptinFileFormat.Crin,
        magicLength: 4,
        headerLength: 66,
        versionOffset: 4,
        flagsOffset: 5,
        publicKeyOffset: 6,
        saltOffset: 38,
        nonceOffset: 54);

    /// <summary>Layout of .cryptin — header 69 bytes, AAD = bytes [0, 69); always
    /// the legacy derivation, since the chip never writes this container.
    /// No flags byte: the folder concept never existed on the Python side.</summary>
    public static readonly CryptinLayout Cryptin = new(
        format: CryptinFileFormat.Cryptin,
        magicLength: 8,
        headerLength: 69,
        versionOffset: 8,
        flagsOffset: null,
        publicKeyOffset: 9,
        saltOffset: 41,
        nonceOffset: 57);

    /// <summary>
    /// §4 — dispatch on the magic, never on the file name. A user who renames a
    /// file must still get the right reader.
    ///
    /// The two magics diverge at byte 2 ("CRIN" against "CRYP"), so testing the
    /// shorter one first is unambiguous: "CRYPTIN1" cannot begin with "CRIN".
    /// </summary>
    /// <exception cref="InvalidCryptinFileException">Neither magic matched.</exception>
    public static CryptinLayout Detect(ReadOnlySpan<byte> blob)
    {
        if (blob.Length >= Crin.MagicLength && blob[..Crin.MagicLength].SequenceEqual(CrinMagic))
            return Crin;

        if (blob.Length >= Cryptin.MagicLength && blob[..Cryptin.MagicLength].SequenceEqual(CryptinMagic))
            return Cryptin;

        throw new InvalidCryptinFileException("not a Crypt-in file");
    }

    /// <summary>
    /// Builds the 66-byte .crin header (§2). New code writes this container and
    /// never .cryptin, so files it produces stay readable by the old app for as
    /// long as that app exists.
    ///
    /// In the legacy derivation the returned array is also the AAD, which stops
    /// the version, the flags or the public key from being edited independently
    /// of the payload. **The dongle derivation authenticates none of it** (§1,
    /// Known limitation): a flipped folder bit still decrypts.
    /// </summary>
    public static byte[] BuildCrinHeader(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce,
        bool isFolder)
        => BuildCrinHeader(publicKey, salt, nonce, isFolder, Version1);

    /// <summary>The same header with an explicit version byte. In v2 these 66
    /// bytes are the AAD, so every field of it is authenticated (§2a).</summary>
    public static byte[] BuildCrinHeader(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce,
        bool isFolder,
        byte version)
    {
        Require(publicKey, PublicKeyLength, nameof(publicKey));
        Require(salt, SaltLength, nameof(salt));
        Require(nonce, NonceLength, nameof(nonce));

        var header = new byte[Crin.HeaderLength];
        CrinMagic.CopyTo(header);
        header[Crin.VersionOffset] = version;
        header[Crin.FlagsOffset!.Value] = isFolder ? FlagFolder : (byte)0;
        publicKey.CopyTo(header.AsSpan(Crin.PublicKeyOffset));
        salt.CopyTo(header.AsSpan(Crin.SaltOffset));
        nonce.CopyTo(header.AsSpan(Crin.NonceOffset));
        return header;
    }

    private static void Require(ReadOnlySpan<byte> value, int length, string name)
    {
        if (value.Length != length)
            throw new ArgumentException($"expected {length} bytes, got {value.Length}", name);
    }
}

/// <summary>Where each field sits, for one of the two containers.</summary>
public sealed class CryptinLayout
{
    internal CryptinLayout(
        CryptinFileFormat format,
        int magicLength,
        int headerLength,
        int versionOffset,
        int? flagsOffset,
        int publicKeyOffset,
        int saltOffset,
        int nonceOffset)
    {
        Format = format;
        MagicLength = magicLength;
        HeaderLength = headerLength;
        VersionOffset = versionOffset;
        FlagsOffset = flagsOffset;
        PublicKeyOffset = publicKeyOffset;
        SaltOffset = saltOffset;
        NonceOffset = nonceOffset;
    }

    public CryptinFileFormat Format { get; }
    public int MagicLength { get; }

    /// <summary>Bytes before the ciphertext. This exact slice is the AAD of the
    /// legacy derivation; the dongle derivation has none (§1).</summary>
    public int HeaderLength { get; }

    public int VersionOffset { get; }

    /// <summary>Null for .cryptin, which has no flags byte.</summary>
    public int? FlagsOffset { get; }

    public int PublicKeyOffset { get; }
    public int SaltOffset { get; }
    public int NonceOffset { get; }

    /// <summary>Shortest possible file: header + tag, with empty ciphertext.
    /// 82 for .crin, 85 for .cryptin (§5 step 1).</summary>
    public int MinimumFileLength => HeaderLength + CrinFormat.TagLength;
}
