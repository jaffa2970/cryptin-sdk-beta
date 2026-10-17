using System.IO.Ports;
using System.Text;
using CryptinSDK.Seed;

namespace CryptinPersonal;

/// <summary>
/// Walks a locked-out dongle through the serial `recover` command.
///
/// Three wrong PINs block the chip, and the only way back is the seed phrase
/// over the UART console — a fact the app used to discharge by telling the user
/// to "open a serial terminal at 115200 baud", which is not an instruction most
/// people can follow. This wizard does it for them: they pick a port from a list
/// that says which one is the dongle, type 24 words into 24 boxes, and press one
/// button.
///
/// **The exchange is a conversation, not a payload.** `main.cpp::recoveryFlow()`
/// is interactive and takes three turns:
///
///   1. host sends `recover`      → device: "Recovery mode: paste your 24-word…"
///   2. host sends the 24 words   → device: "Checksum OK…/Type YES to confirm:"
///                                  or:     "ERROR: invalid mnemonic…"
///   3. host sends `YES`          → device: "Recovery OK."
///
/// Sending the words alone reaches the top-level command dispatcher, which does
/// not know them, and nothing happens at all. The `YES` step is not ceremony
/// either: recovery **overwrites the keypair**, so the firmware insists on being
/// told twice. Pressing Recover here is that second telling, which is why step 3
/// says out loud what is about to be overwritten.
///
/// The 24 words never reach a log, a progress message, or an exception string.
/// The device does not echo them either — `readStringUntil` consumes the line
/// without printing it — so nothing on the wire comes back carrying the phrase.
/// </summary>
internal sealed class SerialRecoveryWizard : Form
{
    /// <summary>Console speed, fixed in the firmware's `Serial.begin`.</summary>
    private const int Baud = 115200;

    /// <summary>
    /// Budget for any one reply. The firmware answers within milliseconds; this
    /// covers a board that reset when the port opened and is still booting.
    /// </summary>
    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(5);

    private readonly Panel _body = new();
    private readonly Label _heading = new();
    private readonly Label _detail = new();
    private readonly Button _back = new();
    private readonly Button _next = new();

    private readonly Panel _step1 = new();
    private readonly Panel _step2 = new();
    private readonly Panel _step3 = new();
    private readonly Panel _step4 = new();

    private ComboBox? _ports;
    private readonly TextBox[] _words = new TextBox[Bip39.WordCount];
    private readonly Panel[] _frames = new Panel[Bip39.WordCount];
    private Label? _counter;
    private Label? _outcome;
    private Label? _stage;

    private int _step = 1;

    /// <summary>Guards the paste fan-out from re-entering through TextChanged.</summary>
    private bool _distributing;

    /// <summary>Held so <see cref="OnFormClosing"/> can shut the port even while
    /// the exchange is mid-flight. Never left dangling.</summary>
    private SerialPort? _activePort;

    private CancellationTokenSource? _running;

    public SerialRecoveryWizard(bool startAtPortSelection = false)
    {
        Text = "Crypt-in — recover a locked dongle";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 560);

        _heading.Location = new Point(20, 18);
        _heading.Size = new Size(580, 26);
        _heading.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);

        _detail.Location = new Point(20, 48);
        _detail.Size = new Size(580, 60);

        _body.Location = new Point(20, 116);
        _body.Size = new Size(580, 384);

        // Both buttons dispatch on the current step rather than being rewired as
        // the wizard moves. Swapping handlers is how a form ends up with two
        // subscriptions on one button and a Done that also advances.
        _back.Text = "← Back";
        _back.Location = new Point(362, 512);
        _back.Size = new Size(114, 30);
        _back.Click += (_, _) => Show(_step == 4 ? 2 : _step - 1);

        _next.Text = "Next →";
        _next.Location = new Point(486, 512);
        _next.Size = new Size(114, 30);
        _next.Click += async (_, _) => await NextClickedAsync();

        BuildStep1();
        BuildStep2();
        BuildStep3();
        BuildStep4();
        _body.Controls.AddRange([_step1, _step2, _step3, _step4]);

        Controls.AddRange([_heading, _detail, _body, _back, _next]);

        // Straight to the port picker when the caller already knows why we are
        // here: someone who just watched their dongle lock out does not need to
        // be told what a lockout is.
        Show(startAtPortSelection ? 2 : 1);
    }

    // --- step plumbing ----------------------------------------------------

    private void Show(int step)
    {
        _step = Math.Clamp(step, 1, 4);

        _step1.Visible = _step == 1;
        _step2.Visible = _step == 2;
        _step3.Visible = _step == 3;
        _step4.Visible = _step == 4;

        _back.Enabled = _step is 2 or 3;
        _back.Visible = _step != 4;
        _next.Visible = _step != 4;

        switch (_step)
        {
            case 1:
                _heading.Text = "Connect the UART cable";
                _detail.Text = "Recovery does not go through the USB port you normally use.";
                _next.Enabled = true;
                break;

            case 2:
                _heading.Text = "Select the dongle's port";
                _detail.Text = "The dongle appears as a serial port only while the UART cable " +
                               "is connected.";
                LoadPorts();
                break;

            case 3:
                _heading.Text = "Enter your 24 words";
                _detail.Text = "These are the words you wrote down when the dongle was first set " +
                               "up. They are never sent anywhere except to the dongle in front of you.";
                _next.Text = "Recover";
                UpdateWordState();
                break;

            case 4:
                _heading.Text = "Recovery";
                _detail.Text = "";
                break;
        }

        if (_step != 3) _next.Text = "Next →";
    }

    /// <summary>On step 4 the button means Done; everywhere else it advances.</summary>
    private async Task NextClickedAsync()
    {
        if (_step == 4)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        await AdvanceAsync();
    }

    private async Task AdvanceAsync()
    {
        switch (_step)
        {
            case 1:
                Show(2);
                return;

            case 2:
                if (SelectedPort() is null) return;
                Show(3);
                return;

            case 3:
                Show(4);
                await RunRecoveryAsync();
                return;
        }
    }

    // --- step 1: the cable ------------------------------------------------

    private void BuildStep1()
    {
        _step1.Location = new Point(0, 0);
        _step1.Size = new Size(580, 384);

        var text = new Label
        {
            Location = new Point(0, 0),
            Size = new Size(560, 90),
            Text = "Connect the second cable to the socket marked UART on your dongle.\n\n" +
                   "In normal use the dongle shows no COM port — that is correct.\n" +
                   "This cable is only needed for recovery.",
        };

        // A photo of the socket, if uart_cable.png sits beside the executable:
        // dropped in later, it is picked up with no code change.
        var art = new Panel
        {
            Location = new Point(0, 104),
            Size = new Size(560, 200),
            BackColor = SystemColors.ControlLightLight,
        };

        // Without the photo the step is just the text: a box announcing a missing
        // picture read as unfinished software to the user who saw it (beta update
        // test, 2026-10-12).
        var image = LoadCableImage();
        if (image is null)
        {
            art.Dispose();
            _step1.Controls.Add(text);
            return;
        }

        art.BackgroundImage = image;
        art.BackgroundImageLayout = ImageLayout.Zoom;
        _step1.Controls.AddRange([text, art]);
    }

    /// <summary>Beside the executable, so it can be added without a rebuild.</summary>
    private static Image? LoadCableImage()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "uart_cable.png");
            return File.Exists(path) ? Image.FromFile(path) : null;
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException or UnauthorizedAccessException)
        {
            // A missing or corrupt decoration must never stop a recovery.
            return null;
        }
    }

    // --- step 2: the port -------------------------------------------------

    private void BuildStep2()
    {
        _step2.Location = new Point(0, 0);
        _step2.Size = new Size(580, 384);

        _ports = new ComboBox
        {
            Location = new Point(0, 6),
            Size = new Size(400, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _ports.SelectedIndexChanged += (_, _) => _next.Enabled = SelectedPort() is not null;

        var refresh = new Button { Text = "Refresh", Location = new Point(412, 5), Size = new Size(100, 28) };
        refresh.Click += (_, _) => LoadPorts();

        var hint = new Label
        {
            Location = new Point(0, 44),
            Size = new Size(560, 90),
            ForeColor = SystemColors.GrayText,
            Text = "Plug the cable in, then click Refresh if your port does not appear.\n\n" +
                   SerialPorts.OnlyUartNote,
        };

        _step2.Controls.AddRange([_ports, refresh, hint]);
    }

    /// <summary>
    /// Uses the repo's registry-backed enumeration rather than
    /// <c>SerialPort.GetPortNames()</c>, which cannot tell a CH343 bridge from
    /// anything else. A user who does not know what a COM port is cannot be
    /// handed a list of four and asked to guess -- so they are not: only the
    /// bridges are listed, and the label says what was left out.
    /// </summary>
    private void LoadPorts()
    {
        var ports = SerialPorts.Programmable();
        _ports!.Items.Clear();

        if (ports.Count == 0)
        {
            _ports.Items.Add(SerialPorts.NoPortText());
            _ports.SelectedIndex = 0;
            _ports.Enabled = false;
            _next.Enabled = false;
            return;
        }

        foreach (var port in ports) _ports.Items.Add(port);
        _ports.Enabled = true;
        _ports.SelectedIndex = 0;          // every entry is a CH343 bridge now
        _next.Enabled = true;
    }

    private SerialPortInfo? SelectedPort() => _ports?.SelectedItem as SerialPortInfo;

    // --- step 3: the words ------------------------------------------------

    private void BuildStep3()
    {
        _step3.Location = new Point(0, 0);
        _step3.Size = new Size(580, 384);

        var grid = new TableLayoutPanel
        {
            Location = new Point(0, 0),
            Size = new Size(560, 240),
            ColumnCount = 4,
            RowCount = 6,
        };
        for (var c = 0; c < 4; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var r = 0; r < 6; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 6));

        for (var i = 0; i < Bip39.WordCount; i++)
        {
            var box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Fill,
                PlaceholderText = $"{i + 1}",
                Font = new Font(FontFamily.GenericMonospace, 9.5f),
            };
            box.TextChanged += OnWordChanged;

            // WinForms will not colour a TextBox border, so the border is a
            // one-pixel Panel behind it. That is what makes "green means this
            // word exists" readable at a glance across 24 fields.
            var frame = new Panel
            {
                Padding = new Padding(2),
                BackColor = SystemColors.ControlDark,
                Margin = new Padding(3, 3, 6, 3),
                Dock = DockStyle.Fill,
            };
            frame.Controls.Add(box);

            _words[i] = box;
            _frames[i] = frame;
            grid.Controls.Add(frame, i % 4, i / 4);
        }

        _counter = new Label
        {
            Location = new Point(0, 252),
            Size = new Size(560, 40),
            ForeColor = SystemColors.GrayText,
        };

        var paste = new Button
        {
            Text = "Paste 24 words",
            Location = new Point(0, 300),
            Size = new Size(160, 30),
        };
        paste.Click += (_, _) => Distribute(Clipboard.GetText());

        var warn = new Label
        {
            Location = new Point(176, 300),
            Size = new Size(384, 60),
            ForeColor = Color.Firebrick,
            Text = "Recovering overwrites the keys currently on this dongle. " +
                   "With the right words that restores exactly what was there before.",
        };

        _step3.Controls.AddRange([grid, _counter, paste, warn]);
    }

    private void OnWordChanged(object? sender, EventArgs e)
    {
        if (_distributing || sender is not TextBox box) return;

        // A pasted phrase lands in whichever field had focus. Spreading it is
        // the difference between one paste and twenty-four.
        if (box.Text.Any(char.IsWhiteSpace))
        {
            Distribute(box.Text);
            return;
        }

        UpdateWordState();
    }

    private void Distribute(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var parts = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);

        _distributing = true;
        try
        {
            for (var i = 0; i < Bip39.WordCount; i++)
                _words[i].Text = i < parts.Length ? Bip39.Normalize(parts[i]) : "";
        }
        finally
        {
            _distributing = false;
        }

        UpdateWordState();
    }

    private void UpdateWordState()
    {
        var words = _words.Select(w => Bip39.Normalize(w.Text)).ToArray();
        var valid = 0;

        for (var i = 0; i < Bip39.WordCount; i++)
        {
            if (words[i].Length == 0)
            {
                _frames[i].BackColor = SystemColors.ControlDark;
                continue;
            }

            var ok = Bip39.IsWord(words[i]);
            if (ok) valid++;
            _frames[i].BackColor = ok ? Color.FromArgb(46, 125, 50) : Color.FromArgb(211, 47, 47);
        }

        var allWords = valid == Bip39.WordCount;
        _next.Enabled = allWords;

        if (!allWords)
        {
            _counter!.ForeColor = SystemColors.GrayText;
            _counter.Text = $"{valid} / {Bip39.WordCount} words valid";
            return;
        }

        // All 24 exist, but the phrase still has to add up. Saying so here saves
        // a round trip to a dongle that would only answer "invalid mnemonic",
        // and points at the real cause: a word in the wrong place.
        var problem = Bip39.Validate(words);
        _counter!.ForeColor = problem is null ? Color.FromArgb(46, 125, 50) : Color.Firebrick;
        _counter.Text = problem is null
            ? $"{Bip39.WordCount} / {Bip39.WordCount} words valid — phrase checks out"
            : $"{Bip39.WordCount} / {Bip39.WordCount} words valid, but the phrase does not check " +
              $"out: {problem}";
    }

    /// <summary>Built only at the moment of use and dropped immediately after.</summary>
    private string Mnemonic()
        => string.Join(' ', _words.Select(w => Bip39.Normalize(w.Text)));

    // --- step 4: the exchange ---------------------------------------------

    private void BuildStep4()
    {
        _step4.Location = new Point(0, 0);
        _step4.Size = new Size(580, 384);

        _stage = new Label
        {
            Location = new Point(0, 0),
            Size = new Size(560, 24),
            ForeColor = SystemColors.GrayText,
        };

        _outcome = new Label
        {
            Location = new Point(0, 36),
            Size = new Size(560, 200),
        };

        _step4.Controls.AddRange([_stage, _outcome]);
    }

    private async Task RunRecoveryAsync()
    {
        if (SelectedPort() is not { } port) return;

        _back.Visible = false;
        _next.Visible = false;
        _stage!.Text = "Working…";
        _outcome!.Text = "";
        _outcome.ForeColor = SystemColors.ControlText;

        using var cancellation = new CancellationTokenSource();
        _running = cancellation;

        // Reports stage names only. The phrase is never a parameter here, and
        // never becomes one: whatever this receives can end up on screen.
        var progress = new Progress<string>(s => _stage.Text = s);

        RecoveryResult result;
        try
        {
            result = await Task.Run(
                () => Exchange(port.PortName, Mnemonic(), progress, cancellation.Token),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;                                   // the form is closing
        }
        finally
        {
            _running = null;
        }

        if (IsDisposed || Disposing) return;

        _stage.Text = "";
        if (result.Ok)
        {
            _outcome.ForeColor = Color.FromArgb(46, 125, 50);
            _outcome.Text =
                "Recovery complete.\n\n" +
                "Disconnect the UART cable, then reconnect your dongle\n" +
                "on the USB port. Enter your PIN to unlock.";

            _next.Text = "Done";
            _next.Visible = true;
            _next.Enabled = true;
            return;
        }

        _outcome.ForeColor = Color.Firebrick;
        _outcome.Text =
            "Recovery failed.\n\n" +
            "Check that the correct port is selected and the cable\n" +
            "is connected to the UART socket, then try again.\n\n" +
            result.Detail;

        // Stays on this step so the reason survives long enough to be read. Back
        // returns to the port picker, which is where both likely causes live.
        _back.Text = "← Back";
        _back.Visible = true;
        _back.Enabled = true;
    }

    private sealed record RecoveryResult(bool Ok, string Detail);

    /// <summary>
    /// The three-turn conversation with <c>recoveryFlow()</c>. Runs on a
    /// thread-pool thread; every wait is bounded.
    /// </summary>
    private RecoveryResult Exchange(
        string portName, string mnemonic, IProgress<string> progress, CancellationToken ct)
    {
        SerialPort? port = null;
        try
        {
            progress.Report("Opening the port…");

            // DTR and RTS stay low. On these boards they are wired to the
            // auto-reset circuit — RTS pulls EN, DTR pulls IO0 — so raising
            // either would reset the chip, or drop it into the serial
            // bootloader with no firmware to talk to.
            port = new SerialPort(portName, Baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = (int)ReplyBudget.TotalMilliseconds,
                WriteTimeout = (int)ReplyBudget.TotalMilliseconds,
                DtrEnable = false,
                RtsEnable = false,
                NewLine = "\n",
                Encoding = Encoding.ASCII,
            };
            port.Open();
            _activePort = port;

            // Let anything already in flight land, then start from silence.
            Thread.Sleep(300);
            port.DiscardInBuffer();

            progress.Report("Entering recovery mode…");
            port.Write("recover\n");
            if (!WaitFor(port, ct, out var banner, "Recovery mode"))
                return new RecoveryResult(false,
                    "The dongle did not enter recovery mode. " + Excerpt(banner));

            progress.Report("Sending the recovery phrase…");
            port.Write(mnemonic + "\n");
            if (!WaitFor(port, ct, out var verdict, "Checksum OK", "ERROR"))
                return new RecoveryResult(false,
                    "The dongle did not answer the phrase. " + Excerpt(verdict));

            if (verdict.Contains("ERROR", StringComparison.Ordinal))
                return new RecoveryResult(false,
                    "The dongle rejected the phrase: one word is wrong or out of order. " +
                    "Nothing on the dongle was changed.");

            // The firmware asks to be told twice because this overwrites keys.
            // Pressing Recover, after typing 24 words, was the second telling.
            progress.Report("Confirming…");
            port.Write("YES\n");
            if (!WaitFor(port, ct, out var done, "Recovery OK", "cancelled"))
                return new RecoveryResult(false,
                    "The dongle did not confirm the recovery. " + Excerpt(done));

            return done.Contains("Recovery OK", StringComparison.Ordinal)
                ? new RecoveryResult(true, "")
                : new RecoveryResult(false, "The dongle cancelled the recovery. Nothing was changed.");
        }
        catch (UnauthorizedAccessException)
        {
            return new RecoveryResult(false,
                "That port is already open in another program. Close it and try again.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            return new RecoveryResult(false, $"The port could not be used: {e.Message}");
        }
        catch (ObjectDisposedException)
        {
            // FormClosing pulled the port out from under us.
            return new RecoveryResult(false, "Cancelled.");
        }
        finally
        {
            _activePort = null;
            try { port?.Dispose(); } catch (IOException) { /* already gone */ }
        }
    }

    /// <summary>
    /// Accumulates until one of <paramref name="markers"/> appears or the budget
    /// runs out. Polling rather than <c>ReadLine</c> because the firmware answers
    /// in several lines and the marker is not always on the first of them —
    /// and because a blocking read cannot be cancelled when the form closes.
    /// </summary>
    private static bool WaitFor(SerialPort port, CancellationToken ct,
                                out string seen, params string[] markers)
    {
        var buffer = new StringBuilder();
        var deadline = DateTime.UtcNow + ReplyBudget;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = port.ReadExisting();
            if (chunk.Length > 0)
            {
                buffer.Append(chunk);
                var text = buffer.ToString();
                if (markers.Any(m => text.Contains(m, StringComparison.Ordinal)))
                {
                    seen = text;
                    return true;
                }
            }

            Thread.Sleep(40);
        }

        seen = buffer.ToString();
        return false;
    }

    /// <summary>
    /// A short, safe tail of what the device said, for a failure the user has to
    /// act on. The device never echoes the phrase — <c>readStringUntil</c>
    /// consumes it without printing — so nothing here can carry it.
    /// </summary>
    private static string Excerpt(string raw)
    {
        var line = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                      .LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length == 0 ? "" : $"({(line.Length > 120 ? line[..120] + "…" : line)})";
    }

    /// <summary>
    /// Closing mid-exchange must not leave the port held: the next attempt would
    /// meet its own stale handle and report the port as busy.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _running?.Cancel();

        try { _activePort?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _activePort = null;

        base.OnFormClosing(e);
    }
}
