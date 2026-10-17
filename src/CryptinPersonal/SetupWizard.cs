using CryptinSDK.Dongle;
using CryptinSDK.Licensing;
using CryptinSDK.Updater;

namespace CryptinPersonal;

/// <summary>
/// First run, end to end: blank chip or existing dongle, PIN, licence, backup.
///
/// It is the piece that joins everything else together, so it is also the piece
/// that has to be honest about what is not there yet. One step still depends on
/// something that can be absent -- the flash needs <c>CryptinUpdater.exe</c> --
/// and it says so plainly rather than dead-ending, because a wizard that stops
/// on a missing component is worse than one that admits the gap.
/// </summary>
internal sealed class SetupWizard : Form
{
    private readonly CryptinDongle _dongle;
    private readonly AppSettings _settings;
    private readonly UpdaterProcess _updater = new();

    private readonly Panel _body = new();
    private readonly Label _heading = new();
    private readonly Label _detail = new();
    private readonly Button _back = new();
    private readonly Button _next = new();

    private int _step = 1;
    private LicenseStatus? _licence;

    /// <summary>Set when an activation's token did not reach the chip. Null
    /// means either that no activation happened here, or that it arrived.</summary>
    private string? _tokenProblem;

    public SetupWizard(CryptinDongle dongle, AppSettings settings)
    {
        _dongle = dongle;
        _settings = settings;

        Text = "Crypt-in — setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(580, 420);

        _heading.Location = new Point(20, 18);
        _heading.Size = new Size(540, 26);
        _heading.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);

        _detail.Location = new Point(20, 48);
        _detail.Size = new Size(540, 60);

        _body.Location = new Point(20, 112);
        _body.Size = new Size(540, 244);

        _back.Text = "Back";
        _back.Location = new Point(322, 370);
        _back.Size = new Size(114, 30);
        _back.Click += (_, _) => Show(_step - 1);

        _next.Text = "Next";
        _next.Location = new Point(446, 370);
        _next.Size = new Size(114, 30);
        _next.Click += async (_, _) => await NextAsync();

        Controls.AddRange([_heading, _detail, _body, _back, _next]);
        Show(1);
    }

    // --- step plumbing ----------------------------------------------------

    private void Show(int step)
    {
        _step = Math.Clamp(step, 1, 5);
        _body.Controls.Clear();
        _back.Enabled = _step > 1;
        _next.Text = _step == 5 ? "Finish" : "Next";

        switch (_step)
        {
            case 1: BuildHaveDongle(); break;
            case 2: BuildFlash(); break;
            case 3: BuildInsert(); break;
            case 4: BuildLicence(); break;
            case 5: BuildBackup(); break;
        }
    }

    private async Task NextAsync()
    {
        switch (_step)
        {
            case 1:
                // The only branch in the wizard: an existing dongle skips flashing.
                Show(_hasDongle!.Checked ? 3 : 2);
                return;

            case 2:
                Show(3);
                return;

            case 3:
                if (!await UnlockAsync()) return;
                Show(4);
                return;

            case 4:
                Show(5);
                return;

            default:
                DialogResult = DialogResult.OK;
                Close();
                return;
        }
    }

    // --- step 1: do you already have a dongle? ----------------------------

    private RadioButton? _hasDongle;

    private void BuildHaveDongle()
    {
        _heading.Text = "Do you already have a Crypt-in dongle?";
        _detail.Text = "If you have one that is already programmed, we go straight to plugging it in. " +
                       "If you have a blank chip, the next step prepares it.";

        _hasDongle = new RadioButton
        {
            Text = "Yes, I already have a dongle",
            Location = new Point(0, 0),
            Size = new Size(500, 24),
            Checked = true,
        };
        var no = new RadioButton
        {
            Text = "No, I have a new chip to program",
            Location = new Point(0, 30),
            Size = new Size(500, 24),
        };

        _body.Controls.AddRange([_hasDongle, no]);
    }

    // --- step 2: flash a blank chip ---------------------------------------

    private ComboBox? _ports;
    private ProgressBar? _progress;

    private void BuildFlash()
    {
        _heading.Text = "Program the chip";
        _detail.Text = "Flashing does NOT go through the socket you will use afterwards. Connect the " +
                       "second cable, the one on the socket marked UART: in normal use the dongle is " +
                       "HID only and exposes no COM port, and that is as it should be.";

        _ports = new ComboBox
        {
            Location = new Point(0, 6),
            Size = new Size(380, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        var refresh = new Button { Text = "Refresh", Location = new Point(392, 5), Size = new Size(100, 28) };
        refresh.Click += (_, _) => LoadPorts();

        // DropDown alone does not do it, and that was measured on 2026-09-03
        // rather than reasoned: the list window is built and sized before
        // DropDown is raised, so refilling Items there shows the *previous*
        // contents and only the open after that is right. The user saw exactly
        // that -- "it works only if I press Refresh".
        //
        // So the reload hangs off the gestures that come strictly BEFORE the
        // list exists: the pointer arriving on the box, and focus arriving on
        // it. DropDown stays as a backstop, since by then the Items are already
        // correct for the open after. SerialPorts.Programmable() is a registry read, so
        // running it on hover costs nothing worth measuring.
        _ports.MouseEnter += (_, _) => LoadPorts();
        _ports.Enter += (_, _) => LoadPorts();
        _ports.DropDown += (_, _) => LoadPorts();

        // Says out loud what the filtered list leaves out. Without it, a user
        // with a working dongle on the native socket sees an empty box and is
        // entitled to conclude the app is broken.
        var uartNote = new Label
        {
            Location = new Point(0, 38),
            Size = new Size(500, 48),
            ForeColor = SystemColors.GrayText,
            Text = SerialPorts.OnlyUartNote,
        };

        var flash = new Button { Text = "Program", Location = new Point(0, 90), Size = new Size(130, 30) };
        flash.Click += async (_, _) => await FlashAsync(flash);

        _progress = new ProgressBar { Location = new Point(0, 128), Size = new Size(492, 22) };

        var note = new Label { Location = new Point(0, 158), Size = new Size(500, 86) };
        if (!_updater.IsInstalled)
        {
            note.ForeColor = Color.Firebrick;
            note.Text = "CryptinUpdater.exe is not installed, so nothing can be programmed from here. " +
                        "It is a separate signed executable, distributed on its own.\n\n" +
                        "If your dongle is already programmed, go back and choose “Yes”.";
            flash.Enabled = false;
        }
        else
        {
            note.ForeColor = SystemColors.GrayText;
            note.Text = "After flashing you can unplug the UART cable and use only the native socket.";
        }

        _body.Controls.AddRange([_ports, refresh, uartNote, flash, _progress, note]);
        LoadPorts();
    }

    private void LoadPorts()
    {
        var chosen = (_ports!.SelectedItem as SerialPortInfo)?.PortName;

        var ports = SerialPorts.Programmable();
        _ports.Items.Clear();
        foreach (var port in ports) _ports.Items.Add(port);

        if (ports.Count == 0)
        {
            // Almost always the cable, and saying which cable saves a support
            // round trip: the OTG socket never produces a COM port. NoPortText
            // separates that from the other empty state -- ports on the machine,
            // none of them ours -- because the two send the user elsewhere.
            //
            // Left ENABLED on purpose, though there is nothing to choose. A
            // disabled box cannot be opened, and opening it is what reloads the
            // list -- so disabling it here would make the empty state the one
            // state the user cannot get out of without finding the Refresh
            // button.
            _ports.Items.Add(SerialPorts.NoPortText());
            _ports.SelectedIndex = 0;
            _ports.Enabled = true;
            return;
        }

        _ports.Enabled = true;

        // A reload must not silently move the choice: on a machine with several
        // adapters the right port is not always the first one, and the user may
        // have already found it.
        var at = IndexOfPort(_ports, chosen);
        _ports.SelectedIndex = at >= 0 ? at : 0;
    }

    /// <summary>Position of <paramref name="portName"/> in the box, or -1.</summary>
    private static int IndexOfPort(ComboBox box, string? portName)
    {
        if (portName is null) return -1;
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is SerialPortInfo info
                && info.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private async Task FlashAsync(Button flash)
    {
        // The placeholder is selectable now that the box stays enabled, so this
        // is reachable -- and returning in silence from a button press is the
        // habit this app is trying to lose.
        if (_ports?.SelectedItem is not SerialPortInfo port)
        {
            MessageBox.Show(this,
                "No COM port to program.\n\n" +
                "Connect the second cable, the one on the socket marked UART, then open the list " +
                "above: it re-reads the ports every time it opens.",
                "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // The list can be stale -- it is filled by UI events, and on 2026-09-03
        // a port was unplugged and Program still ran against it. Nothing was
        // written that time, because the port was simply gone. The case that
        // matters is the other one: had a different board taken that COM number
        // in the meantime, this would have flashed the wrong chip and destroyed
        // a seed. So the port is looked up again here, where it costs a registry
        // read and does not depend on which event WinForms chose to raise.
        if (!SerialPorts.Programmable().Any(p => p.PortName.Equals(port.PortName, StringComparison.OrdinalIgnoreCase)))
        {
            LoadPorts();
            MessageBox.Show(this,
                $"{port.PortName} is not there any more.\n\n" +
                "Nothing was written. Connect the cable to the socket marked UART, pick the port " +
                "again and press Program.",
                "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        flash.Enabled = false;
        _progress!.Value = 0;
        try
        {
            var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
            var result = await _updater.FirstFlashAsync(port.PortName, progress);

            // Nothing has been written yet, which is the whole point of asking
            // here: after the write there is no seed left to warn about.
            if (result == FlashResult.AlreadyProvisioned && ConfirmOverwrite(port.PortName))
            {
                _progress.Value = 0;
                result = await _updater.FirstFlashAsync(port.PortName, progress, force: true);
            }

            MessageBox.Show(this, result switch
            {
                FlashResult.Success => "Chip programmed. You can now unplug the UART cable.",
                FlashResult.PortNotFound => _updater.LastError is { } gone
                    ? $"{port.PortName} is not responding: {gone}"
                    : $"{port.PortName} is not responding. Check the cable and try again.",
                FlashResult.SignatureInvalid =>
                    "The firmware signature is not valid: the downloaded file is not the one from lake8.dev. " +
                    "Nothing was written to the chip.",
                FlashResult.AlreadyProvisioned =>
                    "Nothing was written: the chip is not blank, and you chose to leave it alone.",
                // The updater's own reason, as the transfer and backup wizards
                // already show it. The generic "try another USB port" sent the
                // reader after the cable when the updater had refused the image
                // before touching the chip (bench, 2026-10-14).
                _ => _updater.LastError is { } reason
                    ? $"Programming failed. {reason}"
                    : "Programming failed. Try again; if it persists, try another USB port.",
            }, "Programming", MessageBoxButtons.OK,
               result == FlashResult.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (UpdaterNotInstalledException e)
        {
            MessageBox.Show(this, e.Message, "Updater missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            flash.Enabled = true;
        }
    }

    /// <summary>
    /// The last chance to stop, and the only one there is.
    ///
    /// Programming writes the merged image at 0x0, and 20K of that image is 0xFF
    /// padding sitting exactly on the nvs partition — so it erases the keys.
    /// Measured 2026-08-31: a reflash of an already-provisioned chip minted it a
    /// new seed and re-opened the handover, silently. The 24 words are no
    /// insurance, because K_attestation is not derived from them.
    ///
    /// Defaulting to "No" is deliberate: someone hammering Enter through a wizard
    /// must not destroy a dongle by rhythm.
    ///
    /// The updating paragraph is there because this is where an update goes wrong
    /// (beta update test, 2026-10-12): a forced flash from here comes back with a
    /// new set of 24 words and no licence, while Transfer license does the same
    /// write and keeps both. It shows for a vendor chip too -- telling the two
    /// apart would mean deciding on the updater's error text, which is for people.
    /// </summary>
    private bool ConfirmOverwrite(string portName)
        => MessageBox.Show(this,
            $"The chip on {portName} already holds data where a Crypt-in dongle keeps its keys.\n\n" +
            "If this is a dongle that has already been set up, programming it destroys its " +
            "recovery seed and the identity the license server knows it by. The 24 words cannot " +
            "bring that identity back, and the license stays with the old one.\n\n" +
            "Updating the firmware of a dongle you already use? Answer No, close this wizard and " +
            "use Transfer license in the main window, with your 24 words at hand: it writes the " +
            "new firmware, puts your words back and moves your license. Done from here, the " +
            "dongle comes back with NEW words and no license.\n\n" +
            "If it is a new chip that simply came with the manufacturer's firmware, there is " +
            "nothing to lose.\n\n" +
            "Program it anyway?",
            "This chip is not blank", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    // --- step 3: insert the dongle and unlock it --------------------------

    private Label? _waiting;
    private System.Windows.Forms.Timer? _poll;

    private void BuildInsert()
    {
        _heading.Text = "Plug in the dongle";
        _detail.Text = "Connect the dongle to the native USB socket — one cable is enough. " +
                       "Windows can take a few seconds to recognize it.";

        _waiting = new Label { Location = new Point(0, 6), Size = new Size(500, 48) };
        _body.Controls.Add(_waiting);

        _poll?.Stop();
        _poll = new System.Windows.Forms.Timer { Interval = 800 };
        _poll.Tick += (_, _) => UpdateWaiting();
        _poll.Start();
        UpdateWaiting();
    }

    private void UpdateWaiting()
    {
        if (_step != 3) { _poll?.Stop(); return; }

        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0)
        {
            _waiting!.ForeColor = SystemColors.GrayText;
            _waiting.Text = "Waiting for the dongle…";
            _next.Enabled = false;
            return;
        }

        _waiting!.ForeColor = SystemColors.ControlText;
        _waiting.Text = attached.Count == 1
            ? $"Found: {attached[0]}"
            : $"Found {attached.Count} dongles. {attached[0]} will be used.";
        _next.Enabled = true;
    }

    private async Task<bool> UnlockAsync()
    {
        _poll?.Stop();

        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0) return false;

        if (!_dongle.IsConnected && !_dongle.Connect(attached[0], waitMs: 5000))
        {
            MessageBox.Show(this, "The dongle is visible but will not open. Disconnect it and connect it again.",
                "Dongle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Before the PIN, because on a new dongle the PIN is not something the
        // user could possibly know yet -- it is derived from a seed that was
        // minted seconds ago. Asking first would be asking for a secret we have
        // not handed over.
        if (!ProvisioningHandoverUi.EnsureHandedOver(this, _dongle)) return false;

        using var pin = new PinForm(_dongle, _dongle.UsbSerial);
        if (pin.ShowDialog(this) != DialogResult.OK) return false;

        await Task.CompletedTask;
        return true;
    }


    // --- step 4: where the licence stands ---------------------------------

    private void BuildLicence()
    {
        _heading.Text = "License";
        _detail.Text = "Checking…";
        _ = LoadLicenceAsync();
    }

    private async Task LoadLicenceAsync()
    {
        _next.Enabled = false;
        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            _licence = await Task.Run(() => client.VerifyAsync(_dongle));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _detail.Text = "License server unreachable.";
            ShowLicenceBody("You can continue: the check is retried on the next start, " +
                            "and files already encrypted remain readable regardless.", Color.DarkOrange);
            _next.Enabled = true;
            return;
        }
        catch (Exception e)
        {
            _detail.Text = "Check failed.";
            ShowLicenceBody(e.Message, Color.Firebrick);
            _next.Enabled = true;
            return;
        }

        _next.Enabled = true;
        switch (_licence.State)
        {
            case LicenseState.Valid:
                await ShowValidAsync();
                return;

            case LicenseState.InGracePeriod:
                _detail.Text = "License being revoked.";
                ShowLicenceBody($"It stays usable until {_licence.GraceUntil:dd/MM/yyyy}. " +
                                "Contact support to find out why.", Color.DarkOrange);
                return;

            case LicenseState.NotFound:
                _detail.Text = "No active license found.";
                // _tokenProblem is set only by an activation that the server
                // accepted; landing here right after one means the verify that
                // followed did not see it yet. ShowValidAsync says the chip's
                // refusal out loud, and this branch used to drop it.
                ShowLicenceAction("Activate now", async () =>
                {
                    using var form = new ActivationForm(_dongle, _settings);
                    if (form.ShowDialog(this) != DialogResult.OK) return;
                    if (form.Result is { } activated) await ConfirmTokenReachedDongleAsync(activated);
                    await LoadLicenceAsync();
                }, _tokenProblem, _tokenProblem is null ? null : Color.Firebrick);
                return;

            case LicenseState.Expired:
                _detail.Text = _licence.ExpiresAt is null
                    ? "License expired." : $"License expired on {_licence.ExpiresAt:dd/MM/yyyy}.";
                ShowLicenceAction("Renew", async () => await RenewAsync(),
                    "Files already encrypted remain readable: the license covers the product, " +
                    "not your data. Renewing early adds the remaining days on top.");
                return;

            case LicenseState.Revoked:
                _detail.Text = "License revoked.";
                ShowLicenceBody("Files already encrypted remain readable. Finding out why needs " +
                                "support: it cannot be reactivated from here.", Color.Firebrick);
                return;

            case LicenseState.UnauthorizedDongle:
                _detail.Text = "This dongle is not authorized on the license.";
                // Three ways, and until 2026-09-04 this listed two -- both of
                // which are impossible for the person most likely to be reading
                // it. "Start from the main one" needs the main dongle, and the
                // usual reason a chip lands here is that the main one broke;
                // "activate a new license" then charges 30 euro for a repair
                // /api/recovery does for nothing, three times a year. The route
                // that fits was in the SDK, uncalled, and is now a button.
                ShowLicenceBody(
                    "A license exists for this seed, but on another chip. Three ways forward:\n\n" +
                    "\u2022 The old dongle is broken, lost or stolen \u2014 close this and use " +
                    "Transfer license in the main window. Free, three times a year, and the " +
                    "license ends up on this chip.\n" +
                    "\u2022 The old dongle still works and you want both \u2014 use Backup dongle " +
                    "instead, starting from that one. Free, once a year, and both keep working.\n" +
                    "\u2022 This chip should have a license of its own \u2014 activate a new one.\n\n" +
                    (_licence.Message ?? ""), Color.Firebrick);
                return;
        }
    }

    /// <summary>
    /// Hands the freshly issued token to the chip a second time, so that the
    /// answer can be looked at.
    ///
    /// <c>LicenseClient.ActivateAsync</c> already pushes it and swallows every
    /// refusal on purpose: throwing there would leave someone with a paid
    /// licence and an exception. That is the right call, and it is also why
    /// nothing upstream knows whether the token actually arrived. Handing it
    /// over again is the only honest way to find out — one this chip trusts is
    /// stored again harmlessly, one it does not is refused again, and this time
    /// it is said out loud.
    /// </summary>
    private async Task ConfirmTokenReachedDongleAsync(ActivateResponse activated)
    {
        _tokenProblem = null;
        try
        {
            await _dongle.SetLicenseJwtAsync(activated.Jwt);
        }
        catch (LicenseRejectedException e) when (e.Error == DongleLicenseError.Unsupported)
        {
            // Firmware older than licences on the chip. Nothing is wrong here,
            // and saying so sends the user after a fault they cannot fix.
        }
        catch (LicenseRejectedException e)
        {
            _tokenProblem = "The license is recorded on the server, but this dongle refused the "
                            + $"token: {e.Message}\n\nThe dongle keeps whatever license it already had.";
        }
        catch (CryptinDongleException e)
        {
            _tokenProblem = $"The license token did not reach the dongle: {e.Message}";
        }
    }

    /// <summary>
    /// The server says the licence is valid. Whether the <b>dongle</b> agrees is
    /// a different question, and this step used to answer the second with the
    /// first: <c>VerifyAsync</c> talks to the server, and the chip was never
    /// asked at all.
    ///
    /// Measured 2026-08-31, walking this wizard for the first time on a new
    /// chip: the page said "the license is valid until 31/03/2027" while
    /// CMD_GET_LICENSE_STATUS answered ERR_NO_LICENSE. The refusal was correct —
    /// the run was against the red-team clone, which signs with a key the
    /// firmware does not have — but the screen stated it as a fact about the
    /// chip, and it would have said the same on any later launch, with no
    /// activation involved.
    ///
    /// <see cref="MainForm"/> carries both halves of this already. The wizard
    /// had neither, and the wizard is the screen a new user actually walks.
    /// </summary>
    private async Task ShowValidAsync()
    {
        var serverExpiry = _licence!.ExpiresAt;

        // An activation that just failed to land is more specific than anything
        // the comparison below could work out, so it wins.
        if (_tokenProblem is not null)
        {
            _detail.Text = "License recorded, but not on this dongle.";
            ShowLicenceBody(_tokenProblem, Color.Firebrick);
            return;
        }

        DongleLicenseStatus onChip;
        try
        {
            onChip = await _dongle.GetLicenseStatusAsync();
        }
        catch (CryptinDongleException e)
        {
            // Not a disagreement: the question could not be put. Saying "valid"
            // here would be the old bug with extra steps.
            _detail.Text = "License valid on the server.";
            ShowLicenceBody($"The dongle could not be asked what it holds: {e.Message}",
                Color.DarkOrange);
            return;
        }

        if (onChip.Error == DongleLicenseError.Unsupported)
        {
            // Firmware predating enforcement: there is nothing to compare, and
            // the server's answer is the only one there is.
            ShowServerVerdict(serverExpiry);
            return;
        }

        if (!onChip.IsValid)
        {
            _detail.Text = "The server and the dongle do not agree.";
            ShowLicenceBody(
                $"The license is valid on the server until {serverExpiry:dd/MM/yyyy}, but this "
                + "dongle is not carrying it.\n\nFiles you have already encrypted stay readable. "
                + "The main window hands the token over again every time it starts, so this often "
                + "clears by itself; if it does not, contact support.",
                Color.Firebrick);
            return;
        }

        // Both say valid, so the dates have to match. A minute of slack: the chip
        // answers whole Unix seconds and the server a DateTimeOffset, and the
        // rounding between them must not read as a divergence. Same test as
        // MainForm.ReportExpiryDivergence.
        if (serverExpiry is { } server && onChip.ExpiresAt is { } chip
            && (server.UtcDateTime - chip).Duration() > TimeSpan.FromMinutes(1))
        {
            _detail.Text = "The server and the dongle hold different licenses.";
            ShowLicenceBody(
                $"The server says {server:dd/MM/yyyy}, the dongle says {chip:dd/MM/yyyy}. "
                + "The main window can transfer the server's license onto this dongle.",
                Color.DarkOrange);
            return;
        }

        ShowServerVerdict(serverExpiry);
    }

    private void ShowServerVerdict(DateTimeOffset? expiry)
    {
        _detail.Text = "All set.";
        ShowLicenceBody($"Welcome. The license is valid until {expiry:dd/MM/yyyy}.",
            Color.ForestGreen);
    }

    private void ShowLicenceBody(string text, Color colour)
    {
        _body.Controls.Clear();
        _body.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(0, 0),
            Size = new Size(520, 160),
            ForeColor = colour,
        });
    }

    private void ShowLicenceAction(string caption, Func<Task> action, string? note = null,
                                   Color? noteColour = null)
    {
        _body.Controls.Clear();
        var button = new Button { Text = caption, Location = new Point(0, 0), Size = new Size(150, 32) };
        button.Click += async (_, _) => await action();
        _body.Controls.Add(button);

        if (note is not null)
            _body.Controls.Add(new Label
            {
                Text = note,
                Location = new Point(0, 44),
                Size = new Size(520, 120),
                ForeColor = noteColour ?? SystemColors.GrayText,
            });
    }

    private async Task RenewAsync()
    {
        // The session has to be for the licence's own SKU (the server matches
        // it), and it is paid in the browser before /api/dongle/renew is ever
        // called -- so this is settled here, before the money, not by the 402 or
        // BETA_NOT_RENEWABLE that would come back after it.
        //
        // Product comes from the server when it says so. For an expired licence
        // it does not, today: the LICENSE_EXPIRED body carries expires_at only,
        // and the JWT is not reissued. Until it does, the build decides: a beta
        // build only ever activated beta licences, and a paid build has exactly
        // one paid SKU to renew.
        var sku = _licence?.Product
                  ?? (ActivationForm.BetaActivation ? ActivateRequest.BetaSku : ActivateRequest.DefaultSku);

        if (sku == ActivateRequest.BetaSku)
        {
            MessageBox.Show(this,
                "Beta licenses cannot be renewed: they end on a fixed date for every tester. " +
                "Nothing has been charged.",
                "Renewal", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var payment = new ExternalBrowserPaymentSource(_settings);
        var session = await payment.AcquireSessionIdAsync(
            this, CheckoutPurpose.Renewal, sku, "A", CancellationToken.None);
        if (session is null) return;

        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            var renewed = await Task.Run(() => client.RenewAsync(_dongle, session));
            payment.Spent(session);
            MessageBox.Show(this,
                renewed.PreviousExpiresAt is null
                    ? $"License renewed until {renewed.ExpiresAt:dd/MM/yyyy}."
                    : $"License extended from {renewed.PreviousExpiresAt:dd/MM/yyyy} " +
                      $"to {renewed.ExpiresAt:dd/MM/yyyy}.",
                "Renewal", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await LoadLicenceAsync();
        }
        catch (LicenseApiException e) when (e.Code == LicenseErrorCodes.ConcurrentRetry)
        {
            // Already retried once inside LicenseClient. The request that won may
            // have been this very renewal sent twice, so reload before anyone is
            // told to try again: the new expiry may already be there.
            MessageBox.Show(this,
                "Another operation on this license was in progress at the same moment. " +
                "Nothing was charged twice. Check the expiry date shown now: if it has not " +
                "moved, try the renewal again in a few seconds.",
                "Renewal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await LoadLicenceAsync();
        }
        catch (LicenseApiException e) when (e.Code == LicenseErrorCodes.PaymentRequired)
        {
            MessageBox.Show(this,
                "The payment does not show as completed yet. If you have just made it, wait a few " +
                "seconds and press Renew again: the same payment is offered back, you will not pay twice.",
                "Renewal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Renewal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // --- step 5: offer a backup -------------------------------------------

    private void BuildBackup()
    {
        _heading.Text = "Backup dongle";
        _detail.Text = "If you lose the dongle and have no backup, the license has to be bought again. " +
                       "Registering one now is free, once a year.";

        var yes = new Button { Text = "Create backup now", Location = new Point(0, 6), Size = new Size(180, 32) };
        yes.Click += (_, _) => StartBackup();

        var later = new Button { Text = "Remind me in 30 days", Location = new Point(196, 6), Size = new Size(200, 32) };
        later.Click += (_, _) =>
        {
            _settings.RemindInThirtyDays(DateTimeOffset.UtcNow);
            DialogResult = DialogResult.OK;
            Close();
        };

        var note = new Label
        {
            Text = "Keep the backup physically apart from the main dongle and from the sheet with " +
                   "the 24 words: three things in the same drawer are one thing.",
            Location = new Point(0, 52),
            Size = new Size(520, 60),
            ForeColor = SystemColors.GrayText,
        };

        _body.Controls.AddRange([yes, later, note]);
    }

    /// <summary>
    /// Hands over to <see cref="BackupWizard"/>, and ends setup there.
    ///
    /// Setup does not resume afterwards, on purpose: the backup wizard swaps the
    /// dongles over, so the chip this wizard was holding is not the one in the
    /// socket when it returns, and no step here would still be talking to what
    /// it thinks it is.
    /// </summary>
    private void StartBackup()
    {
        // A backup is registered against an active licence. Without this check
        // the server refuses at the last of six steps -- after the user has
        // typed twenty-four words -- which is the worst place to learn it.
        if (_licence?.IsUsable != true)
        {
            MessageBox.Show(this,
                "A backup dongle is registered on an active license, and this one is not " +
                "active yet.\n\nFinish setup, sort the license out, then use Backup from " +
                "the main window.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using (var wizard = new BackupWizard(_dongle, _settings))
            wizard.ShowDialog(this);

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll?.Stop();
        base.OnFormClosed(e);
    }
}
