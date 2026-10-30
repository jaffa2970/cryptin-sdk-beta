using CryptinSDK.Dongle;
using CryptinSDK.Licensing;
using CryptinSDK.Seed;
using CryptinSDK.Updater;

namespace CryptinPersonal;

/// <summary>
/// Moving a licence onto a rebuilt chip, when the one it lived on is gone.
///
/// **Transfer, not registration.** Afterwards the licence sits on one chip, and
/// the old one — if it ever turns up again — is no longer authorised. That is
/// the opposite of <see cref="BackupWizard"/>, which adds a second dongle and
/// keeps the first, and the two must never be reached for interchangeably: a
/// user with both chips in hand wants the backup, and a user with one broken
/// chip wants this.
///
/// **Why it exists at all.** Until this file the app could not do it. The route
/// out of a broken dongle was in the SDK — <c>LicenseClient.RecoverAsync</c>,
/// written and never called — and the interface offered two ways forward from
/// an unauthorised chip, "register it as a backup, starting from the main one"
/// and "activate a new licence". The first is impossible once the main dongle is
/// the thing that broke; the second costs €30 for something the server gives
/// away three times a year. So the app charged for a repair the licence already
/// covered, and the button that looked like this one — "Transfer license" —
/// wrote a JWT onto the chip and had nothing to do with any of it. That button
/// is now called Load license, and this is what the name was pointing at.
///
/// **The chip has to be built here.** A transfer needs the rebuilt dongle on the
/// wire: the signature comes from K_identity, which comes from the seed, and the
/// new K_attestation is read from that same chip. So the wizard flashes and runs
/// the serial <c>recover</c> itself, exactly as the backup wizard does — a clean
/// Windows 11 has no serial terminal, and telling someone to open one is how the
/// backup was unreachable for its whole audience until 2026-09-02.
///
/// And, for the same reason, it carries the same restart: the flash replaces the
/// USB device under the process, so step 4 saves a public resume record and
/// closes. See <see cref="TransferResume"/>.
///
/// **Never walked from the interface.** <c>RecoverAsync</c> itself has never
/// been executed against the server either — see crypt-in-status.md, "scritta,
/// NON verificata". Written from the contract in `app/routers/attestation.py`
/// (endpoint 4) read directly, not from the summary of it.
/// </summary>
internal sealed class LicenseTransferWizard : Form
{
    private readonly CryptinDongle _dongle;
    private readonly AppSettings _settings;

    private readonly Label _heading = new();
    private readonly Label _detail = new();
    private readonly Panel _body = new();
    private readonly Button _back = new();
    private readonly Button _next = new();

    private int _step = 1;

    /// <summary>Entered on a chip that was already flashed and recovered in an
    /// earlier run. Steps 1 to 4 are behind it and there is no way back: the 24
    /// words were deliberately not kept.</summary>
    private readonly bool _resumed;

    /// <summary>
    /// Set once this process has rebuilt a chip, and never cleared. Same reason
    /// as <see cref="BackupWizard.RestartRequired"/>: reopening the wizard in
    /// the same process walks into the step that cannot work.
    /// </summary>
    internal static bool RestartRequired { get; private set; }

    internal const string RestartMessage =
        "The chip is ready: it carries your seed, so it has the identity the license was " +
        "activated with.\n\n" +
        "Close Crypt-in now and open it again. It will offer to finish the transfer and pick up " +
        "at the last step — nothing has to be typed twice, not the 24 words and not the " +
        "programming.\n\n" +
        "Why a restart and not a button: the chip was rewritten and reset while this application " +
        "held it open, and Windows replaced the device underneath. Reconnecting from inside was " +
        "tried first and is not enough; only ending the process clears it.\n\n" +
        "Until then the license has NOT moved. The chip opens files encrypted with this seed — " +
        "the dongle governs the data — but the server still has the license on the old chip, so " +
        "this one cannot encrypt anything new.";

    /// <summary>The route that existed before the recovery was automated. Still
    /// true, and still the only way out on a machine whose serial port cannot be
    /// opened at all.</summary>
    private const string ManualSteps =
        "To load it by hand, on the same port:\n" +
        "  1. open a serial terminal at 115200 baud\n" +
        "  2. issue the 'recover' command\n" +
        "  3. enter the 24 words from step 2\n" +
        "  4. type YES when it asks\n" +
        "  5. the PIN it prints is your old dongle's PIN — it comes from the seed";

    private readonly TextBox[] _words = new TextBox[Bip39.WordCount];
    private RecoveryReason _reason = RecoveryReason.DamagedDevice;

    /// <summary>The identity the serial recovery printed on the rebuilt chip,
    /// hex. Step 5 compares it with what the chip answers over HID, which is how
    /// "the chip I prepared" is told from "some other chip in the socket".
    ///
    /// It is not proof the seed is the right one — nothing on this machine can
    /// be. The server is the only thing that knows which K_identity the licence
    /// belongs to, and it says so by answering LICENSE_NOT_FOUND.</summary>
    private string _rebuiltIdentityHex = "";

    private bool _seedLoaded;

    /// <param name="resume">True to pick up the chip recorded in
    /// <see cref="AppSettings.PendingTransfer"/> at the last step. False always
    /// starts at step 1 — the only way to abandon a chip that turned out to be
    /// the wrong one — so the caller decides and the wizard never resumes behind
    /// someone's back.</param>
    public LicenseTransferWizard(CryptinDongle dongle, AppSettings settings, bool resume = false)
    {
        _dongle = dongle;
        _settings = settings;

        Text = "Crypt-in — transfer the license to a new dongle";
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

        if (resume && !RestartRequired && settings.PendingTransfer is { } pending)
        {
            if (LoadResume(pending))
            {
                _resumed = true;
                Show(5);
                return;
            }

            // Unreadable, so nothing can be resumed from it — and left alone it
            // would offer to finish a transfer on every launch, forever.
            settings.ClearPendingTransfer();
        }

        Show(1);
    }

    private bool LoadResume(TransferResume pending)
    {
        if (pending.RebuiltIdentityHex.Length == 0) return false;

        _rebuiltIdentityHex = pending.RebuiltIdentityHex;
        _reason = pending.Reason;
        _seedLoaded = true;
        return true;
    }

    /// <summary>Written the instant the chip carries the seed, before anything
    /// else can go wrong — including the user closing the window on the message
    /// that tells them to restart.</summary>
    private void SaveResume(string identityHex)
    {
        _settings.PendingTransfer = new TransferResume
        {
            RebuiltIdentityHex = identityHex,
            Reason = _reason,
            StartedAt = DateTimeOffset.Now,
        };
        _settings.Save();
        RestartRequired = true;
    }

    private void Show(int step)
    {
        _step = Math.Clamp(step, 1, 6);
        _body.Controls.Clear();
        // Nothing to go back to on a resumed wizard: steps 1 to 4 belong to a
        // run that has ended, and the words they need were never kept.
        _back.Enabled = !_resumed && _step is > 1 and < 5;
        _next.Enabled = true;
        _next.Text = _step == 6 ? "Finish" : "Next";

        switch (_step)
        {
            case 1: BuildExplain(); break;
            case 2: BuildSeed(); break;
            case 3: BuildInsertChip(); break;
            case 4: BuildFlashAndRecover(); break;
            case 5: BuildTransfer(); break;
            case 6: BuildDone(); break;
        }
    }

    private async Task NextAsync()
    {
        switch (_step)
        {
            case 1:
                Show(2);
                return;

            case 2:
                if (Bip39.Validate(CurrentWords()) is { } problem)
                {
                    MessageBox.Show(this, problem, "Recovery phrase",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // Snapshot the ports before anything is plugged in, so step 3 can
                // name the one that appears. Same trick as the backup wizard,
                // minus the part that told the backup from the main dongle:
                // here there is no main dongle to tell it from, which is the
                // premise of the whole wizard.
                _portsBefore = SerialPorts.Programmable().Select(p => p.PortName).ToArray();
                Show(3);
                return;

            case 3:
                Show(4);
                return;

            case 4:
                // Measured on the backup wizard on 2026-09-02 and true here for
                // the same reason: without the seed on it, the chip is a new
                // independent dongle, and the next step would ask the server to
                // move a licence to an identity that owns none. The server would
                // answer LICENSE_NOT_FOUND and burn nothing, but the user would
                // read it as "my 24 words are wrong" when the truth is that the
                // words never reached the chip.
                if (!_seedLoaded)
                {
                    MessageBox.Show(this,
                        "The chip does not carry your seed yet, so there is nothing for the next " +
                        "step to transfer the license to.\n\n" +
                        "Run Program again, or load the seed by hand:\n\n" + ManualSteps,
                        "Not rebuilt yet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Show(5);
                return;

            case 5:
                if (!await TransferAsync()) return;
                Show(6);
                return;

            default:
                DialogResult = DialogResult.OK;
                Close();
                return;
        }
    }

    // --- step 1: what this does, and what it costs ------------------------

    private void BuildExplain()
    {
        _heading.Text = "Move the license to a new dongle";
        _detail.Text = "For a dongle that is broken, lost or stolen. You need the 24 words and a " +
                       "chip to put them on.";

        var group = new GroupBox
        {
            Text = "What happened to the old dongle",
            Location = new Point(0, 0),
            Size = new Size(600, 116),
        };

        // An enum rather than free text because the server validates it:
        // anything outside the three is 400 INVALID_REASON, which would surface
        // as a mysterious refusal at the last step of a long wizard.
        var damaged = Radio("It stopped working, or it was damaged", RecoveryReason.DamagedDevice, 26);
        var lost = Radio("I lost it", RecoveryReason.LostDevice, 54);
        var stolen = Radio("It was stolen", RecoveryReason.StolenDevice, 82);
        damaged.Checked = true;
        group.Controls.AddRange([damaged, lost, stolen]);

        _body.Controls.AddRange([
            group,
            new Label
            {
                Location = new Point(0, 128),
                Size = new Size(600, 180),
                Text =
                    "This is a transfer, not a second dongle. When it is done the license lives on " +
                    "the new chip and the old one is no longer authorized — if it turns up later it " +
                    "will not encrypt anything new.\n\n" +
                    "If the old dongle still works and you want a spare alongside it, close this and " +
                    "use Backup dongle instead. That one keeps both.\n\n" +
                    "Three transfers per license per year, counted over the last 365 days rather " +
                    "than per calendar year. If you have used them up the server says the date the " +
                    "oldest one falls out of the window.\n\n" +
                    "It costs nothing. Renewal is separate: a transfer moves the license, it does " +
                    "not extend it.",
            },
        ]);
    }

    private RadioButton Radio(string text, RecoveryReason reason, int top)
    {
        var radio = new RadioButton
        {
            Text = text,
            Location = new Point(14, top),
            Size = new Size(560, 24),
        };
        radio.CheckedChanged += (_, _) => { if (radio.Checked) _reason = reason; };
        return radio;
    }

    // --- step 2: the 24 words ---------------------------------------------

    private void BuildSeed()
    {
        _heading.Text = "Your 24 words";
        _detail.Text = "They rebuild the identity the license was activated with. The server finds " +
                       "the license from that identity and from nothing else — which is why a wrong " +
                       "phrase cannot be told from a phrase belonging to somebody else's license.";

        var grid = new TableLayoutPanel
        {
            Location = new Point(0, 0),
            Size = new Size(600, 230),
            ColumnCount = 4,
            RowCount = 6,
        };
        for (var c = 0; c < 4; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var r = 0; r < 6; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6));

        var status = new Label
        {
            Location = new Point(0, 242),
            Size = new Size(600, 40),
            ForeColor = SystemColors.GrayText,
        };

        for (var i = 0; i < Bip39.WordCount; i++)
        {
            var box = _words[i] ?? new TextBox();
            _words[i] = box;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 3, 6, 3);
            box.PlaceholderText = $"{i + 1}";
            box.TextChanged -= OnWordChanged;
            box.TextChanged += OnWordChanged;
            box.Tag = status;
            grid.Controls.Add(box, i % 4, i / 4);
        }

        var paste = new Button { Text = "Paste the 24 words", Location = new Point(0, 286), Size = new Size(180, 30) };
        paste.Click += (_, _) => PasteAll(status);

        _body.Controls.AddRange([grid, status, paste]);
        UpdateSeedStatus(status);
    }

    private void OnWordChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBox box) return;

        box.ForeColor = box.TextLength == 0 || Bip39.IsWord(box.Text)
            ? SystemColors.WindowText
            : Color.Firebrick;

        if (box.Tag is Label status) UpdateSeedStatus(status);
    }

    private void UpdateSeedStatus(Label status)
    {
        var words = CurrentWords();
        var filled = words.Count(w => w.Length > 0);
        if (filled < Bip39.WordCount)
        {
            status.ForeColor = SystemColors.GrayText;
            status.Text = $"{filled} of {Bip39.WordCount} words.";
            return;
        }

        var problem = Bip39.Validate(words);
        status.ForeColor = problem is null ? Color.ForestGreen : Color.Firebrick;
        status.Text = problem ?? "Phrase valid.";
    }

    private void PasteAll(Label status)
    {
        var text = Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var parts = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Bip39.WordCount; i++)
            _words[i].Text = i < parts.Length ? Bip39.Normalize(parts[i]) : "";

        UpdateSeedStatus(status);
    }

    private string[] CurrentWords()
        => _words.Select(w => Bip39.Normalize(w?.Text ?? "")).ToArray();

    // --- step 3: the chip that will carry it ------------------------------

    private string[] _portsBefore = [];
    private string? _newPort;

    private void BuildInsertChip()
    {
        _heading.Text = "Plug in the new chip";
        _detail.Text = "Connect the cable to the socket marked UART. A chip that has never been " +
                       "programmed has no dongle interface yet: it shows up as a COM port and " +
                       "nothing else, and the next step is where it gets the firmware.";

        var state = new Label { Location = new Point(0, 0), Size = new Size(600, 110) };

        _body.Controls.AddRange([
            state,
            new Label
            {
                Location = new Point(0, 120),
                Size = new Size(600, 150),
                ForeColor = SystemColors.GrayText,
                Text =
                    "Any spare ESP32-S3 board will do — this is the case the license is meant to " +
                    "cover, and a replacement chip costs a few euro.\n\n" +
                    "It does not have to be blank. If it has been a Crypt-in dongle before, the " +
                    "next step asks before overwriting it — and overwriting destroys that dongle's " +
                    "own seed and the identity its license knows it by. Your 24 words do not bring " +
                    "that one back.",
            },
        ]);

        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            if (_step != 3) { timer.Stop(); timer.Dispose(); return; }
            Evaluate(state);
        };
        timer.Start();

        // Once now as well: Show() has just enabled Next, and waiting 800 ms for
        // the first tick leaves it clickable with nothing said on the screen.
        Evaluate(state);
    }

    /// <summary>
    /// Reports what is on the wire. Unlike the backup wizard this one never
    /// refuses: there is no main dongle to be confused with, because the premise
    /// is that it is gone. What it does do is name a dongle that answers, since
    /// the one case worth catching is a user about to flash over a chip that
    /// still works — their old dongle found in a drawer, or the borrowed one
    /// they are reading this on.
    /// </summary>
    private void Evaluate(Label state)
    {
        var ports = SerialPorts.Programmable();
        _newPort = ports.FirstOrDefault(p =>
            !_portsBefore.Contains(p.PortName, StringComparer.OrdinalIgnoreCase))?.PortName;

        var attached = CryptinDongle.ListSerials();
        if (attached.Count > 0)
        {
            state.ForeColor = Color.DarkGoldenrod;
            state.Text =
                $"A working Crypt-in dongle is answering: {string.Join(", ", attached)}.\n\n" +
                "If that is the chip you mean to program, it will be asked for by name at the next " +
                "step and programming it destroys its seed. If it is a dongle you still need, " +
                "unplug it before going on — a COM port is picked from a list, and lists are how " +
                "the wrong chip gets flashed.";
            _next.Enabled = true;
            return;
        }

        if (_newPort is not null)
        {
            state.ForeColor = Color.ForestGreen;
            state.Text = $"{_newPort} appeared. That is what a chip with no Crypt-in firmware on " +
                         "it looks like: a COM port and no dongle. The next step programs it.";
            _next.Enabled = true;
            return;
        }

        state.ForeColor = SystemColors.GrayText;
        state.Text = ports.Count == 0
            ? "Waiting… no port that can be programmed. The cable has to be on the socket " +
              "marked UART; on the native USB socket a blank chip shows nothing, and no other " +
              "adapter on this machine is one of ours."
            : "Waiting… nothing new since this step opened. You can still go on and pick the port " +
              "by hand at the next step, but then it is on you to know which one it is.";
        _next.Enabled = true;
    }

    // --- step 4: flash and recover ----------------------------------------

    private ComboBox? _ports;
    private ProgressBar? _flashProgress;

    private void BuildFlashAndRecover()
    {
        _heading.Text = "Program the chip and load your seed onto it";
        _detail.Text = "Same cable, same socket marked UART. Program writes the firmware and then " +
                       "walks the chip through the 'recover' command with the words from step 2.";

        _ports = new ComboBox
        {
            Location = new Point(0, 4),
            Size = new Size(380, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        var refresh = new Button { Text = "Refresh", Location = new Point(392, 3), Size = new Size(100, 28) };
        refresh.Click += (_, _) => LoadPorts();

        // Hover and focus, not DropDown. Measured on the backup wizard on
        // 2026-09-03: the list window is built and sized before DropDown is
        // raised, so refilling Items there shows the previous contents and only
        // the open after that is right — "it works only if I press Refresh".
        _ports.MouseEnter += (_, _) => LoadPorts();
        _ports.Enter += (_, _) => LoadPorts();
        _ports.DropDown += (_, _) => LoadPorts();

        // Says out loud what the filtered list leaves out: here more than
        // anywhere, since the premise of this wizard is that the user's dongle
        // is gone, and an empty box is exactly what they are afraid of.
        var uartNote = new Label
        {
            Location = new Point(0, 34),
            Size = new Size(600, 40),
            ForeColor = SystemColors.GrayText,
            Text = SerialPorts.OnlyUartNote,
        };

        var flash = new Button { Text = "Program", Location = new Point(0, 80), Size = new Size(130, 30) };
        flash.Click += async (_, _) => await FlashAndRecoverAsync(flash);

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
                        "CryptinUpdater.exe is not installed: the flash has to be done by hand with esptool.\n\n") +
                    "It takes a few seconds and needs no other tool. When it is done the chip's PIN " +
                    "is your old dongle's PIN — both come from the seed, so nothing new has to be " +
                    "remembered.\n\n" +
                    "Nothing has been asked of the license server yet, and nothing will be until the " +
                    "last step. A chip programmed here and abandoned costs one of your three " +
                    "transfers nothing.\n\n" +
                    "If the seed cannot be loaded automatically you get the manual steps, and they " +
                    "still work: 'recover' lives on the serial console and has no HID equivalent.",
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

        // Enabled even when empty: a disabled box cannot be opened, and opening
        // it is what reloads the list.
        _ports.Enabled = true;
        if (ports.Count == 0)
        {
            _ports.Items.Add(SerialPorts.NoPortText());
            _ports.SelectedIndex = 0;
            return;
        }

        var at = IndexOfPort(_ports, chosen);
        if (at < 0) at = IndexOfPort(_ports, _newPort);
        _ports.SelectedIndex = at >= 0 ? at : 0;
    }

    private static int IndexOfPort(ComboBox box, string? portName)
    {
        if (portName is null) return -1;
        for (var i = 0; i < box.Items.Count; i++)
            if (box.Items[i] is SerialPortInfo info
                && info.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Twin of the backup wizard's, and the warning is if anything more
    /// pointed here: the user is standing in front of this wizard because a
    /// dongle was lost, so the chance that the chip in the socket is the one
    /// working dongle left in the house is not theoretical.</summary>
    private bool ConfirmOverwrite(string portName)
        => MessageBox.Show(this,
            $"The chip on {portName} already holds data where a Crypt-in dongle keeps its keys.\n\n" +
            "If it has been a dongle before, programming it destroys its recovery seed and the " +
            "identity the license server knows it by. Your 24 words rebuild YOUR identity on it, " +
            "not that one, and any license that chip held stays where it is.\n\n" +
            "Updating the firmware of this same dongle? Then Yes is the right answer: the next " +
            "steps put your 24 words back on it and move your license onto it.\n\n" +
            "If it is a new chip that simply came with the manufacturer's firmware, there is " +
            "nothing to lose.\n\n" +
            "Program it anyway?",
            "This chip is not blank", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private async Task FlashAndRecoverAsync(Button flash)
    {
        if (_ports?.SelectedItem is not SerialPortInfo port)
        {
            MessageBox.Show(this,
                "No COM port to program.\n\n" +
                "Connect the cable to the socket marked UART, then open the list above: it " +
                "re-reads the ports every time it opens.",
                "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // The list is filled by UI events and can be stale. On 2026-09-03 a port
        // was unplugged and Program still ran against it; nothing was written
        // that time because the port was simply gone. The case that matters is
        // the other one — a different board taking that COM number in the
        // meantime — so the port is looked up again here, where it costs a
        // registry read.
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
        _flashProgress!.Value = 0;
        try
        {
            var updater = new UpdaterProcess();
            var progress = new Progress<int>(p => _flashProgress.Value = Math.Clamp(p, 0, 100));
            var result = await updater.FirstFlashAsync(port.PortName, progress);

            if (result == FlashResult.AlreadyProvisioned && ConfirmOverwrite(port.PortName))
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
                    FlashResult.AlreadyProvisioned => "Left alone: the chip still holds whatever it held.",
                    _ => $"Programming failed. {updater.LastError}",
                }, "Programming", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // A freshly flashed dongle provisions itself on first boot, so
            // stopping here would leave a working chip with a seed of its own —
            // a new dongle, not the old one rebuilt, and the transfer at the
            // last step would ask the server to move a license to an identity
            // that owns none.
            _flashProgress.Value = 100;
            var recovery = await SerialRecovery.RunAsync(port.PortName, CurrentWords(), justFlashed: true);

            if (recovery.Outcome == SerialRecovery.Outcome.Ok)
            {
                _rebuiltIdentityHex = recovery.PublicKeyHex;
                _seedLoaded = true;

                // Recorded before the message rather than after: from here the
                // only thing between the user and a finished transfer is this
                // record, and it has to survive the window being closed on the
                // instruction as well as by it.
                SaveResume(recovery.PublicKeyHex);

                // The handover matters here for the same reason it does on a
                // backup, and the reason is not obvious: the chip now holds a
                // seed that arrived from outside, on paper. Firmware built after
                // 2026-09-03 acknowledges that itself at the end of
                // recoveryFlow(); older firmware takes the `ok` SerialRecovery
                // sends. When neither lands, the chip goes on offering these 24
                // words to any host it meets — over HID, where
                // CMD_GET_PROVISIONING is exempt from the PIN gate, and over the
                // console, which reprints them every five seconds.
                var stillOffering = recovery.HandoverClosed
                    ? ""
                    : "One thing did not happen: this chip could not be told the phrase has been " +
                      "written down, so it may still offer your 24 words and PIN to any computer " +
                      "it is plugged into. Finish the transfer before putting it away, and if it " +
                      "keeps printing them, connect it to the main window and confirm there.\n\n";

                MessageBox.Show(this,
                    "Chip programmed, and your seed is on it. Its PIN is your old dongle's PIN: " +
                    "they come from the same seed.\n\n" + stillOffering + RestartMessage,
                    "Chip ready — one restart left",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            // Not a dead end — the manual route still works for anyone with a
            // terminal. What it must not do is imply the chip is ready: it is
            // flashed, it carries a seed of its own, and it is nobody's dongle.
            MessageBox.Show(this,
                "The chip is programmed, but the seed could not be loaded onto it automatically.\n\n" +
                $"Reason: {recovery.Detail}\n\n" +
                "This chip does NOT carry your identity yet: it currently holds a seed of its own.\n\n" +
                ManualSteps,
                "Seed not loaded", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    // --- step 5: move the licence -----------------------------------------

    private void BuildTransfer()
    {
        _heading.Text = "Move the license onto it";
        _detail.Text =
            (_resumed
                ? $"Picking up the chip prepared on {_settings.PendingTransfer?.StartedAt.LocalDateTime:dd/MM/yyyy HH:mm}. " +
                  "It is written and carries your seed; what is left is this. "
                : "")
            + "Connect the rebuilt chip on its native USB socket — not the UART one — and unlock " +
              "it with your PIN. The signature it makes is what proves the license is yours.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 260),
            Text =
                "The PIN is the one you had before: it is derived from the seed, and the seed has " +
                "not changed.\n\n" +
                "If no PIN box appears, unplug the chip, wait about ten seconds and plug it back " +
                "in. The chip was rewritten and reset while this application was running and " +
                "Windows can leave a device instance behind — nothing is lost by replugging, the " +
                "seed is already on it.\n\n" +
                "This is the only step that talks to the license server, and the only one that " +
                "spends a transfer. If the server does not recognize the identity these words " +
                "produce, it says so and nothing is spent.\n\n" +
                "Press Next when the chip is connected.",
            ForeColor = SystemColors.GrayText,
        });
    }

    /// <summary>
    /// Unlock, prove it is the chip we prepared, transfer, then hand the new
    /// token to the chip.
    ///
    /// The identity comparison here is weaker than the backup wizard's and says
    /// something different. There it proved two chips share a seed, because both
    /// were in hand. Here the other chip is gone, so all this can prove is that
    /// the dongle answering is the one the recovery wrote — a guard against the
    /// wrong chip in the socket, not against wrong words. The only thing that
    /// knows whether the words were right is the server, and it answers
    /// LICENSE_NOT_FOUND when they were not.
    /// </summary>
    private async Task<bool> TransferAsync()
    {
        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0)
        {
            MessageBox.Show(this,
                "No dongle is answering. Connect the rebuilt chip on its native USB socket — on " +
                "the UART socket it is a COM port and speaks no HID.",
                "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _dongle.Disconnect();
        if (!_dongle.Connect(attached[0], waitMs: 5000))
        {
            MessageBox.Show(this, "The dongle will not open. Disconnect it and connect it again.",
                "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        using var pin = new PinForm(_dongle, _dongle.UsbSerial);
        if (pin.ShowDialog(this) != DialogResult.OK) return false;

        byte[] identity;
        try
        {
            identity = await Task.Run(() => _dongle.GetIdentityPubKey());
        }
        catch (CryptinDongleException e)
        {
            MessageBox.Show(this, e.Message, "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Only when the recovery managed to print one. A chip recovered by hand
        // over a terminal has no printed value to compare against, and refusing
        // the transfer for that would punish the fallback route the manual steps
        // send people down.
        var seen = Convert.ToHexString(identity);
        if (_rebuiltIdentityHex.Length > 0
            && !seen.Equals(_rebuiltIdentityHex, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "This is not the chip that was prepared.\n\n" +
                $"The recovery wrote {Shorten(_rebuiltIdentityHex)}; the dongle in the socket " +
                $"answers {Shorten(seen)}.\n\n" +
                "Put the prepared chip in, or close this window and start again to prepare " +
                "another one.",
                "Different chip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        RecoveryResponse moved;
        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            moved = await Task.Run(() => client.RecoverAsync(_dongle, _reason));
        }
        catch (LicenseApiException e)
        {
            MessageBox.Show(this, Describe(e), "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Transfer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // The licence has moved on the server by now, whatever happens next. So
        // the pending record goes first: leaving it would offer to redo a
        // transfer that is done, and doing it twice would spend a second one of
        // three for nothing.
        _settings.PendingTransfer = null;
        // The server voids any open rekey ticket of the licence on a transfer
        // (report 41afc3fd): the ticket belonged to the chip that was main at
        // the time. Kept, it would offer a move that can only fail.
        _settings.PendingRekey = null;
        _settings.Save();

        // An expired licence can still be moved, on purpose: renewal wants the
        // physical chip, so refusing the transfer off a broken one would leave
        // nothing to renew from. The token that comes back is therefore
        // sometimes already dead, and pushing it would earn an ERR_JWT_EXPIRED
        // that reads as a transport fault in the log. Better to not send it and
        // say the true thing.
        //
        // Two sources, deliberately: `expired` is what the server says, and the
        // date is what it has always sent. A server that predates the flag
        // leaves it false, and the comparison still catches the case.
        var expired = moved.Expired || moved.ExpiresAt <= DateTimeOffset.Now;

        // The chip refuses to encrypt without a token it accepts, so the JWT the
        // server just issued has to reach it. Reported rather than thrown: the
        // transfer succeeded, and a user told "transfer failed" here would try
        // again and spend another of their three.
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

        // Three endings, and the middle one is not a failure. Saying "expired"
        // in the same breath as "transferred" is the point: moving it was still
        // the right thing, because it is what makes renewing possible at all.
        var closing = expired
            ? $"It expired on {moved.ExpiresAt.UtcDateTime:dd/MM/yyyy}, so the token has not been " +
              "written to the chip — the chip would refuse it, and that refusal would read as " +
              "a fault.\n\n" +
              "This is not a wasted transfer. Renewing needs the physical dongle, so the license " +
              "had to reach a working chip before it could be renewed at all. Renew it on this " +
              "one and it will encrypt again."
            : landed
                ? "The token has been written to the chip: it can encrypt again straight away."
                : $"One thing is left: the chip did not take the new token ({why}). Close this " +
                  "window and press Load license in the main window — the server has already " +
                  "moved the license and will hand the same token back.";

        MessageBox.Show(this,
            "The license is on this dongle now. A transfer moves the license, it does not "
            + (expired
                ? "renew it.\n\n"
                : $"extend it — it still expires {moved.ExpiresAt.UtcDateTime:dd/MM/yyyy}.\n\n")
            + closing,
            "Transfer", MessageBoxButtons.OK,
            landed && !expired ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return true;
    }

    /// <summary>
    /// The server's codes, said in words. The date on a rate limit comes off
    /// <see cref="LicenseApiException.NextAvailable"/> and is printed verbatim,
    /// which is the lesson of 2026-09-03: the old backup code searched for the
    /// string "next_available" inside the message prose, a branch that could
    /// only fire if the server wrote that token into its own sentence, and so
    /// announced a date it then did not show.
    ///
    /// Here the same field means something else — a rolling 365-day window, not
    /// a calendar year — so the sentence around it says which, rather than
    /// borrowing the backup wording and being subtly wrong.
    /// </summary>
    private static string Describe(LicenseApiException e) => e.Code switch
    {
        LicenseErrorCodes.RecoveryRateLimit =>
            "This license has already been transferred three times in the last 365 days. " +
            (e.NextAvailable is { Length: > 0 } when_
                ? $"The oldest of them falls out of the window on {when_}, and another becomes " +
                  "possible then."
                : "The server did not say when the next one opens."),

        // The one that actually means "wrong words", and the only check there is:
        // the server finds the licence from K_identity and nothing else.
        // LicenseClient has already retried it once, so this is the second.
        LicenseErrorCodes.ConcurrentRetry =>
            "Another operation on this license was in progress at the same moment. " +
            "Nothing was changed: try again in a few seconds.",

        LicenseErrorCodes.LicenseNotFound =>
            "The server has no license for the identity these 24 words produce.\n\n" +
            "Either a word is wrong — the checksum passes on plenty of phrases that are not " +
            "yours — or this seed belongs to a dongle that was never activated. Nothing has been " +
            "spent: check the words and try again.",

        LicenseErrorCodes.SameDevice =>
            "The license is already on this chip. Nothing to transfer.",

        LicenseErrorCodes.AlreadyActivated =>
            "This chip is the main dongle of another license. One chip cannot sit on two.",

        // Live since 2026-09-04. /api/recovery used to check the licenze table
        // and never dongle_trusted, so a chip that was somebody's registered
        // backup sailed through and ended up authorized twice; the guard its
        // sibling register_backup always had is now there too.
        //
        // Split on the flag, because the two halves are not two wordings of one
        // problem. True is not really a failure: the user already owns the
        // answer and is standing in the wrong wizard. False is a chip they
        // cannot use. And no recovery is spent either way — worth saying,
        // because "409" on the third of three reads like a loss.
        LicenseErrorCodes.AlreadyRegistered when e.SameLicence == true =>
            "Good news: this chip is already the registered backup of this license, so it is " +
            "authorized and works right now.\n\n" +
            "You do not need a transfer at all. Close this window, plug it in, and carry on — " +
            "that is what a backup is for. None of your three transfers has been used.\n\n" +
            "Prepare a different chip only if you want a second dongle beside this one.",

        LicenseErrorCodes.AlreadyRegistered when e.SameLicence == false =>
            "This chip is the registered backup of a different license, and one chip cannot " +
            "cover two.\n\n" +
            "Use a spare board instead. Nothing has been spent: your three transfers are " +
            "untouched.",

        // The flag is missing, so say what covers both.
        LicenseErrorCodes.AlreadyRegistered =>
            "This chip is registered as a backup dongle — on this license or on another.\n\n" +
            "If it is the backup of THIS license you do not need a transfer at all: a backup is " +
            "already authorized, so just use it. Otherwise prepare a different chip. Nothing has " +
            "been spent either way.",

        LicenseErrorCodes.LicenseRevoked =>
            "The license has been revoked, so it cannot be moved.",

        LicenseErrorCodes.InvalidSignature =>
            "The dongle's signature was refused. Unplug it, plug it back in and try again.",

        LicenseErrorCodes.ChallengeExpired =>
            "The server's challenge expired before the dongle answered. Press Next again.",

        // Unreachable from the interface — the reason is a radio button, not a
        // typed string — but a server that tightens the list would land here,
        // and a bare HTTP line would send someone hunting through their words.
        LicenseErrorCodes.InvalidReason =>
            "The server did not accept the reason given for the transfer. This is a fault in the " +
            "application, not in your words or your chip.",

        _ => e.Message,
    };

    private static string Shorten(string hex)
        => hex.Length <= 16 ? hex : hex[..8] + "…" + hex[^8..];

    // --- step 6: what changed ---------------------------------------------

    private void BuildDone()
    {
        _heading.Text = "Done";
        _detail.Text = "The license lives on this chip now.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 240),
            Text =
                "The old dongle is no longer authorized. If it turns up, it still opens files " +
                "encrypted with this seed — the dongle governs the data — but it will not encrypt " +
                "anything new, and the server answers “not authorized on the license” for it.\n\n" +
                "Now is the moment to register a backup, if you do not have one: Backup dongle in " +
                "the main window, free, once a year. It is the difference between the next broken " +
                "chip costing an evening and costing nothing.\n\n" +
                "Keep the 24 words where they are. They are still the only way back if this chip " +
                "goes the same way, and there is nobody to call without them.",
        });
    }
}
