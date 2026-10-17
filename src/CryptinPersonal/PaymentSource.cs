using System.Diagnostics;
using CryptinSDK.Licensing;

namespace CryptinPersonal;

/// <summary>
/// Where a <c>stripe_session_id</c> comes from.
///
/// What must not happen is the app inventing the value. With the development
/// bypass on, any unused string activates a licence, so a client that generated
/// its own would work perfectly in that setup and hand out free licences the
/// day the bypass is switched off. The id has to come from a payment.
///
/// And since 2026-09-24 it has to come from <b>this</b> kind of payment: the
/// server matches the session's <c>metadata[product_sku]</c>, amount, currency
/// and mode against what is being activated, and only
/// <c>POST /api/stripe/create-checkout-session</c> writes that metadata. A
/// Payment Link from the Dashboard, or a session made by hand, is refused with
/// the same <c>402 PAYMENT_REQUIRED</c> as an unpaid one.
/// </summary>
internal interface IPaymentSource
{
    /// <summary>
    /// Returns a session id for <paramref name="productSku"/>, or null if the
    /// user gave up or no payment could be opened (in which case they have
    /// already been told why).
    /// </summary>
    Task<string?> AcquireSessionIdAsync(
        IWin32Window owner, CheckoutPurpose purpose, string productSku, string mode, CancellationToken ct);

    /// <summary>
    /// The server has just turned this session into a licence, so it is no
    /// longer worth keeping. Call it on success and on nothing else.
    ///
    /// Not on <c>ALREADY_ACTIVATED</c>, tempting as it is. <c>/api/activate</c>
    /// answers that code in five places and only one of them means "this payment
    /// was already used"; the others mean the dongle or the identity already has
    /// a licence, and the payment is still good. They differ only in the prose,
    /// and switching on prose is what <see cref="LicenseApiException.Code"/>
    /// exists to prevent. Keeping a spent id costs a second 409 later; dropping a
    /// good one costs the customer their money.
    /// </summary>
    void Spent(string sessionId);
}

/// <summary>
/// Asks the licence server to open a Checkout Session, stores the id, and shows
/// the payment page in the user's own browser.
///
/// Deliberately not an embedded browser. Payment pages belong in the browser the
/// user already trusts, with its own address bar and its own password manager;
/// an embedded WebView showing a card form is the shape of a phishing page, and
/// Stripe blocks some payment methods and 3-D Secure redirects inside one anyway
/// (contract §12.3).
///
/// The id is known <b>before</b> the payment — the server returns it together
/// with the URL — so nothing has to travel back from the browser. That is what
/// retired the paste box this dialog used to have, and what makes the pending
/// record possible: it is written before the browser opens.
/// </summary>
internal sealed class ExternalBrowserPaymentSource : IPaymentSource
{
    private readonly AppSettings _settings;

    public ExternalBrowserPaymentSource(AppSettings settings) => _settings = settings;

    public async Task<string?> AcquireSessionIdAsync(
        IWin32Window owner, CheckoutPurpose purpose, string productSku, string mode, CancellationToken ct)
    {
        // A payment already opened for the same thing comes first. Starting a
        // fresh one without asking is how a customer who paid and lost the tab
        // gets charged twice.
        if (_settings.PendingCheckout is { } pending
            && pending.ProductSku == productSku && pending.Purpose == purpose)
        {
            using var resume = new PaymentForm(pending);
            switch (resume.ShowDialog(owner))
            {
                case DialogResult.OK: return pending.SessionId;
                case DialogResult.Retry: break;          // start a new one, below
                default: return null;
            }
        }

        CheckoutSession session;
        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            session = await client.CreateCheckoutSessionAsync(productSku, mode, ct);
        }
        catch (Exception e)
        {
            MessageBox.Show(owner, Describe(e), "Payment", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        // Before the browser. From here on the customer may pay at any moment,
        // and whatever happens to the app afterwards, this is what lets them
        // come back to the licence without support.
        _settings.PendingCheckout = new CheckoutResume
        {
            SessionId = session.SessionId,
            ProductSku = productSku,
            Purpose = purpose,
            CheckoutUrl = session.CheckoutUrl,
            CreatedAt = DateTimeOffset.Now,
        };
        _settings.Save();

        OpenInBrowser(session.CheckoutUrl);

        using var wait = new PaymentForm(session.CheckoutUrl);
        return wait.ShowDialog(owner) == DialogResult.OK ? session.SessionId : null;
    }

    public void Spent(string sessionId) => _settings.ClearPendingCheckout(sessionId);

    /// <summary>Nothing has been charged in any of these: the session was never
    /// opened, so there was nothing to pay.</summary>
    private static string Describe(Exception e) => e switch
    {
        LicenseApiException x when x.Code == LicenseErrorCodes.StripeNotConfigured =>
            "Online payment is not open yet. Nothing has been charged.",

        LicenseApiException x when x.Code == LicenseErrorCodes.StripeError =>
            "The payment provider could not be reached. Nothing has been charged: try again in a minute.",

        LicenseApiException x when x.Code is LicenseErrorCodes.InvalidSku or LicenseErrorCodes.InvalidMode =>
            "Internal application error while opening the payment. Please report it: this is not your fault. " +
            "Nothing has been charged.",

        HttpRequestException or TaskCanceledException =>
            "License server unreachable. Check your connection and try again. Nothing has been charged.",

        _ => $"{e.Message}\n\nNothing has been charged.",
    };

    internal static void OpenInBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
