// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Crypto;

/// <summary>
/// The payload is larger than one transfer is allowed to carry.
///
/// A policy limit rather than a protocol one, and the distinction matters. With
/// three-byte `seq` and `total` the wire could address about 900 MB; what runs
/// out first is time, at the measured 27.8 KB/s a 5 MB file already takes three
/// minutes. So the ceiling is <see cref="Dongle.HidConstants.MaxEncryptBytes"/>,
/// set well below what the counters can number, and it is enforced before the
/// first chunk goes out rather than discovered part-way through.
///
/// The counters were two bytes once, and a 4.2 MB file wrapped them: the dongle
/// reached what it believed was the final chunk, answered with the tag, and the
/// host read that reply as ciphertext. That is why the check refuses up front
/// instead of trusting the arithmetic.
/// </summary>
public sealed class CryptinFileTooLargeException : CryptinFileException
{
    public CryptinFileTooLargeException(int size, int maximum)
        // Decimal MB, matching how MaxEncryptBytes is written (5_000_000): the
        // binary reading would print that ceiling as "4.8 MB", and a limit shown
        // in different units from the one the product documents is a support call.
        : base($"the file is {size / 1_000_000.0:F1} MB and the limit for a " +
               $"single transfer is {maximum / 1_000_000.0:F0} MB")
    {
        Size = size;
        Maximum = maximum;
    }

    /// <summary>
    /// The payload passed the ceiling and was refused before its full size was
    /// known — what <see cref="FolderArchive"/> throws when compression crosses
    /// the limit.
    ///
    /// Measuring the folder exactly would mean compressing all of it, which is
    /// the work being declined: a 120 MB folder would cost 120 MB of memory to
    /// produce a number that only appears in a message. So the size stays
    /// unknown, and <see cref="Size"/> is null rather than a placeholder that
    /// reads like a measurement.
    /// </summary>
    public CryptinFileTooLargeException(int maximum)
        : base($"the folder compresses to more than {maximum / 1_000_000.0:F0} MB, " +
               $"which is the limit for a single transfer")
    {
        Size = null;
        Maximum = maximum;
    }

    /// <summary>The payload size, or <c>null</c> when it was refused while still
    /// growing and only known to exceed <see cref="Maximum"/>.</summary>
    public int? Size { get; }

    public int Maximum { get; }
}
