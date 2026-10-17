using CryptinSDK.Dongle;
using CryptinSDK.Licensing;

namespace CryptinPersonal;

/// <summary>
/// Anonymous activation against registered activation, then payment, then
/// <c>POST /api/activate</c>.
///
/// The two modes are one request with six optional fields, and the server tells
/// them apart by whether the email is empty. So this form does not branch on the
/// protocol — it branches on what it asks the user for, and hands the SDK either
/// an <see cref="ActivationData"/> or null.
///
/// Everything the user types is checked **before** the payment step. The server
/// will not reject any of it, by design; see <see cref="ActivationValidation"/>
/// for why that makes checking here mandatory rather than polite.
/// </summary>
internal sealed class ActivationForm : Form
{
    /// <summary>
    /// Whether this build activates as the developer beta — one knob, because
    /// nothing else in the app can tell a beta dongle from a production one.
    /// The firmware does not expose its build mode over HID, and K_attestation
    /// has the same shape either way, so the product code is what the server
    /// goes on and this line is where it is chosen.
    ///
    /// <c>true</c> asks for <see cref="ActivateRequest.BetaSku"/>, which is what
    /// gets the fixed 2027-03-31 expiry and the closed renewal, and it skips the
    /// payment step: a beta tester has no Checkout Session to paste.
    ///
    /// ⚠️ <b>Flip this to <c>false</c> before the first paid activation</b>
    /// (Kit Standard / Kit Pro, Q1 2027). Left true, a paying customer is handed
    /// a licence that expires in March 2027 and that renewal refuses. It is a
    /// <c>static readonly</c> and not a <c>const</c> on purpose: a const would
    /// make the other branch unreachable and the Release build carries no
    /// warnings.
    /// </summary>
    internal static readonly bool BetaActivation = true;

    private readonly CryptinDongle _dongle;
    private readonly IPaymentSource _payment;

    private readonly RadioButton _anonymous = new();
    private readonly RadioButton _registered = new();
    private readonly Panel _fields = new();
    private readonly Label _error = new();
    private readonly Button _continue = new();

    private readonly TextBox _email = new();
    private readonly TextBox _firstName = new();
    private readonly TextBox _lastName = new();
    private readonly TextBox _company = new();
    private readonly TextBox _vatId = new();
    private readonly CheckBox _terms = new();

    /// <summary>Set when activation succeeded.</summary>
    public ActivateResponse? Result { get; private set; }

    /// <param name="settings">The app's one shared instance, where a payment
    /// opened here is recorded as pending (see <see cref="CheckoutResume"/>).</param>
    public ActivationForm(CryptinDongle dongle, AppSettings settings, IPaymentSource? payment = null)
    {
        _dongle = dongle;
        _payment = payment ?? new ExternalBrowserPaymentSource(settings);

        Text = LicenseEndpoint.Stamp("Crypt-in — activation");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 470);

        _anonymous.Text = "Anonymous activation — no personal data";
        _anonymous.Location = new Point(16, 16);
        _anonymous.Size = new Size(520, 22);
        _anonymous.Checked = true;
        _anonymous.CheckedChanged += (_, _) => _fields.Enabled = _registered.Checked;

        var anonNote = new Label
        {
            Text = "The license stays tied to the dongle alone. We will not be able to help you " +
                   "recover it or open a ticket in your name: with no email there is nothing to link you to.",
            Location = new Point(36, 40),
            Size = new Size(500, 36),
            ForeColor = SystemColors.GrayText,
        };

        _registered.Text = "Registered activation — support and recovery by email";
        _registered.Location = new Point(16, 84);
        _registered.Size = new Size(520, 22);

        _fields.Location = new Point(36, 112);
        _fields.Size = new Size(500, 250);
        _fields.Enabled = false;

        var y = 0;
        _fields.Controls.Add(Field("Email", _email, ref y));
        _fields.Controls.Add(_email);
        _fields.Controls.Add(Field("First name", _firstName, ref y));
        _fields.Controls.Add(_firstName);
        _fields.Controls.Add(Field("Last name", _lastName, ref y));
        _fields.Controls.Add(_lastName);
        _fields.Controls.Add(Field("Company", _company, ref y));
        _fields.Controls.Add(_company);
        _fields.Controls.Add(Field("Tax code / VAT number", _vatId, ref y));
        _fields.Controls.Add(_vatId);

        // Labelled for a private buyer as much as a company: someone without a
        // VAT number uses their tax code, and a field called "VAT number" alone
        // tells them the product is not for them.
        _vatId.MaxLength = ActivationValidation.MaxVatId;

        _terms.Text = "I accept the terms of service and the privacy policy";
        _terms.Location = new Point(0, y + 6);
        _terms.Size = new Size(480, 22);
        _fields.Controls.Add(_terms);

        _error.Location = new Point(16, 372);
        _error.Size = new Size(520, 40);
        _error.ForeColor = Color.Firebrick;

        _continue.Text = "Continue";
        _continue.Location = new Point(422, 424);
        _continue.Size = new Size(114, 30);
        _continue.Click += async (_, _) => await ContinueAsync();
        AcceptButton = _continue;

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(300, 424),
            Size = new Size(114, 30),
            DialogResult = DialogResult.Cancel,
        };
        CancelButton = cancel;

        Controls.AddRange([_anonymous, anonNote, _registered, _fields, _error, _continue, cancel]);
    }

    private static Label Field(string caption, TextBox box, ref int y)
    {
        var label = new Label { Text = caption, Location = new Point(0, y), Size = new Size(150, 22) };
        box.Location = new Point(156, y - 3);
        box.Size = new Size(330, 26);
        y += 32;
        return label;
    }

    // --- the flow ---------------------------------------------------------

    private async Task ContinueAsync()
    {
        _error.Text = "";

        ActivationData? data = null;
        if (_registered.Checked)
        {
            var problem = ValidateFields();
            if (problem is not null)
            {
                _error.Text = problem;
                return;
            }

            data = new ActivationData
            {
                Email = Empty(_email.Text),
                FirstName = Empty(_firstName.Text),
                LastName = Empty(_lastName.Text),
                Company = Empty(_company.Text),
                VatId = Empty(ActivationValidation.Normalize(_vatId.Text)),
                AcceptTerms = _terms.Checked,
            };
        }

        // Before the server round trip, because a "no" here must cost nothing:
        // activation replaces whatever licence the chip is carrying.
        if (!await ConfirmReplacingExistingLicenceAsync())
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        // Only now, with everything the user can still fix already fixed.
        //
        // The beta does not go through payment at all: PaymentForm only accepts a
        // value shaped like cs_live_… / cs_test_…, so routing a beta activation
        // through it would deadlock the flow on a dialog the tester cannot
        // satisfy. Nothing is skipped that the server then asks for — the beta
        // SKU is exactly the one it does not want a session for.
        var product = BetaActivation ? ActivateRequest.BetaSku : ActivateRequest.DefaultSku;

        string? sessionId;
        if (BetaActivation)
        {
            sessionId = ActivateRequest.BetaSessionId;
        }
        else
        {
            // The mode goes into the session's metadata, so a payment whose
            // activation never arrives can still be told apart by support.
            sessionId = await _payment.AcquireSessionIdAsync(
                this, CheckoutPurpose.Activation, product, _registered.Checked ? "B" : "A",
                CancellationToken.None);
            if (sessionId is null) return;
        }

        _continue.Enabled = false;
        _error.ForeColor = SystemColors.ControlText;
        _error.Text = "Activating…";

        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            Result = await Task.Run(() => client.ActivateAsync(_dongle, sessionId, data, product));

            // Only now. Every failure above — a 402 because the browser is still
            // finishing, a dropped connection — leaves the id pending, and
            // pressing Continue again offers it back.
            if (!BetaActivation) _payment.Spent(sessionId);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e)
        {
            _error.ForeColor = Color.Firebrick;
            _error.Text = Describe(e);
            _continue.Enabled = true;
        }
    }

    /// <summary>Not called Validate: <c>ContainerControl.Validate()</c> already
    /// exists and means something else entirely in WinForms.</summary>
    private string? ValidateFields()
    {
        // Required here and not on the wire: the server accepts accept_terms
        // false and activates anyway, so the tick has to be enforced before the
        // payment or the case simply never gets recorded.
        if (!_terms.Checked) return "Registered activation requires accepting the terms.";

        if (_email.Text.Trim().Length == 0)
            return "The email is the whole point of a registered activation: without one, choose anonymous.";

        return ActivationValidation.Email(_email.Text) is { } e ? $"Email: {e}"
             : ActivationValidation.Name(_firstName.Text, "First name")
             ?? ActivationValidation.Name(_lastName.Text, "Last name")
             ?? (ActivationValidation.Company(_company.Text) is { } c ? $"Company: {c}" : null)
             ?? (ActivationValidation.VatId(_vatId.Text) is { } v ? $"Tax code / VAT number: {v}" : null);
    }

    private static string? Empty(string value) => value.Trim().Length == 0 ? null : value.Trim();

    /// <summary>
    /// Asks before replacing a licence the dongle already holds.
    ///
    /// The <b>chip</b> is asked, not the server: the two can disagree, and what
    /// activation is about to overwrite is what the chip has.
    /// <c>CMD_GET_LICENSE_STATUS</c> answers with an expiry and nothing else —
    /// four bytes of Unix seconds (<c>hid_device.cpp:244-252</c>) — so the date
    /// is all this dialog can honestly show. The product code exists only
    /// server-side, and quoting it here would blend the two sources whose
    /// blending cost a day on 2026-08-29.
    ///
    /// Unreadable state is not a refusal. A locked session, firmware without the
    /// command, a transport fault: activation goes ahead. A question nobody can
    /// answer must not stand in front of a licence someone is buying.
    /// </summary>
    private async Task<bool> ConfirmReplacingExistingLicenceAsync()
    {
        DongleLicenseStatus existing;
        try
        {
            existing = await Task.Run(_dongle.GetLicenseStatus);
        }
        catch (CryptinDongleException)
        {
            return true;
        }

        // Absent or expired: nothing worth keeping, so nothing worth asking.
        if (!existing.IsValid || existing.ExpiresAt is not { } expiry) return true;

        return AskReplace(expiry);
    }

    /// <summary>
    /// The confirmation itself, built by hand like every other dialog here so
    /// the buttons can say what they do. <c>MessageBox</c> would only offer
    /// OK/Cancel, and "OK" is a poor label for overwriting a licence.
    /// </summary>
    private bool AskReplace(DateTime expiry)
    {
        using var dialog = new Form
        {
            Text = LicenseEndpoint.Stamp("Crypt-in — replace license"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(440, 186),
        };

        var message = new Label
        {
            Location = new Point(16, 18),
            Size = new Size(408, 106),
            Text = $"""
                    This dongle already has an active license.

                    Current license: expires {expiry:dd/MM/yyyy}

                    Replace it with a new license?
                    """,
        };

        var replace = new Button
        {
            Text = "Replace",
            Location = new Point(196, 138),
            Size = new Size(114, 30),
            DialogResult = DialogResult.OK,
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(318, 138),
            Size = new Size(106, 30),
            DialogResult = DialogResult.Cancel,
        };

        // Both Enter and Esc answer "no". The safe reply to "may I overwrite the
        // licence you already have?" is the one a stray keystroke should give.
        dialog.AcceptButton = cancel;
        dialog.CancelButton = cancel;
        dialog.Controls.AddRange([message, replace, cancel]);

        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private static string Describe(Exception e) => e switch
    {
        LicenseApiException x when x.Code == LicenseErrorCodes.AlreadyActivated =>
            "This dongle already has an active license, or that payment has already been used. " +
            "One payment activates one dongle.",

        LicenseApiException x when x.Code == LicenseErrorCodes.PaymentRequired =>
            "The payment does not show as completed yet. If you have just made it, wait a few seconds and " +
            "press Continue again: the same payment is offered back, you will not pay twice.",

        // Final. Revocation follows the seed since 2026-09-22, so rebuilding
        // the dongle on another chip lands here too, and retrying cannot help.
        LicenseApiException x when x.Code == LicenseErrorCodes.IdentityRevoked =>
            "The license tied to this dongle's recovery phrase has been revoked. " +
            "Contact support: only they can lift it.",

        // Hard cap since 2026-09-23: when it arrives the beta really is full.
        LicenseApiException x when x.Code == LicenseErrorCodes.BetaLimitReached =>
            "The beta is full: every place has been taken. Nothing was changed on your dongle.",

        // LicenseClient has already retried it once, so this is the second.
        LicenseApiException x when x.Code == LicenseErrorCodes.BetaBusy =>
            "Another beta activation was being processed at the same moment. " +
            "Wait a few seconds and press Continue again.",

        // The server's problem, not the user's; no point asking them to hammer it.
        LicenseApiException x when x.Code == LicenseErrorCodes.BetaCapUnavailable =>
            "The activation service is temporarily unavailable. Please try again later.",

        LicenseApiException x when x.Code == LicenseErrorCodes.ChallengeExpired =>
            "The activation session has expired. Try again: each attempt needs a fresh one.",

        LicenseApiException { IsMalformedRequest: true } =>
            "Internal application error while composing the request. Please report it: this is not your fault.",

        LicenseApiException x when x.Code == LicenseErrorCodes.InvalidSignature =>
            "The server rejected the dongle's signature. Disconnect it, connect it again and retry.",

        PinRequiredException =>
            "The dongle was reconnected during activation: the PIN is needed again.",

        CryptinTimeoutException =>
            "The dongle did not respond in time. Disconnect it, connect it again and retry.",

        // A request that ran past LicenseClient.RequestTimeout lands here as
        // TaskCanceledException. Same advice: we could not reach the server.
        HttpRequestException or TaskCanceledException =>
            "License server unreachable. Check your connection and try again.",

        LicenseApiException x => x.Message,

        _ => $"{e.GetType().Name}: {e.Message}",
    };
}
