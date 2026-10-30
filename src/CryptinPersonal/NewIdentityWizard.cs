using CryptinSDK.Dongle;
using CryptinSDK.Licensing;
using CryptinSDK.Updater;

namespace CryptinPersonal;

/// <summary>
/// Moving the licence onto a <b>new identity</b> on the same chip: new seed, new
/// 24 words, new PIN. The cure for advisory G2 — seeds of the first beta were
/// drawn before the entropy source was on, and <c>recover</c> brings the same
/// seed back, so only a new one fixes it.
///
/// **Why a wizard and not "reflash and set up again".** Until 2026-10-30 the
/// advisory told people to do exactly that, and it costs the licence: the server
/// binds it to the identity of the 24 words, a dongle with new words has none
/// and will not encrypt, and Transfer license only knows the old words. This
/// wizard is the route that keeps it.
///
/// **Two halves, with a reflash between them.** The new seed is minted by
/// erasing the old one, and the old K_identity and K_attestation go with it —
/// so the proof that the licence is ours is taken first (<c>rekey/start</c>,
/// signed by the dongle as it is now) and handed over as a ticket to the second
/// half (<c>rekey/finish</c>, signed by the new identity). Signing the first
/// half on the PC from the 24 words was turned down: it puts the identity's
/// private key on the host.
///
/// **The files come first.** Everything encrypted under the old identity is
/// opened by the old identity only. The chip that comes back from this cannot
/// open any of it, so the user decrypts what they need between the halves.
///
/// ⚠️ Written on 2026-10-30, aligned the same day to the licence server's report
/// (Tartufo 41afc3fd) while its endpoints were on no deployed server. Never
/// walked.
/// </summary>
internal sealed class NewIdentityWizard : Form
{
    private readonly CryptinDongle _dongle;
    private readonly AppSettings _settings;

    private readonly Label _heading = new();
    private readonly Label _detail = new();
    private readonly Panel _body = new();
    private readonly Button _back = new();
    private readonly Button _next = new();

    private int _step = 1;

    /// <summary>Same reason as <see cref="LicenseTransferWizard.RestartRequired"/>:
    /// the reflash replaces the USB device under this process, and only a new
    /// process talks to it reliably.</summary>
    internal static bool RestartRequired { get; private set; }

    internal const string RestartMessage =
        "The chip is programmed and has made itself a new identity. It is waiting to show you " +
        "its NEW 24 words and its NEW PIN.\n\n" +
        "Close Crypt-in now and open it again, with the dongle on its native USB socket. It will " +
        "show the new words first — write them down — and then offer to finish moving the " +
        "license.\n\n" +
        "Until that last step the license is still on the old identity, and this chip cannot " +
        "encrypt anything. Nothing is lost by waiting, as long as it is before the date the " +
        "server gave for the ticket.";

    /// <param name="resume">True to pick up <see cref="AppSettings.PendingRekey"/>
    /// where it stopped. The caller decides, as with the transfer.</param>
    public NewIdentityWizard(CryptinDongle dongle, AppSettings settings, bool resume = false)
    {
        _dongle = dongle;
        _settings = settings;

        Text = "Crypt-in — move to a new identity";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(660, 520);

        _heading.Location = new Point(20, 18);
        _heading.Size = new Size(620, 26);
        _heading.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);

        _detail.Location = new Point(20, 48);
        _detail.Size = new Size(620, 74);

        _body.Location = new Point(20, 126);
        _body.Size = new Size(620, 330);

        _back.Text = "Back";
        _back.Location = new Point(402, 470);
        _back.Size = new Size(114, 30);
        _back.Click += (_, _) => Show(_step - 1);

        _next.Text = "Next";
        _next.Location = new Point(526, 470);
        _next.Size = new Size(114, 30);
        _next.Click += async (_, _) => await NextAsync();

        Controls.AddRange([_heading, _detail, _body, _back, _next]);

        if (resume && !RestartRequired && settings.PendingRekey is { Ticket.Length: > 0 } pending)
        {
            if (pending.Reflashed)
            {
                Show(4);
                return;
            }

            // Before the reflash an expired ticket costs nothing: the dongle
            // still has the old identity and can simply ask for a new one.
            if (pending.TicketExpiresAt > DateTimeOffset.Now)
            {
                Show(2);
                return;
            }
            settings.ClearPendingRekey();
        }

        Show(1);
    }

    private RekeyResume? Pending => _settings.PendingRekey;

    private void Show(int step)
    {
        _step = Math.Clamp(step, 1, 5);
        _body.Controls.Clear();
        // Back only between the two steps that belong to the same half. Step 1
        // is behind a server call, and step 4 behind an erased identity.
        _back.Enabled = _step == 3;
        _next.Enabled = true;
        _next.Text = _step == 5 ? "Finish" : "Next";

        switch (_step)
        {
            case 1: BuildExplain(); break;
            case 2: BuildDecrypt(); break;
            case 3: BuildProgram(); break;
            case 4: BuildFinish(); break;
            case 5: BuildDone(); break;
        }
    }

    private async Task NextAsync()
    {
        switch (_step)
        {
            case 1:
                if (!await StartAsync()) return;
                Show(2);
                return;

            case 2:
                if (_decrypted?.Checked != true)
                {
                    MessageBox.Show(this,
                        "Tick the box first. After the next step this dongle cannot open anything " +
                        "you encrypted with it until now.",
                        "Your files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Show(3);
                return;

            case 3:
                // Next on this step is not how it moves on: Program closes the
                // wizard for the restart. Reaching here means it has not run.
                MessageBox.Show(this,
                    "Press Program first. Nothing has been changed on the chip yet.",
                    "Program", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;

            case 4:
                if (!await FinishAsync()) return;
                Show(5);
                return;

            default:
                DialogResult = DialogResult.OK;
                Close();
                return;
        }
    }

    // --- step 1: what this does, and the first half ------------------------

    private void BuildExplain()
    {
        _heading.Text = "Move to a new identity";
        _detail.Text = "Your dongle gets a new seed: new 24 words, new PIN. The license comes with " +
                       "it. This is what the G2 advisory recommends for a dongle set up with the " +
                       "first beta.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 320),
            Text =
                "What it takes:\n" +
                "• this dongle, unlocked — it is the one that proves the license is yours;\n" +
                "• the UART cable, to program it;\n" +
                "• time to decrypt your files first. The new identity cannot open anything " +
                "encrypted with the old one.\n\n" +
                "What changes:\n" +
                "• the 24 words and the PIN. The old words still rebuild the OLD identity — " +
                "keep them until nothing you need is encrypted with it;\n" +
                "• a registered backup dongle carries the old seed, so it stops counting. " +
                "Registering a new one is free again this year;\n" +
                "• the expiry date does not change. This does not use one of your three " +
                "transfers: it has its own limit, once a year.\n\n" +
                "Next asks the license server to hold the license for the move. Nothing changes " +
                "on the license or on the dongle yet, and you can stop here.",
        });
    }

    /// <summary>
    /// The first half. Recorded before anything else is said, for the same
    /// reason as every resume record here: the ticket is the only bridge to the
    /// second half, and it has to survive the window being closed.
    /// </summary>
    private async Task<bool> StartAsync()
    {
        if (!_dongle.IsConnected)
        {
            MessageBox.Show(this, "Connect and unlock your dongle first.",
                "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        byte[] identity;
        RekeyStartResponse started;
        try
        {
            identity = await Task.Run(() => _dongle.GetIdentityPubKey());
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            started = await Task.Run(() => client.RekeyStartAsync(_dongle));
        }
        catch (LicenseApiException e)
        {
            MessageBox.Show(this, Describe(e), "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _settings.PendingRekey = new RekeyResume
        {
            Ticket = started.Ticket,
            TicketExpiresAt = started.ExpiresAt,
            OldIdentityHex = Convert.ToHexString(identity),
            Reflashed = false,
            StartedAt = DateTimeOffset.Now,
        };
        _settings.Save();
        return true;
    }

    // --- step 2: decrypt first ---------------------------------------------

    private CheckBox? _decrypted;

    private void BuildDecrypt()
    {
        _heading.Text = "Decrypt what you need — now";
        _detail.Text =
            $"The server holds the license for the move until {Pending?.TicketExpiresAt.LocalDateTime:dd/MM/yyyy HH:mm}. " +
            "Use that time to decrypt, with this dongle, every file you still need.";

        _decrypted = new CheckBox
        {
            Location = new Point(0, 190),
            Size = new Size(600, 40),
            Text = "I have decrypted everything I need. I understand the new identity cannot open " +
                   "files encrypted until now.",
        };

        _body.Controls.AddRange([
            new Label
            {
                Location = new Point(0, 0),
                Size = new Size(600, 180),
                Text =
                    "You can close this window and work in the main window as usual: drop the " +
                    ".crin files there to decrypt them. When you come back, this picks up here.\n\n" +
                    "After the next step this dongle is erased and comes back with a new seed. " +
                    // Not "with Transfer license" (said until 2026-10-30): after the move
                    // the old words own no licence, so that wizard would rebuild the chip
                    // and then end on "no license". Decrypting needs no licence, so a chip
                    // carrying the old seed opens the files -- but it is a detour.
                    "Anything still encrypted with the old one can then be opened only from the " +
                    "OLD 24 words, on a chip rebuilt from them — and those words will no longer " +
                    "carry a license. A detour you are better off not needing.\n\n" +
                    "Once the new identity is in place, encrypt again whatever should stay encrypted.",
            },
            _decrypted,
        ]);
    }

    // --- step 3: erase and program -----------------------------------------

    private ComboBox? _ports;
    private ProgressBar? _flashProgress;

    private void BuildProgram()
    {
        _heading.Text = "Program the dongle";
        _detail.Text = "Connect the cable to the socket marked UART on this same dongle. Program " +
                       "erases its identity and writes the firmware; on its first start it makes " +
                       "itself a new seed.";

        _ports = new ComboBox
        {
            Location = new Point(0, 4),
            Size = new Size(380, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        var refresh = new Button { Text = "Refresh", Location = new Point(392, 3), Size = new Size(100, 28) };
        refresh.Click += (_, _) => LoadPorts();

        // Hover and focus, not DropDown alone: the list window is sized before
        // DropDown is raised (measured on the backup wizard, 2026-09-03).
        _ports.MouseEnter += (_, _) => LoadPorts();
        _ports.Enter += (_, _) => LoadPorts();
        _ports.DropDown += (_, _) => LoadPorts();

        var uartNote = new Label
        {
            Location = new Point(0, 34),
            Size = new Size(600, 40),
            ForeColor = SystemColors.GrayText,
            Text = SerialPorts.OnlyUartNote,
        };

        var flash = new Button { Text = "Program", Location = new Point(0, 80), Size = new Size(130, 30) };
        flash.Click += async (_, _) => await ProgramAsync(flash);

        _flashProgress = new ProgressBar { Location = new Point(0, 120), Size = new Size(492, 20) };

        var updater = new UpdaterProcess();
        if (!updater.IsInstalled) flash.Enabled = false;

        _body.Controls.AddRange([
            _ports, refresh, uartNote, flash, _flashProgress,
            new Label
            {
                Location = new Point(0, 150),
                Size = new Size(600, 180),
                Text =
                    (updater.IsInstalled ? "" :
                        "CryptinUpdater.exe is not installed, so this cannot be done from here.\n\n") +
                    "The chip will say it is not blank, and ask before writing: here Yes is the " +
                    "right answer, because erasing this identity is the point.\n\n" +
                    "Make sure it is THIS dongle on the port, and no other Crypt-in dongle you " +
                    "still need. The license is still safe at this point: it moves only at the " +
                    "last step, and only onto the identity this chip makes.",
            },
        ]);

        LoadPorts();
    }

    private void LoadPorts()
    {
        var chosen = (_ports!.SelectedItem as SerialPortInfo)?.PortName;

        var ports = SerialPorts.Programmable();
        _ports.Items.Clear();
        foreach (var port in ports) _ports.Items.Add(port);

        // Enabled even when empty: opening the box is what reloads it.
        _ports.Enabled = true;
        if (ports.Count == 0)
        {
            _ports.Items.Add(SerialPorts.NoPortText());
            _ports.SelectedIndex = 0;
            return;
        }

        var at = -1;
        for (var i = 0; i < _ports.Items.Count; i++)
            if (_ports.Items[i] is SerialPortInfo info
                && info.PortName.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                at = i;
        _ports.SelectedIndex = at >= 0 ? at : 0;
    }

    private bool ConfirmErase(string portName)
        => MessageBox.Show(this,
            $"The chip on {portName} is a Crypt-in dongle.\n\n" +
            "Programming it erases its seed and its identity, which is what this wizard is for. " +
            "Files encrypted with it can afterwards be opened only with the OLD 24 words.\n\n" +
            "If this is not the dongle you started with — another dongle you still use — answer " +
            "No and change the port.\n\n" +
            "Erase it and give it a new identity?",
            "Erase this identity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private async Task ProgramAsync(Button flash)
    {
        if (_ports?.SelectedItem is not SerialPortInfo port)
        {
            MessageBox.Show(this,
                "No COM port to program. Connect the cable to the socket marked UART, then open " +
                "the list above: it re-reads the ports every time it opens.",
                "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Looked up again: the list is filled by UI events and can be stale, and
        // a different board on that COM number is how the wrong chip is erased.
        if (!SerialPorts.Programmable().Any(p => p.PortName.Equals(port.PortName, StringComparison.OrdinalIgnoreCase)))
        {
            LoadPorts();
            MessageBox.Show(this,
                $"{port.PortName} is not there any more. Nothing was written.",
                "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // The chip is about to be reset under the HID handle this window was
        // given unlocked. Let go of it first rather than hold a dead stream.
        _dongle.Disconnect();

        flash.Enabled = false;
        _flashProgress!.Value = 0;
        try
        {
            var updater = new UpdaterProcess();
            var progress = new Progress<int>(p => _flashProgress.Value = Math.Clamp(p, 0, 100));
            var result = await updater.FirstFlashAsync(port.PortName, progress);

            // Expected here, unlike in setup: the chip on the port should be the
            // dongle we started from. Asked in our own words, not setup's, whose
            // advice ("use Transfer license") is the wrong one for this wizard.
            if (result == FlashResult.AlreadyProvisioned && ConfirmErase(port.PortName))
            {
                _flashProgress.Value = 0;
                result = await updater.FirstFlashAsync(port.PortName, progress, force: true);
            }

            if (result != FlashResult.Success)
            {
                MessageBox.Show(this, result switch
                {
                    FlashResult.PortNotFound => $"{port.PortName} is not responding: {updater.LastError}",
                    FlashResult.SignatureInvalid => "Firmware signature not valid: nothing was written.",
                    FlashResult.AlreadyProvisioned => "Left alone: the dongle keeps its identity.",
                    _ => $"Programming failed. {updater.LastError}",
                }, "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _flashProgress.Value = 100;

            // Recorded before the message, as every resume record here is: from
            // now on the only thing between the user and the licence is this.
            if (Pending is { } pending)
            {
                pending.Reflashed = true;
                _settings.Save();
            }
            RestartRequired = true;

            MessageBox.Show(this, "Done. " + RestartMessage, "New identity — one restart left",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.Cancel;
            Close();
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

    // --- step 4: the second half -------------------------------------------

    private void BuildFinish()
    {
        _heading.Text = "Move the license onto the new identity";
        _detail.Text =
            $"Started on {Pending?.StartedAt.LocalDateTime:dd/MM/yyyy HH:mm}; the server holds it until " +
            $"{Pending?.TicketExpiresAt.LocalDateTime:dd/MM/yyyy HH:mm}. Connect the dongle on its native " +
            "USB socket — not the UART one.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 300),
            ForeColor = SystemColors.GrayText,
            Text =
                "Next shows the NEW 24 words if they have not been shown yet — write them down, they " +
                "are the only way back to this identity — and then asks for the NEW PIN that comes " +
                "with them.\n\n" +
                "If no dialog appears, unplug the dongle, wait about ten seconds and plug it back " +
                "in: Windows can leave the old device instance behind after programming.\n\n" +
                "This is the step that moves the license. It uses the once-a-year allowance for a " +
                "new identity, not one of your three transfers.",
        });
    }

    private async Task<bool> FinishAsync()
    {
        if (Pending is not { } pending) return false;

        if (pending.TicketExpiresAt <= DateTimeOffset.Now)
        {
            // Spent for good, so forgotten: offering it on every launch would
            // only repeat this message.
            _settings.ClearPendingRekey();
            MessageBox.Show(this, ExpiredTicketText, "New identity",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0)
        {
            MessageBox.Show(this,
                "No dongle is answering. Connect it on its native USB socket — on the UART socket " +
                "it is a COM port and speaks no HID.",
                "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _dongle.Disconnect();
        if (!_dongle.Connect(attached[0], waitMs: 5000))
        {
            MessageBox.Show(this, "The dongle will not open. Disconnect it and connect it again.",
                "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Before the PIN: the new PIN comes out of the new seed, and nobody can
        // know it until the handover has been read.
        if (!ProvisioningHandoverUi.EnsureHandedOver(this, _dongle)) return false;

        using (var pin = new PinForm(_dongle, _dongle.UsbSerial))
            if (pin.ShowDialog(this) != DialogResult.OK) return false;

        byte[] identity;
        try
        {
            identity = await Task.Run(() => _dongle.GetIdentityPubKey());
        }
        catch (CryptinDongleException e)
        {
            MessageBox.Show(this, e.Message, "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // The one mistake that would otherwise reach the server: a chip that was
        // never reflashed — another board took the flash, or this one was
        // brought back from the old words. The server would refuse it too, but
        // with a code about identities that would read as a fault.
        if (Convert.ToHexString(identity).Equals(pending.OldIdentityHex, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "This dongle still has the OLD identity: it was not erased.\n\n" +
                "Close this window and start New identity again; it will offer to program it. " +
                "The license has not moved.",
                "Still the old identity", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        RekeyFinishResponse moved;
        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            moved = await Task.Run(() => client.RekeyFinishAsync(_dongle, pending.Ticket));
        }
        catch (LicenseApiException e)
        {
            // A ticket the server no longer honours cannot be honoured later
            // either. Forgetting it stops the offer on every launch. Every other
            // refusal leaves it open on the server (report 41afc3fd: it is spent
            // only on success), so those are kept for another try.
            if (e.Code is LicenseErrorCodes.TicketInvalid or LicenseErrorCodes.TicketExpired)
                _settings.ClearPendingRekey();
            MessageBox.Show(this, Describe(e), "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // The licence has moved. The record goes first: the ticket is spent, and
        // a bearer secret has no business outliving its use.
        _settings.ClearPendingRekey();
        if (moved.BackupsRemoved > 0)
        {
            _settings.BackupRegistered = false;
            _settings.Save();
        }

        // Same three endings as the transfer, for the same reasons.
        var expired = moved.Expired || moved.ExpiresAt <= DateTimeOffset.Now;
        var landed = true;
        var why = "";
        if (!expired)
        {
            try
            {
                await _dongle.SetLicenseJwtAsync(moved.Jwt);
            }
            catch (Exception e) when (e is LicenseRejectedException or CryptinDongleException)
            {
                landed = false;
                why = e.Message;
            }
        }

        _backupsRemoved = moved.BackupsRemoved;

        MessageBox.Show(this,
            "The license is on the new identity now. It still expires " +
            $"{moved.ExpiresAt.UtcDateTime:dd/MM/yyyy}.\n\n" +
            (expired
                ? "It has already expired, so the token was not written to the chip. Renew it on " +
                  "this dongle and it will encrypt again."
                : landed
                    ? "The token has been written to the chip: it can encrypt straight away."
                    : $"One thing is left: the chip did not take the new token ({why}). Press Load " +
                      "license in the main window — the server will hand the same token back."),
            "New identity", MessageBoxButtons.OK,
            landed && !expired ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return true;
    }

    private const string ExpiredTicketText =
        "The server stopped holding the license for this move: the ticket has expired. The license " +
        "is still on the OLD identity, and nothing has been spent.\n\n" +
        "To start again, the dongle has to carry the old identity: rebuild it from the OLD 24 words " +
        "with Transfer license (that uses one of your three transfers), then use New identity again.";

    private int _backupsRemoved;

    private static string Describe(LicenseApiException e) => e.Code switch
    {
        LicenseErrorCodes.LicenseNotFound =>
            "The server has no license for this dongle's identity, so there is nothing to move.",

        LicenseErrorCodes.AttestationMismatch =>
            "This dongle is not the one the license is on — a registered backup, for instance. " +
            "Start this from the main dongle.",

        LicenseErrorCodes.LicenseRevoked =>
            "The license has been revoked, so it cannot be moved.",

        LicenseErrorCodes.TicketExpired => ExpiredTicketText,

        LicenseErrorCodes.TicketInvalid =>
            "The server no longer accepts the ticket for this move: it was used already, a newer " +
            "move was started, or the license was transferred in between. Nothing moved here.\n\n" +
            "To try again the dongle has to carry the old identity: rebuild it from the OLD 24 " +
            "words with Transfer license, then use New identity again.",

        LicenseErrorCodes.IdentityInUse =>
            "This identity cannot take the license: it already holds one, or it is a recovery " +
            "phrase that was retired earlier. The dongle has to be programmed again so it makes a " +
            "seed of its own. The license has not moved, and the ticket is still valid.",

        LicenseErrorCodes.AlreadyActivated or LicenseErrorCodes.AlreadyRegistered =>
            "This chip is already registered on a license. Nothing was moved.",

        LicenseErrorCodes.RekeyRateLimit =>
            "This license has already moved to a new identity in the last 365 days. " +
            (e.NextAvailable is { Length: > 0 } when_
                ? $"The next move becomes possible on {when_}."
                : "The server did not say when the next one opens."),

        LicenseErrorCodes.ConcurrentRetry =>
            "Another operation on this license was in progress at the same moment. Nothing was " +
            "changed: try again in a few seconds.",

        LicenseErrorCodes.InvalidSignature =>
            "The dongle's signature was refused. Unplug it, plug it back in and try again.",

        LicenseErrorCodes.ChallengeExpired =>
            "The server's challenge expired before the dongle answered. Press Next again.",

        _ => e.Message,
    };

    // --- step 5: what changed ----------------------------------------------

    private void BuildDone()
    {
        _heading.Text = "Done";
        _detail.Text = "The license lives on the new identity.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 300),
            Text =
                "Encrypt again whatever you decrypted and want kept encrypted.\n\n" +
                "Keep the NEW 24 words safe: they are the way back to this identity. Keep the OLD " +
                "ones too, until nothing you need is encrypted with them — they are the only thing " +
                "left that opens those files.\n\n" +
                (_backupsRemoved > 0
                    ? $"{_backupsRemoved} registered backup dongle(s) carried the old seed and no longer " +
                      "count. Register a new backup from Backup dongle: it is free again this year."
                    : "If you want a backup dongle, register it now from Backup dongle."),
        });
    }
}
