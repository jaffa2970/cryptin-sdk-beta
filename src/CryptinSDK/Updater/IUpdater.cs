// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Updater;

/// <summary>
/// The first flash of a blank chip. That is the whole beta scope, and the
/// interface is this small on purpose: one that promised methods nobody will
/// implement would be worse than one that promises little.
///
/// **There is no update path**, and adding one here would be pretending. A
/// production dongle is in Secure Download Mode and refuses <c>write-flash</c> —
/// not from us, not from the user, not from an attacker holding it. Updating the
/// firmware of a burned chip needs a channel that does not go through the ROM
/// downloader, and that channel does not exist yet.
///
/// It follows that there is no host-side anti-rollback either. It would have
/// constrained only the people using our own updater, and there is no upgrade
/// path to protect in the first place. The real control, when it is needed, is
/// anti-rollback in eFuse on the bootloader side.
///
/// **And "first" is load-bearing, not decorative — measured 2026-08-31.** The
/// merged image is written at 0x0 and carries 20K of 0xFF across the nvs
/// partition at 0x9000, so flashing it over a dongle that is already set up
/// wipes K_root, file_secret, the PIN attempt counter and K_attestation. The
/// 24 words do not undo that: K_attestation is not derived from the seed, so
/// the chip loses the identity the licence server knows it by while the licence
/// stays on the identity that no longer exists. An implementation therefore has
/// to look before it writes, and hand the decision to a person.
/// </summary>
public interface IUpdater
{
    /// <param name="force">Write even when the chip is not blank. Without it an
    /// implementation must answer <see cref="FlashResult.AlreadyProvisioned"/>
    /// and write nothing, so the caller can ask.</param>
    Task<FlashResult> FirstFlashAsync(string portName, IProgress<int>? progress = null,
                                      bool force = false);
}

public enum FlashResult
{
    Success,
    PortNotFound,
    SignatureInvalid,
    FlashFailed,

    /// <summary>
    /// The chip carries data where a dongle keeps its keys, and **nothing was
    /// written**. Not a failure: it is the one moment when the answer still
    /// costs nothing, so it is a question rather than an outcome.
    ///
    /// Appended last on purpose — the values double as exit codes, and
    /// renumbering the four above would change what an existing caller reads.
    /// </summary>
    AlreadyProvisioned,
}

/// <summary>
/// <c>CryptinUpdater.exe</c> is absent.
///
/// Deliberately not a <see cref="FlashResult"/>. The four values above describe
/// how a flash went; a missing executable means no flash was attempted, and
/// folding it into <c>FlashFailed</c> would tell a user their dongle failed to
/// program when nothing ever touched it.
/// </summary>
public sealed class UpdaterNotInstalledException : Exception
{
    public UpdaterNotInstalledException(string path)
        : base($"CryptinUpdater.exe is not installed ({path})")
        => ExpectedPath = path;

    public string ExpectedPath { get; }
}
