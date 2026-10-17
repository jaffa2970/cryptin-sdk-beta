// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinCli;

/// <summary>
/// What the shell gets back. These are the CLI's real interface — a script
/// consumes them, and a person reads stderr.
///
/// They are deliberately specific. Collapsing everything into 1 would force
/// callers to grep an error message that is free to be reworded, which is the
/// habit a stable exit code exists to prevent. Each value below is a different
/// thing for the caller to *do*: plug the dongle in, unlock it, buy a licence,
/// find the other dongle, restore from backup.
///
/// Values are frozen. Adding is fine, renumbering is not.
/// </summary>
internal enum ExitCode
{
    Ok = 0,

    /// <summary>Bad arguments, unknown command, missing file. The caller's typo.</summary>
    Usage = 2,

    /// <summary>No Crypt-in dongle is attached.</summary>
    NoDongle = 3,

    /// <summary>A dongle is attached but the session is locked. Run `cryptin unlock`.</summary>
    PinRequired = 4,

    /// <summary>Three wrong PINs. Only a seed recovery over UART clears this,
    /// which is not something a CLI can do for you.</summary>
    DongleBlocked = 5,

    /// <summary>Encryption needs an active licence. Decryption never does, so
    /// this can only come back from `encrypt`.</summary>
    NoLicense = 6,

    /// <summary>The file belongs to another dongle. Not damage — the wrong key.</summary>
    WrongDongle = 7,

    /// <summary>The AEAD tag did not verify: damaged or tampered with.</summary>
    CorruptFile = 8,

    /// <summary>Not a Crypt-in container at all.</summary>
    NotCryptinFile = 9,

    /// <summary>Over the per-transfer ceiling.</summary>
    FileTooLarge = 10,

    /// <summary>The dongle stopped answering mid-exchange.</summary>
    DongleError = 11,

    /// <summary>Filesystem trouble: permissions, missing directory, full disk.</summary>
    IoError = 12,

    /// <summary>Interrupted with Ctrl-C. Nothing was written.</summary>
    Cancelled = 130,
}
