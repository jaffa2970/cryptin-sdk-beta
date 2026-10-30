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
/// **Or no reflash at all.** A firmware with CMD_REKEY_BEGIN/COMMIT (2026-10-30,
/// CRYPTIN_PROTOCOL.md §4.9) makes the new seed in place: the old identity
/// signs the first half, the chip hands the new words over, the user types the
/// new PIN back, and the same window finishes. K_attestation does not change,
/// which is why it is the only route on <c>-prod</c>, where the eFuse anchor
/// refuses a chip that lost its NVS. Which route is decided once, by asking the
/// chip (<see cref="CryptinDongle.SupportsOnChipRekey"/>); the beta firmware
/// answers no and keeps the reflash.
///
/// ⚠️ Written on 2026-10-30, aligned the same day to the licence server's report
/// (Tartufo 41afc3fd). The reflash route was walked on β against the clone the
/// same day; the on-chip route waits for the server to accept an unchanged
/// attestation (Tartufo f34c35a1).
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

    /// <summary>The in-place route (§4.9) instead of the reflash. From the
    /// resume record when there is one, otherwise asked of the chip.</summary>
    private readonly bool _onChip;

    /// <summary>Set when this window committed the new identity and unlocked it
    /// with the new PIN: the last step then has nothing to reconnect or ask.</summary>
    private bool _unlockedNew;

    /// <summary>The caller's handle came through unlocked (the in-place route),
    /// so the main window can stay unlocked instead of asking a third PIN.</summary>
    internal bool SessionKept => _unlockedNew && _dongle.IsConnected;

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

        _onChip = resume && settings.PendingRekey is { } recorded ? recorded.OnChip : ProbeOnChip(dongle);

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

    /// <summary>Anything but a clear yes is the reflash: an older firmware, a
    /// locked session, a dongle that did not answer. The reflash route is the
    /// one that worked before this existed, so it is the safe default.</summary>
    private static bool ProbeOnChip(CryptinDongle dongle)
    {
        if (!dongle.IsConnected) return false;
        try
        {
            return dongle.SupportsOnChipRekey();
        }
        catch (CryptinDongleException)
        {
            return false;
        }
    }

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
            case 3: if (_onChip) BuildCreate(); else BuildProgram(); break;
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
                    _onChip
                        ? "Press Create the new identity first. Nothing has been changed on the chip yet."
                        : "Press Program first. Nothing has been changed on the chip yet.",
                    _onChip ? "New identity" : "Program", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                (_onChip
                    ? "• your current PIN once more, and paper for the new 24 words — the chip makes " +
                      "the new identity itself, no cable and no programming;\n"
                    : "• the UART cable, to program it;\n") +
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
            OnChip = _onChip,
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
                    (_onChip
                        ? "After the next step this dongle has a new seed, and the old one is gone from it. "
                        : "After the next step this dongle is erased and comes back with a new seed. ") +
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

    // --- step 3, in place: the chip makes the new seed (§4.9) --------------

    private TextBox? _currentPin;

    private void BuildCreate()
    {
        _heading.Text = "Create the new identity";
        _detail.Text = "The dongle makes a new seed and shows you its 24 words and PIN. The old " +
                       "identity stays in charge until you type the new PIN back.";

        _currentPin = new TextBox
        {
            Location = new Point(220, 2),
            Size = new Size(120, 26),
            MaxLength = HidConstants.PinLength,
            UseSystemPasswordChar = true,
        };
        var create = new Button { Text = "Create the new identity", Location = new Point(0, 40), Size = new Size(220, 30) };
        create.Click += async (_, _) => await CreateAsync(create);

        _body.Controls.AddRange([
            new Label { Location = new Point(0, 5), Size = new Size(215, 22), Text = "Your CURRENT PIN, once more:" },
            _currentPin,
            create,
            new Label
            {
                Location = new Point(0, 84),
                Size = new Size(600, 240),
                Text =
                    "Asked again although the dongle is unlocked: changing identity is not something " +
                    "a program riding an open session should be able to do. A wrong PIN counts, as it " +
                    "always does.\n\n" +
                    "Have paper ready. The new words are shown once; then you type the NEW PIN back, " +
                    "from the paper. That is the moment the dongle switches — if the PIN does not " +
                    "match, nothing switches and the words you wrote are void.\n\n" +
                    "If you close this or unplug the dongle before that, the old identity simply stays.",
            },
        ]);
    }

    private async Task CreateAsync(Button create)
    {
        if (_currentPin is not { TextLength: HidConstants.PinLength } box || !box.Text.All(char.IsAsciiDigit))
        {
            MessageBox.Show(this, $"Type your current PIN: {HidConstants.PinLength} digits.",
                "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        create.Enabled = false;
        try
        {
            var current = box.Text.ToCharArray();
            box.Clear();
            RekeyBeginResult begun;
            ProvisioningHandover? handover;
            try
            {
                (begun, handover) = await Task.Run(() =>
                {
                    var r = _dongle.RekeyBegin(current, out var h);
                    return (r, h);
                });
            }
            finally
            {
                Array.Clear(current);
            }

            if (begun != RekeyBeginResult.Ok)
            {
                MessageBox.Show(this, begun switch
                {
                    RekeyBeginResult.WrongPin => "That is not the current PIN. The attempt was counted, " +
                                                 "as on the PIN window.",
                    RekeyBeginResult.Blocked => "Too many wrong PINs: the dongle is blocked. Only a recovery " +
                                                "from the 24 words unblocks it.",
                    RekeyBeginResult.SessionLocked => "The dongle locked itself after ten idle minutes. Close " +
                                                      "this, unlock it in the main window and start New identity " +
                                                      "again — it carries on from here.",
                    _ => "This dongle's firmware cannot make a new identity in place.",
                }, "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // From here a new seed is pending on the chip, and this is its only copy.
            using (handover)
            {
                using (var shown = new HandoverForm(handover!, _dongle.UsbSerial))
                    if (shown.ShowDialog(this) != DialogResult.OK)
                    {
                        MessageBox.Show(this,
                            "Nothing changed: the dongle keeps its current identity. The words that were " +
                            "on screen are void — press Create again for a fresh set when you are ready.",
                            "New identity", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
            }

            await CommitAsync();
        }
        catch (CryptinDongleException e)
        {
            MessageBox.Show(this, e.Message, "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            create.Enabled = true;
        }
    }

    /// <summary>
    /// The switch. The PIN is typed, not passed along from the handover: it is
    /// the user's proof that the paper holds what the chip is about to become.
    /// </summary>
    private async Task CommitAsync()
    {
        while (true)
        {
            var typed = AskNewPin();
            if (typed is null)
            {
                MessageBox.Show(this,
                    "Nothing changed: the dongle keeps its current identity, and the new words are void. " +
                    "Press Create again when you are ready.",
                    "New identity", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            RekeyCommitResult committed;
            try
            {
                committed = await Task.Run(() => _dongle.RekeyCommit(typed));
            }
            finally
            {
                Array.Clear(typed);
            }

            switch (committed)
            {
                case RekeyCommitResult.Ok:
                    break;

                case RekeyCommitResult.SessionLocked:
                    // Idle too long while writing. The pending seed is still on the
                    // chip; unlocking with the CURRENT (old) PIN keeps it.
                    MessageBox.Show(this,
                        "The dongle locked itself while you were writing. Unlock it with your CURRENT " +
                        "PIN — the old one — and then type the new PIN again.",
                        "New identity", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    using (var pin = new PinForm(_dongle, _dongle.UsbSerial))
                        if (pin.ShowDialog(this) != DialogResult.OK) return;
                    continue;

                case RekeyCommitResult.PinMismatch:
                    MessageBox.Show(this,
                        "That is not the PIN shown with the new words, so the dongle did NOT switch: it " +
                        "keeps its current identity. The words you just wrote down are void — cross them " +
                        "out.\n\nPress Create again for a fresh set.",
                        "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;

                default:
                    MessageBox.Show(this,
                        "The dongle had no new identity waiting — it was unplugged or restarted since it " +
                        "showed the words. It keeps its current identity; those words are void.\n\n" +
                        "Press Create again for a fresh set.",
                        "New identity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
            }
            break;
        }

        // Switched. Recorded at once: from here the only way back to the licence
        // is the second half, and a crash must resume there.
        if (Pending is { } pending)
        {
            pending.Reflashed = true;
            _settings.Save();
        }

        MessageBox.Show(this,
            "The dongle now has its new identity. Unlock it with the NEW PIN to move the license.",
            "New identity", MessageBoxButtons.OK, MessageBoxIcon.Information);
        using (var pin = new PinForm(_dongle, _dongle.UsbSerial))
            _unlockedNew = pin.ShowDialog(this) == DialogResult.OK;
        Show(4);
    }

    /// <summary>The new PIN, as typed from the paper; null if the user backed out.
    /// A char array so the caller can wipe it.</summary>
    private char[]? AskNewPin()
    {
        using var form = new Form
        {
            Text = "Crypt-in — the NEW PIN",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(380, 150),
        };
        var box = new TextBox
        {
            Location = new Point(20, 62),
            Size = new Size(120, 26),
            MaxLength = HidConstants.PinLength,
            UseSystemPasswordChar = true,
        };
        var ok = new Button { Text = "Switch", Location = new Point(160, 106), Size = new Size(96, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Location = new Point(264, 106), Size = new Size(96, 30), DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([
            new Label
            {
                Location = new Point(20, 14),
                Size = new Size(340, 44),
                Text = "Type the NEW PIN from your paper. One try: a wrong one keeps the old identity " +
                       "and voids the new words.",
            },
            box, ok, cancel,
        ]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        while (form.ShowDialog(this) == DialogResult.OK)
        {
            if (box.TextLength == HidConstants.PinLength && box.Text.All(char.IsAsciiDigit))
            {
                var typed = box.Text.ToCharArray();
                box.Clear();
                return typed;
            }
            MessageBox.Show(form, $"The PIN is {HidConstants.PinLength} digits.", "New PIN",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        box.Clear();
        return null;
    }

    // --- step 4: the second half -------------------------------------------

    private void BuildFinish()
    {
        _heading.Text = "Move the license onto the new identity";
        _detail.Text =
            $"Started on {Pending?.StartedAt.LocalDateTime:dd/MM/yyyy HH:mm}; the server holds it until " +
            $"{Pending?.TicketExpiresAt.LocalDateTime:dd/MM/yyyy HH:mm}." +
            (_unlockedNew ? "" : " Connect the dongle on its native USB socket — not the UART one.");

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 300),
            ForeColor = SystemColors.GrayText,
            Text =
                (_unlockedNew
                    ? "The dongle has its new identity and is unlocked with the new PIN. Next asks the " +
                      "server to move the license onto it.\n\n"
                    : "Next shows the NEW 24 words if they have not been shown yet — write them down, they " +
                      "are the only way back to this identity — and then asks for the NEW PIN that comes " +
                      "with them.\n\n" +
                      (_onChip ? "" :
                          "If no dialog appears, unplug the dongle, wait about ten seconds and plug it back " +
                          "in: Windows can leave the old device instance behind after programming.\n\n")) +
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

        // Committed in place by this same window and unlocked with the new PIN:
        // same handle, same session, nothing to hand over. Otherwise (a reflash,
        // or a resume after a restart) the dongle is found and unlocked anew.
        if (!_unlockedNew && !ReconnectAndUnlock()) return false;

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
                (_onChip
                    ? "This dongle still has the OLD identity: the switch did not happen.\n\n" +
                      "Close this window and start New identity again; it will offer to create it. "
                    : "This dongle still has the OLD identity: it was not erased.\n\n" +
                      "Close this window and start New identity again; it will offer to program it. ") +
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

    /// <summary>After a reflash or a restart: find the dongle, hand over the new
    /// words if it still holds them, and unlock it with the new PIN.</summary>
    private bool ReconnectAndUnlock()
    {
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
