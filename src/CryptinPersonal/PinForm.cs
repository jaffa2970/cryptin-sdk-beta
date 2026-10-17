using CryptinSDK.Dongle;

namespace CryptinPersonal;

/// <summary>
/// Six masked digits, shown on insertion.
///
/// The wording is constrained by the protocol, not by taste. It says the PIN is
/// derived from the seed, because a user who thinks they chose it will go
/// looking for a way to change it. And after a failure it says "wrong PIN" and
/// nothing else: the attempt counter lives in NVS on the dongle and survives
/// app restarts and power cycles, so this process cannot know how many are left
/// and must not pretend to.
/// </summary>
internal sealed class PinForm : Form
{
    private readonly CryptinDongle _dongle;
    private readonly TextBox _pin = new();
    private readonly Label _message = new();
    private readonly Button _ok = new();
    private readonly LinkLabel _recovery = new();

    public PinForm(CryptinDongle dongle, string usbSerial)
    {
        _dongle = dongle;

        Text = "Crypt-in — PIN";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(380, 210);

        var title = new Label
        {
            Text = $"Dongle {usbSerial}",
            Location = new Point(16, 14),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
        };

        var explain = new Label
        {
            Text = "Your PIN comes from your seed. You do not choose it.",
            Location = new Point(16, 38),
            Size = new Size(348, 20),
        };

        _pin.Location = new Point(16, 66);
        _pin.Size = new Size(120, 26);
        _pin.MaxLength = HidConstants.PinLength;
        _pin.UseSystemPasswordChar = true;
        // Kills cut/copy/paste and the context menu on this field. The useful
        // direction is outward: it stops six digits being lifted out of the box
        // into the clipboard, where they would outlive this dialog entirely.
        _pin.ShortcutsEnabled = false;
        _pin.Font = new Font(FontFamily.GenericMonospace, 12f);
        _pin.TextChanged += (_, _) => _ok.Enabled = _pin.TextLength == HidConstants.PinLength;

        _ok.Text = "Unlock";
        _ok.Location = new Point(152, 65);
        _ok.Size = new Size(96, 28);
        _ok.Enabled = false;
        _ok.Click += async (_, _) => await VerifyAsync();
        AcceptButton = _ok;

        _message.Location = new Point(16, 102);
        _message.Size = new Size(348, 40);
        _message.ForeColor = Color.Firebrick;

        // Opens the wizard rather than describing a serial terminal. The old
        // dialog told the truth and was still useless: "open a serial console at
        // 115200 baud" is not an instruction most owners of this product can act
        // on, and it was the only thing standing between a locked-out dongle and
        // a support case.
        _recovery.Text = "Dongle locked out? Recover with 24 words";
        _recovery.Location = new Point(16, 150);
        _recovery.AutoSize = true;
        _recovery.LinkClicked += (_, _) => OpenRecoveryWizard();

        Controls.AddRange([title, explain, _pin, _ok, _message, _recovery]);
    }

    /// <summary>
    /// Opens the recovery wizard straight at the port picker.
    ///
    /// Anyone who reaches this link is already here because of a lockout, so the
    /// screen explaining what a lockout is would be a step they have to click
    /// past. The dongle must be released first: the wizard talks to the same
    /// chip over its UART bridge, and the HID handle this form holds is of no
    /// use to it.
    /// </summary>
    private void OpenRecoveryWizard()
    {
        _dongle.Disconnect();

        using var wizard = new SerialRecoveryWizard(startAtPortSelection: true);
        if (wizard.ShowDialog(this) != DialogResult.OK) return;

        // The chip needs a power cycle before it will answer over USB again, so
        // there is nothing useful left on this form.
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private async Task VerifyAsync()
    {
        _ok.Enabled = false;
        _pin.Enabled = false;
        _message.ForeColor = SystemColors.ControlText;
        _message.Text = "Checking…";

        // char[], not string, and wiped in the finally. A string cannot be
        // wiped — it is immutable, and the GC may copy it while compacting, so
        // every copy survives until collection with no way to overwrite any of
        // them. This is a reduction, NOT an elimination: TextBox.Text
        // materialises a string by construction, and the control keeps the text
        // in its own buffer, so copies this code never sees still exist. What it
        // buys is that the array actually handed to the SDK is zeroed the moment
        // the exchange ends, instead of being left for the collector.
        var pin = _pin.Text.ToCharArray();
        PinResult result;
        try
        {
            result = await Task.Run(() => _dongle.VerifyPin(pin.AsSpan()));
        }
        catch (CryptinDongleException e)
        {
            _message.ForeColor = Color.Firebrick;
            _message.Text = e.Message;
            _pin.Enabled = true;
            return;
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
            // Drops the control's own copy as well; Clear() is what releases the
            // buffer behind .Text, and it runs on every path including failure.
            _pin.Clear();
        }

        switch (result)
        {
            case PinResult.Ok:
                DialogResult = DialogResult.OK;
                Close();
                return;

            case PinResult.Blocked:
                // Terminal until a seed recovery. Offering another attempt here
                // would be a lie: a correct PIN does not clear this. The box is
                // disabled for the same reason -- leaving it typeable invites
                // three more attempts against a counter that no longer moves.
                _message.ForeColor = Color.Firebrick;
                _message.Text = "Dongle locked out. Recovery requires your " +
                                "24-word seed and the UART cable.";

                // The only way out of this dialog is now the recovery link, so
                // it stops being a footnote: bold, and the PIN box goes away
                // rather than inviting a fourth attempt against a counter that
                // no longer moves.
                _pin.Enabled = false;
                _ok.Enabled = false;
                _recovery.Font = new Font(_recovery.Font, FontStyle.Bold);
                _recovery.Focus();
                return;

            case PinResult.NotReady:
                _message.ForeColor = Color.Firebrick;
                _message.Text = "The dongle has not loaded its keys yet. Try again in a moment.";
                _pin.Enabled = true;
                return;

            default:
                // "Wrong PIN", and nothing about attempts remaining. The counter
                // lives in NVS on the dongle and survives app restarts, so a
                // fresh instance cannot know how many have been spent -- and a
                // guess would be worse than silence.
                _message.ForeColor = Color.Firebrick;
                _message.Text = "Wrong PIN";
                _pin.Enabled = true;
                _pin.Focus();
                return;
        }
    }
}
