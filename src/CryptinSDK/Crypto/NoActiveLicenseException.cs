// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using CryptinSDK.Dongle;

namespace CryptinSDK.Crypto;

/// <summary>
/// Encryption was refused because the dongle has no usable licence.
///
/// Thrown only on the write path. Decryption never consults a licence: a file
/// already encrypted opens with the right dongle for as long as that dongle
/// exists. The asymmetry is the product's promise, and this exception existing
/// at all on the read path would break it.
/// </summary>
public sealed class NoActiveLicenseException : CryptinFileException
{
    public NoActiveLicenseException(DongleLicenseError? error, string message)
        : base(message)
        => Error = error;

    /// <summary>Which of the refusals it was, so a caller can say something
    /// more useful than "no licence" — an expired one is renewed, a missing one
    /// is bought, and an unsupported firmware is updated.</summary>
    public DongleLicenseError? Error { get; }
}
