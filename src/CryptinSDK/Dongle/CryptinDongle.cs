// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Security.Cryptography;
using CryptinSDK.Crypto;
using HidSharp;

namespace CryptinSDK.Dongle;

/// <summary>
/// One attached Crypt-in dongle, over vendor-defined HID.
///
/// The public shape follows CRYPTIN_ARCHITETTURA_CS.md §2; the wire behaviour
/// follows CRYPTIN_PROTOCOL.md, which is the normative reference and wins over
/// this comment. There is no <c>FirmwareVersion</c> and there must not be: the
/// firmware exposes no version command, and a property that guesses is worse
/// than an absent one.
///
/// The methods are synchronous, as documented. Access is still serialised behind
/// a <see cref="SemaphoreSlim"/> rather than a <c>lock</c>, because the firmware
/// stages exactly one command at a time and silently drops anything that
/// overlaps — and because an async overload can then be added without changing
/// the primitive, which a <c>lock</c> would not allow.
/// </summary>
public sealed class CryptinDongle : IDisposable
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private HidDevice? _device;
    private HidStream? _stream;
    private bool _disposed;

    /// <summary>How long to wait for the queue to run dry when clearing up after
    /// an abandoned exchange. Short: anything already queued is there now, and
    /// this only runs on a path that has already failed.</summary>
    private const int DrainTimeoutMs = 50;

    /// <summary>Set before a write, cleared after the matching read. True on
    /// entry means the previous exchange never collected its reply, so the queue
    /// may hold it — see <see cref="DrainIfPending"/>. Guarded by
    /// <see cref="_oneAtATime"/>; never read outside it.</summary>
    private bool _resyncPending;

    public bool IsConnected => _stream is not null;

    /// <summary>
    /// A human-readable label for telling two dongles apart on a desk. **Not an
    /// identity.** The firmware never calls <c>USB.serialNumber()</c>, so this
    /// comes from the USB stack's default and its uniqueness is not guaranteed.
    /// Where identity matters, use K_identity from <see cref="GetIdentityPubKey"/>.
    /// </summary>
    public string UsbSerial { get; private set; } = "";

    /// <summary>Serial numbers of every attached dongle, without opening any of
    /// them or sending a single byte. Useful for asking a user which one they
    /// meant, and for refusing to talk to the wrong one.</summary>
    public static IReadOnlyList<string> ListSerials()
    {
        var found = new List<string>();
        foreach (var device in DeviceList.Local.GetHidDevices(HidConstants.VendorId, HidConstants.ProductId))
        {
            if (!IsCryptinInterface(device)) continue;
            found.Add(SerialOf(device));
        }
        return found;
    }

    /// <summary>
    /// Opens the dongle.
    /// </summary>
    /// <param name="requiredUsbSerial">If given, refuse to open anything whose
    /// serial differs. This is a lab guard, not an identity check: it is there so
    /// a command meant for one chip cannot reach another one that happens to be
    /// plugged in.</param>
    /// <param name="waitMs">How long to keep looking. Windows takes a few seconds
    /// to enumerate after a swap, so a single miss means nothing; 0 tries once.</param>
    public bool Connect(string? requiredUsbSerial = null, int waitMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Disconnect();

        var deadline = Environment.TickCount64 + waitMs;
        do
        {
            foreach (var device in DeviceList.Local.GetHidDevices(HidConstants.VendorId, HidConstants.ProductId))
            {
                if (!IsCryptinInterface(device)) continue;

                var serial = SerialOf(device);
                if (requiredUsbSerial is not null &&
                    !string.Equals(serial, requiredUsbSerial, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!device.TryOpen(out var stream)) continue;

                stream.ReadTimeout = HidConstants.ReadTimeoutMs;
                stream.WriteTimeout = HidConstants.ReadTimeoutMs;
                _device = device;
                _stream = stream;
                UsbSerial = serial;
                return true;
            }

            if (Environment.TickCount64 >= deadline) break;
            Thread.Sleep(250);
        } while (true);

        return false;
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        _device = null;
        UsbSerial = "";
    }

    /// <summary>
    /// §4.1 — the only command besides <see cref="VerifyPin"/> that a locked
    /// dongle answers, and therefore the only way to detect one before the PIN.
    /// </summary>
    /// <returns>true when the opcode echoes, the status is OK, and the magic matches.</returns>
    public bool Ping()
    {
        var reply = Exchange(HidConstants.CmdPing, ReadOnlySpan<byte>.Empty, throwOnStatus: false);
        return reply.Status == HidConstants.StatusOk
               && reply.Payload.AsSpan(0, HidConstants.PingMagic.Length).SequenceEqual(HidConstants.PingMagic);
    }

    /// <summary>
    /// §4.4 — unlocks the session until power is cut.
    ///
    /// The PIN is sent as given, without a client-side length check on purpose:
    /// the firmware answers <c>ERR_PIN_BLOCKED</c> *before* it validates length,
    /// so a client that rejected a malformed PIN locally would report
    /// "bad length" for a dongle that is actually blocked. A wrong length costs
    /// nothing — it is explicitly not counted as an attempt.
    /// </summary>
    public PinResult VerifyPin(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return VerifyPin(pin.AsSpan());
    }

    /// <inheritdoc cref="VerifyPin(string)"/>
    /// <remarks>
    /// Prefer this over the <see cref="string"/> overload where the caller can
    /// hold the PIN in a <c>char[]</c> it is able to wipe. A string cannot be
    /// wiped: it is immutable and the GC may copy it while compacting, so every
    /// copy lingers until collection and none can be overwritten. This overload
    /// does not make the PIN unreachable on its own — it removes the copy *this*
    /// method would otherwise create, and lets the caller clear its own.
    /// </remarks>
    public PinResult VerifyPin(ReadOnlySpan<char> pin)
    {
        // 6 ASCII digit *characters*, not a 6-byte integer.
        var digits = new byte[pin.Length];
        for (var i = 0; i < pin.Length; i++)
        {
            if (pin[i] > 0x7F)
                throw new ArgumentException("the PIN is ASCII digits", nameof(pin));
            digits[i] = (byte)pin[i];
        }

        if (digits.Length > HidConstants.PayloadSize - 2)
            throw new ArgumentException(
                $"a PIN of {digits.Length} bytes does not fit in one report", nameof(pin));

        try
        {
            // The chip takes at most one attempt per second and refuses an
            // earlier one with ERR_PIN_TOO_SOON, uncounted (red-team finding
            // L2). Wait outside the exchange, so other commands on this
            // connection are not held behind us, and try again: the interval
            // runs from the last attempt the chip evaluated, so the bound below
            // is reached only if something else keeps verifying in between.
            var reply = Exchange(HidConstants.CmdVerifyPin, digits, throwOnStatus: false);
            var deadline = Environment.TickCount64 + 3 * HidConstants.PinVerifyMinIntervalMs;
            while (reply.Status == HidConstants.ErrPinTooSoon && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(HidConstants.PinVerifyMinIntervalMs / 4);
                reply = Exchange(HidConstants.CmdVerifyPin, digits, throwOnStatus: false);
            }

            return reply.Status switch
            {
                HidConstants.StatusOk => PinResult.Ok,
                HidConstants.ErrPinWrong => PinResult.Wrong,
                HidConstants.ErrPinBlocked => PinResult.Blocked,
                HidConstants.StatusBadLength => PinResult.BadLength,
                HidConstants.StatusNotReady => PinResult.NotReady,
                var other => throw new CryptinDongleException(
                    $"CMD_VERIFY_PIN answered {HidConstants.DescribeStatus(other)}"),
            };
        }
        finally
        {
            Array.Clear(digits);
        }
    }

    /// <summary>
    /// §4.5 — the one-time handover: the BIP-39 entropy and the PIN, for a
    /// dongle that has not been set up yet.
    ///
    /// Returns null when the dongle answers <c>ERR_ALREADY_ACKNOWLEDGED</c>.
    /// That is not an error to retry or report as a fault: it is a dongle that
    /// has been through setup, and the seed is simply not retrievable from it
    /// any more. The phrase on paper is the only copy, which is the entire point.
    ///
    /// Exempt from the PIN gate, necessarily — it runs before the user has been
    /// told the PIN.
    /// </summary>
    public ProvisioningHandover? GetProvisioning()
    {
        var reply = Exchange(HidConstants.CmdGetProvisioning, ReadOnlySpan<byte>.Empty,
                             throwOnStatus: false);

        if (reply.Status == HidConstants.ErrAlreadyAcknowledged) return null;
        if (reply.Status != HidConstants.StatusOk)
            throw new CryptinDongleException(
                $"CMD_GET_PROVISIONING answered {HidConstants.DescribeStatus(reply.Status)}");

        var entropy = reply.Payload.AsSpan(0, HidConstants.EntropySize).ToArray();
        var pin = System.Text.Encoding.ASCII.GetString(
            reply.Payload, HidConstants.EntropySize, HidConstants.PinLength);

        return new ProvisioningHandover(entropy, pin);
    }

    /// <summary>
    /// §4.5 — confirms the phrase and PIN were written down.
    ///
    /// One-way and irreversible: after this the dongle stops offering them, over
    /// HID and over its serial console alike. Only call it once the user has
    /// actually been shown the words — this is the moment the seed stops being
    /// recoverable from the hardware.
    /// </summary>
    public void AckProvisioning()
        => Exchange(HidConstants.CmdAckProvisioning, ReadOnlySpan<byte>.Empty, throwOnStatus: true);

    /// <summary>
    /// §4.9 — does this firmware have the same-chip rekey? Asked with a
    /// deliberately malformed CMD_REKEY_BEGIN (no PIN): a firmware that has it
    /// answers STATUS_BAD_LENGTH before it looks at any PIN, so nothing is
    /// counted and nothing is minted; one that predates it answers
    /// STATUS_UNKNOWN_CMD; the challenge image answers ERR_DEPRECATED.
    /// Needs an unlocked session, like the command itself.
    /// </summary>
    /// <exception cref="PinRequiredException">The session is locked.</exception>
    public bool SupportsOnChipRekey()
    {
        var reply = Exchange(HidConstants.CmdRekeyBegin, ReadOnlySpan<byte>.Empty, throwOnStatus: false);
        ThrowForStatus(HidConstants.CmdRekeyBegin, reply.Status);
        return reply.Status == HidConstants.StatusBadLength;
    }

    /// <summary>
    /// §4.9 — mints a new seed on this same chip and hands it over, while the
    /// current identity stays active. Takes the CURRENT PIN again, counted like
    /// <see cref="VerifyPin(string)"/>. On <see cref="RekeyBeginResult.Ok"/>
    /// <paramref name="newIdentity"/> holds the new words and PIN: show them, then
    /// <see cref="RekeyCommit"/>. They cannot be read back: a lost reply means
    /// calling this again, which replaces the pending seed.
    /// </summary>
    public RekeyBeginResult RekeyBegin(ReadOnlySpan<char> currentPin, out ProvisioningHandover? newIdentity)
    {
        newIdentity = null;
        var digits = PinBytes(currentPin);
        try
        {
            // Same one-per-second spacing as VERIFY_PIN, same retry.
            var reply = Exchange(HidConstants.CmdRekeyBegin, digits, throwOnStatus: false);
            var deadline = Environment.TickCount64 + 3 * HidConstants.PinVerifyMinIntervalMs;
            while (reply.Status == HidConstants.ErrPinTooSoon && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(HidConstants.PinVerifyMinIntervalMs / 4);
                reply = Exchange(HidConstants.CmdRekeyBegin, digits, throwOnStatus: false);
            }

            switch (reply.Status)
            {
                case HidConstants.StatusOk:
                    var entropy = reply.Payload.AsSpan(0, HidConstants.EntropySize).ToArray();
                    var pin = System.Text.Encoding.ASCII.GetString(
                        reply.Payload, HidConstants.EntropySize, HidConstants.PinLength);
                    CryptographicOperations.ZeroMemory(reply.Payload);
                    newIdentity = new ProvisioningHandover(entropy, pin);
                    return RekeyBeginResult.Ok;
                case HidConstants.ErrPinWrong: return RekeyBeginResult.WrongPin;
                case HidConstants.ErrPinBlocked: return RekeyBeginResult.Blocked;
                case HidConstants.ErrPinRequired: return RekeyBeginResult.SessionLocked;
                case HidConstants.StatusUnknownCmd:
                case HidConstants.ErrDeprecated: return RekeyBeginResult.NotSupported;
                default:
                    throw new CryptinDongleException(
                        $"CMD_REKEY_BEGIN answered {HidConstants.DescribeStatus(reply.Status)}");
            }
        }
        finally
        {
            Array.Clear(digits);
        }
    }

    /// <summary>
    /// §4.9 — makes the pending seed this dongle's identity. Takes the NEW PIN,
    /// one try: a wrong one drops the pending seed
    /// (<see cref="RekeyCommitResult.PinMismatch"/>) and the user starts again
    /// from <see cref="RekeyBegin"/>. On <see cref="RekeyCommitResult.Ok"/> the
    /// session is locked: <see cref="VerifyPin(string)"/> with the new PIN.
    /// </summary>
    public RekeyCommitResult RekeyCommit(ReadOnlySpan<char> newPin)
    {
        var digits = PinBytes(newPin);
        try
        {
            var reply = Exchange(HidConstants.CmdRekeyCommit, digits, throwOnStatus: false);
            return reply.Status switch
            {
                HidConstants.StatusOk => RekeyCommitResult.Ok,
                HidConstants.ErrRekeyPinMismatch => RekeyCommitResult.PinMismatch,
                HidConstants.ErrRekeyNone => RekeyCommitResult.NothingPending,
                HidConstants.ErrPinRequired => RekeyCommitResult.SessionLocked,
                // A PIN of the wrong length never reaches the comparison and
                // drops nothing; to the user it is the same mistake.
                HidConstants.StatusBadLength => RekeyCommitResult.PinMismatch,
                HidConstants.StatusUnknownCmd or HidConstants.ErrDeprecated => RekeyCommitResult.NotSupported,
                var other => throw new CryptinDongleException(
                    $"CMD_REKEY_COMMIT answered {HidConstants.DescribeStatus(other)}"),
            };
        }
        finally
        {
            Array.Clear(digits);
        }
    }

    private static byte[] PinBytes(ReadOnlySpan<char> pin)
    {
        if (pin.Length > HidConstants.PayloadSize - 2)
            throw new ArgumentException($"a PIN of {pin.Length} characters does not fit in one report", nameof(pin));
        var digits = new byte[pin.Length];
        for (var i = 0; i < pin.Length; i++)
        {
            if (pin[i] > 0x7F)
            {
                Array.Clear(digits);
                throw new ArgumentException("the PIN is ASCII digits", nameof(pin));
            }
            digits[i] = (byte)pin[i];
        }
        return digits;
    }

    /// <summary>
    /// §8 — hands the licence token to the dongle, which verifies its RSA
    /// signature itself and stores it only if it holds.
    ///
    /// Implemented in firmware and verified on hardware (0a4429f). Answered
    /// before the PIN gate: a locked dongle still accepts its licence.
    ///
    /// Chunked, because a JWT does not fit in a 63-byte report — an RS256
    /// signature alone is 256 bytes. Every chunk is acknowledged, and a refusal
    /// stops the transfer there rather than pushing the remaining chunks at a
    /// dongle that has already said no.
    ///
    /// Exempt from the PIN gate: the token has to be able to arrive before the
    /// user unlocks anything.
    /// </summary>
    /// <exception cref="LicenseRejectedException">The dongle refused the token.</exception>
    public Task SetLicenseJwtAsync(string jwt) => Task.Run(() => SetLicenseJwt(jwt));

    /// <summary>Synchronous core of <see cref="SetLicenseJwtAsync"/>. The whole
    /// transport is synchronous; the async face exists for callers, not because
    /// anything here awaits.</summary>
    public void SetLicenseJwt(string jwt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jwt);

        // ASCII: a JWT is base64url and dots by definition, so anything outside
        // that is not a token and must not be silently re-encoded into one.
        var bytes = System.Text.Encoding.ASCII.GetBytes(jwt);
        if (bytes.Length != jwt.Length)
            throw new ArgumentException("a JWT is ASCII: base64url and dots", nameof(jwt));

        var total = (bytes.Length + HidConstants.JwtChunkSize - 1) / HidConstants.JwtChunkSize;
        if (total > HidConstants.JwtMaxChunks)
            throw new ArgumentException(
                $"the token is {bytes.Length} bytes, beyond the {HidConstants.JwtMaxChunks} chunks " +
                "the protocol can number", nameof(jwt));

        for (var seq = 0; seq < total; seq++)
        {
            var offset = seq * HidConstants.JwtChunkSize;
            var length = Math.Min(HidConstants.JwtChunkSize, bytes.Length - offset);

            // Same four-byte header as the block commands, and no length byte of
            // our own: Write() already puts the argument length on the wire and
            // that is the byte the firmware reads. Carrying a second one both
            // overran the 61 bytes an argument may occupy and shifted every
            // field after it by one place.
            const int fields = HidConstants.BlockSeqBytes + HidConstants.BlockTotalBytes;
            var argument = new byte[fields + length];
            WriteBigEndian(argument, 0, seq, HidConstants.BlockSeqBytes);
            WriteBigEndian(argument, HidConstants.BlockSeqBytes, total, HidConstants.BlockTotalBytes);
            bytes.AsSpan(offset, length).CopyTo(argument.AsSpan(fields));

            var reply = Exchange(HidConstants.CmdSetLicenseJwt, argument, throwOnStatus: false);
            if (reply.Status == HidConstants.StatusOk) continue;

            throw reply.Status switch
            {
                HidConstants.ErrInvalidJwt => new LicenseRejectedException(
                    DongleLicenseError.InvalidSignature,
                    "the dongle rejected the token's signature"),
                HidConstants.ErrJwtExpired => new LicenseRejectedException(
                    DongleLicenseError.Expired,
                    "the token has already expired"),
                HidConstants.StatusUnknownCmd => new LicenseRejectedException(
                    DongleLicenseError.Unsupported,
                    "this firmware does not handle licences on the dongle"),
                var other => new CryptinDongleException(
                    $"CMD_SET_LICENSE_JWT answered {HidConstants.DescribeStatus(other)}"),
            };
        }
    }

    /// <summary>
    /// §8 — what the dongle says about its own licence.
    ///
    /// Implemented in firmware and verified on hardware (0a4429f). The chip
    /// caches the verification result rather than re-running RSA-4096 per call.
    ///
    /// **Behind the PIN gate since 2026-08-03.** Call it after
    /// <see cref="VerifyPin"/>; on a locked session it throws
    /// <see cref="PinRequiredException"/> like any other gated opcode. Callers
    /// that treat "no answer" as "no licence" would be wrong here — a refusal
    /// says nothing about the token, only about the session.
    ///
    /// Never throws for an ordinary "no" — an absent, expired or unverifiable
    /// licence is an answer, and the caller has to tell those three apart to say
    /// anything useful. Only a transport failure throws.
    /// </summary>
    public Task<DongleLicenseStatus> GetLicenseStatusAsync() => Task.Run(GetLicenseStatus);

    /// <inheritdoc cref="GetLicenseStatusAsync"/>
    public DongleLicenseStatus GetLicenseStatus()
    {
        var reply = Exchange(HidConstants.CmdGetLicenseStatus, ReadOnlySpan<byte>.Empty,
                             throwOnStatus: false);

        if (reply.Status == HidConstants.StatusOk)
        {
            // Unsigned, big-endian: signed would fail in 2038 for nothing.
            var seconds = ((uint)reply.Payload[0] << 24)
                        | ((uint)reply.Payload[1] << 16)
                        | ((uint)reply.Payload[2] << 8)
                        | reply.Payload[3];

            return new DongleLicenseStatus(
                IsValid: true,
                ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
                Error: null);
        }

        var error = reply.Status switch
        {
            HidConstants.ErrNoLicense => DongleLicenseError.NoLicense,
            HidConstants.ErrLicenseExpired => DongleLicenseError.Expired,
            HidConstants.ErrInvalidSignature => DongleLicenseError.InvalidSignature,

            // A dongle that predates enforcement. Reported as its own case
            // rather than as "no licence", which would send the user to buy one
            // that would change nothing.
            HidConstants.StatusUnknownCmd => DongleLicenseError.Unsupported,

            var other => throw new CryptinDongleException(
                $"CMD_GET_LICENSE_STATUS answered {HidConstants.DescribeStatus(other)}"),
        };

        return new DongleLicenseStatus(IsValid: false, ExpiresAt: null, Error: error);
    }

    // The whole-payload overloads that used to live here -- EncryptBlock(byte[],
    // byte[]) and DecryptBlock(byte[], byte[]), over a private Block() helper --
    // were removed on 2026-08-03. They were public, had no callers, and could
    // not have worked: they framed seq and total as two bytes each plus an
    // explicit length byte, while the firmware reads three and three and takes
    // the length from the transport (protocol.h BLOCK_HEADER_SIZE).
    //
    // Widening the header would not have rescued them. Their signatures
    // contradict §4.7 on their own terms: decryption needs the nonce as well as
    // the salt on chunk 0 and there is no parameter for it, encryption must NOT
    // be given a salt because the chip mints it and returns it, and chunk 0
    // carries payload rather than being a header-only exchange. Fixing the
    // framing alone would have produced a method that fails differently.
    //
    // Encrypt and decrypt through the seq/total methods below, or through
    // CryptinCipher.WriteCrin/ReadCrin, which is what every caller already did.

    /// <summary>
    /// §4.7 — one chunk of an encryption, chunk 0 first.
    ///
    /// Implemented and verified on hardware (0a4429f). The licence is checked by
    /// the chip on chunk 0, so a transfer that opens has already met it.
    ///
    /// Chunk 0 takes at most <see cref="HidConstants.BlockOpenChunkSize"/> bytes
    /// and returns the salt and nonce the dongle minted; later chunks take up to
    /// <see cref="HidConstants.BlockChunkSize"/> and return neither.
    /// </summary>
    public EncryptChunkResult EncryptBlock(byte[] plaintext, int seq, int total)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var reply = SendChunk(HidConstants.CmdEncryptBlock, plaintext, seq, total,
                              salt: null, nonce: null);

        // Payload[0] is the length and the bytes start at Payload[1] -- Reply
        // strips the report id, the opcode and the status, so Payload[i] is the
        // firmware's rep[2 + i]. Reading from 3 took the ciphertext two bytes
        // late: encryption still succeeded and wrote a .crin that no dongle
        // could ever open, which surfaced only as a tag failure on decryption.
        if (seq != 0)
            return new EncryptChunkResult(
                TrailingData(reply, 1, reply.Payload[0],
                             HidConstants.CmdEncryptBlock, seq, total), null, null);

        var salt = reply.Payload.AsSpan(0, HidConstants.CrinSaltLength).ToArray();
        var nonce = reply.Payload
            .AsSpan(HidConstants.CrinSaltLength, HidConstants.NonceLength).ToArray();
        var cipher = reply.Payload
            .AsSpan(HidConstants.CrinSaltLength + HidConstants.NonceLength, plaintext.Length)
            .ToArray();

        return new EncryptChunkResult(cipher, salt, nonce);
    }

    /// <summary>
    /// §4.7 — the closing exchange, which produces the GCM tag.
    /// Implemented and verified on hardware (0a4429f).
    /// </summary>
    public byte[] GetEncryptTag(int total)
    {
        // seq == total: data chunks occupy 0..total-1, so this is the next one
        // the dongle expects, not one past it.
        var reply = SendChunk(HidConstants.CmdEncryptBlock, [], total, total,
                              salt: null, nonce: null);
        return reply.Payload.AsSpan(0, CrinFormat.TagLength).ToArray();
    }

    /// <summary>
    /// §4.7 — one chunk of a decryption. No licence gate, ever.
    /// Implemented and verified on hardware (0a4429f).
    ///
    /// Salt and nonce are supplied on chunk 0 and ignored afterwards: the dongle
    /// derived K_file when the transfer opened.
    /// </summary>
    public DecryptChunkResult DecryptBlock(byte[] ciphertext, byte[] salt, byte[] nonce,
                                           int seq, int total)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        var reply = SendChunk(HidConstants.CmdDecryptBlock, ciphertext, seq, total,
                              seq == 0 ? salt : null, seq == 0 ? nonce : null);
        return new DecryptChunkResult(
            TrailingData(reply, 1, reply.Payload[0],
                         HidConstants.CmdDecryptBlock, seq, total));
    }

    /// <summary>
    /// §4.7 — the closing exchange for a decryption: hands the stored tag over
    /// and learns whether it verified. Implemented and verified on hardware
    /// (0a4429f).
    /// </summary>
    /// <exception cref="CorruptedCryptinFileException">The tag did not verify.
    /// Not "wrong dongle" — that comes from the header public key, compared
    /// before any of this runs.</exception>
    public void VerifyDecryptTag(byte[] tag, int total)
    {
        ArgumentNullException.ThrowIfNull(tag);
        SendChunk(HidConstants.CmdDecryptBlock, tag, total, total, null, null);
    }

    // --- Format v2 (CRYPTIN_PROTOCOL.md §4.8) ---------------------------------
    // seq 0 opens and carries no payload, data is seq 1..total, the close is
    // seq total+1. Standard AES-GCM on the chip, so the cuts carry no meaning.

    /// <summary>§4.8 — opens a v2 encryption. Returns the salt and nonce the
    /// dongle minted. The licence is checked here, by the chip.</summary>
    public (byte[] Salt, byte[] Nonce) OpenEncryptV2(int total, bool isFolder)
    {
        var reply = SendChunkV2(HidConstants.CmdEncryptBlockV2,
                                [isFolder ? CrinFormat.FlagFolder : (byte)0], [], 0, total);
        return (reply.Payload.AsSpan(0, HidConstants.CrinSaltLength).ToArray(),
                reply.Payload.AsSpan(HidConstants.CrinSaltLength, HidConstants.NonceLength).ToArray());
    }

    /// <summary>§4.8 — one v2 data chunk, seq 1..total, at most
    /// <see cref="HidConstants.BlockChunkSize"/> bytes.</summary>
    public byte[] EncryptBlockV2(byte[] plaintext, int seq, int total)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var reply = SendChunkV2(HidConstants.CmdEncryptBlockV2, [], plaintext, seq, total);
        return TrailingData(reply, 1, reply.Payload[0], HidConstants.CmdEncryptBlockV2, seq, total);
    }

    /// <summary>§4.8 — the v2 close (seq total+1): the tag.</summary>
    public byte[] GetEncryptTagV2(int total)
    {
        var reply = SendChunkV2(HidConstants.CmdEncryptBlockV2, [], [], total + 1, total);
        return reply.Payload.AsSpan(0, CrinFormat.TagLength).ToArray();
    }

    /// <summary>§4.8 — opens a v2 decryption with the file's flags, salt and
    /// nonce. The chip rebuilds the header from them and its own K_identity and
    /// authenticates it, so a flags byte edited on disk fails the tag.</summary>
    public void OpenDecryptV2(int total, byte flags, byte[] salt, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(nonce);
        var prefix = new byte[1 + HidConstants.CrinSaltLength + HidConstants.NonceLength];
        prefix[0] = flags;
        salt.CopyTo(prefix, 1);
        nonce.CopyTo(prefix, 1 + HidConstants.CrinSaltLength);
        SendChunkV2(HidConstants.CmdDecryptBlockV2, prefix, [], 0, total);
    }

    /// <summary>§4.8 — one v2 data chunk to decrypt. The plaintext it returns is
    /// unauthenticated until <see cref="VerifyDecryptTagV2"/> succeeds: discard
    /// all of it if that throws (§4.7, red-team finding K1).</summary>
    public byte[] DecryptBlockV2(byte[] ciphertext, int seq, int total)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        var reply = SendChunkV2(HidConstants.CmdDecryptBlockV2, [], ciphertext, seq, total);
        return TrailingData(reply, 1, reply.Payload[0], HidConstants.CmdDecryptBlockV2, seq, total);
    }

    /// <summary>§4.8 — the v2 decryption close (seq total+1).</summary>
    /// <exception cref="CorruptedCryptinFileException">The tag did not verify.</exception>
    public void VerifyDecryptTagV2(byte[] tag, int total)
    {
        ArgumentNullException.ThrowIfNull(tag);

        // L3 (round 6): the close carries a random host nonce after the tag, and
        // the dongle echoes it back ONLY when the tag verifies. We then require
        // the reply to carry our nonce. A `10 00` that another local process
        // coaxed out of the dongle (its own decrypt open) does not carry it, so a
        // substituted close reply is rejected instead of taken for a verified tag.
        var nonce = RandomNumberGenerator.GetBytes(HidConstants.CloseNonceLength);
        var payload = new byte[tag.Length + nonce.Length];
        tag.CopyTo(payload, 0);
        nonce.CopyTo(payload, tag.Length);

        var reply = SendChunkV2(HidConstants.CmdDecryptBlockV2, [], payload, total + 1, total);
        if (!reply.Payload.AsSpan(0, nonce.Length).SequenceEqual(nonce))
            throw new CorruptedCryptinFileException(
                "the decrypt close was answered without the request nonce: another process " +
                "may have replied for the dongle (round-6 finding L3)");
    }

    private Reply SendChunkV2(byte opcode, byte[] prefix, byte[] payload, int seq, int total)
    {
        const int fields = HidConstants.BlockSeqBytes + HidConstants.BlockTotalBytes;
        var argument = new byte[fields + prefix.Length + payload.Length];
        WriteBigEndian(argument, 0, seq, HidConstants.BlockSeqBytes);
        WriteBigEndian(argument, HidConstants.BlockSeqBytes, total, HidConstants.BlockTotalBytes);
        prefix.CopyTo(argument, fields);
        payload.CopyTo(argument, fields + prefix.Length);

        var reply = Exchange(opcode, argument, throwOnStatus: false);
        Check(opcode, reply);
        return reply;
    }

    private Reply SendChunk(byte opcode, byte[] payload, int seq, int total,
                            byte[]? salt, byte[]? nonce)
    {
        // No length byte of our own: Write() already puts the argument length on
        // the wire, which is the byte the firmware reads. Adding a second one
        // pushed a full chunk to 62 bytes — one past the 61 an argument may
        // occupy — and shifted seq, total and the payload each one place along.
        var prefix = salt is null ? 0 : HidConstants.CrinSaltLength + HidConstants.NonceLength;
        const int fields = HidConstants.BlockSeqBytes + HidConstants.BlockTotalBytes;
        var argument = new byte[fields + prefix + payload.Length];
        WriteBigEndian(argument, 0, seq, HidConstants.BlockSeqBytes);
        WriteBigEndian(argument, HidConstants.BlockSeqBytes, total, HidConstants.BlockTotalBytes);

        if (salt is not null)
        {
            salt.CopyTo(argument, fields);
            nonce!.CopyTo(argument, fields + HidConstants.CrinSaltLength);
        }
        payload.CopyTo(argument, fields + prefix);

        var reply = Exchange(opcode, argument, throwOnStatus: false);
        Check(opcode, reply);
        return reply;
    }

    /// <summary>
    /// The payload bytes a reply says it carries.
    ///
    /// Refuses a length that does not fit rather than clamping it. Clamping
    /// would be worse than the crash on the encrypt path: a short read there
    /// silently drops ciphertext, and the dongle computes the tag over what it
    /// encrypted rather than over what the host stored — so the file would be
    /// written, reported as a success, and fail only on the way back, which is
    /// the exact failure we just spent two rounds chasing.
    ///
    /// The declared length is in the message because it is the diagnostic: the
    /// firmware writes it into rep[2] and never sends more than 57, so anything
    /// larger means that byte is not a length at all.
    /// </summary>
    /// <summary>Big-endian, width fixed by the protocol. Three bytes for seq and
    /// total: see HidConstants.BlockSeqBytes for why they are that wide.</summary>
    private static void WriteBigEndian(byte[] buffer, int offset, int value, int width)
    {
        for (var i = 0; i < width; i++)
            buffer[offset + i] = (byte)(value >> (8 * (width - 1 - i)));
    }

    private static byte[] TrailingData(Reply reply, int offset, byte length,
                                       byte opcode, int seq, int total)
    {
        if (offset + length > reply.Payload.Length)
            throw new CryptinDongleException(
                $"inconsistent reply on 0x{opcode:X2} chunk {seq}/{total}: " +
                $"it declares {length} bytes starting at {offset}, but the report " +
                $"holds {reply.Payload.Length}. The length byte reads " +
                $"{length}, which the firmware never writes for a data chunk: " +
                "this reply has the layout of an opening (salt) or a " +
                "closing (tag) exchange.");

        return reply.Payload.AsSpan(offset, length).ToArray();
    }

    /// <summary>Shared chunking for 0x0D and 0x0E: they differ only in which
    /// refusals they can produce.</summary>

    private static void Check(byte opcode, Reply reply)
    {
        if (reply.Status == HidConstants.StatusOk) return;

        throw reply.Status switch
        {
            HidConstants.ErrNoLicense => new NoActiveLicenseException(
                DongleLicenseError.NoLicense,
                "An active license is required to encrypt files. " +
                "Existing encrypted files can always be decrypted."),

            HidConstants.ErrLicenseExpired => new NoActiveLicenseException(
                DongleLicenseError.Expired,
                "An active license is required to encrypt files. " +
                "Existing encrypted files can always be decrypted."),

            // Corrupted, never "wrong dongle": the tag cannot tell them apart.
            HidConstants.ErrTagMismatch => new CorruptedCryptinFileException(
                "the AEAD tag does not verify: the file is damaged or has been tampered with"),

            HidConstants.ErrPinRequired => new PinRequiredException(opcode),

            // Not a malfunction and not a missing feature: the dongle is -prod
            // and will never export file_secret. Saying so by name is what stops
            // this being read as "old firmware, try another dongle".
            HidConstants.ErrDeprecated => new CryptinDongleException(
                $"0x{opcode:X2} is deprecated on -prod firmware and no longer answers: " +
                "encrypt and decrypt on the chip with EncryptBlock/DecryptBlock " +
                "(CryptinCipher.WriteCrin/ReadCrin)"),

            // The transfer is gone, not desynchronised: restart from chunk 0.
            HidConstants.ErrSeqError => new CryptinDongleException(
                $"chunk out of order on 0x{opcode:X2}: the transfer was abandoned"),

            // A v2 opcode on firmware from before 2026-10-09: say what to do.
            HidConstants.StatusUnknownCmd when opcode is HidConstants.CmdEncryptBlockV2
                                                    or HidConstants.CmdDecryptBlockV2
                => new CryptinDongleException(
                    $"this dongle's firmware predates file format v2 (0x{opcode:X2} is unknown): " +
                    "update the dongle firmware to encrypt or open v2 files"),

            HidConstants.StatusUnknownCmd => new CryptinDongleException(
                $"this firmware does not implement 0x{opcode:X2}"),

            var other => new CryptinDongleException(
                $"0x{opcode:X2} answered {HidConstants.DescribeStatus(other)}"),
        };
    }

    /// <summary>§4.2 — 32-byte K_identity, derived from the BIP-39 seed.</summary>
    public byte[] GetIdentityPubKey()
        => Fixed(HidConstants.CmdGetPubkey, HidConstants.PubkeySize);

    /// <summary>
    /// §4.2 — 32-byte K_attestation: the physical chip's own identity, minted
    /// from hardware RNG and never derived from the seed. A recovery does not
    /// change it, which is what lets a licence server tell the original chip
    /// apart from another one carrying the same recovered seed.
    /// </summary>
    public byte[] GetAttestationPubKey()
        => Fixed(HidConstants.CmdGetAttestationPubkey, HidConstants.AttestPubkeySize);

    /// <summary>
    /// §4.2 — 32-byte <c>file_secret</c>, opcode 0x04. Input keying material, not
    /// a content key: HKDF with a per-file salt goes between it and anything that
    /// encrypts. Never use it directly.
    ///
    /// Deprecated, and not merely stylistically: a <c>-prod</c> dongle answers
    /// <c>ERR_DEPRECATED</c> (0xF7) and hands out nothing, so this throws there.
    /// Whoever holds <c>file_secret</c> plus the salt in a <c>.crin</c> header
    /// decrypts that file — and every other file the dongle will ever write —
    /// without the dongle and without the licence gate. That is what moving the
    /// cipher onto the chip removed, which is why the value no longer crosses
    /// the wire. <c>-dev</c> still answers, for the compatibility fixtures.
    /// </summary>
    /// <exception cref="CryptinDongleException">
    /// The firmware is <c>-prod</c> and refused with <c>ERR_DEPRECATED</c>.
    /// </exception>
    [Obsolete(
        "CMD_GET_SECRET is deprecated on -prod firmware, which answers " +
        "ERR_DEPRECATED (0xF7). Use EncryptBlock/DecryptBlock — or " +
        "CryptinCipher.WriteCrin/ReadCrin, which wrap them — instead. " +
        "Retained for -dev compatibility and compat tests only.",
        error: false)]
    public byte[] GetFileSecret()
        => Fixed(HidConstants.CmdGetSecret, HidConstants.SecretSize);

    /// <summary>
    /// §4.3 — signs a 32-byte nonce with K_identity, returning 64 bytes.
    ///
    /// The reply arrives in two reports, and byte 1 means two different things:
    /// on success it is a sequence number, on failure it is a status, and seq 1
    /// (0x01) collides numerically with STATUS_NOT_READY. So this reads *one*
    /// report and decides: 0x00 means seq 0 and a second report is coming;
    /// anything else is an error and nothing more is sent. A fixed two-read loop
    /// would hang for the whole timeout on every error — the defect the reference
    /// Python client has.
    /// </summary>
    public byte[] Challenge(byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        if (nonce.Length != HidConstants.NonceSize)
            throw new ArgumentException(
                $"the nonce is {HidConstants.NonceSize} bytes, got {nonce.Length}", nameof(nonce));

        _oneAtATime.Wait();
        try
        {
            DrainIfPending();
            _resyncPending = true;
            Write(HidConstants.CmdChallenge, nonce);

            var first = Read(HidConstants.CmdChallenge);
            if (first.Status != 0x00)
            {
                ThrowForStatus(HidConstants.CmdChallenge, first.Status);
                throw new CryptinDongleException(
                    $"CMD_CHALLENGE answered {HidConstants.DescribeStatus(first.Status)}");
            }

            var second = Read(HidConstants.CmdChallenge);
            _resyncPending = false;

            if (second.Status != 0x01)
                throw new CryptinDongleException(
                    $"expected sequence 1 in the second CMD_CHALLENGE report, got 0x{second.Status:X2}");

            // Placed by sequence number rather than by arrival order, as §4.3 asks.
            var signature = new byte[HidConstants.SigSize];
            first.Payload.AsSpan(0, HidConstants.SigChunk).CopyTo(signature);
            second.Payload.AsSpan(0, HidConstants.SigChunk).CopyTo(signature.AsSpan(HidConstants.SigChunk));
            return signature;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    // --- transport --------------------------------------------------------

    private readonly record struct Reply(byte Status, byte[] Payload);

    private byte[] Fixed(byte opcode, int size)
    {
        var reply = Exchange(opcode, ReadOnlySpan<byte>.Empty, throwOnStatus: true);
        return reply.Payload.AsSpan(0, size).ToArray();
    }

    private Reply Exchange(byte opcode, ReadOnlySpan<byte> argument, bool throwOnStatus)
    {
        _oneAtATime.Wait();
        try
        {
            DrainIfPending();
            _resyncPending = true;
            Write(opcode, argument);
            var reply = Read(opcode);
            _resyncPending = false;

            if (throwOnStatus && reply.Status != HidConstants.StatusOk)
            {
                ThrowForStatus(opcode, reply.Status);
                throw new CryptinDongleException(
                    $"opcode 0x{opcode:X2} answered {HidConstants.DescribeStatus(reply.Status)}");
            }
            return reply;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private static void ThrowForStatus(byte opcode, byte status)
    {
        if (status == HidConstants.ErrPinRequired)
            throw new PinRequiredException(opcode);
    }

    private void Write(byte opcode, ReadOnlySpan<byte> argument)
    {
        var stream = _stream ?? throw new CryptinDongleException("not connected");

        // Every OUT report is padded to the full size; the firmware reads fixed
        // offsets and truncates anything longer.
        var buffer = new byte[HidConstants.ReportSize];
        buffer[0] = HidConstants.ReportId;
        buffer[1] = opcode;
        buffer[2] = (byte)argument.Length;
        argument.CopyTo(buffer.AsSpan(3));

        try
        {
            stream.Write(buffer, 0, buffer.Length);
        }
        catch (Exception e) when (e is not CryptinDongleException)
        {
            throw new CryptinDongleException($"write of opcode 0x{opcode:X2} failed", e);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    /// <summary>
    /// Reads the reply to <paramref name="expectedOpcode"/>, stepping over any
    /// report that answers something else.
    ///
    /// The skipping is the point. A report whose echoed opcode is not the one
    /// awaited is a reply nobody is waiting for any more — the exchange that
    /// asked for it gave up, most plausibly on the 2 s read timeout, and the
    /// queue kept it. Treating that as a fatal error, which this did until
    /// 2026-08-03, made every later read one behind for the life of the
    /// connection: probe_report_desync.py shows one unread reply turning PING
    /// into GET_LICENSE_STATUS's answer and staying wrong. Discarding until the
    /// awaited opcode arrives means one dropped reply costs one exchange rather
    /// than the session.
    ///
    /// Bounded by the same deadline the single read had, so a dongle that has
    /// genuinely stopped answering still fails in 2 s and does not spin.
    /// </summary>
    private Reply Read(byte expectedOpcode)
    {
        var stream = _stream ?? throw new CryptinDongleException("not connected");
        var buffer = new byte[HidConstants.ReportSize];
        var deadline = Environment.TickCount64 + HidConstants.ReadTimeoutMs;
        var skipped = 0;

        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                throw new CryptinTimeoutException(expectedOpcode, HidConstants.ReadTimeoutMs,
                    skipped == 0 ? null : new CryptinDongleException(
                        $"{skipped} stale report(s) discarded while waiting"));

            int read;
            try
            {
                stream.ReadTimeout = (int)Math.Min(remaining, HidConstants.ReadTimeoutMs);
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException e)
            {
                throw new CryptinTimeoutException(expectedOpcode, HidConstants.ReadTimeoutMs, e);
            }
            catch (Exception e) when (e is not CryptinDongleException)
            {
                throw new CryptinDongleException($"read for opcode 0x{expectedOpcode:X2} failed", e);
            }

            if (read < HidConstants.ReportSize)
                throw new CryptinDongleException($"short report: {read} bytes, expected {HidConstants.ReportSize}");
            if (buffer[0] != HidConstants.ReportId)
                throw new CryptinDongleException($"report ID {buffer[0]}, expected {HidConstants.ReportId}");

            // Byte 0 of the payload is the echoed opcode, and it is echoed on
            // every reply including errors — so it is the only reliable way to
            // know which request a report answers. Check it before trusting
            // byte 1.
            if (buffer[1] == expectedOpcode)
            {
                var payload = new byte[HidConstants.PayloadSize - 2];
                buffer.AsSpan(3).CopyTo(payload);
                return new Reply(buffer[2], payload);
            }

            skipped++;
            Array.Clear(buffer);
        }
    }

    /// <summary>
    /// Empties the input queue after an exchange that did not complete.
    ///
    /// Only ever called with <see cref="_oneAtATime"/> held and with no request
    /// outstanding, which is what makes discarding safe: every report still
    /// queued at that moment answers a request that was abandoned. Cheap by
    /// construction — the flag is set before a write and cleared after the
    /// matching read, so a healthy connection never enters the loop and the
    /// per-chunk cost of a block transfer is unchanged.
    /// </summary>
    private void DrainIfPending()
    {
        if (!_resyncPending) return;

        var stream = _stream;
        if (stream is null) { _resyncPending = false; return; }

        var scratch = new byte[HidConstants.ReportSize];
        try
        {
            stream.ReadTimeout = DrainTimeoutMs;
            while (stream.Read(scratch, 0, scratch.Length) > 0) { }
        }
        catch
        {
            // A timeout is the expected way out: the queue is empty. Anything
            // else means the stream is unusable, which the next write reports
            // with the opcode that actually failed — better placed than here.
        }
        finally
        {
            try { stream.ReadTimeout = HidConstants.ReadTimeoutMs; } catch { /* closed */ }
            Array.Clear(scratch);
            _resyncPending = false;
        }
    }

    private static bool IsCryptinInterface(HidDevice device)
    {
        // Filter by usage page where the descriptor can be read; a device that
        // will not describe itself is not excluded on that basis alone, because
        // VID/PID already narrowed it to this product.
        try
        {
            foreach (var item in device.GetReportDescriptor().DeviceItems)
                foreach (var usage in item.Usages.GetAllValues())
                    if ((usage >> 16) == HidConstants.UsagePage)
                        return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string SerialOf(HidDevice device)
    {
        try { return device.GetSerialNumber() ?? ""; }
        catch { return ""; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _oneAtATime.Dispose();
    }
}
