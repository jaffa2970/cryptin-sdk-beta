// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Dongle;

/// <summary>Anything that went wrong talking to the dongle.</summary>
public class CryptinDongleException : Exception
{
    public CryptinDongleException(string message) : base(message) { }
    public CryptinDongleException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// The dongle answered <c>ERR_PIN_REQUIRED</c> (0xE1).
///
/// Its own type because of what it usually means: the unlocked state lives in
/// RAM only, so a physical unplug cuts power and puts the dongle back to LOCKED.
/// Seeing this on a command that worked a moment ago means "the dongle was
/// re-inserted" — re-verify the PIN — not "there is a bug".
/// </summary>
public sealed class PinRequiredException : CryptinDongleException
{
    public PinRequiredException(byte opcode)
        : base($"dongle is locked; verify the PIN before opcode 0x{opcode:X2}")
        => Opcode = opcode;

    public byte Opcode { get; }
}

/// <summary>
/// No reply within the read timeout. Most often this is rule 1 of
/// CRYPTIN_PROTOCOL.md §7: the firmware stages one command at a time and
/// silently drops anything that arrives while a dispatch is pending, so a
/// dropped command looks exactly like a dead device.
/// </summary>
public sealed class CryptinTimeoutException : CryptinDongleException
{
    /// <param name="inner">Nullable: the deadline can also run out while stepping
    /// over stale replies, where there is no underlying exception to carry — see
    /// CryptinDongle.Read.</param>
    public CryptinTimeoutException(byte opcode, int timeoutMs, Exception? inner)
        : base($"no reply to opcode 0x{opcode:X2} within {timeoutMs} ms", inner) { }
}

/// <summary>What <c>CMD_VERIFY_PIN</c> answered.</summary>
public enum PinResult
{
    /// <summary>STATUS_OK — the session is UNLOCKED until power is cut.</summary>
    Ok,

    /// <summary>ERR_PIN_WRONG — the attempt was counted, on the dongle, in NVS.</summary>
    Wrong,

    /// <summary>ERR_PIN_BLOCKED — attempts exhausted. Only a seed recovery over
    /// the UART console undoes this; a correct PIN will not.</summary>
    Blocked,

    /// <summary>STATUS_BAD_LENGTH — not counted as an attempt.</summary>
    BadLength,

    /// <summary>STATUS_NOT_READY — key material not loaded yet.</summary>
    NotReady,
}
