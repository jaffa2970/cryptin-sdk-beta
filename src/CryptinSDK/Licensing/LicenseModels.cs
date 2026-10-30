// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Text.Json.Serialization;

namespace CryptinSDK.Licensing;

/// <summary>
/// <c>GET /api/activation-challenge</c> — a 32-byte nonce in lowercase hex, TTL
/// 300 s, single use. Consumed with a DELETE and a rowcount check, so it is safe
/// against the two uvicorn workers in production.
/// </summary>
public sealed record ActivationChallenge
{
    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }

    /// <summary>64 hex characters. This is what the dongle signs.</summary>
    [JsonPropertyName("challenge")] public required string Challenge { get; init; }
}

/// <summary>
/// Body of <c>POST /api/activate</c>, from §8.7 of the API reconnaissance.
/// Hex is lowercase and unprefixed: <c>normalizza_hex()</c> only strips and
/// lowercases, so a leading <c>0x</c> breaks the length check and comes back as
/// <c>400 INVALID_SIGNATURE</c>.
/// </summary>
public sealed record ActivateRequest
{
    /// <summary>The paid product. Anything that is not <see cref="BetaSku"/>.</summary>
    public const string DefaultSku = "personal-windows-v1";

    /// <summary>
    /// The developer beta (2026-08-24). The server hangs the whole sunset policy
    /// on this string: <c>exp</c> is forced to 2027-03-31T23:59:59 — a fixed date
    /// for every beta dongle, not a duration — and renewal is refused with
    /// <c>403 BETA_NOT_RENEWABLE</c>. Nothing here enforces any of that; the
    /// client only has to ask for the right product.
    /// </summary>
    public const string BetaSku = "personal-beta-v1";

    /// <summary>
    /// What a beta activation sends in place of a Checkout Session, there being
    /// no payment to point at. The server is expected to store <c>NULL</c> in the
    /// licence row and keep this string in the event log.
    ///
    /// It is the same value for every beta dongle, and <c>stripe_session_id</c>
    /// is UNIQUE in the database, so this used to carry a warning: if the server
    /// did not null it before inserting, the second beta activation would come
    /// back <c>409 ALREADY_ACTIVATED</c> and look like a client bug.
    ///
    /// ✅ Answered by the licence server on 2026-09-03, reading the code rather
    /// than guessing: the value is checked and **not persisted**, so repeated
    /// beta activations do not collide. Two more things settled in the same
    /// pass, both of which show up here rather than on the server if they are
    /// wrong: the string must arrive **exact** (otherwise
    /// <c>402 PAYMENT_REQUIRED</c> with <c>reason=BETA_INVALID_CODE</c>), and a
    /// beta licence is issued with the campaign's fixed expiry, not with 365
    /// days from activation.
    /// </summary>
    public const string BetaSessionId = "BETA-2026";

    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("identity_pubkey")] public required string IdentityPubkey { get; init; }
    [JsonPropertyName("attestation_pubkey")] public required string AttestationPubkey { get; init; }
    [JsonPropertyName("product")] public string Product { get; init; } = DefaultSku;

    /// <summary>Optional to the schema, and required in practice for every SKU
    /// except <see cref="BetaSku"/>: an empty string is rejected with
    /// <c>402 PAYMENT_REQUIRED</c> before the Stripe bypass is even consulted.
    /// UNIQUE in the database, so a value may never be reused.
    ///
    /// ⚠️ Nullable since 2026-08-24, which cost the <c>required</c> keyword —
    /// forgetting it now compiles. What catches that is the guard in
    /// <c>LicenseClient.ActivateAsync</c>, not the type system. And null here is
    /// **omitted** from the body rather than sent as JSON <c>null</c>, because
    /// the serializer runs with <c>WhenWritingNull</c>.</summary>
    [JsonPropertyName("stripe_session_id")] public string? StripeSessionId { get; init; }

    // Mode B. Omitted in mode A, where every one of these stays null.
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("first_name")] public string? FirstName { get; init; }
    [JsonPropertyName("last_name")] public string? LastName { get; init; }
    [JsonPropertyName("company")] public string? Company { get; init; }
    [JsonPropertyName("vat_id")] public string? VatId { get; init; }
    [JsonPropertyName("accept_terms")] public bool AcceptTerms { get; init; }
}

/// <summary>
/// The registered-activation fields. All optional at the protocol level, because
/// an activation with a valid payment must never fail over a personal-data field
/// — the customer has already paid at that point. Validate in the UI, before the
/// checkout, which is the last moment the user can still fix anything.
/// </summary>
public sealed record ActivationData
{
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Company { get; init; }

    /// <summary>Max 16 characters — an Italian codice fiscale is 16, an Italian
    /// VAT number 11 digits, an EU VAT id up to 14 with its country prefix.
    /// Label it "Codice Fiscale / P.IVA".</summary>
    public string? VatId { get; init; }

    public bool AcceptTerms { get; init; }
}

public sealed record ActivateResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("jwt")] public required string Jwt { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Nullable on purpose: a server that predates this block does not
    /// send it, and a client that ignores it keeps working.</summary>
    [JsonPropertyName("customer")] public CustomerEcho? Customer { get; init; }
}

/// <summary>
/// Body of <c>POST /api/verify</c>. Every field is required.
///
/// One difference from activation, and it is easy to get wrong: **this nonce is
/// generated by the client**. The one for <c>/api/activate</c> and
/// <c>/api/recovery</c> comes from the server and is single use.
/// </summary>
public sealed record VerifyRequest
{
    [JsonPropertyName("challenge")] public required string Challenge { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("identity_pubkey")] public required string IdentityPubkey { get; init; }
    [JsonPropertyName("attestation_pubkey")] public required string AttestationPubkey { get; init; }
}

/// <summary>
/// Body of <c>POST /api/dongle/renew</c> (§10).
///
/// Renewal is tied to the **main** dongle: a registered backup verifies and
/// works but cannot renew, and trying gives 403 ATTESTATION_MISMATCH. The
/// challenge comes from the same <c>GET /api/activation-challenge</c> as
/// activation, and is single use in the same way.
/// </summary>
public sealed record RenewRequest
{
    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("identity_pubkey")] public required string IdentityPubkey { get; init; }
    [JsonPropertyName("attestation_pubkey")] public required string AttestationPubkey { get; init; }
    [JsonPropertyName("stripe_session_id")] public required string StripeSessionId { get; init; }
}

public sealed record RenewResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("jwt")] public required string Jwt { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// So the app can say "extended from X to Y" without reconstructing it.
    /// Null only for a licence with no expiry at all, which is theoretical.
    ///
    /// Worth showing, because the rule behind it surprises people: the new
    /// expiry is <c>max(old, now) + 365 days</c>. Renew early and the remaining
    /// days are added; renew late and the uncovered period is simply lost.
    /// </summary>
    [JsonPropertyName("previous_expires_at")] public DateTimeOffset? PreviousExpiresAt { get; init; }
}

/// <summary>
/// Body of <c>POST /api/dongle/register_backup</c> (§11.2).
///
/// Everything here is proved by the **main** dongle: the signature proves
/// possession of the seed, and its <c>attestation_pubkey</c> proves it is that
/// physical chip. The backup signs nothing at all — only its K_attestation is
/// read over USB. Whoever is asking already holds both chips, so a signature
/// from the backup would prove nothing further.
/// </summary>
public sealed record RegisterBackupRequest
{
    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("identity_pubkey")] public required string IdentityPubkey { get; init; }

    /// <summary>K_attestation of the MAIN dongle.</summary>
    [JsonPropertyName("attestation_pubkey")] public required string AttestationPubkey { get; init; }

    /// <summary>K_attestation of the BACKUP.</summary>
    [JsonPropertyName("backup_attestation_pubkey")] public required string BackupAttestationPubkey { get; init; }

    /// <summary>Optional, truncated to 100 characters, never a cause of error.</summary>
    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed record BackupResult
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }

    /// <summary>Counts the main dongle too, which has no row in
    /// <c>dongle_trusted</c> — so after a first backup this is 2. The backup
    /// works immediately, with no further step.</summary>
    [JsonPropertyName("trusted_dongles")] public int TrustedDongles { get; init; }
}

/// <summary>Why a licence is being moved to another chip (§2.4).</summary>
public enum RecoveryReason
{
    LostDevice,
    DamagedDevice,
    StolenDevice,
}

/// <summary>
/// Body of <c>POST /api/recovery</c> (§2.4) — moves a licence onto a rebuilt
/// chip.
///
/// **This is a transfer, not a registration.** It leaves one chip working, not
/// two: use <see cref="RegisterBackupRequest"/> to add a second dongle to a
/// licence that keeps the first.
///
/// Five fields, and the one that is missing is the point: there is no
/// <c>attestation_pubkey</c> for the *old* chip. In a recovery that chip is
/// lost, damaged or stolen, so its K_attestation cannot be read — requiring it
/// would make the endpoint useless in exactly the case it exists for. What
/// proves the claim is the signature: only the seed can produce it, and the seed
/// is what the user still has.
/// </summary>
public sealed record RecoveryRequest
{
    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("identity_pubkey")] public required string IdentityPubkey { get; init; }

    /// <summary>K_attestation of the chip doing the recovery — the new one.</summary>
    [JsonPropertyName("new_attestation_pubkey")] public required string NewAttestationPubkey { get; init; }

    /// <summary>One of lost_device, damaged_device, stolen_device. Anything else
    /// is <c>400 INVALID_REASON</c>.</summary>
    [JsonPropertyName("recovery_reason")] public required string RecoveryReason { get; init; }
}

public sealed record RecoveryResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("jwt")] public required string Jwt { get; init; }

    /// <summary>Unchanged by the recovery: it moves the licence, it does not
    /// extend it.</summary>
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The licence moved, and it is already past its term.
    ///
    /// A transfer is deliberately allowed on an expired licence, and the reason
    /// is a trap rather than a kindness: renewal requires the physical chip
    /// (<c>/api/dongle/renew</c> matches on K_attestation), so refusing to move
    /// an expired licence off a broken dongle would leave no route to renew it
    /// at all. The user would have to buy again — a penalty by accident.
    ///
    /// Not <c>required</c>: a server that predates the flag simply leaves it
    /// false, and the caller is expected to fall back on
    /// <see cref="ExpiresAt"/>, which has always been there and says the same
    /// thing.
    /// </summary>
    [JsonPropertyName("expired")] public bool Expired { get; init; }

    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>
/// Body of <c>POST /api/dongle/rekey/start</c> — the first half of moving a
/// licence onto a <em>new identity</em> (advisory G2, 2026-10-30).
///
/// Signed by the dongle the licence is on <b>now</b>, before it is reflashed:
/// the reflash erases the old K_identity and the old K_attestation together, so
/// this is the last moment either can prove anything. Both are sent, and the
/// server requires the attestation to be the licence's own — a seed alone, typed
/// onto some other chip, is what <see cref="RecoveryRequest"/> is for.
///
/// Field names as in the licence server's report of 2026-10-30 (Tartufo
/// 41afc3fd). Two of its rules shape the client: the yearly limit is checked
/// here already, before anything is erased; and <c>/api/recovery</c> voids any
/// open ticket of the licence, so a transfer makes a pending rekey worthless.
/// </summary>
public sealed record RekeyStartRequest
{
    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("identity_pubkey")] public required string IdentityPubkey { get; init; }
    [JsonPropertyName("attestation_pubkey")] public required string AttestationPubkey { get; init; }
}

public sealed record RekeyStartResponse
{
    /// <summary><c>ticket_issued</c>.</summary>
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>
    /// The bearer ticket that the second half presents. Returned once and
    /// stored only hashed on the server, so losing it here means starting over
    /// — which costs nothing, since the licence has not moved yet.
    /// </summary>
    [JsonPropertyName("ticket")] public required string Ticket { get; init; }

    /// <summary>After this the ticket is refused. Long enough to decrypt what
    /// is needed before the reflash: days, not minutes.</summary>
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Body of <c>POST /api/dongle/rekey/finish</c> — the second half, signed by the
/// <b>new</b> identity on the reflashed chip. The ticket is what ties it to the
/// first half; nothing of the old identity survives to sign with.
/// </summary>
public sealed record RekeyFinishRequest
{
    [JsonPropertyName("ticket")] public required string Ticket { get; init; }
    [JsonPropertyName("challenge_id")] public required string ChallengeId { get; init; }
    [JsonPropertyName("challenge_signature")] public required string ChallengeSignature { get; init; }
    [JsonPropertyName("new_identity_pubkey")] public required string NewIdentityPubkey { get; init; }
    [JsonPropertyName("new_attestation_pubkey")] public required string NewAttestationPubkey { get; init; }
}

public sealed record RekeyFinishResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("jwt")] public required string Jwt { get; init; }

    /// <summary>Unchanged: a rekey moves the licence, it does not extend it.</summary>
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Same meaning and same reasoning as
    /// <see cref="RecoveryResponse.Expired"/>.</summary>
    [JsonPropertyName("expired")] public bool Expired { get; init; }

    /// <summary>
    /// Registered backups the server dropped. They carry the old seed, so under
    /// the new identity they would answer "not found" for good; the annual free
    /// registration is available again. The app has to say so, because nothing
    /// else will tell the user their backup stopped counting.
    /// </summary>
    [JsonPropertyName("backups_removed")] public int BackupsRemoved { get; init; }

    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>How a licence stands. Distinct from an error: these are answers.</summary>
public enum LicenseState
{
    /// <summary>Verified, and inside its term.</summary>
    Valid,

    /// <summary>Being revoked, but still usable for now — the JWT stays valid
    /// through the grace period. See <see cref="LicenseStatus.GraceUntil"/>.</summary>
    InGracePeriod,

    /// <summary>402 — the term ran out. The row stays "attiva" in the database
    /// because for a dongle the expiry is a date and nothing sweeps it.</summary>
    Expired,

    /// <summary>403 — revoked or refused.</summary>
    Revoked,

    /// <summary>404 — no licence for this K_identity. For a fresh dongle this is
    /// the normal state, not a fault: it means "not activated yet".</summary>
    NotFound,

    /// <summary>403 ATTESTATION_MISMATCH — the licence exists but this physical
    /// chip is not authorised on it: either a clone, or a backup that was never
    /// registered with <c>register_backup</c>.</summary>
    UnauthorizedDongle,
}

/// <summary>
/// The outcome of a verification.
///
/// Expiry, revocation and an unauthorised chip are returned rather than thrown.
/// They are the documented answers a verification can have, and every one of
/// them is something a user interface has to render calmly; making the caller
/// catch three exception types to draw one label would be backwards. Genuine
/// failures — a rejected signature, an unreachable server, a malformed request —
/// still throw.
/// </summary>
public sealed record LicenseStatus
{
    public required LicenseState State { get; init; }

    /// <summary>Present when the server said so: on success, and on 402, where
    /// the expiry date comes back in the error body.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>A freshly issued licence JWT. Only on the valid paths.</summary>
    public string? Jwt { get; init; }

    /// <summary>End of the grace period, when one is running.</summary>
    public DateTimeOffset? GraceUntil { get; init; }

    /// <summary>The server's accessory detail, e.g. CLONE_OR_UNREGISTERED.</summary>
    public string? Reason { get; init; }

    /// <summary>The server's own explanatory text, when it sent one.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// The licence's SKU, when it can be known — the <c>product</c> claim of
    /// <see cref="Jwt"/> on the valid paths, or a <c>product</c> field in the
    /// error body where the server sends one. Null otherwise.
    ///
    /// Exists for renewal. The Checkout Session has to be created for the SKU of
    /// the licence being renewed, and it is paid in the browser <em>before</em>
    /// <c>/api/dongle/renew</c> is ever called: a client that guessed the SKU
    /// wrong, or offered a paid renewal to a beta licence, would take the money
    /// and then get a refusal. Read without verifying the token's signature,
    /// which is fine for this — it only decides what to offer, and the server
    /// checks the session against the licence regardless.
    /// </summary>
    public string? Product { get; init; }

    public bool IsUsable => State is LicenseState.Valid or LicenseState.InGracePeriod;
}

/// <summary>
/// <c>POST /api/stripe/create-checkout-session</c> (contract §12). The only
/// source of a Checkout Session that <c>/api/activate</c> and
/// <c>/api/dongle/renew</c> accept: since 2026-09-24 the server matches the
/// session's <c>metadata[product_sku]</c>, amount, currency and mode against
/// what is being activated, and only this endpoint writes that metadata.
/// </summary>
public sealed record CheckoutSession
{
    /// <summary><c>cs_…</c> — exactly the <c>stripe_session_id</c> to send later.
    /// Known <em>before</em> anyone pays, which is what lets a client store it
    /// and survive a browser closed before the redirect.</summary>
    [JsonPropertyName("session_id")] public required string SessionId { get; init; }

    /// <summary>Open in the system browser, never in a WebView (§12.3).</summary>
    [JsonPropertyName("checkout_url")] public required string CheckoutUrl { get; init; }
}

internal sealed record CreateCheckoutRequest
{
    [JsonPropertyName("product_sku")] public required string ProductSku { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
}

internal sealed record VerifyResponse
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("jwt")] public string? Jwt { get; init; }
    [JsonPropertyName("in_revoca")] public bool InRevoca { get; init; }
    [JsonPropertyName("data_scadenza_grazia")] public DateTimeOffset? GraceUntil { get; init; }
}

/// <summary>What the server actually stored, after precedence, truncation and
/// degrading — so a client can say "registered as…" without guessing.</summary>
public sealed record CustomerEcho
{
    [JsonPropertyName("email")] public string Email { get; init; } = "";
    [JsonPropertyName("company")] public string Company { get; init; } = "";
    [JsonPropertyName("vat_id")] public string VatId { get; init; } = "";

    /// <summary><c>email != ""</c> — which is how mode A and mode B are told
    /// apart, there being no column for it.</summary>
    [JsonPropertyName("registered")] public bool Registered { get; init; }
}
