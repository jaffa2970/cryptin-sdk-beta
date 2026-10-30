// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Net;
using System.Text.Json;

namespace CryptinSDK.Licensing;

/// <summary>
/// An error from the licence server.
///
/// There are two error shapes and a client has to handle both. The <c>/api/*</c>
/// handlers answer <c>{"error": …, "code": …}</c>, but a malformed body is
/// rejected by Pydantic *before* it reaches the handler, in FastAPI's own shape:
/// <c>{"detail": [{"type": "missing", "loc": [...]}]}</c>. So 422 means the
/// request never got as far as the endpoint, and it is our bug, not the user's.
/// </summary>
public sealed class LicenseApiException : Exception
{
    private LicenseApiException(
        string message, HttpStatusCode status, string code, string? reason,
        string? nextAvailable = null, bool? sameLicence = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Reason = reason;
        NextAvailable = nextAvailable;
        SameLicence = sameLicence;
    }

    public HttpStatusCode Status { get; }

    /// <summary>
    /// The value to switch on. Never switch on <see cref="Reason"/> or on the
    /// message: extra fields are additive and only decide which text to show.
    /// Empty when the server answered in a shape we do not recognise.
    /// </summary>
    public string Code { get; }

    /// <summary>Accessory detail, e.g. <c>CLONE_OR_UNREGISTERED</c> alongside
    /// <c>ATTESTATION_MISMATCH</c>. Null when absent.</summary>
    public string? Reason { get; }

    /// <summary>
    /// The <c>next_available</c> of a 429 from <c>register_backup</c>, exactly as
    /// the server wrote it. Null when the body did not carry one.
    ///
    /// Kept because it cannot be worked out here: the annual slot is calendar
    /// year, the server owns the clock, and <c>/api/verify</c> reports neither
    /// the count nor the unlock date. Both normative documents say to show it
    /// rather than recompute it (CRYPTIN_ARCHITETTURA_CS.md §5,
    /// CRYPTIN_SDK_PROMPT_SESSIONE.md §5) -- and until 2026-09-03 this parser
    /// read only code/reason/error, so the one number that exists nowhere else
    /// was dropped on the floor while the app told the user the server had sent
    /// it. Measured against production that day, on a licence whose slot was
    /// genuinely spent.
    ///
    /// A string, not a DateTimeOffset: displaying what arrived is the whole
    /// point, and a format we did not expect must still reach the user rather
    /// than becoming a null.
    /// </summary>
    public string? NextAvailable { get; }

    /// <summary>
    /// The <c>stessa_licenza</c> of a 409 <c>ALREADY_REGISTERED</c>: whether the
    /// chip that was refused is a backup of <em>this</em> licence or of somebody
    /// else's. Null when the body did not carry one.
    ///
    /// It changes what the user should do, not just the wording. True means they
    /// already own a working answer and do not need this wizard at all — the
    /// backup is authorised, they just plug it in — and no recovery was spent.
    /// False means the chip belongs to another licence and a different one has
    /// to be used.
    ///
    /// Read here rather than sniffed out of the message for the reason
    /// <see cref="NextAvailable"/> carries in full: on 2026-09-03 this parser
    /// took only code/reason/error, and the app announced a value it had thrown
    /// away. Italian on the wire because the server spells it that way; the
    /// property is not, because the rest of this file is not.
    /// </summary>
    public bool? SameLicence { get; }

    /// <summary>422 — the body was malformed and never reached the handler.</summary>
    public bool IsMalformedRequest => Status == HttpStatusCode.UnprocessableEntity;

    internal static LicenseApiException FromResponse(HttpStatusCode status, string body)
    {
        string code = "", reason = "", error = "";
        string? nextAvailable = null;
        bool? sameLicence = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("detail", out var detail))
            {
                // FastAPI's validation shape. Summarised rather than parsed into
                // a model: it means the client built a bad request, and the only
                // consumer of the text is whoever has to fix that.
                return new LicenseApiException(
                    $"HTTP {(int)status}: malformed request, it never reached the endpoint — {detail}",
                    status, "", null);
            }

            if (root.TryGetProperty("code", out var c)) code = c.GetString() ?? "";
            if (root.TryGetProperty("reason", out var r)) reason = r.GetString() ?? "";
            if (root.TryGetProperty("error", out var e)) error = e.GetString() ?? "";

            // Whatever shape it arrives in. A date is usually a JSON string, but
            // a server that sends a number or a nested value must not turn into
            // a null here: this field is the only copy of that information.
            if (root.TryGetProperty("next_available", out var n))
                nextAvailable = n.ValueKind == JsonValueKind.String ? n.GetString() : n.ToString();

            // Only a real boolean. Anything else stays null and the caller falls
            // back on wording that covers both cases, which is what it said
            // before this field existed.
            if (root.TryGetProperty("stessa_licenza", out var sl)
                && sl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                sameLicence = sl.GetBoolean();
        }
        catch (JsonException)
        {
            return new LicenseApiException(
                $"HTTP {(int)status}: response was not JSON — {Excerpt(body)}", status, "", null);
        }

        var text = string.IsNullOrEmpty(error) ? Excerpt(body) : error;
        var suffix = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";
        return new LicenseApiException(
            $"HTTP {(int)status} {code}: {text}{suffix}", status, code,
            string.IsNullOrEmpty(reason) ? null : reason, nextAvailable, sameLicence);
    }

    private static string Excerpt(string body)
        => body.Length <= 300 ? body : body[..300] + "…";
}

/// <summary>
/// The error codes the dongle endpoints return. Constants rather than an enum:
/// an unknown code from a newer server must stay readable instead of failing to
/// parse, and this way the compiler still catches a typo in our own comparisons.
/// </summary>
public static class LicenseErrorCodes
{
    public const string ChallengeExpired = "CHALLENGE_EXPIRED";
    public const string InvalidSignature = "INVALID_SIGNATURE";
    public const string PaymentRequired = "PAYMENT_REQUIRED";
    public const string AlreadyActivated = "ALREADY_ACTIVATED";
    public const string LicenseNotFound = "LICENSE_NOT_FOUND";
    public const string AttestationMismatch = "ATTESTATION_MISMATCH";
    public const string LicenseRevoked = "LICENSE_REVOKED";
    public const string LicenseExpired = "LICENSE_EXPIRED";
    public const string SameDevice = "SAME_DEVICE";
    public const string InvalidReason = "INVALID_REASON";
    public const string RecoveryRateLimit = "RECOVERY_RATE_LIMIT";
    public const string LicenseNotActive = "LICENSE_NOT_ACTIVE";
    public const string SameAsMain = "SAME_AS_MAIN";
    public const string AlreadyRegistered = "ALREADY_REGISTERED";
    public const string BackupLimitReached = "BACKUP_LIMIT_REACHED";

    /// <summary>403 from <c>/api/activate</c> on the beta SKU. Since
    /// 2026-09-23 the cap is hard, so this is final: the beta is full.</summary>
    public const string BetaLimitReached = "BETA_LIMIT_REACHED";

    // The four below came out of the server's September fixes and were added to
    // the contract on 2026-09-24. Before that a client written to the document
    // saw them as unknown codes, where each one has a right thing to do.

    /// <summary>403 from <c>/api/activate</c>: the <em>identity</em> — the seed —
    /// holds a revoked licence. Since 2026-09-22 revocation follows the owner,
    /// not the chip, so rebuilding the dongle on another chip gets here too.
    /// Final; only the admin panel lifts it.</summary>
    public const string IdentityRevoked = "IDENTITY_REVOKED";

    /// <summary>503 from <c>/api/activate</c>, beta SKU only: another activation
    /// was crossing the cap at that instant and the lock is NOWAIT. The one code
    /// here that wants an automatic retry — <see cref="LicenseClient"/> does it,
    /// once.</summary>
    public const string BetaBusy = "BETA_BUSY";

    /// <summary>503 from <c>/api/activate</c>, beta SKU only: the server cannot
    /// check its own cap and refuses rather than overshoot. A server problem, not
    /// the user's, and not something a retry loop fixes.</summary>
    public const string BetaCapUnavailable = "BETA_CAP_UNAVAILABLE";

    /// <summary>409 from recovery, renew and register_backup: two concurrent
    /// requests on the same licence, the database serialised them and this one
    /// lost. Was a 500 before a September fix. <see cref="LicenseClient"/>
    /// retries it once; a second one is a real error.</summary>
    public const string ConcurrentRetry = "CONCURRENT_RETRY";

    /// <summary>403 from <c>/api/dongle/renew</c>: a beta licence ends on the
    /// campaign's fixed date and is never extended. The server checks it before
    /// the payment, but the payment happened in the browser before that call —
    /// so a client has to know the SKU and refuse <em>before</em> opening a
    /// checkout, or this code arrives after the money.</summary>
    public const string BetaNotRenewable = "BETA_NOT_RENEWABLE";

    // POST /api/dongle/rekey/start and /finish (G2, 2026-10-30). Names checked
    // against the licence server's report of the same day (Tartufo 41afc3fd,
    // docs/CRYPTIN_DLL_RICOGNIZIONE_API.md §14), written while the endpoints
    // were still in its working tree and on no deployed server.

    /// <summary>400 from <c>rekey/finish</c>: no open ticket with that value —
    /// never issued, already used, or replaced by a later <c>rekey/start</c>.</summary>
    public const string TicketInvalid = "TICKET_INVALID";

    /// <summary>400 from <c>rekey/finish</c>: the ticket ran out. Nothing moved;
    /// the old words on a recovered chip can start again.</summary>
    public const string TicketExpired = "TICKET_EXPIRED";

    /// <summary>409 from <c>rekey/finish</c>: the new identity already holds a
    /// licence. The chip was not reflashed, or it was activated in between.</summary>
    public const string IdentityInUse = "IDENTITY_IN_USE";

    /// <summary>429 from <c>rekey/start</c> as well as <c>rekey/finish</c>: one
    /// rekey per licence per 365 days, counted apart from the three transfers.
    /// <c>next_available</c> says when. Checked at start on purpose, so a wizard
    /// stops before it erases a working dongle for nothing.</summary>
    public const string RekeyRateLimit = "REKEY_RATE_LIMIT";

    /// <summary>
    /// The <c>reason</c> (not a code) that comes with <c>409 ALREADY_ACTIVATED</c>
    /// from <c>/api/activate</c> when the seed was retired by a rekey. Final;
    /// only the admin panel lifts it.
    ///
    /// Why it exists (licence server, decided by Tommy on 2026-10-30): a rekey
    /// drops the registered backups, but the backup chip still carries the old
    /// seed, and with the beta code it could activate a second free licence.
    /// Every rekey would have handed one out.
    /// </summary>
    public const string ReasonIdentityRekeyed = "IDENTITY_REKEYED";

    // POST /api/stripe/create-checkout-session (contract §12.4).

    /// <summary>400 — unknown or empty SKU. Our bug, not the user's.</summary>
    public const string InvalidSku = "INVALID_SKU";

    /// <summary>400 — mode is not A or B. Our bug, not the user's.</summary>
    public const string InvalidMode = "INVALID_MODE";

    /// <summary>503 — no Stripe key on the server, or the development bypass is
    /// on. A configuration, not a transient fault: not retryable.</summary>
    public const string StripeNotConfigured = "STRIPE_NOT_CONFIGURED";

    /// <summary>502 — Stripe unreachable or answering oddly. Retryable, by hand.</summary>
    public const string StripeError = "STRIPE_ERROR";
}
