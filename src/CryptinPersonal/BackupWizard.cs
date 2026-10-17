using CryptinSDK.Dongle;
using CryptinSDK.Licensing;
using CryptinSDK.Seed;
using CryptinSDK.Updater;

namespace CryptinPersonal;

/// <summary>
/// Registering a second chip on the licence.
///
/// **Registration, not transfer.** The backup is added to the existing licence
/// and the main dongle stays valid; both work afterwards. <c>/api/recovery</c>
/// is the transfer, and reaching for it here would move the licence and leave
/// one chip dead.
///
/// Both chips are needed, one at a time, and the wizard is built around the swap
/// rather than pretending it does not happen: read the backup's K_attestation
/// first, then ask for the main one back to sign the challenge.
///
/// Step 4 also acknowledges the backup's handover once the seed is confirmed.
/// That is not housekeeping: without it the finished backup hands the main
/// dongle's 24 words and PIN to any host that plugs it in.
///
/// **Never walked from the interface.** The six steps were exercised by the
/// hardware harness on 2026-08-01, with two physical chips and a real
/// registration against the server; the wizard itself has not been through a
/// pair of hands. Written from the contract in
/// `CRYPTIN_DLL_RICOGNIZIONE_API.md` §11 and from the protocol.
/// </summary>
internal sealed class BackupWizard : Form
{
    private readonly CryptinDongle _dongle;
    private readonly AppSettings _settings;

    private readonly Label _heading = new();
    private readonly Label _detail = new();
    private readonly Panel _body = new();
    private readonly Button _back = new();
    private readonly Button _next = new();

    private int _step = 1;

    /// <summary>True when this run of the wizard was entered on a backup that
    /// was already flashed and recovered, in an earlier run of the application.
    /// Steps 1 to 3 are behind it and there is no way back to them: the words
    /// are not kept anywhere, by design.</summary>
    private readonly bool _resumed;

    /// <summary>The identity the recovery printed on the destination chip,
    /// carried across the restart so the mismatch message at step 4 can show
    /// both values instead of only the disagreement.</summary>
    private string _resumedBackupIdentityHex = "";

    /// <summary>
    /// Set once this process has prepared a backup, and never cleared: the
    /// restart is the point. Opening the wizard again in the same process would
    /// walk into the step 4 that does not work -- which is the whole defect
    /// being worked around -- so the entry points ask this first.
    /// </summary>
    internal static bool RestartRequired { get; private set; }

    /// <summary>What to say when the chip is written and the process has to end
    /// before anything else can happen. Shared by the wizard and the main
    /// window, because a tester who presses Backup again must read the same
    /// thing they just read.</summary>
    internal const string RestartMessage =
        "The backup is ready: the chip carries the main dongle's seed.\n\n" +
        "Close Crypt-in now and open it again. It will offer to finish the backup and pick " +
        "up from the PIN check — nothing has to be typed twice, not the 24 words and not " +
        "the programming.\n\n" +
        "Why a restart and not a button: the chip was rewritten and reset while this " +
        "application held it open, and Windows replaced the device underneath. Reconnecting " +
        "from inside was tried first and is not enough; only ending the process clears it.\n\n" +
        "Until then the backup is NOT registered on the license. It opens your files — the " +
        "dongle governs the data — but the server does not know it exists, and that is the " +
        "half that shows up on the day you need it.";

    private readonly TextBox[] _words = new TextBox[Bip39.WordCount];
    private string _mainSerial = "";
    private byte[]? _mainIdentity;

    /// <summary>Twin of SetupWizard.ConfirmOverwrite, with the difference that
    /// matters here: in this wizard the chip being written is the destination of
    /// a backup, so the user has already said what they intend. The warning is
    /// still worth showing -- the cost falls on whatever that chip used to be,
    /// which might have been someone's only dongle.</summary>
    private bool ConfirmOverwrite(string portName)
        => MessageBox.Show(this,
            $"The chip on {portName} already holds data where a Crypt-in dongle keeps its keys.\n\n" +
            "If it has been a dongle before, programming it destroys its recovery seed and the " +
            "identity the license server knows it by. The 24 words do not bring that identity " +
            "back, and any license stays with the old one.\n\n" +
            "If it is a new chip that simply came with the manufacturer's firmware, there is " +
            "nothing to lose.\n\n" +
            "Use it as the backup anyway?",
            "This chip is not blank", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    /// <summary>True only once the main dongle's seed is actually on the backup.
    /// Step 3 can succeed at flashing and fail at this, and the two must not be
    /// confused: a flashed chip provisions itself, so it looks like a working
    /// dongle while being a backup of nothing.</summary>
    private bool _seedLoaded;

    /// <summary>The route that existed before the recovery was automated. Kept
    /// because it is still true, and because it is the only way out on a machine
    /// where the serial port cannot be opened at all.</summary>
    private const string ManualSteps =
        "To load it by hand, on the same port:\n" +
        "  1. open a serial terminal at 115200 baud\n" +
        "  2. issue the 'recover' command\n" +
        "  3. enter the 24 words from step 1\n" +
        "  4. type YES when it asks\n" +
        "  5. check that the PIN shown is identical to the main dongle's\n\n" +
        "The PIN matches because it comes from the seed: if it differs, a word is wrong.";
    private byte[]? _backupAttestation;

    /// <param name="resume">True to pick up the backup recorded in
    /// <see cref="AppSettings.PendingBackup"/> at step 4. False always starts at
    /// step 1, which is the only way to replace a prepared backup that turned
    /// out to be the wrong chip -- so the caller decides, and the wizard never
    /// resumes behind someone's back.</param>
    public BackupWizard(CryptinDongle dongle, AppSettings settings, bool resume = false)
    {
        _dongle = dongle;
        _settings = settings;

        Text = "Crypt-in — backup dongle";
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

        // An unfinished backup is picked up where it stopped, not started over:
        // step 3 has already written a chip, and running it again would write it
        // a second time for nothing.
        if (resume && !RestartRequired && settings.PendingBackup is { } pending)
        {
            if (LoadResume(pending))
            {
                _resumed = true;
                Show(4);
                return;
            }

            // Unreadable, so nothing can ever be resumed from it -- and left
            // alone it would offer to finish a backup on every launch, forever.
            settings.ClearPendingBackup();
        }

        Show(1);
    }

    /// <summary>Puts back the three values step 4 and step 5 need. False if the
    /// stored state cannot be read, in which case the wizard starts from step 1
    /// -- redoing a backup is tedious, refusing to open is worse.</summary>
    private bool LoadResume(BackupResume pending)
    {
        try
        {
            _mainIdentity = Convert.FromHexString(pending.MainIdentityHex);
        }
        catch (FormatException)
        {
            return false;
        }

        if (pending.MainSerial.Length == 0 || _mainIdentity.Length == 0) return false;

        _mainSerial = pending.MainSerial;
        _resumedBackupIdentityHex = pending.BackupIdentityHex;
        _seedLoaded = true;
        return true;
    }

    /// <summary>Written the instant the chip becomes a backup, before anything
    /// else can go wrong -- including the user closing the window on the message
    /// that tells them to.</summary>
    private void SaveResume(string backupIdentityHex)
    {
        if (_mainIdentity is null) return;

        _settings.PendingBackup = new BackupResume
        {
            MainSerial = _mainSerial,
            MainIdentityHex = Convert.ToHexString(_mainIdentity),
            BackupIdentityHex = backupIdentityHex,
            StartedAt = DateTimeOffset.Now,
        };
        _settings.Save();
        RestartRequired = true;
    }

    private void Show(int step)
    {
        _step = Math.Clamp(step, 1, 6);
        _body.Controls.Clear();
        // No going back once the swap started -- and on a resumed wizard not at
        // all: steps 1 to 3 belong to a run that has ended, and the 24 words
        // they need were deliberately not kept.
        _back.Enabled = !_resumed && _step is > 1 and < 5;
        _next.Enabled = true;
        _next.Text = _step == 6 ? "Finish" : "Next";

        switch (_step)
        {
            case 1: BuildSeed(); break;
            case 2: BuildInsertBackup(); break;
            case 3: BuildFlashAndRecover(); break;
            case 4: BuildPinCheck(); break;
            case 5: BuildRegister(); break;
            case 6: BuildStorage(); break;
        }
    }

    private async Task NextAsync()
    {
        switch (_step)
        {
            case 1:
                if (Bip39.Validate(CurrentWords()) is { } problem)
                {
                    MessageBox.Show(this, problem, "Recovery phrase",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // Read the main dongle's identity now, while it is still the one
                // plugged in and unlocked. After the swap there is no way back to
                // it without asking for the PIN again, and step 4 needs it to
                // prove the two chips share a seed.
                try
                {
                    _mainSerial = _dongle.UsbSerial;
                    _mainIdentity = await Task.Run(() => _dongle.GetIdentityPubKey());
                }
                catch (CryptinDongleException e)
                {
                    MessageBox.Show(this,
                        $"Cannot read the main dongle's identity: {e.Message}",
                        "Main dongle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // Snapshot the COM ports while the main dongle is still the one
                // plugged in. A port that appears after the swap is the backup,
                // and on a chip that has never been programmed that port is the
                // only sign of it there is -- see Evaluate.
                _portsBeforeSwap = SerialPorts.Programmable().Select(p => p.PortName).ToArray();
                Show(2);
                return;

            case 2:
                Show(_step + 1);
                return;

            case 3:
                // Measured on 2026-09-02: with the seed not loaded, the wizard
                // walked on to step 4 and asked for a PIN that would only exist
                // if this step had worked -- so the user was asked for a
                // credential nobody had ever given them, at the end of a road
                // with no way back. Refusing to advance costs one dialog and
                // removes the dead end entirely.
                if (!_seedLoaded)
                {
                    MessageBox.Show(this,
                        "The backup does not carry the main dongle's seed yet, so there is " +
                        "nothing for the next step to check.\n\n" +
                        "Run Program again, or load the seed by hand:\n\n" + ManualSteps,
                        "Not a backup yet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Show(_step + 1);
                return;

            case 4:
                if (!await CheckBackupAsync()) return;
                Show(5);
                return;

            case 5:
                if (!await RegisterAsync()) return;
                Show(6);
                return;

            default:
                DialogResult = DialogResult.OK;
                Close();
                return;
        }
    }

    // --- step 1: the 24 words ---------------------------------------------

    private void BuildSeed()
    {
        _heading.Text = "The main dongle's 24 words";
        _detail.Text = "The backup must be born from the same seed as the main dongle. " +
                       "A chip with a different seed registers without error and then never finds " +
                       "the license again — that is the case this wizard exists to catch.";

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

        // Per-word feedback while typing; the phrase-level check comes after.
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

    // --- step 2: the backup must be a different chip ----------------------

    private string[] _portsBeforeSwap = [];
    private string? _newPort;
    private CheckBox? _blank;

    private void BuildInsertBackup()
    {
        _heading.Text = "Plug in the backup dongle";
        _detail.Text = $"Disconnect the main dongle ({_mainSerial}) and connect the other chip. " +
                       "A chip that has never been programmed has no dongle interface yet: it shows " +
                       "up as a COM port and nothing else, and step 3 is where it gets the firmware.";

        var state = new Label { Location = new Point(0, 0), Size = new Size(600, 90) };

        _blank = new CheckBox
        {
            Location = new Point(0, 98),
            Size = new Size(600, 44),
            Text = "The chip in the socket has never been programmed \u2014 it is not the main dongle.",
            Visible = false,
        };
        _blank.CheckedChanged += (_, _) => Evaluate(state);

        _body.Controls.AddRange([state, _blank]);

        var timer = new System.Windows.Forms.Timer { Interval = 800 };
        timer.Tick += (_, _) =>
        {
            if (_step != 2) { timer.Stop(); timer.Dispose(); return; }
            Evaluate(state);
        };
        timer.Start();

        // Once now as well: Show() has just enabled Next, and waiting 800 ms for
        // the first tick leaves it clickable with nothing attached at all.
        Evaluate(state);
    }

    /// <summary>
    /// Four states, and the fourth is the one this step used to get wrong: a chip
    /// straight out of the bag answers no HID at all, so a guard that waits for a
    /// dongle with a serial different from the main one can never be satisfied by
    /// it -- while the flash that would give it that interface is the *next*
    /// step. The wizard blocked exactly the chip it exists to enrol.
    ///
    /// The dangerous state stays blocked: the main dongle answering here is
    /// refused outright. What stands in for a serial on a blank chip is a COM
    /// port plus an explicit statement from the user, and it has to be explicit,
    /// because nothing on this machine distinguishes a blank chip on the UART
    /// socket from the main dongle on its own UART socket -- and step 3 writes
    /// over whichever one it is, seed and K_attestation included.
    /// </summary>
    private void Evaluate(Label state)
    {
        var attached = CryptinDongle.ListSerials();

        if (attached.Contains(_mainSerial, StringComparer.OrdinalIgnoreCase))
        {
            OfferBlankChip(false);
            state.ForeColor = Color.Firebrick;
            state.Text = $"This is still the main dongle ({_mainSerial}). " +
                         "The backup must be a second physical chip.";
            _next.Enabled = false;
            return;
        }

        if (attached.Count > 0)
        {
            OfferBlankChip(false);
            state.ForeColor = Color.ForestGreen;
            state.Text = $"Found a different chip: {attached[0]}";
            _next.Enabled = true;
            return;
        }

        var ports = SerialPorts.Programmable();

        // Programmable() carries only CH343 bridges, so a port that appears here
        // is a board and not a phone, a printer or the motherboard's own COM1 --
        // and the port named to the user is one that can actually be flashed.
        _newPort = ports.FirstOrDefault(p =>
            !_portsBeforeSwap.Contains(p.PortName, StringComparer.OrdinalIgnoreCase))?.PortName;

        // Sets, not counts. Measured 2026-09-01: this machine always carries a
        // motherboard COM1, so "no ports at all" never happened and the waiting
        // state below was unreachable -- with nothing plugged in at all the user
        // got the warning meant for an ambiguous port instead, and was offered
        // the confirmation that leads to a flash. What actually distinguishes
        // the two is whether anything CHANGED since the main dongle was
        // unplugged, and that is what is asked here.
        var vanished = _portsBeforeSwap
            .Any(before => !ports.Any(p => p.PortName.Equals(before, StringComparison.OrdinalIgnoreCase)));

        if (_newPort is null && !vanished)
        {
            OfferBlankChip(false);
            state.ForeColor = SystemColors.GrayText;
            state.Text = "Waiting for the backup dongle\u2026 nothing is answering and nothing has " +
                         "changed since the main dongle came out. A chip with no firmware on it needs " +
                         "the cable on the socket marked UART; on the native socket there is nothing " +
                         "to see yet.";
            _next.Enabled = false;
            return;
        }

        OfferBlankChip(true);
        state.ForeColor = Color.DarkGoldenrod;
        state.Text = _newPort is not null
            ? $"No dongle is answering, and {_newPort} appeared after the main one was unplugged. " +
              "That is what a chip with no firmware on it looks like. Confirm below and step 3 " +
              "will program it."
            : "No dongle is answering, and a port that was here before has gone while no new " +
              "one appeared. A chip plugged into the socket the main dongle just left can be " +
              $"handed the same COM number, so this cannot tell it from {_mainSerial} itself on " +
              "its UART socket -- and step 3 would write over that one's seed and K_attestation. " +
              "Check the main dongle is out, cable included, before confirming.";
        _next.Enabled = _blank!.Checked;
    }

    private void OfferBlankChip(bool offered)
    {
        if (_blank is null) return;

        // Re-arm when the state stops being the blank-chip one: a claim made
        // about one chip must not still be ticked over the next thing plugged
        // in. Clearing it re-enters Evaluate once, and that pass finds it
        // already false and raises nothing further.
        if (!offered && _blank.Checked) _blank.Checked = false;

        _blank.Visible = _blank.Enabled = offered;
    }

    // --- step 3: flash and recover ----------------------------------------

    private ComboBox? _ports;
    private ProgressBar? _flashProgress;

    private void BuildFlashAndRecover()
    {
        _heading.Text = "Program the backup and load the seed onto it";
        _detail.Text = "Connect the cable to the socket marked UART on the backup chip. " +
                       "On the native socket the dongle is pure HID and exposes no COM port: " +
                       "that is not a fault, it is how it has to be for the end user.";

        _ports = new ComboBox
        {
            Location = new Point(0, 4),
            Size = new Size(380, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        var refresh = new Button { Text = "Refresh", Location = new Point(392, 3), Size = new Size(100, 28) };
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

        // Says out loud what the filtered list leaves out, so an empty box does
        // not read as a broken app to someone holding a working dongle.
        var uartNote = new Label
        {
            Location = new Point(0, 34),
            Size = new Size(600, 40),
            ForeColor = SystemColors.GrayText,
            Text = SerialPorts.OnlyUartNote,
        };

        var flash = new Button { Text = "Program", Location = new Point(0, 80), Size = new Size(130, 30) };
        flash.Click += async (_, _) => await FlashBackupAsync(flash);

        _flashProgress = new ProgressBar { Location = new Point(0, 120), Size = new Size(492, 20) };

        var updater = new UpdaterProcess();
        if (!updater.IsInstalled) flash.Enabled = false;

        // Both halves are automatable, and until 2026-09-02 the second one was
        // not automated: this screen printed instructions telling the user to
        // open a serial terminal, type 'recover' and paste the 24 words. The
        // comment here said "the flash is automatable; the recovery is not".
        //
        // It is. tools/recover_over_serial.py does the whole exchange in thirty
        // lines and worked first try. What made it worth changing is that a
        // clean Windows 11 has no serial terminal and the beta package ships
        // none -- so those instructions sent a tester to a program they did not
        // have, and the backup was unreachable for the audience it was built
        // for. The manual route survives as the fallback, in ManualSteps.
        _body.Controls.AddRange([
            _ports, refresh, uartNote, flash, _flashProgress,
            new Label
            {
                Location = new Point(0, 150),
                Size = new Size(600, 180),
                Text =
                    (updater.IsInstalled ? "" :
                        "CryptinUpdater.exe is not installed: the flash has to be done by hand with esptool.\n\n") +
                    "Program writes the firmware and then loads the seed from step 1 onto the " +
                    "chip, over the same cable. It takes a few seconds and needs no other tool.\n\n" +
                    "When it is done the backup's PIN is the main dongle's PIN, because both come " +
                    "from the same seed. Step 4 checks exactly that.\n\n" +
                    "If the seed cannot be loaded automatically you get the manual steps, and they " +
                    "still work: the 'recover' command lives on the serial console and there is no " +
                    "HID equivalent.",
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

        // Enabled even with nothing in it: a disabled box cannot be opened, and
        // opening it is what reloads the list. Disabling it made the empty state
        // the one state the instruction on screen could not get anyone out of.
        _ports.Enabled = true;
        if (ports.Count == 0)
        {
            _ports.Items.Add(SerialPorts.NoPortText());
            _ports.SelectedIndex = 0;
            return;
        }

        // In order: what the user already picked, then the port step 2 saw
        // arrive with the backup, then the first one. Step 2 knows which chip
        // this is and making the user find it again is how the wrong one gets
        // flashed -- but a choice already made outranks a guess.
        var at = IndexOfPort(_ports, chosen);
        if (at < 0) at = IndexOfPort(_ports, _newPort);
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

    private async Task FlashBackupAsync(Button flash)
    {
        if (_ports?.SelectedItem is not SerialPortInfo port)
        {
            MessageBox.Show(this,
                "No COM port to program.\n\n" +
                "Connect the cable to the socket marked UART on the backup chip, then open the " +
                "list above: it re-reads the ports every time it opens.",
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
        _flashProgress!.Value = 0;
        try
        {
            var updater = new UpdaterProcess();
            var progress = new Progress<int>(p => _flashProgress.Value = Math.Clamp(p, 0, 100));
            var result = await updater.FirstFlashAsync(port.PortName, progress);

            // A chip that has already been a Crypt-in dongle is refused by the
            // updater, and rightly: writing over one destroys a seed and an
            // identity the licence server knows. But refusing outright is wrong
            // *here*. Reusing a spare chip is the ordinary way to make a backup,
            // not an edge case, and SetupWizard has offered the confirm-and-force
            // route since 2026-08-31 while this wizard just stopped. Found on
            // 2026-09-02, on the first chip that had been a dongle before.
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

            // The chip has firmware now, and a seed of its own: a freshly
            // flashed dongle provisions itself on first boot. Stopping here is
            // what produced the state measured on 2026-09-02 -- a second
            // independent dongle, and a step 4 asking for a PIN that would only
            // exist if this next part had happened.
            _flashProgress.Value = 100;
            var recovery = await SerialRecovery.RunAsync(port.PortName, CurrentWords(), justFlashed: true);

            if (recovery.Outcome == SerialRecovery.Outcome.Ok)
            {
                _resumedBackupIdentityHex = recovery.PublicKeyHex;
                _seedLoaded = true;

                // Recorded before the message rather than after: from here the
                // only thing between the user and a finished backup is this
                // record, and it has to survive the window being closed on the
                // instruction as well as by it.
                SaveResume(recovery.PublicKeyHex);

                // Said here and not only at step 4, because the restart that
                // sits between the two is exactly the interval this warns
                // about: a chip that was not acknowledged offers the main
                // dongle's 24 words to any computer it meets, and between
                // these two steps it may meet several.
                var stillOffering = recovery.HandoverClosed
                    ? ""
                    : "One thing did not happen: this chip could not be told the phrase has been " +
                      "written down, so it may still offer the main dongle's 24 words to any " +
                      "computer it is plugged into. Do not put it away before finishing — the " +
                      "check at step 4 closes it.\n\n";

                MessageBox.Show(this,
                    "Chip programmed, and the seed is on it. Its PIN is now the main dongle's " +
                    "PIN: they come from the same seed.\n\n" + stillOffering + RestartMessage,
                    "Backup ready — one restart left",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Walking on to step 4 in this process is the dead end measured
                // on 2026-09-02: the PIN box either never appears or refuses a
                // PIN the chip never saw. Closing here is the decision to go
                // with the restart instead of fighting it.
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            // Not a dead end: the manual route is the one that worked until
            // today and it still works for anyone with a terminal. What it must
            // not do is imply the chip is ready. It is flashed, it carries a
            // seed of its own, and it is a backup of nothing.
            MessageBox.Show(this,
                "The chip is programmed, but the seed could not be loaded onto it automatically.\n\n" +
                $"Reason: {recovery.Detail}\n\n" +
                "This chip is NOT a backup yet: it currently holds a seed of its own.\n\n" +
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

    // --- step 4: prove it is the same seed --------------------------------

    private void BuildPinCheck()
    {
        _heading.Text = "Check that it is the same seed";
        _detail.Text =
            (_resumed
                ? $"Picking up the backup prepared on {_settings.PendingBackup?.StartedAt.LocalDateTime:dd/MM/yyyy HH:mm}. " +
                  "The chip is written and carries the seed; what is left is this check and the registration. "
                : "")
            + "Unlock the backup with the main dongle's PIN: if the seed is the right one, " +
              "it is the same PIN. Then the two K_identity values are compared, which is the real proof.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 120),
            ForeColor = SystemColors.GrayText,
            Text =
                "If a word is wrong the PIN will not match and the attempt is counted: " +
                "after three, the chip blocks. This is not unrecoverable — a 'recover' resets the " +
                "counter — but it means programming the chip again"
                + (_resumed ? ", from a fresh run of this wizard.\n\n" : ".\n\n") +
                // Written down because it is what actually works, and because a
                // user who is not told will read the missing dialog as a broken
                // app. The chip has just been rewritten and reset: Windows tears
                // the old device instance down and builds a new one, and the app
                // can be left holding the one that no longer exists. Measured
                // repeatedly on 2026-09-02 -- and unplugging is the only thing
                // that has ever cleared it.
                "If no PIN box appears, unplug the backup, wait about ten seconds, and plug it " +
                "back in: the PIN box comes up on its own when it is recognized again. Nothing " +
                "is lost by doing this — the chip already carries the seed.\n\n" +
                "Press Next when the backup is connected.",
        });
    }

    /// <summary>
    /// The documented check is "read the backup's PIN and compare it". That is
    /// impossible: GET_SECRET sits behind the PIN gate, so reading the PIN would
    /// need the PIN. What is possible is stronger — unlock the backup with the
    /// main dongle's PIN, then compare the two K_identity values. Both derive
    /// from the seed, so equality proves the seed matches; the PIN merely
    /// suggests it.
    /// </summary>
    private async Task<bool> CheckBackupAsync()
    {
        var attached = CryptinDongle.ListSerials();
        var backup = attached.FirstOrDefault(s => !s.Equals(_mainSerial, StringComparison.OrdinalIgnoreCase));
        if (backup is null)
        {
            MessageBox.Show(this, "The backup dongle does not appear to be connected.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _dongle.Disconnect();
        if (!_dongle.Connect(backup, waitMs: 5000))
        {
            MessageBox.Show(this, "The backup will not open. Disconnect it and connect it again.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        using var pin = new PinForm(_dongle, _dongle.UsbSerial);
        if (pin.ShowDialog(this) != DialogResult.OK) return false;

        byte[] identity;
        try
        {
            identity = await Task.Run(() => _dongle.GetIdentityPubKey());
            _backupAttestation = await Task.Run(() => _dongle.GetAttestationPubKey());
        }
        catch (CryptinDongleException e)
        {
            MessageBox.Show(this, e.Message, "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (_mainIdentity is null)
        {
            // Recorded on the main dongle before the swap; if it is missing the
            // wizard was entered in a way that skipped step 1.
            MessageBox.Show(this, "The main dongle's K_identity is missing: start the wizard again.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (!identity.AsSpan().SequenceEqual(_mainIdentity))
        {
            // On a resumed wizard step 3 is not a place that can be gone back
            // to: it belongs to a run that has ended, and the words it needs
            // were never kept. Saying "go back to step 3" there would send
            // someone looking for a button that is not on the screen.
            var route = _resumed
                ? "If this is not the chip you prepared, close this window, put that one in and " +
                  "press Backup again. If it is, close this window, press Backup and choose to " +
                  "start a new backup: the words are asked for at step 1 and the chip is " +
                  "programmed again at step 3."
                : "Go back to step 3 and redo the recover, checking the words.";

            // Both values, because one of them tells which half went wrong: a
            // chip answering the identity the recovery printed is the chip that
            // was prepared, and then the words were wrong; a different one is a
            // different chip in the socket.
            var seen = Convert.ToHexString(identity);
            var expected = _resumedBackupIdentityHex.Length > 0
                ? $"\nThe recovery printed {Shorten(_resumedBackupIdentityHex)}; this chip answers {Shorten(seen)}.\n"
                : "";

            MessageBox.Show(this,
                "This chip has a different seed from the main dongle.\n\n" +
                "Registering it anyway would not raise an error, but every check afterwards would " +
                "answer “license not found”: the server looks the license up from the K_identity.\n" +
                expected + "\n" + route,
                "Different seed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Close the handover on the backup, and do it here rather than at the
        // end: this is the first moment it is honest to. The phrase was typed
        // at step 1 and the PIN has just opened the session, so nothing is
        // being shut that the user does not already have on paper.
        //
        // **Why it has to happen at all -- measured 2026-09-01**, on a chip this
        // wizard had just produced. CMD_GET_PROVISIONING is exempt from the PIN
        // gate (hid_device.cpp), by a rule written for a chip nobody can know
        // the PIN of yet. On a backup that rule inverts: the chip holds the
        // MAIN dongle's seed. So a backup left unacknowledged hands the main's
        // 24 words and PIN to any host it is plugged into -- the drawer copy,
        // the one kept far from everything, readable by whoever finds it.
        //
        // Since 2026-09-03 this is the second line of defence, not the first:
        // step 3 closes the handover as the recovery ends, which is what the
        // restart introduced that day made necessary -- waiting until here
        // would leave the window open across a stop of the application, and
        // across a "not now" that puts the chip in a drawer. This stays because
        // a resumed wizard reaches step 4 without having run step 3 at all,
        // and because it is the only route left for a chip flashed before that
        // change. Idempotent either way.
        //
        // Only on a seed that matched. A chip with its own seed owns its own
        // handover, and acknowledging that would destroy the only copy of a
        // phrase nobody has written down.
        var handoverClosed = true;
        try
        {
            _dongle.AckProvisioning();   // idempotent in firmware: OK even if already set
        }
        catch (CryptinDongleException)
        {
            // A chip that never had a handover pending, or firmware without the
            // command. Neither is a reason to fail a registration that is
            // otherwise sound -- but it must be said, because what stays open
            // is exactly the copy that will live in a drawer.
            handoverClosed = false;
        }

        MessageBox.Show(this,
            "Same seed, confirmed: the two K_identity values match."
            + (handoverClosed
                ? "\n\nThis chip will not offer the 24 words to a computer again: it now knows " +
                  "they have been written down."
                : "\n\nThis chip could not be told the phrase has been written down, so it may keep " +
                  "offering the 24 words to any computer it is plugged into. Connect it once to the " +
                  "main window and confirm them there before putting it away."),
            "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    // --- step 5: register it, from the main dongle ------------------------

    private TextBox? _label;

    private void BuildRegister()
    {
        _heading.Text = "Reconnect the main dongle";
        _detail.Text = "The main dongle signs the registration: the signature proves possession of the " +
                       "seed, its K_attestation proves it is that chip. The backup signs " +
                       "nothing — its K_attestation has already been read.";

        _label = new TextBox { Location = new Point(0, 6), Size = new Size(300, 26), PlaceholderText = "Label, e.g. Safe" };
        _label.MaxLength = 100;

        _body.Controls.AddRange([
            new Label { Text = $"Reconnect {_mainSerial}, then press Next.", Location = new Point(0, 44), Size = new Size(600, 24) },
            _label,
            new Label
            {
                Location = new Point(0, 76),
                Size = new Size(600, 80),
                ForeColor = SystemColors.GrayText,
                Text = "One backup a year is free. If you have already registered one this year the " +
                       "server answers with the date it becomes possible again — that one, not " +
                       "a date worked out here.",
            },
        ]);
    }

    private async Task<bool> RegisterAsync()
    {
        if (_backupAttestation is null) return false;

        var attached = CryptinDongle.ListSerials();
        if (!attached.Contains(_mainSerial, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"Reconnect the main dongle ({_mainSerial}).",
                "Main dongle", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _dongle.Disconnect();
        if (!_dongle.Connect(_mainSerial, waitMs: 5000)) return false;

        using var pin = new PinForm(_dongle, _dongle.UsbSerial);
        if (pin.ShowDialog(this) != DialogResult.OK) return false;

        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            var result = await Task.Run(() =>
                client.RegisterBackupAsync(_dongle, _backupAttestation, EmptyToNull(_label?.Text)));

            // Both, and in this order: the pending record exists only to bring
            // someone back here, and leaving it behind would offer to finish a
            // backup that is finished.
            _settings.BackupRegistered = true;
            _settings.PendingBackup = null;
            _settings.Save();

            MessageBox.Show(this,
                $"Backup registered. Dongles authorized on this license: {result.TrustedDongles}.\n\n" +
                "It works immediately, with no further steps.",
                "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (LicenseApiException e)
        {
            MessageBox.Show(this, Describe(e), "Registration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Registration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static string Describe(LicenseApiException e) => e.Code switch
    {
        // The date comes off the exception, not out of the message text. The old
        // test here was e.Message.Contains("next_available"), which could only
        // fire if the server put that token in its prose -- and the parser was
        // dropping the field anyway, so the branch was dead and what a user got
        // was the sentence below, announcing a date nobody had. Measured against
        // production 2026-09-03. Switching on the message is also the one thing
        // LicenseApiException tells callers never to do.
        LicenseErrorCodes.BackupLimitReached =>
            "You have already registered this year's backup. " +
            (e.NextAvailable is { Length: > 0 } when_
                ? $"It becomes possible again on {when_}."
                : "One license accepts one backup per calendar year, and this server did not say " +
                  "when the next one opens. Registering another now needs a new license."),

        // LicenseClient has already retried it once, so this is the second.
        LicenseErrorCodes.ConcurrentRetry =>
            "Another operation on this license was in progress at the same moment. " +
            "Nothing was changed: try again in a few seconds.",

        LicenseErrorCodes.SameAsMain =>
            "What you presented is the main dongle, not a second chip.",

        LicenseErrorCodes.AlreadyRegistered =>
            "This chip is already registered as a backup — on this license or on another.",

        LicenseErrorCodes.AlreadyActivated =>
            "This chip is the main dongle of another license. " +
            "One chip cannot sit on two licenses.",

        LicenseErrorCodes.LicenseExpired =>
            "The license has expired: it must be renewed before registering a backup.",

        LicenseErrorCodes.LicenseNotActive =>
            "The license is not active, so it does not accept backups.",

        LicenseErrorCodes.AttestationMismatch when e.Reason == "NOT_MAIN_DONGLE" =>
            "You are using a backup that is already registered. The main dongle must sign the registration.",

        LicenseErrorCodes.AttestationMismatch =>
            "This chip is not authorized on the license.",

        _ => e.Message,
    };

    /// <summary>Enough of a public key to tell two of them apart in a dialog,
    /// without pasting sixty-odd characters into a message someone has to read
    /// under stress.</summary>
    private static string Shorten(string hex)
        => hex.Length <= 16 ? hex : hex[..8] + "…" + hex[^8..];

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // --- step 6: where to keep it -----------------------------------------

    private void BuildStorage()
    {
        _heading.Text = "Where to keep it";
        _detail.Text = "The last step is the only one the software cannot do for you.";

        _body.Controls.Add(new Label
        {
            Location = new Point(0, 0),
            Size = new Size(600, 200),
            Text =
                "Keep the backup physically apart from the main dongle, AND from the sheet with the " +
                "24 words. Three things in the same drawer are one thing: whoever opens the drawer " +
                "takes all of them, and a fire burns all of them.\n\n" +
                "Both dongles work, always, with no difference between them. There is no “activate " +
                "the backup”: if you lose the main one, you pick up the other and carry on.\n\n" +
                "The 24 words remain the only way out if you lose both chips. Without them and " +
                "without a dongle, the encrypted files do not open again — there is nobody to " +
                "call, and that is the reason the system works.",
        });
    }
}
