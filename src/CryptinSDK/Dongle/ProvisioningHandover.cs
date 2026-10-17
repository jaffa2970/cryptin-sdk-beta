// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Security.Cryptography;
using CryptinSDK.Seed;

namespace CryptinSDK.Dongle;

/// <summary>
/// What a brand-new dongle hands over, once: the seed and the PIN.
///
/// Held as entropy rather than as a string of words because that is what comes
/// off the wire (see CRYPTIN_PROTOCOL.md §4.5) and because the words can then be
/// rendered on demand and not kept alive any longer than the form that shows
/// them. <see cref="Dispose"/> zeroes the entropy; the PIN is a string and
/// cannot be, which is noted rather than hidden.
///
/// The caller's obligation is the part that matters: display this, and only
/// after the user confirms, call <see cref="CryptinDongle.AckProvisioning"/>.
/// Acknowledging first and displaying after would recreate exactly the failure
/// this whole mechanism exists to prevent — a seed nobody has seen and nobody
/// can ever retrieve.
/// </summary>
public sealed class ProvisioningHandover : IDisposable
{
    private readonly byte[] _entropy;

    internal ProvisioningHandover(byte[] entropy, string pin)
    {
        _entropy = entropy;
        Pin = pin;
    }

    /// <summary>The dongle's 6-digit PIN. Derived from the seed, never chosen.</summary>
    public string Pin { get; }

    /// <summary>The 24 words, rendered from the entropy on each call.</summary>
    public string Mnemonic => Bip39.EntropyToMnemonic(_entropy);

    /// <summary>The 24 words as separate items, for a grid rather than a paragraph.</summary>
    public IReadOnlyList<string> Words => Mnemonic.Split(' ');

    public void Dispose() => CryptographicOperations.ZeroMemory(_entropy);
}
