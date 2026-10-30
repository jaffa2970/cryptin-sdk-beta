// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
namespace CryptinSDK.Dongle;

/// <summary>
/// The wire protocol, mirrored by hand from firmware/include/protocol.h.
///
/// This is a mirror, not a source. `protocol.h` is the authority and
/// CRYPTIN_PROTOCOL.md is checked against it like any other copy. This file is
/// checked too — from the pre-build step, with the Krypt checkout present:
///
///     python ../scripts/check-protocol-mirrors.py \
///         --mirror cryptin-sdk/src/CryptinSDK/Dongle/HidConstants.cs
///
/// That check exists because hand-copying has already failed once here: the PIN
/// gate reached the firmware and the Python tools but never the C# app, and the
/// only end-user client answered ERR_PIN_REQUIRED for a week before anyone
/// noticed. Do not add a constant the firmware does not have — 0x05 and 0x07 are
/// reserved and unimplemented, and declaring them would be a contradiction, not
/// an omission.
/// </summary>
public static class HidConstants
{
    // --- transport (§1) ---------------------------------------------------
    public const ushort VendorId = 0x303A;   // Espressif; placeholder until registered
    public const ushort ProductId = 0x8123;  // placeholder
    public const ushort UsagePage = 0xFF00;  // vendor-defined
    public const ushort Usage = 0x01;

    /// <summary>Explicit, never 0. arduino-esp32's USBHID device routing has a
    /// documented bug (espressif/arduino-esp32#9288) where OUT reports never
    /// reach the callback for report-ID-less devices.</summary>
    public const byte ReportId = 1;

    /// <summary>Data bytes per report, after the report-ID byte.</summary>
    public const int PayloadSize = 63;

    /// <summary>What actually goes on the wire: report ID + payload.</summary>
    public const int ReportSize = 1 + PayloadSize;

    /// <summary>The reply is sent from loop(), not from the USB callback, so a
    /// read must always be bounded. The reference Python client uses 2000 ms.</summary>
    public const int ReadTimeoutMs = 2000;

    // --- payload sizes (§4) -----------------------------------------------
    public const int NonceSize = 32;
    public const int PubkeySize = 32;
    public const int SigSize = 64;
    public const int SigChunk = SigSize / 2;   // the signature spans two IN reports
    public const int SecretSize = 32;
    public const int AttestPubkeySize = 32;

    // --- opcodes (§3) -----------------------------------------------------
    public const byte CmdPing = 0x01;
    public const byte CmdGetPubkey = 0x02;
    public const byte CmdChallenge = 0x03;
    public const byte CmdGetSecret = 0x04;
    public const byte CmdGetAttestationPubkey = 0x06;
    public const byte CmdVerifyPin = 0x08;
    public const byte CmdGetProvisioning = 0x09;
    public const byte CmdAckProvisioning = 0x0A;

    // 0x0B–0x0E were all marked "not implemented yet" here until 2026-08-03,
    // long after the firmware had them: the app was already encrypting through
    // 0x0D. A client reading these would have concluded the licence gate did not
    // exist on the chip and written around it. Implemented and verified on
    // hardware as of firmware 0a4429f.

    /// <summary>The licence token, chunked. Answered before the PIN gate — a
    /// dongle can be licensed while locked, which is what lets activation run on
    /// a chip whose PIN the user has not been told yet.</summary>
    public const byte CmdSetLicenseJwt = 0x0B;

    /// <summary>What the chip makes of the token it holds. **Behind the PIN gate
    /// since 2026-08-03** — call it after VerifyPin, not before, or it answers
    /// ERR_PIN_REQUIRED. Its verification result is cached: it re-parsed and
    /// re-verified RSA-4096 on every call, which while the opcode was still
    /// PIN-exempt was a denial-of-service lever.</summary>
    public const byte CmdGetLicenseStatus = 0x0C;

    /// <summary>Encryption on the chip, licence-gated. Behind the PIN gate:
    /// ERR_PIN_REQUIRED while the session is locked.</summary>
    public const byte CmdEncryptBlock = 0x0D;

    /// <summary>Decryption on the chip. No licence gate, ever — old files must
    /// open forever. Behind the PIN gate all the same: ERR_PIN_REQUIRED while
    /// the session is locked.</summary>
    public const byte CmdDecryptBlock = 0x0E;

    /// <summary>Format v2 encryption (2026-10-09): standard AES-GCM whatever the
    /// cuts, header authenticated as AAD. What the SDK writes. Licence-gated and
    /// behind the PIN gate, like 0x0D. Firmware older than 2026-10-09 answers
    /// STATUS_UNKNOWN_CMD.</summary>
    public const byte CmdEncryptBlockV2 = 0x0F;

    /// <summary>Format v2 decryption. No licence gate; PIN gate as 0x0E.</summary>
    public const byte CmdDecryptBlockV2 = 0x10;

    /// <summary>New identity on the same chip (2026-10-30, §4.9): takes the
    /// current PIN again, replies with the new 32-byte entropy and 6-digit PIN.
    /// Behind the PIN gate; the PIN in it is counted like CMD_VERIFY_PIN.</summary>
    public const byte CmdRekeyBegin = 0x11;

    /// <summary>Makes the pending seed the identity; takes the NEW PIN, one try.
    /// On success the session is locked: unlock again with the new PIN.</summary>
    public const byte CmdRekeyCommit = 0x12;

    // --- status codes (§5) ------------------------------------------------
    public const byte StatusOk = 0x00;
    public const byte StatusNotReady = 0x01;
    public const byte StatusBadLength = 0x02;
    public const byte StatusUnknownCmd = 0xFF;

    public const byte ErrPinRequired = 0xE1;
    public const byte ErrPinWrong = 0xE2;
    public const byte ErrPinBlocked = 0xE3;
    public const byte ErrAlreadyAcknowledged = 0xE4;

    /// <summary>CMD_VERIFY_PIN sooner than one second after the previous one:
    /// refused before anything is counted or written. Retry. Since 2026-10-10
    /// (red-team finding L2; before that the chip held the command instead).</summary>
    public const byte ErrPinTooSoon = 0xE5;

    /// <summary>The chip's minimum spacing between two CMD_VERIFY_PIN
    /// (PIN_VERIFY_MIN_INTERVAL_MS).</summary>
    public const int PinVerifyMinIntervalMs = 1000;

    /// <summary>CMD_REKEY_COMMIT with no pending seed (never begun, dropped by a
    /// reboot or by a wrong PIN).</summary>
    public const byte ErrRekeyNone = 0xE6;

    /// <summary>CMD_REKEY_COMMIT with a PIN that is not the pending seed's. The
    /// pending seed is dropped: start again with CMD_REKEY_BEGIN.</summary>
    public const byte ErrRekeyPinMismatch = 0xE7;

    // --- licence enforcement (§8) -----------------------------------------
    public const byte ErrInvalidJwt = 0xF1;
    public const byte ErrJwtExpired = 0xF2;
    public const byte ErrNoLicense = 0xF3;
    public const byte ErrLicenseExpired = 0xF4;
    public const byte ErrInvalidSignature = 0xF5;

    /// <summary>The GCM tag did not verify on CMD_DECRYPT_BLOCK.
    ///
    /// Maps to <c>CorruptedCryptinFileException</c>, never to
    /// <c>WrongDongleException</c>: a failed tag cannot tell the two apart. The
    /// "wrong dongle" verdict comes from comparing the header's public key
    /// first, host-side, which is why the two exceptions stay distinct.</summary>
    public const byte ErrTagMismatch = 0xF6;

    /// <summary>CMD_GET_SECRET on firmware that no longer exports it.</summary>
    public const byte ErrDeprecated = 0xF7;

    /// <summary>A block-cipher chunk arrived out of order. The transfer is gone;
    /// start again from the opening chunk.</summary>
    public const byte ErrSeqError = 0xF8;

    /// <summary>CMD_CHALLENGE computed a signature that failed its own
    /// verification, so the chip did not send it. Only a fault gets here;
    /// retrying is fine. Since 2026-10-08.</summary>
    public const byte ErrSignFault = 0xF9;

    // --- block cipher (§4.7) ----------------------------------------------
    /// <summary>Chunk 0 carries only the salt: opcode, seq, total and the
    /// 16-byte salt, no payload.</summary>
    public const int BlockSaltChunkSize = CrinSaltLength;

    /// <summary>
    /// seq and total are 3 bytes each, by design. The practical ceiling is a
    /// software one (5 MB), not the protocol's ~900 MB.
    ///
    /// Why: one protocol for USB, WiFi and BLE, so there is no fork to maintain
    /// when Network Edition arrives (Q4 2027). A one-person team does not run
    /// twenty parallel firmwares. Widening the counters costs two payload bytes
    /// per chunk now and saves a migration later.
    ///   -- lake8.dev, August 2026
    /// </summary>
    public const int BlockSeqBytes = 3;

    /// <inheritdoc cref="BlockSeqBytes"/>
    public const int BlockTotalBytes = 3;

    /// <summary>Header on the wire: opcode, the transport's length byte, then
    /// seq and total.</summary>
    public const int BlockHeaderSize = 2 + BlockSeqBytes + BlockTotalBytes;

    /// <summary>Payload bytes per data chunk: 63 less the header.</summary>
    public const int BlockChunkSize = PayloadSize - BlockHeaderSize;

    /// <summary>Payload the opening chunk carries. Smaller than a data chunk
    /// because salt and nonce share the exchange, and bounded by the *decrypt*
    /// request (63 − 6 − 16 − 12) rather than the encrypt reply (63 − 2 − 16 −
    /// 12 = 33): sizing it from the larger side would let a file be written that
    /// could never be read back.</summary>
    public const int BlockOpenChunkSize =
        PayloadSize - BlockHeaderSize - CrinSaltLength - NonceLength;

    /// <summary>The container's nonce length, which the wire follows.</summary>
    public const int NonceLength = 12;

    /// <summary>Bytes of random the host appends to a v2 decrypt close; the dongle
    /// echoes them only on a verified tag, binding the reply to the request
    /// (round-6 finding L3). Must match BLOCK_HOST_NONCE_SIZE in the firmware.</summary>
    public const int CloseNonceLength = 16;

    /// <summary>
    /// Most chunks one transfer can address: seq and total are two bytes each.
    ///
    /// Past this the counters wrap silently, and the failure is vicious — the
    /// dongle reaches what it believes is the last chunk, answers with the tag,
    /// and the host reads that reply as data. Observed on a 4.2 MB file: 77204
    /// chunks arrived as 77204 − 65536 = 11668.
    /// </summary>
    public const int BlockMaxChunks = (1 << (8 * BlockSeqBytes)) - 1;

    /// <summary>Largest payload the counters could address: the opening chunk
    /// plus the full ones after it, roughly 900 MB with 3-byte fields. Not the
    /// operative limit — see <see cref="MaxEncryptBytes"/>.</summary>
    public const int BlockMaxPayload =
        BlockOpenChunkSize + BlockChunkSize * (BlockMaxChunks - 1);

    /// <summary>
    /// The declared software limit: 5 MB.
    ///
    /// Well under what the protocol can address, because the binding constraint
    /// is time rather than counter width. At the measured 27.8 KB/s — a
    /// full-speed USB ceiling, not a firmware one — 5 MB already takes about
    /// three minutes; 50 MB would take half an hour and read as a hang.
    /// </summary>
    public const int MaxEncryptBytes = 5_000_000;

    /// <summary>The container's salt length, which the wire follows rather than
    /// the other way round — see CrinFormat.</summary>
    public const int CrinSaltLength = 16;

    /// <summary>Payload bytes per CMD_SET_LICENSE_JWT chunk. Same six-byte
    /// header as the block commands — opcode, the transport's own length byte,
    /// then seq and total — so 63 − 6.</summary>
    public const int JwtChunkSize = PayloadSize - BlockHeaderSize;

    /// <summary>Two bytes of sequence now, but the firmware caps the token far
    /// below what they could address: an RS256 JWT with a 4096-bit signature
    /// runs to roughly 900 bytes, and it accepts 2 kB.</summary>
    public const int JwtMaxChunks = 255;

    // --- provisioning handover (§7) ---------------------------------------
    /// <summary>BIP-39 entropy for 24 words. The dongle sends the entropy, not
    /// the words: they do not fit in a 63-byte report, and a client that offers
    /// recovery already needs a BIP-39 codec anyway.</summary>
    public const int EntropySize = 32;

    // --- PIN gate (§6) ----------------------------------------------------
    /// <summary>6 ASCII digit characters, not a 6-byte integer.</summary>
    public const int PinLength = 6;

    /// <summary>Three, ever, until a correct PIN or a seed recovery. The counter
    /// lives in NVS and survives power cycles — which is why a client must never
    /// present its own count of attempts as truth.</summary>
    public const int PinMaxAttempts = 3;

    /// <summary>CMD_PING's reply payload: ASCII "CRYPTIN" and a NUL.</summary>
    public static ReadOnlySpan<byte> PingMagic => "CRYPTIN\0"u8;

    /// <summary>Human-readable name for a status byte, for messages and logs.</summary>
    public static string DescribeStatus(byte status) => status switch
    {
        StatusOk => "STATUS_OK",
        StatusNotReady => "STATUS_NOT_READY",
        StatusBadLength => "STATUS_BAD_LENGTH",
        StatusUnknownCmd => "STATUS_UNKNOWN_CMD",
        ErrPinRequired => "ERR_PIN_REQUIRED",
        ErrPinWrong => "ERR_PIN_WRONG",
        ErrPinBlocked => "ERR_PIN_BLOCKED",
        ErrAlreadyAcknowledged => "ERR_ALREADY_ACKNOWLEDGED",
        ErrPinTooSoon => "ERR_PIN_TOO_SOON",
        ErrRekeyNone => "ERR_REKEY_NONE",
        ErrRekeyPinMismatch => "ERR_REKEY_PIN_MISMATCH",
        ErrInvalidJwt => "ERR_INVALID_JWT",
        ErrJwtExpired => "ERR_JWT_EXPIRED",
        ErrNoLicense => "ERR_NO_LICENSE",
        ErrLicenseExpired => "ERR_LICENSE_EXPIRED",
        ErrInvalidSignature => "ERR_INVALID_SIGNATURE",
        ErrTagMismatch => "ERR_TAG_MISMATCH",
        ErrDeprecated => "ERR_DEPRECATED",
        ErrSeqError => "ERR_SEQ_ERROR",
        ErrSignFault => "ERR_SIGN_FAULT",
        _ => $"0x{status:X2}",
    };
}
