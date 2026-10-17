// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Security.Cryptography;
using CryptinSDK.Dongle;

namespace CryptinSDK.Crypto;

/// <summary>
/// The cryptographic core: HKDF-SHA256 over the dongle's file_secret, then
/// AES-256-GCM. Written from CRYPTIN_FILE_FORMAT.md §1 and §5, not ported from
/// either legacy implementation (architecture decision of 2026-08-01, option C).
///
/// **Two derivations since the correction of 2026-09-30** (§1). The dongle one
/// — info "cryptin-file-key-v1", empty AAD — is what the chip has written since
/// 2026-08-02, measured on hardware by measure_hkdf_info.py. The legacy one —
/// info "cryptin-file-v1", whole header as AAD — is what host-side writers
/// produced before that. The host reader tries the dongle derivation first and
/// the legacy one second; the host writer produces the dongle derivation, so
/// what it writes opens on the chip.
///
/// Written against a gate that existed first: tests/CryptinSDK.Compat ran and
/// failed before a line of this was written, so every line has an objective
/// criterion for "it works". It reads .crin and .cryptin in both derivations
/// and reproduces both sets of fixtures byte for byte.
///
/// The dongle-backed entry points (ReadCrin/WriteCrin taking a CryptinDongle)
/// never touch file_secret at all: the key derivation and the AES-GCM run on
/// the chip, behind the licence gate. The overloads taking a raw secret are
/// what the compatibility suite uses, and nothing in the app calls them.
/// </summary>
public static class CryptinCipher
{
    /// <summary>
    /// HKDF info of the dongle derivation (§1): what the chip uses, and what
    /// this SDK writes. Until 2026-09-30 this property held "cryptin-file-v1",
    /// which is the legacy string and never matched the chip.
    /// </summary>
    public static ReadOnlySpan<byte> HkdfInfo => "cryptin-file-key-v1"u8;

    /// <summary>HKDF info of the legacy derivation (§1): host-written files
    /// from before 2026-08-02, every .cryptin among them. Read, never written.</summary>
    public static ReadOnlySpan<byte> LegacyHkdfInfo => "cryptin-file-v1"u8;

    /// <summary>HKDF info of format v2 (§2a): its own label, so a v1 key and a
    /// v2 key from the same secret and salt are unrelated.</summary>
    public static ReadOnlySpan<byte> HkdfInfoV2 => "cryptin-file-key-v2"u8;

    /// <summary>Derived key length, 32 bytes — AES-256 (§1).</summary>
    public const int FileKeyLength = 32;

    /// <summary>
    /// §5 — the reader algorithm, steps 1 to 9, for both containers.
    /// </summary>
    /// <param name="blob">The whole file.</param>
    /// <param name="fileSecret">The dongle's 32-byte file_secret (CMD_GET_SECRET,
    /// opcode 0x04). Input keying material, never a content key: HKDF with a
    /// per-file salt sits between it and anything that encrypts.</param>
    /// <param name="donglePublicKey">The attached dongle's 32-byte K_identity,
    /// compared against the header before decryption is attempted (step 7).</param>
    /// <exception cref="InvalidCryptinFileException">Bad magic, truncated,
    /// unsupported version, or an unknown flag bit.</exception>
    /// <exception cref="WrongDongleException">Step 7 — the file belongs to
    /// another dongle. Distinct from a tag failure, on purpose.</exception>
    /// <exception cref="CorruptedCryptinFileException">Step 9 — the AEAD tag did
    /// not verify: corrupted or tampered with.</exception>
    public static CryptinContent Read(
        ReadOnlySpan<byte> blob,
        ReadOnlySpan<byte> fileSecret,
        ReadOnlySpan<byte> donglePublicKey)
    {
        if (donglePublicKey.Length != CrinFormat.PublicKeyLength)
            throw new ArgumentException(
                $"K_identity is {CrinFormat.PublicKeyLength} bytes, got {donglePublicKey.Length}",
                nameof(donglePublicKey));

        // Steps 1 and 2 are inverted with respect to the document, because the
        // minimum length depends on which container it is: 82 for .crin, 85 for
        // .cryptin. A file too short to hold even a magic falls out of Detect as
        // "not a Crypt-in file", which is the truer of the two answers anyway.
        var layout = CrinFormat.Detect(blob);                                   // 2

        if (blob.Length < layout.MinimumFileLength)                             // 1
            throw new InvalidCryptinFileException(
                $"file too short: {blob.Length} bytes, minimum {layout.MinimumFileLength}");

        var version = blob[layout.VersionOffset];                               // 3
        var isV2 = layout.Format == CryptinFileFormat.Crin && version == CrinFormat.Version2;
        if (version != CrinFormat.Version1 && !isV2)
            throw new InvalidCryptinFileException($"unsupported version {version}");

        var isFolder = false;                                                   // 4
        if (layout.FlagsOffset is int flagsOffset)
        {
            var flags = blob[flagsOffset];
            if ((flags & ~CrinFormat.KnownFlags) != 0)
                throw new InvalidCryptinFileException($"unknown flag bits set: 0x{flags:X2}");
            isFolder = (flags & CrinFormat.FlagFolder) != 0;
        }

        var publicKey = blob.Slice(layout.PublicKeyOffset, CrinFormat.PublicKeyLength);   // 5
        var salt = blob.Slice(layout.SaltOffset, CrinFormat.SaltLength);
        var nonce = blob.Slice(layout.NonceOffset, CrinFormat.NonceLength);

        var header = blob[..layout.HeaderLength];                               // 6 — legacy AAD
        var body = blob[layout.HeaderLength..];

        // Before decryption, and reported as its own error type: "wrong dongle"
        // and "damaged file" need opposite advice to the user.
        if (!publicKey.SequenceEqual(donglePublicKey))                          // 7
            throw new WrongDongleException(publicKey.ToArray());

        // On disk both containers end in ciphertext‖tag; only the offsets where
        // that run starts differ, so one split serves both.
        var ciphertext = body[..^CrinFormat.TagLength];
        var tag = body[^CrinFormat.TagLength..];

        // Format v2: one derivation, the whole header as AAD, standard GCM. No
        // fallback: a v2 header that fails is damaged, whatever else it is.
        if (isV2)
        {
            if (TryDecrypt(fileSecret, salt, HkdfInfoV2, nonce, ciphertext, tag,
                           header, out var plainV2))
                return new CryptinContent(plainV2, isFolder, layout.Format);
            throw new CorruptedCryptinFileException("corrupted or tampered with");
        }

        // Step 8, the dongle derivation, only for .crin: the chip never writes
        // a .cryptin. The header says nothing about which derivation was used,
        // so a failure here is not yet a verdict.
        if (layout.Format == CryptinFileFormat.Crin
            && TryDecrypt(fileSecret, salt, HkdfInfo, nonce, ciphertext, tag,
                          ReadOnlySpan<byte>.Empty, out var plaintext))
            return new CryptinContent(plaintext, isFolder, layout.Format);

        // Step 8b, same key, the chip's per-chunk GCM (§2.1): how the chip wrote
        // every v1 file over 27 bytes. Without it a beta file opens only on a
        // dongle, never from file_secret alone.
        if (layout.Format == CryptinFileFormat.Crin
            && TryDecryptChipV1(fileSecret, salt, nonce, ciphertext, tag, out plaintext))
            return new CryptinContent(plaintext, isFolder, layout.Format);

        // Step 9, the legacy derivation. Failing all three is the verdict.
        if (TryDecrypt(fileSecret, salt, LegacyHkdfInfo, nonce, ciphertext, tag,
                       header, out plaintext))
            return new CryptinContent(plaintext, isFolder, layout.Format);

        // Step 10, unzipping a folder payload, belongs to whoever writes to
        // disk: the success paths above return the ZIP and say it is one.
        throw new CorruptedCryptinFileException("corrupted or tampered with");
        // The other half of step 11, wiping file_secret, belongs to the caller:
        // it owns those bytes and this method only borrowed them.
    }

    /// <summary>
    /// One AEAD attempt under one derivation. Trying two keys weakens nothing:
    /// each is a full tag verification, and a file that is neither fails both.
    /// </summary>
    private static bool TryDecrypt(ReadOnlySpan<byte> fileSecret, ReadOnlySpan<byte> salt,
                                   ReadOnlySpan<byte> info, ReadOnlySpan<byte> nonce,
                                   ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
                                   ReadOnlySpan<byte> aad, out byte[] plaintext)
    {
        var fileKey = new byte[FileKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileSecret, fileKey, salt, info);   // 8/9
        try
        {
            plaintext = new byte[ciphertext.Length];
            using var gcm = new AesGcm(fileKey, CrinFormat.TagLength);
            try
            {
                gcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
                return true;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                plaintext = [];
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);                        // 11
        }
    }

    /// <summary>Step 8b: the dongle derivation, decrypted with <see cref="ChipGcmV1"/>.</summary>
    private static bool TryDecryptChipV1(ReadOnlySpan<byte> fileSecret, ReadOnlySpan<byte> salt,
                                         ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
                                         ReadOnlySpan<byte> tag, out byte[] plaintext)
    {
        var fileKey = new byte[FileKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileSecret, fileKey, salt, HkdfInfo);
        try
        {
            return ChipGcmV1.TryDecrypt(fileKey, nonce, ciphertext, tag, out plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);                        // 11
        }
    }

    /// <summary>
    /// §5 step 8 — file_key = HKDF-SHA256(ikm = file_secret, salt = header salt,
    /// info = "cryptin-file-key-v1", len = 32): the dongle derivation, the one
    /// the chip uses. The legacy one is internal and read-only.
    ///
    /// **Public, and deliberately left so — but it is host-side cryptography and
    /// nothing in the product path reaches it.** It needs a <c>file_secret</c> the
    /// caller already holds, and on a <c>-prod</c> dongle that value never leaves
    /// the chip: <c>CMD_GET_SECRET</c> answers <c>ERR_DEPRECATED</c>, so there is
    /// no supported way to obtain the input this method requires. The reachable
    /// callers are the compatibility fixtures, which carry a synthetic secret of
    /// their own and never touch hardware. Anything encrypting for real goes
    /// through <see cref="WriteCrin(CryptinDongle, ReadOnlySpan{byte}, bool)"/>,
    /// where the key derivation happens on the chip behind the licence gate.
    /// </summary>
    public static byte[] DeriveFileKey(ReadOnlySpan<byte> fileSecret, ReadOnlySpan<byte> salt)
    {
        var key = new byte[FileKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileSecret, key, salt, HkdfInfo);
        return key;
    }

    /// <summary>
    /// Writes a .crin v1 (§2), with a fresh salt and nonce.
    /// </summary>
    /// <param name="isFolder">Set flag bit 0. The plaintext must then be a ZIP
    /// of the directory; zipping is the caller's job, as unzipping is on read.</param>
    /// <summary>
    /// Encrypts using a dongle that has been asked for its licence first.
    ///
    /// Implemented in firmware and verified on hardware (0a4429f).
    ///
    /// The licence is not consulted from here at all, and the older note saying
    /// it was checked "before GetFileSecret()" described a body that no longer
    /// exists. The chip checks on chunk 0 and refuses to encrypt without one, so
    /// asking here as well would produce two answers that can disagree — and the
    /// one that decides is the chip's.
    ///
    /// There is deliberately no matching read overload. Decryption does not
    /// consult a licence, and offering a symmetric-looking pair here would
    /// invite one to be added.
    /// </summary>
    /// <param name="progress">Reports 0..100 as the chunks go out. At the measured
    /// 27.8 KB/s a 1 MB file takes about half a minute, which is long enough that
    /// a caller with no progress to draw looks hung — so this is a courtesy the
    /// UI cannot do without, not an optional extra.</param>
    /// <param name="cancellationToken">Checked between chunks, never inside one.
    /// Abandoning a transfer costs nothing to clean up: the firmware re-opens on
    /// <c>seq == 0</c> whatever state it was left in (hid_device.cpp), and this
    /// method writes no file, so a cancelled encryption leaves nothing behind.</param>
    /// <exception cref="NoActiveLicenseException">The dongle has no usable licence.</exception>
    /// <exception cref="OperationCanceledException">Cancelled between chunks.</exception>
    public static byte[] WriteCrin(CryptinDongle dongle, ReadOnlySpan<byte> plaintext, bool isFolder,
                                   IProgress<int>? progress = null,
                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);

        // The licence is not checked here: the dongle checks it when the
        // transfer opens, and refuses to encrypt at all without one. Checking
        // twice would mean two answers that can disagree.
        //
        // Format v2 since 2026-10-09 (§2a): standard AES-GCM, header as AAD.
        // The opening carries no payload, so every data chunk is a full one.
        var chunks = SplitV2(plaintext);
        var cipher = new List<byte>(plaintext.Length);

        // Open and close are steps like any other, so they are counted:
        // reporting 100 before the tag has come back would show a finished bar
        // over a transfer still waiting on the chip.
        var steps = chunks.Count + 2;
        var reported = -1;

        var (salt, nonce) = dongle.OpenEncryptV2(chunks.Count, isFolder);
        Advance(progress, 100 / steps, ref reported);

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cipher.AddRange(dongle.EncryptBlockV2(chunks[i], i + 1, chunks.Count));
            Advance(progress, (i + 2) * 100 / steps, ref reported);
        }

        // Not cancellable from here on: the tag is one round trip and the blob is
        // already paid for.
        var tag = dongle.GetEncryptTagV2(chunks.Count);
        Advance(progress, 100, ref reported);

        var header = CrinFormat.BuildCrinHeader(dongle.GetIdentityPubKey(), salt, nonce,
                                                isFolder, CrinFormat.Version2);
        var blob = new byte[header.Length + cipher.Count + tag.Length];
        header.CopyTo(blob, 0);
        cipher.CopyTo(blob, header.Length);
        tag.CopyTo(blob, header.Length + cipher.Count);
        return blob;
    }

    /// <summary>
    /// Decrypts on the chip (§4.7). No licence is consulted, ever.
    ///
    /// Implemented in firmware and verified on hardware (0a4429f).
    ///
    /// The public key in the header is compared *before* a single byte reaches
    /// the dongle. That ordering is what keeps "wrong dongle" and "damaged file"
    /// apart: a GCM tag that fails cannot tell them apart, so the verdict has to
    /// be reached here or not at all.
    /// </summary>
    /// <param name="progress">Reports 0..100 as the chunks go out. Same reasoning
    /// as on the write side: the USB round trip is what makes this slow, and the
    /// caller has to be able to show it happening.</param>
    /// <param name="cancellationToken">Checked between chunks. Nothing is written
    /// to disk here, so a cancelled decryption leaves no partial output.</param>
    /// <exception cref="WrongDongleException">The file belongs to another dongle.</exception>
    /// <exception cref="CorruptedCryptinFileException">The tag did not verify.</exception>
    /// <exception cref="OperationCanceledException">Cancelled between chunks.</exception>
    public static CryptinContent ReadCrin(CryptinDongle dongle, byte[] blob,
                                          IProgress<int>? progress = null,
                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);
        ArgumentNullException.ThrowIfNull(blob);

        var layout = CrinFormat.Detect(blob);
        if (blob.Length < layout.MinimumFileLength)
            throw new InvalidCryptinFileException("file troncato");

        var headerKey = blob.AsSpan(layout.PublicKeyOffset, CrinFormat.PublicKeyLength);
        var identity = dongle.GetIdentityPubKey();

        // Step 7 before step 9, exactly as the container format requires.
        if (!headerKey.SequenceEqual(identity))
            throw new WrongDongleException(headerKey.ToArray());

        var salt = blob.AsSpan(layout.SaltOffset, CrinFormat.SaltLength).ToArray();
        var nonce = blob.AsSpan(layout.NonceOffset, CrinFormat.NonceLength).ToArray();

        var bodyLength = blob.Length - layout.HeaderLength - CrinFormat.TagLength;
        var body = blob.AsSpan(layout.HeaderLength, bodyLength).ToArray();
        var tag = blob.AsSpan(layout.HeaderLength + bodyLength, CrinFormat.TagLength).ToArray();

        var version = blob[layout.VersionOffset];
        if (layout.Format == CryptinFileFormat.Crin && version == CrinFormat.Version2)
            return ReadCrinV2(dongle, blob, layout, salt, nonce, body, tag, progress, cancellationToken);
        if (version != CrinFormat.Version1)
            throw new InvalidCryptinFileException($"unsupported version {version}");

        // Chunked the same way it was written, or the dongle's GCM stream would
        // not line up with the one that produced it.
        var chunks = Split(body);
        var plain = new List<byte>(bodyLength);
        var steps = chunks.Count + 1;   // the tag exchange counts, as on the write side
        var reported = -1;

        for (var seq = 0; seq < chunks.Count; seq++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            plain.AddRange(dongle.DecryptBlock(chunks[seq], salt, nonce, seq, chunks.Count).Plaintext);
            Advance(progress, (seq + 1) * 100 / steps, ref reported);
        }

        // Throws CorruptedCryptinFileException if the tag does not verify.
        dongle.VerifyDecryptTag(tag, chunks.Count);
        Advance(progress, 100, ref reported);

        var isFolder = layout.FlagsOffset is { } flags && (blob[flags] & CrinFormat.FlagFolder) != 0;
        return new CryptinContent(plain.ToArray(), isFolder, layout.Format);
    }

    /// <summary>
    /// Format v2 on the chip (§2a, CRYPTIN_PROTOCOL.md §4.8). The flags byte goes
    /// to the chip, which authenticates it with the rest of the header; the
    /// public key was already compared by the caller.
    /// </summary>
    private static CryptinContent ReadCrinV2(CryptinDongle dongle, byte[] blob, CryptinLayout layout,
                                             byte[] salt, byte[] nonce, byte[] body, byte[] tag,
                                             IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var flags = blob[layout.FlagsOffset!.Value];
        if ((flags & ~CrinFormat.KnownFlags) != 0)
            throw new InvalidCryptinFileException($"unknown flag bits set: 0x{flags:X2}");

        var chunks = SplitV2(body);
        var plain = new List<byte>(body.Length);
        var steps = chunks.Count + 2;
        var reported = -1;

        dongle.OpenDecryptV2(chunks.Count, flags, salt, nonce);
        Advance(progress, 100 / steps, ref reported);

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plain.AddRange(dongle.DecryptBlockV2(chunks[i], i + 1, chunks.Count));
            Advance(progress, (i + 2) * 100 / steps, ref reported);
        }

        // Throws CorruptedCryptinFileException if the tag does not verify; the
        // plaintext gathered above is then dropped with this frame, never returned.
        dongle.VerifyDecryptTagV2(tag, chunks.Count);
        Advance(progress, 100, ref reported);

        return new CryptinContent(plain.ToArray(), (flags & CrinFormat.FlagFolder) != 0, layout.Format);
    }

    /// <summary>
    /// v2 chunking: every data chunk up to <see cref="HidConstants.BlockChunkSize"/>,
    /// none in the opening, and no chunk at all for an empty payload. The cuts
    /// carry no meaning in v2 (standard GCM); these are simply the largest.
    /// </summary>
    private static List<byte[]> SplitV2(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > HidConstants.MaxEncryptBytes)
            throw new CryptinFileTooLargeException(payload.Length, HidConstants.MaxEncryptBytes);

        var chunks = new List<byte[]>();
        for (var offset = 0; offset < payload.Length; offset += HidConstants.BlockChunkSize)
            chunks.Add(payload.Slice(offset, Math.Min(HidConstants.BlockChunkSize, payload.Length - offset)).ToArray());
        return chunks;
    }

    /// <summary>
    /// Reports a percentage only when it actually changes.
    ///
    /// Not a micro-optimisation. A 1 MB file is about 18,000 chunks, and
    /// <see cref="Progress{T}"/> marshals every report onto the UI thread — so
    /// reporting per chunk would post 18,000 callbacks over the ~36 s the transfer
    /// takes, roughly 500 a second, and the message pump handling them is the same
    /// one that has to repaint the bar. The progress indicator would itself be
    /// what made the window unresponsive. Clamped to 0..100, this fires at most
    /// 101 times whatever the file size.
    /// </summary>
    private static void Advance(IProgress<int>? progress, int percent, ref int reported)
    {
        if (progress is null || percent == reported) return;
        reported = percent;
        progress.Report(percent);
    }

    /// <summary>
    /// header || ciphertext || tag — the same container shape the host-side
    /// writer produces. Same shape, not same bytes: the chip derives with the
    /// dongle derivation and authenticates no header byte (§1).
    /// </summary>
    private static byte[] Assemble(byte[] ciphertext, byte[] tag,
                                   byte[] salt, byte[] nonce,
                                   byte[] publicKey, bool isFolder)
    {
        var header = CrinFormat.BuildCrinHeader(publicKey, salt, nonce, isFolder);
        var blob = new byte[header.Length + ciphertext.Length + tag.Length];
        header.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, header.Length);
        tag.CopyTo(blob, header.Length + ciphertext.Length);
        return blob;
    }

    /// <summary>
    /// Splits a payload the way §4.7 requires: the opening chunk is smaller,
    /// because salt and nonce share its exchange.
    /// </summary>
    private static List<byte[]> Split(ReadOnlySpan<byte> plaintext)
    {
        // Refusing here rather than letting the counters wrap. A wrap is not a
        // clean failure: the dongle reaches what it thinks is the last chunk,
        // replies with the tag, and the host parses that as data -- which is
        // what a 4.2 MB file did when seq and total were two bytes each.
        // The protocol could address ~900 MB with 3-byte counters. The limit
        // that bites first is time: at the measured 27.8 KB/s, 5 MB already
        // takes three minutes, and the counters are wide for a different reason
        // (see HidConstants.BlockSeqBytes) than to be filled.
        if (plaintext.Length > HidConstants.MaxEncryptBytes)
            throw new CryptinFileTooLargeException(plaintext.Length,
                HidConstants.MaxEncryptBytes);

        var chunks = new List<byte[]>();
        var offset = 0;

        // An empty payload still needs chunk 0: that is where the transfer opens
        // and where the salt and nonce come from, so a zero-byte file is one
        // empty opening chunk rather than none at all.
        var first = Math.Min(HidConstants.BlockOpenChunkSize, plaintext.Length);
        chunks.Add(plaintext[..first].ToArray());
        offset += first;

        while (offset < plaintext.Length)
        {
            var take = Math.Min(HidConstants.BlockChunkSize, plaintext.Length - offset);
            chunks.Add(plaintext.Slice(offset, take).ToArray());
            offset += take;
        }

        return chunks;
    }

    /// <summary>
    /// The host-side writer: takes a raw <c>file_secret</c> instead of a dongle,
    /// and derives the file key in this process.
    ///
    /// **Public, and deliberately left so — see
    /// <see cref="DeriveFileKey"/> for why that is not the hole it looks like.**
    /// A caller needs a <c>file_secret</c> to reach it, and on a <c>-prod</c>
    /// dongle no caller can get one: <c>CMD_GET_SECRET</c> answers
    /// <c>ERR_DEPRECATED</c> and the value stays on the chip. What remains
    /// reachable is the compatibility suite, reproducing containers from a
    /// synthetic secret with no hardware in the loop.
    ///
    /// Writes the **dongle** derivation since 2026-09-30, so its output opens on
    /// the chip. Before that it wrote the legacy one, which the chip rejects.
    ///
    /// This overload also does not meet the licence gate, because there is no
    /// dongle here to ask — a second reason not to route real encryption through
    /// it. Use <see cref="WriteCrin(CryptinDongle, ReadOnlySpan{byte}, bool)"/>.
    /// </summary>
    public static byte[] WriteCrin(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> fileSecret,
        ReadOnlySpan<byte> donglePublicKey,
        bool isFolder)
    {
        // Fresh per file, never per dongle: one dongle encrypting a thousand
        // files must produce a thousand unrelated keys, so that a nonce
        // collision cannot reach further than the file it happened in.
        Span<byte> salt = stackalloc byte[CrinFormat.SaltLength];
        Span<byte> nonce = stackalloc byte[CrinFormat.NonceLength];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        return WriteCrinV2(plaintext, fileSecret, donglePublicKey, isFolder, salt, nonce);
    }

    /// <summary>
    /// Format v2 host-side writer with the salt and nonce supplied (§2a): info
    /// "cryptin-file-key-v2", the whole header as AAD. Internal for the same
    /// reason as the v1 one: a supplied nonce is only for reproducing fixtures.
    /// </summary>
    internal static byte[] WriteCrinV2(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> fileSecret,
        ReadOnlySpan<byte> donglePublicKey,
        bool isFolder,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce)
        => Seal(plaintext, fileSecret, donglePublicKey, isFolder, salt, nonce,
                HkdfInfoV2, bindHeader: true, version: CrinFormat.Version2);

    /// <summary>
    /// The same writer with the salt and nonce supplied, which makes it
    /// deterministic — and therefore testable against a fixture byte for byte,
    /// with no dongle and no round-trip.
    ///
    /// Internal on purpose. Reusing a (key, nonce) pair under AES-GCM is not a
    /// degraded outcome but a total one: it leaks the XOR of the plaintexts and
    /// the authentication key. The only legitimate caller is a test reproducing
    /// bytes that already exist.
    /// </summary>
    internal static byte[] WriteCrin(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> fileSecret,
        ReadOnlySpan<byte> donglePublicKey,
        bool isFolder,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce)
        => Seal(plaintext, fileSecret, donglePublicKey, isFolder, salt, nonce,
                HkdfInfo, bindHeader: false);

    /// <summary>
    /// The legacy writer: info "cryptin-file-v1", whole header as AAD. Exists
    /// only so the compatibility suite can rebuild the legacy fixtures byte for
    /// byte; nothing writes this derivation for real, because the chip cannot
    /// open it.
    /// </summary>
    internal static byte[] WriteLegacyCrin(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> fileSecret,
        ReadOnlySpan<byte> donglePublicKey,
        bool isFolder,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce)
        => Seal(plaintext, fileSecret, donglePublicKey, isFolder, salt, nonce,
                LegacyHkdfInfo, bindHeader: true);

    private static byte[] Seal(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> fileSecret,
        ReadOnlySpan<byte> donglePublicKey,
        bool isFolder,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> info,
        bool bindHeader,
        byte version = CrinFormat.Version1)
    {
        var header = CrinFormat.BuildCrinHeader(donglePublicKey, salt, nonce, isFolder, version);

        var fileKey = new byte[FileKeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, fileSecret, fileKey, salt, info);
        try
        {
            // header ‖ ciphertext ‖ tag, in one run, which is what both the old
            // app and a combined-tag API produce.
            var output = new byte[header.Length + plaintext.Length + CrinFormat.TagLength];
            header.CopyTo(output.AsSpan());

            using var gcm = new AesGcm(fileKey, CrinFormat.TagLength);
            gcm.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(header.Length, plaintext.Length),
                output.AsSpan(header.Length + plaintext.Length, CrinFormat.TagLength),
                bindHeader ? header : ReadOnlySpan<byte>.Empty);

            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }
}
