namespace CryptinPersonal;

/// <summary>
/// The payment step, while the customer pays in their own browser.
///
/// There is nothing to type. The session id was handed back by the licence
/// server together with the payment page, and is already stored (see
/// <see cref="ExternalBrowserPaymentSource"/>). Until 2026-09-24 this dialog
/// asked the user to paste a <c>cs_…</c> from the confirmation page, which
/// nothing produced and which would have been refused anyway: a session has to
/// be created by the server to carry the metadata it is checked against.
///
/// "Continue" never decides whether the payment happened. That is the
/// server's call, made on <c>/api/activate</c>: an unpaid session comes back
/// <c>402</c>, the id stays pending, and the user can simply come back here.
///
/// Two shapes, one per constructor: waiting on a payment just opened, or
/// offering back one opened earlier and never turned into a licence. The second
/// answers <see cref="DialogResult.Retry"/> for "start a new payment".
/// </summary>
internal sealed class PaymentForm : Form
{
    /// <summary>A payment just opened in the browser.</summary>
    public PaymentForm(string checkoutUrl)
        : this(checkoutUrl,
               heading: "Complete the payment in your browser.",
               body: "The payment page has opened in your browser, not inside this window: a card " +
                     "form belongs in the browser you use every day, with its own address bar and " +
                     "your own password manager.\n\n" +
                     "Once it says the payment went through, come back here and press Continue.",
               offerNew: false)
    {
    }

    /// <summary>A payment opened earlier that has not produced a licence yet.</summary>
    public PaymentForm(CheckoutResume pending)
        : this(pending.CheckoutUrl,
               heading: $"You opened a payment on {pending.CreatedAt:dd/MM/yyyy HH:mm} that has not been used yet.",
               body: "If you completed it, press Continue: it will be used now, and you will not " +
                     "pay again.\n\n" +
                     "If you never paid it, you can reopen its page, or start a new payment. " +
                     "Start a new one only if you are sure: if the earlier one did go through, " +
                     "a new one charges you twice.",
               offerNew: true)
    {
    }

    private PaymentForm(string checkoutUrl, string heading, string body, bool offerNew)
    {
        Text = LicenseEndpoint.Stamp("Crypt-in — payment");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 250);

        var title = new Label
        {
            Text = heading,
            Location = new Point(16, 16),
            Size = new Size(488, 40),
            Font = new Font(Font, FontStyle.Bold),
        };

        var text = new Label
        {
            Text = body,
            Location = new Point(16, 60),
            Size = new Size(488, 130),
        };

        var reopen = new Button
        {
            Text = "Reopen payment page",
            Location = new Point(16, 206),
            Size = new Size(160, 30),
        };
        reopen.Click += (_, _) => ExternalBrowserPaymentSource.OpenInBrowser(checkoutUrl);

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(270, 206),
            Size = new Size(110, 30),
            DialogResult = DialogResult.Cancel,
        };
        CancelButton = cancel;

        var proceed = new Button
        {
            Text = "Continue",
            Location = new Point(394, 206),
            Size = new Size(110, 30),
            DialogResult = DialogResult.OK,
        };

        // Not the accept button: Enter pressed out of habit, before the browser
        // has finished, would only earn a 402. Harmless, but a wasted round trip
        // and a confusing message at the worst moment.
        Controls.AddRange([title, text, reopen, cancel, proceed]);

        if (offerNew)
        {
            ClientSize = new Size(520, 290);
            var fresh = new Button
            {
                Text = "Start a new payment",
                Location = new Point(16, 246),
                Size = new Size(160, 30),
                DialogResult = DialogResult.Retry,
            };
            Controls.Add(fresh);
        }
    }
}
