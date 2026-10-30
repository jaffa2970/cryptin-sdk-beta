// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CryptinSDK.Dongle;

namespace CryptinSDK.Licensing;

/// <summary>
/// Client for the Crypt-in endpoints of the licence server.
///
/// Written against `server-licence/docs/CRYPTIN_DLL_RICOGNIZIONE_API.md`, which
/// is the contract. Three of its traps are structural rather than incidental and
/// are handled here rather than left to callers:
///
///   * `stripe_session_id` is UNIQUE. Reusing one gives 409 ALREADY_ACTIVATED,
///     so every test needs a fresh value — this client never invents one, since
///     a client that generated its own would be inventing a payment.
///   * Challenges are single use and expire in 300 s. Every operation fetches
///     its own; a retry after a network timeout needs a new one, never the old.
///   * Hex goes on the wire lowercase and unprefixed.
/// </summary>
public sealed class LicenseClient : IDisposable
{
    /// <summary>Production. There is no staging deployment.</summary>
    public static readonly Uri DefaultBaseUrl = new("https://license.lake8.dev");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Mode A sends no personal data at all rather than a row of nulls.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// How long any one call may take before it is given up on.
    ///
    /// Every endpoint here exchanges a few hundred bytes of JSON, so this is a
    /// bound on the network rather than on the work. <c>HttpClient</c> defaults to
    /// 100 s, and the app blocks a dialog on these calls: a user on a dead
    /// connection would sit in front of "verifying…" for over a minute and a half
    /// before being told anything. Thirty seconds is long enough to survive a
    /// slow mobile link and short enough to be a wait rather than a hang.
    ///
    /// A timeout surfaces as <c>TaskCanceledException</c>, which the UI reports as
    /// "server unreachable" — the same class of answer, and never as "no licence".
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public LicenseClient(Uri? baseUrl = null, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _http.BaseAddress = baseUrl ?? DefaultBaseUrl;

        // Only on a client we made. A caller-supplied HttpClient may already have
        // sent a request, and HttpClient refuses to have its Timeout changed after
        // that — so reaching in here would turn an injected client into a crash.
        if (_ownsHttp) _http.Timeout = RequestTimeout;
    }

    /// <summary>
    /// <c>GET /api/activation-challenge</c>. No authentication, no rate limit,
    /// and no errors of its own — a failure here is the database being down.
    /// </summary>
    public async Task<ActivationChallenge> GetActivationChallengeAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/api/activation-challenge", ct).ConfigureAwait(false);
        return await ReadAsync<ActivationChallenge>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/stripe/create-checkout-session</c> — opens a payment for
    /// <paramref name="productSku"/> and returns its id and the URL to show.
    ///
    /// The id comes back before the payment happens. A caller should store it
    /// at once: if the browser is closed before Stripe redirects, that stored id
    /// is the only way back to the licence without going through support.
    /// </summary>
    /// <param name="productSku">The SKU the session will be matched against —
    /// for a renewal, the licence's own, not the default.</param>
    /// <param name="mode">"A" anonymous or "B" registered. Only lands in the
    /// session's metadata; it is not Stripe's own mode, which is always
    /// <c>payment</c>.</param>
    public async Task<CheckoutSession> CreateCheckoutSessionAsync(
        string productSku, string mode = "A", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productSku);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        var request = new CreateCheckoutRequest { ProductSku = productSku, Mode = mode };
        using var response = await _http
            .PostAsJsonAsync("/api/stripe/create-checkout-session", request, Json, ct).ConfigureAwait(false);
        return await ReadAsync<CheckoutSession>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/activate</c> — activates the dongle and returns the licence JWT.
    ///
    /// The order below is mandatory, and it is the order because the challenge is
    /// single use with a 300 s TTL: it is fetched immediately before it is signed,
    /// so nothing slow sits between issue and use.
    ///
    ///   1. GET the challenge
    ///   2. send the nonce to the dongle, get the signature
    ///   3. GET_PUBKEY      -> K_identity
    ///   4. GET_ATTESTATION_PUBKEY -> K_attestation
    ///   5. POST /api/activate
    ///
    /// Steps 2 to 4 need the dongle unlocked: verify the PIN first.
    /// </summary>
    /// <param name="dongle">Connected and PIN-verified.</param>
    /// <param name="stripeSessionId">The checkout session. Must be non-empty and
    /// never reused — it is UNIQUE in the database. May be null only for
    /// <see cref="ActivateRequest.BetaSku"/>, which has no payment behind it.</param>
    /// <param name="data">Mode B registration data. Null gives mode A, anonymous.</param>
    /// <param name="product">The SKU to activate. Defaults to the paid product;
    /// pass <see cref="ActivateRequest.BetaSku"/> for a beta dongle, which is
    /// what selects the fixed 2027-03-31 expiry and the closed renewal on the
    /// server side.</param>
    public async Task<ActivateResponse> ActivateAsync(
        CryptinDongle dongle,
        string? stripeSessionId,
        ActivationData? data = null,
        string product = ActivateRequest.DefaultSku,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);

        // Not cosmetic: a null product would be dropped from the body by
        // WhenWritingNull, the server would fall back to its own default, and a
        // beta dongle would come back with a paid licence and no sign anything
        // went wrong.
        ArgumentException.ThrowIfNullOrWhiteSpace(product);

        // The beta is the one SKU with nothing to pay for, so it is the one SKU
        // allowed through without a session. Conditioning on the product rather
        // than dropping the check keeps a forgotten session id an exception here
        // instead of a 402 from the server after the dongle has already signed.
        if (product != ActivateRequest.BetaSku && string.IsNullOrWhiteSpace(stripeSessionId))
            throw new ArgumentException(
                "an empty stripe_session_id is refused with 402 before the bypass is consulted",
                nameof(stripeSessionId));

        var activated = await OnceMoreOnAsync(LicenseErrorCodes.BetaBusy, BetaBusyPause,
            () => PostActivationAsync(dongle, stripeSessionId, data, product, ct), ct).ConfigureAwait(false);

        await HandJwtToDongleAsync(dongle, activated.Jwt).ConfigureAwait(false);        // 6
        return activated;
    }

    /// <summary>Steps 1 to 5 of <see cref="ActivateAsync"/>, as one attempt. A
    /// retry has to repeat all of them: the challenge is single use, so the only
    /// thing a second POST of the same body could earn is CHALLENGE_EXPIRED.</summary>
    private async Task<ActivateResponse> PostActivationAsync(
        CryptinDongle dongle, string? stripeSessionId, ActivationData? data, string product,
        CancellationToken ct)
    {
        var challenge = await GetActivationChallengeAsync(ct).ConfigureAwait(false);   // 1

        var nonce = Convert.FromHexString(challenge.Challenge);
        var signature = dongle.Challenge(nonce);                                       // 2
        var identity = dongle.GetIdentityPubKey();                                     // 3
        var attestation = dongle.GetAttestationPubKey();                               // 4

        var request = new ActivateRequest
        {
            ChallengeId = challenge.ChallengeId,
            ChallengeSignature = Hex(signature),
            IdentityPubkey = Hex(identity),
            AttestationPubkey = Hex(attestation),
            Product = product,
            StripeSessionId = stripeSessionId,
            Email = data?.Email,
            FirstName = data?.FirstName,
            LastName = data?.LastName,
            Company = data?.Company,
            VatId = data?.VatId,
            AcceptTerms = data?.AcceptTerms ?? false,
        };

        using var response = await _http                                               // 5
            .PostAsJsonAsync("/api/activate", request, Json, ct).ConfigureAwait(false);
        return await ReadAsync<ActivateResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes a freshly issued token onto the dongle, which is what actually
    /// lets it encrypt (protocol.h §8).
    ///
    /// **Deliberately does not fail the activation if the dongle refuses it.**
    /// The licence has been bought and recorded server-side by this point;
    /// throwing here would leave the user with a paid licence and an exception,
    /// and no way to retry that does not look like paying twice. The token can
    /// always be pushed again later from a fresh verify, so a failure here is a
    /// deferred problem, not a lost one.
    ///
    /// A firmware without 0x0B is no longer the ordinary case — 0a4429f
    /// implements it and it is verified on hardware — but it stays tolerated
    /// rather than required: a dongle flashed before that answers
    /// STATUS_UNKNOWN_CMD, and refusing to activate it would strand a chip that
    /// is otherwise fine over a command it was never built with.
    /// </summary>
    private static async Task HandJwtToDongleAsync(CryptinDongle dongle, string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return;

        try
        {
            await dongle.SetLicenseJwtAsync(jwt).ConfigureAwait(false);
        }
        catch (LicenseRejectedException)
        {
            // Includes Unsupported: a dongle that predates enforcement.
        }
        catch (CryptinDongleException)
        {
            // Unplugged mid-activation, or a transport fault. Same reasoning.
        }
    }

    /// <summary>
    /// <c>POST /api/recovery</c> (§2.4) — moves the licence onto this chip.
    ///
    /// **A transfer, not a registration.** Afterwards one chip works, not two.
    /// For a second dongle alongside the first, use
    /// <see cref="RegisterBackupAsync"/>.
    ///
    /// The dongle on the wire is the **rebuilt** one: it was recovered from the
    /// seed, so it carries the original K_identity and can sign, while its
    /// K_attestation is its own and is what gets registered. The old chip's
    /// K_attestation is never sent — it is gone, which is the situation this
    /// endpoint is for.
    ///
    /// Rate-limited to <c>CRYPTIN_RECOVERY_MAX_YEAR</c> = 3 per licence per 365
    /// days, on a rolling window.
    /// </summary>
    /// <param name="dongle">The rebuilt chip, connected and unlocked.</param>
    /// <param name="reason">Why. Anything the server does not recognise is
    /// <c>400 INVALID_REASON</c>, so it is an enum here rather than a string.</param>
    public async Task<RecoveryResponse> RecoverAsync(
        CryptinDongle dongle,
        RecoveryReason reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);

        return await OnceMoreOnAsync(LicenseErrorCodes.ConcurrentRetry, ConcurrentRetryPause,
            async () =>
            {
                var challenge = await GetActivationChallengeAsync(ct).ConfigureAwait(false);
                var nonce = Convert.FromHexString(challenge.Challenge);

                var request = new RecoveryRequest
                {
                    ChallengeId = challenge.ChallengeId,
                    ChallengeSignature = Hex(dongle.Challenge(nonce)),
                    IdentityPubkey = Hex(dongle.GetIdentityPubKey()),
                    NewAttestationPubkey = Hex(dongle.GetAttestationPubKey()),
                    RecoveryReason = Wire(reason),
                };

                using var response = await _http
                    .PostAsJsonAsync("/api/recovery", request, Json, ct).ConfigureAwait(false);
                return await ReadAsync<RecoveryResponse>(response, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    /// <summary>snake_case, as the server spells it.</summary>
    private static string Wire(RecoveryReason reason) => reason switch
    {
        RecoveryReason.LostDevice => "lost_device",
        RecoveryReason.DamagedDevice => "damaged_device",
        RecoveryReason.StolenDevice => "stolen_device",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>
    /// <c>POST /api/dongle/register_backup</c> (§11) — authorises a second chip
    /// on the licence. Free, once per calendar year.
    ///
    /// This is **registration, not transfer**: the main dongle stays valid and
    /// both work afterwards. <c>/api/recovery</c> is the transfer, and using it
    /// here would move the licence and leave one chip dead.
    ///
    /// The caller must have verified, before getting here, that the backup was
    /// initialised from the **same seed**. The server cannot check it — it never
    /// sees the backup's K_identity — and a chip with a different seed registers
    /// without error and then answers 404 LICENSE_NOT_FOUND at every verify.
    /// </summary>
    /// <param name="mainDongle">Connected and unlocked. Signs the challenge.</param>
    /// <param name="backupAttestationPubKey">32 bytes, read from the backup with
    /// opcode 0x06. The backup signs nothing.</param>
    public async Task<BackupResult> RegisterBackupAsync(
        CryptinDongle mainDongle,
        byte[] backupAttestationPubKey,
        string? label = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mainDongle);
        ArgumentNullException.ThrowIfNull(backupAttestationPubKey);
        if (backupAttestationPubKey.Length != HidConstants.AttestPubkeySize)
            throw new ArgumentException(
                $"K_attestation is {HidConstants.AttestPubkeySize} bytes, got {backupAttestationPubKey.Length}",
                nameof(backupAttestationPubKey));

        return await OnceMoreOnAsync(LicenseErrorCodes.ConcurrentRetry, ConcurrentRetryPause,
            async () =>
            {
                var challenge = await GetActivationChallengeAsync(ct).ConfigureAwait(false);
                var nonce = Convert.FromHexString(challenge.Challenge);

                var request = new RegisterBackupRequest
                {
                    ChallengeId = challenge.ChallengeId,
                    ChallengeSignature = Hex(mainDongle.Challenge(nonce)),
                    IdentityPubkey = Hex(mainDongle.GetIdentityPubKey()),
                    AttestationPubkey = Hex(mainDongle.GetAttestationPubKey()),
                    BackupAttestationPubkey = Hex(backupAttestationPubKey),
                    Label = label,
                };

                using var response = await _http
                    .PostAsJsonAsync("/api/dongle/register_backup", request, Json, ct).ConfigureAwait(false);
                return await ReadAsync<BackupResult>(response, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/dongle/renew</c> (§10) — extends the licence.
    ///
    /// Same five-step order as activation, for the same reason. Renewal requires
    /// the **main** dongle in hand: a registered backup verifies fine but cannot
    /// renew, and gets 403 ATTESTATION_MISMATCH if it tries.
    /// </summary>
    public async Task<RenewResponse> RenewAsync(
        CryptinDongle dongle,
        string stripeSessionId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);
        if (string.IsNullOrWhiteSpace(stripeSessionId))
            throw new ArgumentException("every renewal needs a fresh Checkout Session",
                nameof(stripeSessionId));

        var renewed = await OnceMoreOnAsync(LicenseErrorCodes.ConcurrentRetry, ConcurrentRetryPause,
            async () =>
            {
                var challenge = await GetActivationChallengeAsync(ct).ConfigureAwait(false);
                var nonce = Convert.FromHexString(challenge.Challenge);

                var request = new RenewRequest
                {
                    ChallengeId = challenge.ChallengeId,
                    ChallengeSignature = Hex(dongle.Challenge(nonce)),
                    IdentityPubkey = Hex(dongle.GetIdentityPubKey()),
                    AttestationPubkey = Hex(dongle.GetAttestationPubKey()),
                    StripeSessionId = stripeSessionId,
                };

                using var response = await _http
                    .PostAsJsonAsync("/api/dongle/renew", request, Json, ct).ConfigureAwait(false);
                return await ReadAsync<RenewResponse>(response, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        // Same reasoning as activation: the renewal is paid and recorded, so a
        // dongle that will not take the token yet must not undo it.
        await HandJwtToDongleAsync(dongle, renewed.Jwt).ConfigureAwait(false);
        return renewed;
    }

    /// <summary>
    /// <c>POST /api/dongle/rekey/start</c> — first half of moving the licence onto
    /// a new identity. Changes nothing on the licence: it returns a ticket that
    /// <see cref="RekeyFinishAsync"/> presents after the chip has been reflashed.
    ///
    /// Two halves because one chip cannot sign with both identities: the new
    /// seed is only minted by erasing the old one. The alternative — signing this
    /// half on the host with a K_identity derived from the 24 words — was turned
    /// down on 2026-10-30, because it puts the identity's private key on the PC.
    /// </summary>
    /// <param name="dongle">The dongle the licence is on now: connected, unlocked,
    /// and the main one — a registered backup is refused with
    /// ATTESTATION_MISMATCH.</param>
    public async Task<RekeyStartResponse> RekeyStartAsync(CryptinDongle dongle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);

        return await OnceMoreOnAsync(LicenseErrorCodes.ConcurrentRetry, ConcurrentRetryPause,
            async () =>
            {
                var challenge = await GetActivationChallengeAsync(ct).ConfigureAwait(false);
                var nonce = Convert.FromHexString(challenge.Challenge);

                var request = new RekeyStartRequest
                {
                    ChallengeId = challenge.ChallengeId,
                    ChallengeSignature = Hex(dongle.Challenge(nonce)),
                    IdentityPubkey = Hex(dongle.GetIdentityPubKey()),
                    AttestationPubkey = Hex(dongle.GetAttestationPubKey()),
                };

                using var response = await _http
                    .PostAsJsonAsync("/api/dongle/rekey/start", request, Json, ct).ConfigureAwait(false);
                return await ReadAsync<RekeyStartResponse>(response, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/dongle/rekey/finish</c> — second half: the reflashed chip,
    /// under its new identity, claims the licence with the ticket.
    ///
    /// Does not hand the token to the chip, like <see cref="RecoverAsync"/> and
    /// for the same reason: the caller has to decide whether an already expired
    /// token is worth sending, and has to say what happened either way.
    /// </summary>
    /// <param name="dongle">The reflashed chip, connected and unlocked with its
    /// new PIN.</param>
    /// <param name="ticket">From <see cref="RekeyStartAsync"/>.</param>
    public async Task<RekeyFinishResponse> RekeyFinishAsync(
        CryptinDongle dongle, string ticket, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);

        return await OnceMoreOnAsync(LicenseErrorCodes.ConcurrentRetry, ConcurrentRetryPause,
            async () =>
            {
                var challenge = await GetActivationChallengeAsync(ct).ConfigureAwait(false);
                var nonce = Convert.FromHexString(challenge.Challenge);

                var request = new RekeyFinishRequest
                {
                    Ticket = ticket,
                    ChallengeId = challenge.ChallengeId,
                    ChallengeSignature = Hex(dongle.Challenge(nonce)),
                    NewIdentityPubkey = Hex(dongle.GetIdentityPubKey()),
                    NewAttestationPubkey = Hex(dongle.GetAttestationPubKey()),
                };

                using var response = await _http
                    .PostAsJsonAsync("/api/dongle/rekey/finish", request, Json, ct).ConfigureAwait(false);
                return await ReadAsync<RekeyFinishResponse>(response, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /api/verify</c> — checks the hardware and the commercial state of
    /// the licence in one call: clone, revocation and expiry are three distinct
    /// outcomes.
    ///
    /// The nonce here is **generated by this client**, unlike activation where
    /// the server issues it. So there is nothing single-use to burn and a retry
    /// is free.
    ///
    /// Expiry, revocation, an unknown licence and an unauthorised chip come back
    /// as a <see cref="LicenseStatus"/>, not as exceptions — they are answers,
    /// and a user interface has to draw all four.
    /// </summary>
    public async Task<LicenseStatus> VerifyAsync(CryptinDongle dongle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dongle);

        var nonce = RandomNumberGenerator.GetBytes(HidConstants.NonceSize);
        var request = new VerifyRequest
        {
            Challenge = Hex(nonce),
            ChallengeSignature = Hex(dongle.Challenge(nonce)),
            IdentityPubkey = Hex(dongle.GetIdentityPubKey()),
            AttestationPubkey = Hex(dongle.GetAttestationPubKey()),
        };

        using var response = await _http
            .PostAsJsonAsync("/api/verify", request, Json, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var ok = JsonSerializer.Deserialize<VerifyResponse>(body, Json)
                     ?? throw new LicenseProtocolException("/api/verify answered 200 with an empty body");

            return new LicenseStatus
            {
                State = ok.InRevoca ? LicenseState.InGracePeriod : LicenseState.Valid,
                ExpiresAt = ok.ExpiresAt,
                Jwt = ok.Jwt,
                GraceUntil = ok.GraceUntil,
                Product = JwtClaim(ok.Jwt, "product"),
            };
        }

        var failure = LicenseApiException.FromResponse(response.StatusCode, body);
        var state = failure.Code switch
        {
            LicenseErrorCodes.LicenseExpired => LicenseState.Expired,
            LicenseErrorCodes.LicenseRevoked => LicenseState.Revoked,
            LicenseErrorCodes.LicenseNotFound => LicenseState.NotFound,
            LicenseErrorCodes.AttestationMismatch => LicenseState.UnauthorizedDongle,

            // INVALID_SIGNATURE, a 422, anything unrecognised: not an answer
            // about the licence, a fault in the exchange.
            _ => (LicenseState?)null,
        } ?? throw failure;

        return new LicenseStatus
        {
            State = state,
            ExpiresAt = ReadDate(body, "expires_at"),
            Reason = failure.Reason,
            Message = ReadString(body, "message"),
            Product = ReadString(body, "product"),
        };
    }

    /// <summary>How long to wait before retrying a BETA_BUSY. The server's lock
    /// is NOWAIT, so the activation that beat us is already mid-flight; the
    /// contract asks for one retry after one to two seconds.</summary>
    internal static readonly TimeSpan BetaBusyPause = TimeSpan.FromSeconds(1.5);

    /// <summary>How long to wait before retrying a CONCURRENT_RETRY. The other
    /// request has already committed by the time we hear we lost, so this only
    /// has to be long enough not to be the same instant.</summary>
    internal static readonly TimeSpan ConcurrentRetryPause = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Runs <paramref name="attempt"/>, and once more if the server answered
    /// <paramref name="code"/>. Exactly once more: both codes that come through
    /// here mean "you collided with something that has since finished", and a
    /// second collision is a real error the caller has to see, not a reason to
    /// keep knocking.
    ///
    /// The attempt is the whole exchange, challenge included. Replaying the same
    /// body would spend a single-use challenge twice.
    /// </summary>
    private static async Task<T> OnceMoreOnAsync<T>(
        string code, TimeSpan pause, Func<Task<T>> attempt, CancellationToken ct)
    {
        try
        {
            return await attempt().ConfigureAwait(false);
        }
        catch (LicenseApiException e) when (e.Code == code)
        {
            await Task.Delay(pause, ct).ConfigureAwait(false);
            return await attempt().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One string claim from a JWT's payload, <b>unverified</b>. Only for
    /// deciding what to offer the user; nothing that grants anything may rest on
    /// it. Null on anything that is not a well-formed token carrying that claim.
    /// </summary>
    internal static string? JwtClaim(string? jwt, string claim)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty(claim, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    /// <summary>Lowercase, unprefixed — the only form the server accepts.</summary>
    internal static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    /// <summary>Accessory fields on an error body are additive, so they are read
    /// opportunistically and never required.</summary>
    private static string? ReadString(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? ReadDate(string body, string property)
        => DateTimeOffset.TryParse(ReadString(body, property), out var parsed) ? parsed : null;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw LicenseApiException.FromResponse(response.StatusCode, body);

        return JsonSerializer.Deserialize<T>(body, Json)
               ?? throw new LicenseProtocolException(
                   $"the server answered 200 with an empty body where {typeof(T).Name} was expected");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>
/// The server answered successfully but not in the shape the contract describes.
/// Distinct from <see cref="LicenseApiException"/>, which carries an error the
/// server meant to send: this one means the two sides disagree about the
/// protocol, and no error code will explain it.
/// </summary>
public sealed class LicenseProtocolException : Exception
{
    public LicenseProtocolException(string message) : base(message) { }
}
