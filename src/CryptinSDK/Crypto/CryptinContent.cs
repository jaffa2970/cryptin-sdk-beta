// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Crypto;

/// <summary>What a Crypt-in file decrypts to.</summary>
public sealed class CryptinContent
{
    public CryptinContent(byte[] plaintext, bool isFolder, CryptinFileFormat format)
    {
        Plaintext = plaintext;
        IsFolder = isFolder;
        Format = format;
    }

    public byte[] Plaintext { get; }

    /// <summary>.crin flag bit 0 — the plaintext is a ZIP archive of a directory
    /// and is unzipped back to a folder on decrypt. Always false for .cryptin,
    /// which has no flags byte.</summary>
    public bool IsFolder { get; }

    /// <summary>Which container it came out of, decided by the magic and never
    /// by the file name.</summary>
    public CryptinFileFormat Format { get; }
}
