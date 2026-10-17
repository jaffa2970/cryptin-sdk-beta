// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Dongle;

/// <summary>
/// The dongle refused a licence token handed to it (CMD_SET_LICENSE_JWT).
///
/// A transport-level failure is a <see cref="CryptinDongleException"/>; this is
/// the dongle working correctly and saying no, which the caller usually wants to
/// report rather than retry.
/// </summary>
public sealed class LicenseRejectedException : CryptinDongleException
{
    public LicenseRejectedException(DongleLicenseError error, string message)
        : base(message)
        => Error = error;

    public DongleLicenseError Error { get; }
}
