// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Dongle;

/// <summary>
/// Why the dongle will not accept a licence.
///
/// <see cref="Unsupported"/> is not in the wire protocol: it is what a dongle
/// whose firmware predates licence enforcement produces, answering
/// STATUS_UNKNOWN_CMD. It is kept distinct from <see cref="NoLicense"/> because
/// the two need opposite handling — "this dongle has no licence" is a thing the
/// user can fix by activating one, while "this dongle cannot be asked" is a
/// thing only a firmware update changes, and reporting it as a missing licence
/// would send people to buy one that would not help.
/// </summary>
public enum DongleLicenseError
{
    /// <summary>No token stored (ERR_NO_LICENSE).</summary>
    NoLicense,

    /// <summary>Stored token is past its expiry (ERR_LICENSE_EXPIRED).</summary>
    Expired,

    /// <summary>Stored token no longer verifies against the compiled-in
    /// lake8.dev key (ERR_INVALID_SIGNATURE).</summary>
    InvalidSignature,

    /// <summary>The firmware does not implement CMD_GET_LICENSE_STATUS.</summary>
    Unsupported,
}

/// <summary>
/// What the dongle itself says about its licence.
///
/// Distinct from <c>LicenseStatus</c>, which is what the *server* says. The two
/// can legitimately disagree — a renewal that reached the server but not yet the
/// dongle is the ordinary case — and collapsing them would hide exactly the gap
/// that matters when encryption is refused.
/// </summary>
/// <param name="IsValid">True only on STATUS_OK. Never inferred from the
/// absence of an error.</param>
/// <param name="ExpiresAt">Present only when valid. UTC.</param>
/// <param name="Error">Why not, when not valid.</param>
public record DongleLicenseStatus(
    bool IsValid,
    DateTime? ExpiresAt,
    DongleLicenseError? Error);
