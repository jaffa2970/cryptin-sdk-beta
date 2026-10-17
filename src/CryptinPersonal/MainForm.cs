using CryptinSDK.Crypto;
using CryptinSDK.Dongle;
using CryptinSDK.Licensing;

namespace CryptinPersonal;

/// <summary>
/// The main window, built around one explicit state machine.
///
/// Before this refactor the window carried three booleans — `_unlocked`,
/// `_hadDongle`, `_busy` — and every control worked out its own enabled state
/// from some subset of them. The combinations that could not happen were kept
/// out only by luck, and "what is this window showing right now" had no single
/// answer. <see cref="DongleState"/> is now that answer: one field, five values,
/// and <see cref="ApplyState"/> is the only place a control is enabled or a
/// colour is chosen.
///
/// Four behaviours come from the hardware rather than from taste:
///
///   * The unlocked session lives in RAM on the dongle, so cutting its power
///     returns it to LOCKED. This form watches for that and forgets the session
///     instead of discovering it later as a mysterious error mid-encryption.
///   * The session also expires on its own after ten idle minutes, so
///     `ERR_PIN_REQUIRED` can arrive on a session this window believes is open.
///     That is a request for the PIN, never an error to show.
///   * "Wrong dongle" and "damaged file" are reported as different things, with
///     different advice. They are the same AEAD failure underneath, and
///     collapsing them is the one mistake that makes the error useless.
///   * Every byte crosses USB at about 27.8 KB/s, so a 1 MB file takes over half
///     a minute — far past the point where a still window reads as a crash.
///
/// All user-facing text is English.
/// </summary>
internal sealed class MainForm : Form
{
    /// <summary>
    /// What this window is showing. Exactly one of these holds at any moment,
    /// and every enabled/disabled decision is derived from it in
    /// <see cref="ApplyState"/> rather than recomputed at each call site.
    /// </summary>
    private enum DongleState
    {
        /// <summary>No Crypt-in dongle is enumerated.</summary>
        Disconnected,

        /// <summary>A dongle is present but this session has not verified a PIN.</summary>
        Locked,

        /// <summary>PIN verified. The only state in which files may be dropped.</summary>
        Unlocked,

        /// <summary>A file operation is running. Unlocked underneath.</summary>
        Busy,

        /// <summary>Transient, after the Lock button. See <see cref="LockAsync"/>.</summary>
        Locking,
    }

    // --- fixed copy -------------------------------------------------------

    /// <summary>
    /// The drop area says the same thing in every state, by design. It used to
    /// double as a status area — carrying "Working…", and the disconnection
    /// warning in red — which meant the largest surface in the window changed
    /// meaning depending on state the user had to already know. The status bar
    /// owns state now, so this can be an instruction and nothing else.
    /// </summary>
    private const string DropLine1 = "Drop files or folders here to encrypt";
    private const string DropLine2 = "Drop a .crin file to decrypt";

    /// <summary>
    /// Said wherever a re-plug is involved. Windows needs a few seconds to tear
    /// the HID device down and build it back up, and a dongle pushed back in
    /// immediately tends to enumerate as a half-open handle that fails on the
    /// first command. Naming a number tells the user when to stop waiting.
    /// </summary>
    private const string ReconnectAdvice = "Wait 5 seconds before reconnecting";

    /// <summary>
    /// The one-line threat model, on the surface where the work happens.
    ///
    /// The second sentence is the one that matters. Encryption at rest invites
    /// the belief that the running machine is covered too, and a user holding
    /// that belief keeps working on a compromised PC because the files are
    /// "encrypted".
    /// </summary>
    private const string ThreatModelSummary =
        "Crypt-in protects your files at rest.\n" +
        "It does not protect against malware already running on this PC.";

    // --- tuning -----------------------------------------------------------

    /// <summary>Session log ceiling. Oldest lines fall off the top.</summary>
    private const int LogMaxLines = 200;

    /// <summary>How long the Lock button waits before looking again.</summary>
    private static readonly TimeSpan LockSettle = TimeSpan.FromSeconds(5);

    /// <summary>Total budget for the dongle to be visible again after a lock.</summary>
    private static readonly TimeSpan LockDeadline = TimeSpan.FromSeconds(8);

    // --- palette ----------------------------------------------------------
    // Muted backgrounds, saturated dots. The panel is a background the user
    // reads text on all day, so it stays pale; the dot is the thing that has to
    // be identifiable at a glance and across the room.

    private static readonly Color DisconnectedBack = Color.FromArgb(253, 237, 237);
    private static readonly Color DisconnectedDot = Color.FromArgb(211, 47, 47);
    private static readonly Color LockedBack = Color.FromArgb(255, 248, 225);
    private static readonly Color LockedDot = Color.FromArgb(230, 145, 0);
    private static readonly Color UnlockedBack = Color.FromArgb(237, 247, 237);
    private static readonly Color UnlockedDot = Color.FromArgb(46, 125, 50);

    private static readonly Color EncryptedText = Color.FromArgb(34, 139, 34);
    private static readonly Color FailedText = Color.Firebrick;

    // --- state ------------------------------------------------------------

    private readonly CryptinDongle _dongle = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };

    /// <summary>Drives the disconnected dot only. Stopped in every other state:
    /// a blink is an alarm, and an alarm that never stops stops being one.</summary>
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 1000 };

    private readonly Panel _statusBar = new();
    private readonly Label _dot = new();
    private readonly Label _headline = new();
    private readonly Label _subtitle = new();
    private readonly LinkLabel _revive = new();
    private readonly LinkLabel _recoverLink = new();

    private readonly Panel _drop = new();
    private readonly Label _dropText = new();

    private readonly ProgressBar _progress = new();
    private readonly Label _percent = new();
    private readonly Button _cancel = new();

    private readonly ListBox _log = new();

    private readonly Button _enterPin = new();
    private readonly Button _lock = new();
    private readonly Button _activate = new();
    private readonly Button _loadLicence = new();
    private readonly Button _backup = new();
    private readonly Button _transfer = new();
    private readonly Button _firmware = new();

    private DongleState _state = DongleState.Disconnected;

    private string _connectedSerial = "";

    /// <summary>Re-entrancy guard, separate from <see cref="_state"/> on purpose.
    /// A modal licence dialog must keep the poll timer out without the window
    /// claiming a file operation is running.</summary>
    private bool _busy;

    /// <summary>Nesting depth of <see cref="Foreground"/>. UI thread only.</summary>
    private int _foregroundDepth;

    /// <summary>Live only while a drop is being processed.</summary>
    private CancellationTokenSource? _cancellation;

    /// <summary>Re-entrancy guard for the handover dialog: ShowDialog pumps
    /// messages, so the poll timer keeps firing underneath it.</summary>
    private bool _handoverBusy;

    /// <summary>The serial this session already offered a handover for, so
    /// backing out is not punished with the same dialog a second later.</summary>
    private string _handoverOfferedFor = "";

    /// <summary>The serial already told that only a restart finishes this, so
    /// the line is written once and not on every tick of the poll.</summary>
    private string _restartNoticeFor = "";

    /// <summary>Last answer from /api/verify. Null when it has not run yet, or
    /// when the server could not be reached — which is not the same as "no
    /// licence" and must not be treated as one.</summary>
    private LicenseStatus? _licenceStatus;

    /// <summary>The refresh currently in flight, so a caller that needs an
    /// answer can wait for the one already running instead of reading
    /// <see cref="_licenceStatus"/> while it is still null.
    ///
    /// Every launch site of the refresh is fire-and-forget on purpose: the
    /// window must come up without waiting on the network. What was missing is
    /// a handle for the few places where "not yet" and "no licence" are
    /// different answers -- measured on 2026-09-02, where pressing Backup right
    /// after the unlock reported "no active license" on a dongle that had one.
    /// </summary>
    private Task _licenceRefresh = Task.CompletedTask;

    /// <summary>What the status subtitle says while unlocked and idle. Held
    /// because a running operation borrows that line for progress and has to put
    /// it back afterwards.</summary>
    private string _licenceLine = "";

    public MainForm()
    {
        Text = LicenseEndpoint.Stamp("Crypt-in Personal");
        ClientSize = new Size(620, 604);
        MinimumSize = new Size(636, 630);
        AllowDrop = true;

        BuildMenu();
        BuildStatusBar();
        BuildDropArea();
        BuildProgressRow();
        BuildSessionLog();
        BuildButtons();

        _watch.Tick += (_, _) => PollDongle();
        _blink.Tick += (_, _) => _dot.Visible = !_dot.Visible;

        ApplyState();
        _watch.Start();
        PollDongle();
    }

    // --- construction -----------------------------------------------------

    private void BuildMenu()
    {
        // Always enabled, whatever the dongle is doing. A locked-out chip answers
        // nothing over USB, so gating its own way out on dongle state would hide
        // the entry point exactly when it is needed.
        var recover = new ToolStripMenuItem("&Recover locked dongle…");
        recover.Click += (_, _) => OpenRecoveryWizard();

        // Named for the symptom, not the cause. The two entries sit next to each
        // other and are for opposite situations: this one is "the app says
        // nothing is plugged in although it is", the other is "three wrong PINs".
        // A user in trouble picks from the words, so "recover" and "revive" carry
        // the difference and the wording states the symptom outright.
        var revive = new ToolStripMenuItem("Dongle not detected? &Revive it…");
        revive.Click += (_, _) => ReviveUndetectedDongle();

        var about = new ToolStripMenuItem("&About Crypt-in Personal…");
        about.Click += (_, _) => ShowAbout();

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(revive);
        help.DropDownItems.Add(recover);
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(about);

        var menu = new MenuStrip();
        menu.Items.Add(help);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    /// <summary>
    /// Recovery talks to the chip over its UART bridge, not over HID, so this
    /// window has to let go of the dongle first — and afterwards the chip needs
    /// a power cycle before it answers over USB again. Re-polling from scratch
    /// is what puts the window back in step with whatever is now attached.
    /// </summary>
    /// <summary>
    /// Reboots a dongle that came up in USB-Serial/JTAG instead of running its
    /// application. Measured on 2026-08-05 at roughly one plug in six: the chip
    /// is connected and powered, exposes a serial port, and shows no HID — so
    /// this window sees exactly what it sees when nothing is plugged in at all.
    ///
    /// **This is the only recovery a customer can perform themselves.** A
    /// shipped dongle has one socket, so the UART bridge that
    /// <c>DongleHardReset.Reset</c> needs is not available to them; on the
    /// chip's own interface no line-driven reset reaches EN. The RTC watchdog
    /// does the job from inside instead.
    ///
    /// Told to unplug first, and offered anyway: a replug clears this most of
    /// the time and costs nothing, so it would be dishonest to present the
    /// clever route as the only one.
    /// </summary>
    private async void ReviveUndetectedDongle()
    {
        if (CryptinDongle.ListSerials().Count > 0)
        {
            MessageBox.Show(this,
                "A dongle is already answering, so there is nothing to revive.\n\n"
                + "If the one you meant is a different dongle, unplug the working "
                + "one first.",
                "Revive dongle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var go = MessageBox.Show(this,
            "The dongle can start up without its application, and then it looks "
            + "to this app exactly like a dongle that is not plugged in.\n\n"
            + "First try the simple thing: unplug it, wait a moment, plug it back "
            + "in. That clears it most of the time.\n\n"
            + "If you run virtual machines, check that yours is not taking the "
            + "dongle. VMware, VirtualBox and WSL can claim it while it is still "
            + "starting up, which leaves it exactly like this — if one of them "
            + "offers you a JTAG or serial debug device, keep Crypt-in on this "
            + "computer.\n\n"
            + "Otherwise this will restart the chip over the same USB cable. It "
            + "usually takes about ten seconds, and afterwards the dongle is "
            + "locked again, so you will be asked for your PIN.\n\n"
            + "Restart the dongle now?",
            "Revive dongle", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (go != DialogResult.Yes) return;

        using (Foreground())
        {
            // The handle is released even though a wedged dongle has none to
            // release: if the user got here with a working dongle on another
            // port, the scan must not fight this window for it.
            _dongle.Disconnect();

            LogInfo("Looking for a dongle that started without its application…");

            var revived = await Task.Run(() =>
            {
                foreach (var port in DongleHardReset.ListRecoveryCandidates())
                {
                    try
                    {
                        if (DongleHardReset.TryRecoverOverNativePort(port)) return port;
                    }
                    // A port that is not a wedged dongle is not an error: it is
                    // most of them. Busy or unreadable ports are skipped for the
                    // same reason.
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
                return null;
            });

            if (revived is not null)
                LogInfo($"The dongle restarted on {revived} · it is locked, so enter your PIN");
            else
                LogFail("No dongle answered · unplug it and plug it back in, "
                        + "which clears this most of the time");
        }

        _connectedSerial = "";
        SetState(DongleState.Disconnected);
        PollDongle();
    }

    private void OpenRecoveryWizard()
    {
        using (Foreground())
        {
            _dongle.Disconnect();

            using var wizard = new SerialRecoveryWizard();
            if (wizard.ShowDialog(this) == DialogResult.OK)
                LogInfo("Recovery completed · reconnect the dongle on the USB port");
        }

        _connectedSerial = "";
        _handoverOfferedFor = "";
        SetState(DongleState.Disconnected);
        PollDongle();
    }

    private void BuildStatusBar()
    {
        _statusBar.Location = new Point(0, 24);
        _statusBar.Size = new Size(620, 64);
        _statusBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // A filled circle in a label, rather than a custom-painted control: the
        // blink is a Visible toggle, which costs one invalidation and cannot go
        // out of step with a paint handler.
        _dot.Text = "●";
        _dot.Font = new Font(Font.FontFamily, 15f);
        _dot.Location = new Point(16, 18);
        _dot.Size = new Size(22, 26);
        _dot.TextAlign = ContentAlignment.MiddleCenter;

        _headline.Location = new Point(44, 14);
        _headline.Size = new Size(560, 20);
        _headline.Font = new Font(Font, FontStyle.Bold);
        _headline.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _subtitle.Location = new Point(44, 36);
        _subtitle.Size = new Size(560, 18);
        _subtitle.ForeColor = SystemColors.GrayText;
        _subtitle.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // Lives in the status bar and not in a menu, because this is the one
        // remedy whose whole purpose is to be found while the app is saying it
        // sees nothing. Under Help it would be invisible at the exact moment it
        // is needed — which is where it was first put, and it was wrong.
        _revive.Text = "Plugged in but not detected? Restart the dongle";
        _revive.Location = new Point(44, 56);
        _revive.AutoSize = true;
        _revive.Visible = false;
        _revive.LinkBehavior = LinkBehavior.HoverUnderline;
        _revive.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _revive.Click += (_, _) => ReviveUndetectedDongle();

        // Same row as the revive link, and that is safe: the two belong to
        // states that cannot both hold. PinForm already surfaces this at the
        // moment of lockout, but only there — close that dialog and the way
        // out went back to being a menu entry.
        _recoverLink.Text = "Forgot your PIN, or locked out? Recover with your 24-word phrase";
        _recoverLink.Location = new Point(44, 56);
        _recoverLink.AutoSize = true;
        _recoverLink.Visible = false;
        _recoverLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _recoverLink.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _recoverLink.Click += (_, _) => OpenRecoveryWizard();

        _statusBar.Size = new Size(620, 84);   // room for the link row
        _statusBar.Controls.AddRange([_dot, _headline, _subtitle, _revive, _recoverLink]);
        Controls.Add(_statusBar);
    }

    private void BuildDropArea()
    {
        _drop.Location = new Point(16, 120);
        _drop.Size = new Size(588, 186);
        _drop.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _drop.BorderStyle = BorderStyle.FixedSingle;
        _drop.AllowDrop = true;
        _drop.DragEnter += OnDragEnter;
        _drop.DragLeave += (_, _) => RestoreDropColour();
        _drop.DragDrop += OnDragDrop;

        _dropText.Dock = DockStyle.Fill;
        _dropText.TextAlign = ContentAlignment.MiddleCenter;
        _dropText.Text = $"{DropLine1}\n{DropLine2}";
        _drop.Controls.Add(_dropText);

        var tips = new ToolTip();
        tips.SetToolTip(_drop, ThreatModelSummary);
        tips.SetToolTip(_dropText, ThreatModelSummary);

        Controls.Add(_drop);
    }

    private void BuildProgressRow()
    {
        _progress.Location = new Point(16, 316);
        _progress.Size = new Size(380, 18);
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Visible = false;

        _percent.Location = new Point(404, 316);
        _percent.Size = new Size(86, 18);
        _percent.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _percent.Visible = false;

        _cancel.Text = "Cancel";
        _cancel.Location = new Point(500, 312);
        _cancel.Size = new Size(104, 26);
        _cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _cancel.Visible = false;
        _cancel.Click += (_, _) => CancelOperation();

        Controls.AddRange([_progress, _percent, _cancel]);
    }

    private void BuildSessionLog()
    {
        var caption = new Label
        {
            Text = "Session log",
            Location = new Point(16, 324),
            Size = new Size(200, 16),
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };

        // Owner-drawn because each line carries its own colour, and selection is
        // off because this is a record of what happened, not a list to pick from.
        _log.Location = new Point(16, 344);
        _log.Size = new Size(588, 140);
        _log.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _log.DrawMode = DrawMode.OwnerDrawFixed;
        _log.ItemHeight = 18;
        _log.IntegralHeight = false;
        _log.SelectionMode = SelectionMode.None;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.BackColor = SystemColors.Window;
        _log.DrawItem += OnDrawLogItem;

        Controls.AddRange([caption, _log]);
    }

    private void BuildButtons()
    {
        _enterPin.Text = "Enter PIN";
        _enterPin.Location = new Point(16, 496);
        _enterPin.Size = new Size(130, 32);
        _enterPin.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _enterPin.Click += (_, _) => UnlockFromButton();

        _lock.Text = "Lock";
        _lock.Location = new Point(156, 496);
        _lock.Size = new Size(110, 32);
        _lock.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _lock.Click += async (_, _) => await LockAsync();

        // Says what it does rather than what it sounds like. There is no firmware
        // lock opcode, so this closes our handle and asks for the PIN again; the
        // chip's own session survives until it loses power or times out. A button
        // called "Lock" that quietly meant less than that would be the worst kind
        // of security control -- one the user trusts more than it earns.
        var tips = new ToolTip();
        tips.SetToolTip(_lock,
            "Closes this app's connection and asks for your PIN again.\n" +
            "The dongle itself stays unlocked until it loses power or 10 idle\n" +
            "minutes pass — there is no firmware lock command yet.\n" +
            "Unplug the dongle if you need it locked for certain.");

        _activate.Text = "Activate license";
        _activate.Location = new Point(400, 496);
        _activate.Size = new Size(150, 32);
        _activate.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _activate.Visible = false;
        _activate.Click += async (_, _) => await ActivateAsync();

        _loadLicence.Text = "Load license";
        _loadLicence.Location = new Point(400, 496);
        _loadLicence.Size = new Size(150, 32);
        _loadLicence.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        _loadLicence.Visible = false;
        _loadLicence.Click += async (_, _) => await LoadLicenceOntoDongleAsync();

        // Called "Transfer license" until 2026-09-04, which is what it looked
        // like to anyone whose dongle had just died: it writes the token the
        // server already issued onto the chip in the socket, and moves nothing
        // anywhere. The real transfer is the button in the row below.
        tips.SetToolTip(_loadLicence,
            "Writes the license the server already issued onto this dongle.\n" +
            "Nothing to pay, nothing moves: the chip needs the token in hand\n" +
            "before it will encrypt. To move a license to a DIFFERENT chip,\n" +
            "use Transfer license at the bottom of the window.");

        // Advanced, and deliberately quieter: smaller, grey, flat, at the bottom
        // edge. Both lead somewhere most users never need to go, and one of them
        // cannot go anywhere at all yet.
        var muted = new Font(Font.FontFamily, 7.5f);

        _backup.Text = "Backup dongle";
        _backup.Location = new Point(16, 542);
        _backup.Size = new Size(126, 24);
        _backup.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _backup.Font = muted;
        _backup.ForeColor = SystemColors.GrayText;
        _backup.FlatStyle = FlatStyle.Flat;
        _backup.Click += async (_, _) => await BackupAsync();

        // Reachable with no dongle connected, and that is the whole point of
        // it: the user is here because the dongle broke. Gating it on `unlocked`
        // like Backup would have hidden it in exactly the situation it exists
        // for -- there is nothing to unlock, and the chip it will build has not
        // been programmed yet.
        _transfer.Text = "Transfer license";
        _transfer.Location = new Point(150, 542);
        _transfer.Size = new Size(126, 24);
        _transfer.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _transfer.Font = muted;
        _transfer.ForeColor = SystemColors.GrayText;
        _transfer.FlatStyle = FlatStyle.Flat;
        _transfer.Click += (_, _) => TransferLicence();

        _firmware.Text = "Update firmware";
        _firmware.Location = new Point(284, 542);
        _firmware.Size = new Size(126, 24);
        _firmware.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _firmware.Font = muted;
        _firmware.ForeColor = SystemColors.GrayText;
        _firmware.FlatStyle = FlatStyle.Flat;
        _firmware.Enabled = false;

        tips.SetToolTip(_backup, "Register a second chip on this license. Free, once a year.\n" +
                                 "Needs the 24 words and the backup chip.\n" +
                                 "Both dongles keep working \u2014 this does not move anything.");

        // Spelled out against Backup, because the two sit side by side and the
        // difference between them is the whole decision: one adds a chip, one
        // retires one.
        tips.SetToolTip(_transfer,
            "Dongle broken, lost or stolen? Move the license onto a new chip.\n" +
            "Free, three times a year. Needs the 24 words and any spare board.\n" +
            "Afterwards the license is on the new chip ONLY: the old one stops\n" +
            "being authorized. For a second dongle beside a working first one,\n" +
            "use Backup dongle instead.");
        tips.SetToolTip(_firmware,
            "No firmware update path exists in the beta.\n" +
            "Flashing needs the second cable on the UART socket, and a production\n" +
            "dongle refuses write-flash altogether — Secure Download Mode.");

        Controls.AddRange([_enterPin, _lock, _activate, _loadLicence, _backup, _transfer, _firmware]);
    }

    private void ShowAbout() => MessageBox.Show(this,
        "Crypt-in Personal — Beta\n" +
        "lake8.dev\n\n" +
        "Protects: files at rest, stolen disks, cloud backups, shared files.\n\n" +
        "Does not protect: malware already running on this PC, active sessions " +
        "on a compromised machine.\n\n" +
        "security@lake8.dev",
        "About Crypt-in Personal", MessageBoxButtons.OK, MessageBoxIcon.Information);

    // --- the state machine ------------------------------------------------

    private void SetState(DongleState next)
    {
        if (_state == next) return;
        _state = next;
        ApplyState();
    }

    /// <summary>
    /// The single place a control is enabled and a colour is chosen.
    ///
    /// Every branch sets every affected property. Leaving one out is how a
    /// window ends up with a green bar and a dead drop area — the bug class this
    /// whole refactor exists to remove — so the repetition is deliberate.
    /// </summary>
    private void ApplyState()
    {
        var unlocked = _state == DongleState.Unlocked;
        var busy = _state is DongleState.Busy or DongleState.Locking;

        _blink.Enabled = _state == DongleState.Disconnected;
        if (!_blink.Enabled) _dot.Visible = true;

        // Offered only where it applies. "Not detected" is the one state this
        // fixes; showing it beside a working dongle would invite a pointless
        // reboot of a session the user is in the middle of.
        _revive.Visible = _state == DongleState.Disconnected;
        _recoverLink.Visible = _state == DongleState.Locked;

        switch (_state)
        {
            case DongleState.Disconnected:
                _statusBar.BackColor = DisconnectedBack;
                _dot.ForeColor = DisconnectedDot;
                _headline.Text = "Dongle not connected";
                _subtitle.Text = "Plug in your Crypt-in dongle to continue";
                break;

            case DongleState.Locked:
                _statusBar.BackColor = LockedBack;
                _dot.ForeColor = LockedDot;
                _headline.Text = $"Dongle locked — {_connectedSerial}";
                _subtitle.Text = "Enter your PIN to unlock";
                break;

            case DongleState.Unlocked:
                _statusBar.BackColor = UnlockedBack;
                _dot.ForeColor = UnlockedDot;
                _headline.Text = $"Dongle ready — {_connectedSerial}";
                _subtitle.Text = _licenceLine.Length > 0 ? _licenceLine : "License valid";
                break;

            case DongleState.Busy:
                // Still green: the dongle has not changed state, the app is
                // simply working. The progress row carries the detail.
                _statusBar.BackColor = UnlockedBack;
                _dot.ForeColor = UnlockedDot;
                _headline.Text = $"Dongle ready — {_connectedSerial}";
                break;

            case DongleState.Locking:
                _statusBar.BackColor = LockedBack;
                _dot.ForeColor = LockedDot;
                _headline.Text = "Locking…";
                _subtitle.Text = ReconnectAdvice;
                break;
        }

        // The drop area is the one control whose disabled look has to read from
        // across the room, because it is the one the user aims at first.
        _drop.Enabled = unlocked;
        _drop.BackColor = unlocked ? SystemColors.ControlLightLight : SystemColors.Control;
        _dropText.ForeColor = unlocked ? SystemColors.ControlText : SystemColors.GrayText;
        _drop.Cursor = Cursors.Default;

        _enterPin.Enabled = _state == DongleState.Locked;
        _lock.Enabled = unlocked;
        _backup.Enabled = unlocked;
        // Not `unlocked`, unlike every other button here. See where it is built.
        _transfer.Enabled = !busy;
        _firmware.Enabled = false;          // no update path in the beta, ever
        _activate.Enabled = unlocked;
        _loadLicence.Enabled = unlocked;

        _progress.Visible = _state == DongleState.Busy;
        _percent.Visible = _state == DongleState.Busy;
        _cancel.Visible = _state == DongleState.Busy;

        if (busy)
        {
            _activate.Enabled = false;
            _loadLicence.Enabled = false;
        }
    }

    /// <summary>Puts the unlocked subtitle back after a run of progress text.</summary>
    private void RestoreSubtitle()
    {
        if (_state == DongleState.Unlocked)
            _subtitle.Text = _licenceLine.Length > 0 ? _licenceLine : "License valid";
    }

    // --- first run --------------------------------------------------------

    /// <summary>
    /// The wizard runs on the first launch, and keeps coming back on every
    /// launch until it is finished. Everything it does is reachable from this
    /// window anyway — it exists to put the steps
    /// in an order for someone who has just opened the box, not to gate them.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_settings.FirstRunCompleted)
        {
            OfferHandoverIfPending();
            OfferBackupResume();
            OfferTransferResume();
            return;
        }

        DialogResult outcome;
        _handoverBusy = true;
        try
        {
            using var wizard = new SetupWizard(_dongle, _settings);
            outcome = wizard.ShowDialog(this);
        }
        finally
        {
            _handoverBusy = false;
        }

        // Only a wizard that reached its last step counts as a first run. This
        // used to be set whatever came back, so closing the window once -- which
        // is what someone does with the dongle still in the envelope -- burned
        // the one automatic route to setup, and nothing in this window opens it
        // again. Coming back on the next launch is the re-entry.
        if (outcome == DialogResult.OK)
        {
            _settings.FirstRunCompleted = true;
            _settings.Save();
        }

        if (_dongle.IsConnected)
        {
            _connectedSerial = _dongle.UsbSerial;
            SetState(DongleState.Unlocked);
            _licenceRefresh = RefreshLicenceAsync();
        }

        // On both paths, and this is not symmetry for its own sake. An
        // unfinished backup outlives a first run that was closed rather than
        // finished -- and closing it is what someone does with the dongle still
        // in the envelope, which is why the flag is only set on DialogResult.OK
        // twenty lines up. Left only in the branch above, the offer never
        // reached the one machine the beta is aimed at: a clean one, where
        // FirstRunCompleted is false and stays false.
        OfferBackupResume();
        OfferTransferResume();
    }

    /// <summary>
    /// The other half of the restart the backup wizard asks for. Step 3 leaves
    /// a chip that carries the seed and a licence server that has never heard
    /// of it, and the only thing that closes that gap is coming back — so this
    /// asks, on every launch, until it is either finished or the registration
    /// makes it moot.
    ///
    /// Deliberately a question and not a resumption: a backup dongle lives in a
    /// drawer somewhere else, and starting the wizard on a launch where it is
    /// not in the room would only produce "the backup does not appear to be
    /// connected".
    /// </summary>
    private void OfferBackupResume()
    {
        if (_settings.PendingBackup is not { } pending) return;

        var answer = MessageBox.Show(this,
            $"A backup dongle was prepared on {pending.StartedAt.LocalDateTime:dd/MM/yyyy} at " +
            $"{pending.StartedAt.LocalDateTime:HH:mm} and never finished.\n\n" +
            "The chip already carries the main dongle's seed, so it opens your files. What is " +
            "missing is the registration: until it is done the license server does not know that " +
            "chip exists, and a check against it answers as if it were a clone.\n\n" +
            "Finish it now? Plug the backup in first — the main dongle is asked for at the end. " +
            "Nothing has to be typed again.\n\n" +
            "No means this will be offered again on the next start.",
            "Finish the backup", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        RunBackupWizard(resume: true);
    }

    /// <summary>Shared by the Backup button and the resume offer, because what
    /// has to happen afterwards is the same either way: the wizard swaps
    /// dongles, so no session survives it.</summary>
    private void RunBackupWizard(bool resume)
    {
        using (Foreground())
        {
            using var wizard = new BackupWizard(_dongle, _settings, resume);
            wizard.ShowDialog(this);
        }

        // Outside the scope on purpose: PollDongle returns immediately while
        // _busy holds, so re-polling from inside would silently do nothing.
        _connectedSerial = "";
        SetState(DongleState.Disconnected);
        PollDongle();
    }

    // --- moving the licence to another chip -------------------------------

    /// <summary>
    /// Opens the transfer wizard, after saying plainly what a transfer costs the
    /// old dongle.
    ///
    /// The confirmation is not ceremony. This and Backup dongle sit next to each
    /// other, are both about "a second chip and my 24 words", and do opposite
    /// things to the dongle already on the licence: one keeps it, one retires
    /// it. Someone who reaches for the wrong one finds out at the end, on a
    /// server call that cannot be taken back and has spent one of three.
    ///
    /// Not gated on a connected dongle, and not on an unlocked one. The premise
    /// of the wizard is that the dongle is gone.
    /// </summary>
    private void TransferLicence()
    {
        if (_busy) return;

        if (LicenseTransferWizard.RestartRequired)
        {
            MessageBox.Show(this, LicenseTransferWizard.RestartMessage,
                "Transfer — one restart left", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // The unfinished-transfer case first, for the same reason the backup
        // button asks it first: resuming silently would be the wrong choice
        // exactly when it matters, and starting over would reprogram a chip that
        // is already right.
        var resume = false;
        if (_settings.PendingTransfer is { } pending)
        {
            var answer = MessageBox.Show(this,
                $"A chip prepared on {pending.StartedAt.LocalDateTime:dd/MM/yyyy} at " +
                $"{pending.StartedAt.LocalDateTime:HH:mm} never received the license.\n\n" +
                "Yes finishes that one: plug it in and it goes straight to the PIN and the " +
                "transfer.\n" +
                "No starts again from the 24 words on whichever chip you choose. The prepared one " +
                "is left as it is — it keeps the seed, it just never got the license.",
                "Unfinished transfer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return;
            resume = answer == DialogResult.Yes;
        }

        if (!resume)
        {
            var go = MessageBox.Show(this,
                "This moves the license off the dongle it is on now and onto a chip you are about " +
                "to program. Afterwards the old one is no longer authorized: it still opens files " +
                "encrypted with your seed, but it cannot encrypt anything new.\n\n" +
                "It is for a dongle that is broken, lost or stolen. If yours still works and you " +
                "want a spare beside it, close this and use Backup dongle instead — that one " +
                "keeps both.\n\n" +
                "You will need your 24 words and a chip to put them on.\n\n" +
                "Go on?",
                "Transfer the license", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (go != DialogResult.Yes) return;
        }

        RunTransferWizard(resume);
    }

    /// <summary>
    /// The other half of the restart the transfer wizard asks for. It leaves a
    /// chip carrying the seed and a licence still registered to the chip that
    /// broke, and only coming back closes that — so this asks on every launch
    /// until it is finished.
    ///
    /// A question rather than a resumption, as with the backup: the wizard's
    /// last step wants that chip in the socket, and opening it on a launch where
    /// it is in another room produces nothing but a refusal.
    /// </summary>
    private void OfferTransferResume()
    {
        if (_settings.PendingTransfer is not { } pending) return;

        var answer = MessageBox.Show(this,
            $"A chip was prepared on {pending.StartedAt.LocalDateTime:dd/MM/yyyy} at " +
            $"{pending.StartedAt.LocalDateTime:HH:mm} to take over your license, and never " +
            "received it.\n\n" +
            "It already carries your seed, so it opens your files. What is missing is the " +
            "transfer: until it is done the license is still registered to the old dongle, and " +
            "this chip cannot encrypt anything new.\n\n" +
            "Finish it now? Plug that chip in first. Nothing has to be typed again — not the " +
            "24 words, not the programming.\n\n" +
            "No means this will be offered again on the next start.",
            "Finish the transfer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        RunTransferWizard(resume: true);
    }

    /// <summary>Shared by the Transfer button and the resume offer. The wizard
    /// reprograms and reconnects chips, so no session survives it either.</summary>
    private void RunTransferWizard(bool resume)
    {
        using (Foreground())
        {
            using var wizard = new LicenseTransferWizard(_dongle, _settings, resume);
            wizard.ShowDialog(this);
        }

        _connectedSerial = "";
        SetState(DongleState.Disconnected);
        PollDongle();
    }

    // --- dongle presence --------------------------------------------------

    /// <summary>
    /// Enumerates without opening anything. Windows takes a few seconds to
    /// enumerate after a swap, so an absence here is a fact about this instant
    /// and not a conclusion.
    /// </summary>
    private void PollDongle()
    {
        if (_busy) return;

        var attached = CryptinDongle.ListSerials();
        var serial = attached.Count > 0 ? attached[0] : "";

        // Even when nothing changed: the first poll runs from the constructor,
        // before this form can host a dialog, so the handover has to be retried
        // on a later tick rather than only on a change of serial.
        if (serial == _connectedSerial)
        {
            OfferHandoverIfPending();
            return;
        }

        var hadDongle = _connectedSerial.Length > 0;
        _connectedSerial = serial;

        // Any change means power was cut somewhere, so whatever session existed
        // is gone: the unlocked state lives in the dongle's RAM.
        _dongle.Disconnect();
        _activate.Visible = false;
        _loadLicence.Visible = false;
        _licenceLine = "";

        if (serial.Length == 0)
        {
            SetState(DongleState.Disconnected);

            // Only worth a log line if something actually left. On a cold start
            // with no dongle the status bar already says it, in red, blinking.
            if (hadDongle) LogFail($"Dongle disconnected · {ReconnectAdvice}");
            return;
        }

        SetState(DongleState.Locked);
        if (attached.Count > 1)
            LogInfo($"{attached.Count} dongles connected · using {serial}");

        OfferHandoverIfPending();
    }

    /// <summary>
    /// Hands the seed over the moment a dongle that has never done it appears —
    /// not later, when something happens to need the PIN.
    ///
    /// A dongle with a pending handover must not sit here looking merely locked.
    /// Its PIN derives from a seed nobody has been told, so "enter your PIN" is,
    /// for that dongle, a dead end: there is nothing the user could type.
    ///
    /// Guarded twice over. <c>_handoverBusy</c> because ShowDialog pumps messages
    /// and the poll timer would otherwise re-enter this on its next tick;
    /// <c>_handoverOfferedFor</c> because a user who backs out must not be asked
    /// again every second — reconnecting offers it afresh.
    /// </summary>
    private void OfferHandoverIfPending()
    {
        if (_handoverBusy || _busy) return;
        if (_connectedSerial.Length == 0) return;

        // A backup this process has just written is the one dongle that must
        // not be asked. Its handover was closed as the recovery ended (step 3),
        // so there is nothing to hand over -- and the USB device was replaced
        // underneath us, so the ask dies on the transport instead of answering.
        //
        // Measured on the VM, 2026-09-03: what a tester actually got was "the
        // dongle 14C19... did not answer (write of opcode 0x09 failed)", about
        // a chip that was perfectly well, as the first thing between them and
        // the restart the wizard had just asked for. The diagnosis in that box
        // is correct and it is still the wrong box: nothing here can succeed
        // until the process ends, so the honest move is to say that and stop.
        if (BackupWizard.RestartRequired)
        {
            if (_restartNoticeFor != _connectedSerial)
            {
                _restartNoticeFor = _connectedSerial;
                LogInfo("Backup prepared · close Crypt-in and open it again to finish it");
            }
            return;
        }

        if (_handoverOfferedFor == _connectedSerial) return;
        if (!IsHandleCreated || !Visible) return;

        _handoverBusy = true;
        try
        {
            if (!_dongle.IsConnected && !_dongle.Connect(_connectedSerial, waitMs: 3000)) return;

            _handoverOfferedFor = _connectedSerial;
            ProvisioningHandoverUi.EnsureHandedOver(this, _dongle);
        }
        catch (CryptinDongleException e)
        {
            LogFail($"Handover failed · {e.Message}");
        }
        finally
        {
            _handoverBusy = false;
        }
    }

    /// <summary>
    /// Marks a stretch in which this window owns the dongle, so the poll timer
    /// stays out of it.
    ///
    /// The timer sends no HID command of its own — <c>ListSerials</c> only
    /// enumerates — so this is not what keeps replies from crossing; the SDK's
    /// own serialisation does that. What it stops is the timer acting on a dongle
    /// somebody is already using: <see cref="PollDongle"/> calls
    /// <c>Disconnect()</c> the moment the serial appears to change, which would
    /// close the stream under an encryption running on a thread-pool thread, and
    /// <see cref="OfferHandoverIfPending"/> would raise a second dialog over the
    /// PIN prompt because <c>ShowDialog</c> keeps pumping messages.
    ///
    /// Nesting is counted, not assumed away.
    /// </summary>
    private IDisposable Foreground()
    {
        if (_foregroundDepth++ == 0)
        {
            _busy = true;
            _watch.Stop();
        }
        return new ForegroundScope(this);
    }

    private sealed class ForegroundScope(MainForm owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            if (--owner._foregroundDepth != 0) return;
            owner._busy = false;

            // Not on a window that is going away: OnFormClosed stops the timer
            // deliberately, and a scope unwinding afterwards would restart it to
            // tick against a disposed form.
            if (owner.IsDisposed || owner.Disposing) return;

            owner._watch.Start();
        }
    }

    // --- unlocking --------------------------------------------------------

    private void UnlockFromButton()
    {
        if (EnsureUnlocked()) LogInfo($"Unlocked · {_connectedSerial}");
    }

    /// <summary>
    /// Connect, then PIN if needed. Returns false if the user gave up.
    ///
    /// Deliberately does not trust <see cref="_state"/> alone: the dongle can
    /// have auto-locked underneath it, and the caller finds that out from
    /// <see cref="PinRequiredException"/> rather than from here.
    /// </summary>
    private bool EnsureUnlocked()
    {
        if (_state == DongleState.Unlocked && _dongle.IsConnected) return true;
        if (_connectedSerial.Length == 0) return false;

        using var foreground = Foreground();

        if (!_dongle.IsConnected && !_dongle.Connect(_connectedSerial, waitMs: 3000))
        {
            LogFail("The dongle is not responding. Unplug it and plug it back in.");
            return false;
        }

        // A dongle can reach this window without ever seeing the wizard, which
        // runs once per app rather than once per dongle. Asking for the PIN
        // before handing the seed over would be asking for something the user
        // was never told.
        if (!ProvisioningHandoverUi.EnsureHandedOver(this, _dongle)) return false;

        using var prompt = new PinForm(_dongle, _dongle.UsbSerial);
        if (prompt.ShowDialog(this) != DialogResult.OK) return false;

        SetState(DongleState.Unlocked);
        _licenceRefresh = RefreshLicenceAsync();
        return true;
    }

    /// <summary>
    /// Closes this app's handle and returns to the locked state.
    ///
    /// **This does not lock the chip, and cannot.** There is no lock opcode in
    /// the protocol: the dongle's session lives in its RAM and ends only when it
    /// loses power or ten idle minutes pass. <c>Disconnect()</c> disposes the HID
    /// stream host-side, so the device never leaves the bus and never
    /// re-enumerates — another process could open it and use the session that is
    /// still open. The button's tooltip says so, and the honest instruction for
    /// anyone who needs it locked for certain is to unplug it.
    ///
    /// The wait therefore is not "waiting for re-enumeration", which would never
    /// come. It is a settle: the same five seconds the app quotes everywhere a
    /// replug is involved, so that a user who does pull the dongle during it ends
    /// up in the right state rather than in a stale one.
    /// </summary>
    private async Task LockAsync()
    {
        using var foreground = Foreground();

        _dongle.Disconnect();
        SetState(DongleState.Locking);
        LogInfo("Locked · PIN required again");

        var started = DateTime.UtcNow;
        await Task.Delay(LockSettle);

        // Past the settle, keep looking until the deadline. Normally the dongle
        // is already there -- it never went anywhere -- and this returns at once.
        while (DateTime.UtcNow - started < LockDeadline)
        {
            if (CryptinDongle.ListSerials().Count > 0) break;
            await Task.Delay(250);
        }

        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0)
        {
            _connectedSerial = "";
            SetState(DongleState.Disconnected);
            return;
        }

        _connectedSerial = attached[0];
        _licenceLine = "";
        SetState(DongleState.Locked);
    }

    // --- licence ----------------------------------------------------------

    /// <summary>
    /// Asks the server where the licence stands. Never blocks *decryption*: a
    /// file encrypted under a lapsed licence still opens, because the licence
    /// governs the product and the dongle governs the data.
    ///
    /// Encryption is gated — see <see cref="AllowEncryptionAsync"/>. That is a
    /// deliberate asymmetry and the only one.
    /// </summary>
    private async Task RefreshLicenceAsync()
    {
        _licenceLine = "Checking license…";
        RestoreSubtitle();
        _activate.Visible = false;
        _loadLicence.Visible = false;

        LicenseStatus status;
        _licenceStatus = null;
        try
        {
            using var client = new LicenseClient(LicenseEndpoint.BaseUrl);
            status = _licenceStatus = await Task.Run(() => client.VerifyAsync(_dongle));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Offline is not a licence problem. A request past
            // LicenseClient.RequestTimeout arrives as TaskCanceledException and
            // means the same thing: we could not ask.
            _licenceLine = "License server unreachable · will retry on next start";
            RestoreSubtitle();
            return;
        }
        catch (Exception e)
        {
            _licenceLine = $"License check failed · {e.Message}";
            RestoreSubtitle();
            return;
        }

        _licenceLine = status.State switch
        {
            LicenseState.Valid when status.ExpiresAt is not null =>
                $"License valid · expires {status.ExpiresAt:dd/MM/yyyy}",
            LicenseState.Valid => "License valid",
            LicenseState.InGracePeriod =>
                $"License being revoked · usable until {status.GraceUntil:dd/MM/yyyy}",
            LicenseState.Expired when status.ExpiresAt is not null =>
                $"License expired {status.ExpiresAt:dd/MM/yyyy} · existing files still open",
            LicenseState.Expired => "License expired · existing files still open",
            LicenseState.Revoked => "License revoked · existing files still open",
            LicenseState.NotFound => "No active license · click Activate license",
            LicenseState.UnauthorizedDongle =>
                "This dongle is not authorized on the license · if the old one is gone, " +
                "click Transfer license",
            _ => "License state unknown",
        };

        _activate.Visible = status.State is LicenseState.NotFound or LicenseState.UnauthorizedDongle;
        RestoreSubtitle();
        ApplyState();

        await RefreshDongleLicenceAsync();
    }

    /// <summary>
    /// Asks the dongle itself what it thinks of its licence, and speaks up only
    /// when it disagrees with the server.
    ///
    /// Two sources that can legitimately differ: the server knows what was paid
    /// for, the dongle knows what it was handed. A renewal that reached the
    /// server but not the chip is the ordinary case, and it is the one worth
    /// surfacing.
    ///
    /// Silent on <see cref="DongleLicenseError.Unsupported"/>: reporting "no
    /// licence on the dongle" for firmware that has no such concept would be
    /// alarming and wrong.
    /// </summary>
    private async Task RefreshDongleLicenceAsync()
    {
        DongleLicenseStatus dongleLicence;
        try
        {
            dongleLicence = await _dongle.GetLicenseStatusAsync();
        }
        catch (CryptinDongleException)
        {
            return;
        }

        if (dongleLicence.Error == DongleLicenseError.Unsupported) return;

        if (dongleLicence.IsValid)
        {
            ReportExpiryDivergence(dongleLicence);
            return;
        }

        if (_licenceStatus is { IsUsable: true })
        {
            // The server already handed us the token when it verified, so this
            // is a write to the chip, not a purchase: nothing needs paying
            // again, and nothing moves anywhere.
            _loadLicence.Visible = _licenceStatus.Jwt is { Length: > 0 };
            _licenceLine = _loadLicence.Visible
                ? "License valid, but not yet on the dongle · click Load license"
                : "License valid, but the server returned no token · retry on next start";
            RestoreSubtitle();
            ApplyState();
            return;
        }

        _licenceLine = dongleLicence.Error switch
        {
            DongleLicenseError.Expired =>
                "License expired on the dongle · existing files still open",
            DongleLicenseError.InvalidSignature =>
                "The dongle does not recognize the license signature · contact support",
            _ => "The dongle has no license · activate one to encrypt new files",
        };
        _activate.Visible = true;
        RestoreSubtitle();
        ApplyState();
    }

    /// <summary>
    /// Says so when the dongle holds a licence that is not the one the server
    /// just described.
    ///
    /// What stood here was <c>if (dongleLicence.IsValid) return;</c> — "does the
    /// chip hold *a* valid licence", never "does it hold *this* one". A chip
    /// still carrying an older, unexpired token therefore silenced the exact
    /// report <see cref="RefreshDongleLicenceAsync"/> exists to make. Measured
    /// 2026-08-29: the window showed the server's 31/03/2027 while the chip held
    /// 02/08/2027, five months apart, and nothing anywhere said a word.
    ///
    /// Only speaks when both dates are known. A server that could not be reached,
    /// or a reply carrying no expiry, is an absence and not a disagreement —
    /// warning on it would cry wolf on every offline start.
    /// </summary>
    private void ReportExpiryDivergence(DongleLicenseStatus dongleLicence)
    {
        if (_licenceStatus is not { IsUsable: true, ExpiresAt: { } serverExpiry }) return;
        if (dongleLicence.ExpiresAt is not { } chipExpiry) return;

        // A minute of slack. The two values travel as different types — the chip
        // answers whole Unix seconds, the server a DateTimeOffset — and rounding
        // between them must not read as a divergence.
        if ((serverExpiry.UtcDateTime - chipExpiry).Duration() <= TimeSpan.FromMinutes(1)) return;

        _loadLicence.Visible = _licenceStatus.Jwt is { Length: > 0 };
        _licenceLine =
            $"Dongle licence expires {chipExpiry:dd/MM/yyyy}, the server says {serverExpiry:dd/MM/yyyy}"
            + (_loadLicence.Visible
                ? " · click Load license"
                : " · the server returned no token to load");
        RestoreSubtitle();
        ApplyState();
    }

    private async Task BackupAsync()
    {
        if (!EnsureUnlocked()) return;

        // Both of the checks below come BEFORE the licence gate, and that order
        // is the whole point. They are about a backup already under way, and at
        // that moment the chip in the socket is deliberately the BACKUP -- one
        // the licence server has never heard of, so /api/verify answers
        // CLONE_OR_UNREGISTERED for it. Asking first meant someone whose only
        // problem was "you have not restarted yet" got told they had no
        // licence: the message they needed, hidden behind a check that had
        // nothing to do with them. Measured on the VM run of 2026-09-03.
        if (BackupWizard.RestartRequired)
        {
            MessageBox.Show(this, BackupWizard.RestartMessage,
                "Backup — one restart left", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // With a backup half done, this button has two meanings and only the
        // user knows which. Resuming silently would be the wrong one exactly
        // when it matters: a step 4 that reports the wrong seed is answered by
        // starting over, and a wizard that always reopens at step 4 has no
        // step 1 to start over from.
        var resume = false;
        if (_settings.PendingBackup is { } pending)
        {
            var answer = MessageBox.Show(this,
                $"A backup prepared on {pending.StartedAt.LocalDateTime:dd/MM/yyyy} at " +
                $"{pending.StartedAt.LocalDateTime:HH:mm} was never registered.\n\n" +
                "Yes finishes that one: put it in and it goes straight to the PIN check.\n" +
                "No starts a new backup from the 24 words, on whichever chip you choose. The " +
                "old one is left exactly as it is — it keeps the seed, it just stays unregistered.",
                "Unfinished backup", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return;
            resume = answer == DialogResult.Yes;
        }

        // Only a backup being started from scratch needs the licence answered
        // here, and it is the MAIN dongle that has to be in the socket for it:
        // step 1 reads that chip's identity. A resume is past all of that, and
        // its step 5 asks the server itself, with the main dongle reconnected.
        if (!resume)
        {
            // The unlock fires the licence refresh without awaiting it, so
            // pressing Backup straight afterwards used to land here with
            // _licenceStatus still null and get "no active license" -- on a
            // dongle that had one. Waiting for the answer already on its way
            // costs whatever is left of one request.
            if (_licenceStatus is null)
            {
                using (Foreground()) await _licenceRefresh;
            }

            if (_licenceStatus?.IsUsable != true)
            {
                LogFail(_licenceStatus?.State switch
                {
                    LicenseState.Expired => "License expired · renew it before registering a backup",
                    LicenseState.NotFound => "Activate a license on this dongle first",
                    // Almost always the backup itself, still in the socket: the
                    // server does not know that chip, which says nothing about
                    // the licence. Naming the dongle beats naming the licence.
                    LicenseState.UnauthorizedDongle =>
                        "This dongle is not the one the license is on · connect the main dongle to register a backup",
                    LicenseState.Revoked => "License revoked · it does not accept backups",
                    // Still nothing after waiting: the server was never reached.
                    // That is not a verdict on the license, and saying it is
                    // sends someone to re-buy what they already own.
                    null => "The license server could not be asked · registering a backup needs it",
                    _ => "A backup can only be registered on an active license",
                });
                return;
            }
        }

        RunBackupWizard(resume);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Hands the dongle the token the server already issued. Not a purchase and
    /// not a renewal: sending the user back through activation would ask for a
    /// session id they no longer have, for something already bought.
    /// </summary>
    private async Task LoadLicenceOntoDongleAsync()
    {
        if (_licenceStatus?.Jwt is not { Length: > 0 } jwt)
        {
            LogFail("The server returned no token to load");
            return;
        }

        if (!EnsureUnlocked()) return;

        using var foreground = Foreground();
        try
        {
            await _dongle.SetLicenseJwtAsync(jwt);
            LogInfo("License loaded onto the dongle");
            _loadLicence.Visible = false;
            await RefreshLicenceAsync();
        }
        catch (LicenseRejectedException e)
        {
            // The dongle looked at the token and said no. Worth reporting
            // verbatim: a token issued for another chip and an expired one need
            // different answers, and neither is fixed by trying again.
            LogFail($"The dongle rejected the license · {e.Message}");
        }
        catch (CryptinDongleException e)
        {
            LogFail($"Loading the license failed · {e.Message}");
        }
    }

    private async Task ActivateAsync()
    {
        if (!EnsureUnlocked()) return;

        ActivateResponse? activated;
        using (Foreground())
        {
            using var form = new ActivationForm(_dongle, _settings);
            if (form.ShowDialog(this) != DialogResult.OK) return;
            activated = form.Result;
        }

        LogInfo($"License activated until {activated?.ExpiresAt:dd/MM/yyyy}");
        if (activated is not null) await ConfirmTokenReachedDongleAsync(activated);
        await RefreshLicenceAsync();
    }

    /// <summary>
    /// Checks that the token the server just issued actually reached the chip,
    /// and says what happened when it did not.
    ///
    /// <c>LicenseClient.ActivateAsync</c> already pushes it (step 6), and
    /// **discards a refusal on purpose**: the licence is bought and recorded
    /// server-side by then, so throwing there would leave the user holding a paid
    /// licence and an exception. That reasoning is sound and stays — the SDK is
    /// Apache-2.0 and third parties depend on it. What was missing is anyone
    /// checking afterwards. Measured 2026-08-29: a token the chip refused left no
    /// trace at all, and the window went on showing the server's date.
    ///
    /// The push is the check, and it is deliberately unconditional. An earlier
    /// version of this method first read 0x0C and skipped the push when the
    /// chip's expiry already matched the one just issued — which was the same
    /// mistake it exists to catch, one level down. **A date is not the identity
    /// of a licence**, and for the beta SKU it is emphatically not: `exp` is a
    /// fixed 2027-03-31 shared by every beta licence ever issued, by any server.
    /// So a token refused by this chip and the token already on it compared
    /// equal, and the refusal went unreported exactly where it mattered.
    /// Measured on hardware 2026-08-29, against the red-team clone.
    ///
    /// Nothing readable from the chip identifies *which* token it holds:
    /// CMD_GET_LICENSE_STATUS answers four bytes of expiry and no issuer, no
    /// serial, no signature. So the only honest verification left is to hand it
    /// over and look at the answer. A token this chip trusts is stored again,
    /// harmlessly; one it does not is refused again, and this time said out loud.
    /// </summary>
    private async Task ConfirmTokenReachedDongleAsync(ActivateResponse activated)
    {
        try
        {
            await _dongle.SetLicenseJwtAsync(activated.Jwt);
            LogInfo("License token stored on the dongle");
        }
        catch (LicenseRejectedException e) when (e.Error == DongleLicenseError.Unsupported)
        {
            // Firmware older than licences on the chip. Nothing is wrong here,
            // and saying so would send the user looking for a fault they cannot
            // fix and do not have.
        }
        catch (LicenseRejectedException e)
        {
            LogFail($"The dongle refused the license token · {e.Message} · the license is "
                    + "recorded on the server, the dongle keeps the one it already had");
        }
        catch (CryptinDongleException e)
        {
            LogFail($"The license token did not reach the dongle · {e.Message}");
        }
    }

    // --- drag and drop ----------------------------------------------------

    private void RestoreDropColour()
        => _drop.BackColor = _state == DongleState.Unlocked
            ? SystemColors.ControlLightLight
            : SystemColors.Control;

    /// <summary>
    /// Silently ignored unless unlocked, as the brief requires. A disabled Panel
    /// does not raise these at all in practice, but the guard is kept: the state
    /// can change between the drag starting and the drop landing.
    /// </summary>
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (_state != DongleState.Unlocked) return;
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;

        e.Effect = DragDropEffects.Copy;
        _drop.BackColor = Color.FromArgb(232, 240, 254);
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        RestoreDropColour();
        if (_state != DongleState.Unlocked) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        _ = ProcessAsync(paths);
    }

    private async Task ProcessAsync(string[] paths)
    {
        if (!EnsureUnlocked()) return;

        // The licence gates *writing*, never reading. A drop can hold both, so
        // the two are separated here rather than refusing the whole gesture:
        // dropping a .crin together with a new file must still open the .crin.
        if (!await AllowEncryptionAsync(paths))
        {
            paths = paths.Where(FileOperations.LooksEncrypted).ToArray();
            if (paths.Length == 0) return;
        }

        using var foreground = Foreground();
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;

        SetState(DongleState.Busy);
        _progress.Value = 0;
        _percent.Text = "0%";

        try
        {
            for (var i = 0; i < paths.Length; i++)
                if (!await ProcessOneGuardedAsync(paths[i], i, paths.Length, cancellation.Token))
                    break;
        }
        catch (OperationCanceledException)
        {
            // Nothing to clean up and nothing to log: FileOperations writes only
            // once a file has finished, so a cancelled one left no output, and
            // the brief asks for no log line on a user cancellation.
            _subtitle.Text = "Cancelled";
        }
        finally
        {
            _cancellation = null;
            SetState(DongleState.Unlocked);
            RestoreSubtitle();
            PollDongle();
        }
    }

    /// <summary>
    /// One file, with the two failures that mean different things kept apart.
    ///
    /// A file-level fault — wrong dongle, damaged, too large, unreadable — is
    /// logged and the drop carries on: dropping five files where the second is
    /// corrupt should still open the other four. A transport fault means the
    /// dongle is gone or unwell, and continuing would just produce four more of
    /// the same error, so it stops the drop.
    /// </summary>
    private async Task<bool> ProcessOneGuardedAsync(
        string path, int index, int count, CancellationToken ct)
    {
        try
        {
            await ProcessOneWithReunlockAsync(path, index, count, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (IsFileLevel(e))
        {
            LogFail($"Failed → {Path.GetFileName(path)} · {ShortReason(e)}");
            return true;
        }
        catch (Exception e)
        {
            LogFail($"Failed → {Path.GetFileName(path)} · {ShortReason(e)}");

            // If the dongle has left mid-transfer the user lost work, and a log
            // line is not enough. This cannot come from the poll timer --
            // Foreground() stopped it -- so the transport exception is what
            // drives it.
            if (CryptinDongle.ListSerials().Count == 0)
            {
                MessageBox.Show(this,
                    $"Dongle disconnected. {ReconnectAdvice}.\n\n" +
                    "The operation was interrupted. No partial files were written — " +
                    "reconnect the dongle and try again.",
                    "Dongle disconnected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return false;
        }
    }

    /// <summary>Faults that belong to one file rather than to the link.</summary>
    private static bool IsFileLevel(Exception e) => e
        is WrongDongleException
        or CorruptedCryptinFileException
        or InvalidCryptinFileException
        or CryptinFileTooLargeException
        or NoActiveLicenseException
        or UnauthorizedAccessException
        or IOException;

    /// <summary>
    /// May these paths be encrypted? True when there is nothing to encrypt.
    ///
    /// The rule is deliberately not "state == Valid". <c>InGracePeriod</c> is
    /// documented as still usable, and a null status is not a verdict — it means
    /// the check has not run or the server was unreachable, and treating "we
    /// could not ask" as "you have no licence" would take the app offline the
    /// moment the network did.
    /// </summary>
    private async Task<bool> AllowEncryptionAsync(string[] paths)
    {
        if (paths.All(FileOperations.LooksEncrypted)) return true;
        if (_licenceStatus is not { IsUsable: false }) return true;

        var choice = MessageBox.Show(this,
            "No active license found.\n\n" +
            "An active license is required to encrypt new files. " +
            "Files already encrypted always remain accessible.\n\n" +
            "Would you like to activate a license now?",
            "License required", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        _activate.Visible = true;
        ApplyState();

        if (choice != DialogResult.Yes)
        {
            LogFail("No active license · click Activate license to continue");
            return false;
        }

        await ActivateAsync();
        return _licenceStatus is { IsUsable: true };
    }

    private void ProcessOne(string path, IProgress<int> progress, CancellationToken ct)
    {
        var decrypting = FileOperations.LooksEncrypted(path);
        var result = decrypting
            ? FileOperations.Decrypt(path, _dongle, progress, ct)
            : FileOperations.Encrypt(path, _dongle, progress, ct);

        var name = Path.GetFileName(result);
        if (decrypting) LogDecrypted(name);
        else LogEncrypted(name);
    }

    /// <summary>
    /// One file through the dongle, surviving a session that locked itself.
    ///
    /// The dongle ends an idle session on its own after
    /// <c>PIN_SESSION_TIMEOUT_MS</c> (CRYPTIN_PROTOCOL.md §6), so
    /// <c>ERR_PIN_REQUIRED</c> can arrive on a session this window believes is
    /// open. It is never shown as an error: the window drops to Locked, asks for
    /// the PIN, and resumes this one file.
    ///
    /// Redoing the file from the start has no side effects to undo:
    /// FileOperations builds the whole blob in memory and writes it in a single
    /// call, so an interrupted transfer leaves no partial output. Only this file
    /// repeats; the ones already finished in this drop stay finished.
    ///
    /// One retry only. A second refusal is not a stale session, and prompting in
    /// a loop would be worse than the error it replaced.
    /// </summary>
    private async Task ProcessOneWithReunlockAsync(
        string path, int index, int count, CancellationToken ct)
    {
        var decrypting = FileOperations.LooksEncrypted(path);
        var progress = FileProgress(path, index, count, decrypting);

        try
        {
            await Task.Run(() => ProcessOne(path, progress, ct), ct);
        }
        catch (PinRequiredException)
        {
            // Silent transition, as the brief requires: the user asked for a
            // file, not for a lecture about session lifetimes.
            SetState(DongleState.Locked);

            if (!EnsureUnlocked())
            {
                LogFail($"Cancelled → {Path.GetFileName(path)} · PIN required");
                throw new OperationCanceledException();
            }

            SetState(DongleState.Busy);
            _progress.Value = 0;
            _percent.Text = "0%";

            await Task.Run(() => ProcessOne(path, progress, ct), ct);
        }
    }

    // --- progress ---------------------------------------------------------

    /// <summary>
    /// A progress sink for one file in a drop that may hold several.
    ///
    /// Built on the UI thread so <see cref="Progress{T}"/> captures this window's
    /// synchronisation context and marshals every report back to it; the work
    /// runs on a thread-pool thread. The SDK only reports on a change of integer
    /// percent, so this runs at most 101 times per file.
    /// </summary>
    private IProgress<int> FileProgress(string path, int index, int count, bool decrypting)
    {
        var verb = decrypting ? "Decrypting" : "Encrypting";
        var name = Path.GetFileName(path);
        var which = count > 1 ? $" ({index + 1}/{count})" : "";

        return new Progress<int>(pct =>
        {
            _progress.Value = Math.Clamp(pct, 0, 100);
            if (_cancel.Enabled) _percent.Text = $"{pct}%";
            _subtitle.Text = $"{verb} {name}{which}…";
        });
    }

    private void CancelOperation()
    {
        // Disabled rather than hidden: the transfer stops at the next chunk
        // boundary, not instantly, and a button that vanishes on click leaves the
        // user wondering whether it registered.
        _cancel.Enabled = false;
        _percent.Text = "cancelling…";
        _cancellation?.Cancel();
    }

    // --- session log ------------------------------------------------------

    private sealed record LogEntry(string Text, Color Colour);

    private void OnDrawLogItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _log.Items.Count) return;

        e.DrawBackground();
        if (_log.Items[e.Index] is LogEntry entry)
            TextRenderer.DrawText(e.Graphics, entry.Text, _log.Font, e.Bounds,
                entry.Colour, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void LogEncrypted(string name) => Append($"Encrypted → {name}", EncryptedText);
    private void LogDecrypted(string name) => Append($"Decrypted → {name}", SystemColors.GrayText);
    private void LogFail(string text) => Append(text, FailedText);
    private void LogInfo(string text) => Append(text, SystemColors.ControlText);

    /// <summary>
    /// In memory only, and gone when the window closes. A log of which files were
    /// encrypted, kept on disk, is a map of what is worth stealing — and it would
    /// outlive the session it describes.
    /// </summary>
    private void Append(string text, Color colour)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(text, colour)); return; }
        if (IsDisposed || Disposing) return;

        _log.Items.Add(new LogEntry($"{DateTime.Now:HH:mm:ss}  {text}", colour));
        while (_log.Items.Count > LogMaxLines) _log.Items.RemoveAt(0);
        _log.TopIndex = _log.Items.Count - 1;
    }

    // --- error wording ----------------------------------------------------

    /// <summary>
    /// One line for the session log — short, but not at the cost of the two
    /// clauses this product cannot drop.
    ///
    /// "Wrong dongle" keeps *not damaged*, because the whole container format
    /// exists to tell those two apart and the advice is opposite: find the other
    /// dongle, versus the file is gone. And the size ceiling keeps *will rise*,
    /// because "too large" on its own invites retrying the same file, which is
    /// the one thing that cannot work.
    /// </summary>
    private static string ShortReason(Exception e) => e switch
    {
        WrongDongleException => "encrypted with a different dongle · not damaged",
        NoActiveLicenseException => "no active license · encryption needs one",
        CryptinFileTooLargeException x =>
            $"over the {x.Maximum / 1_000_000.0:F0} MB limit · this will rise in a future update",
        CorruptedCryptinFileException => "damaged or tampered with · right dongle, bad data",
        InvalidCryptinFileException => "not a readable Crypt-in file",
        PinRequiredException => "PIN required",
        CryptinTimeoutException => "the dongle did not respond in time",
        UnauthorizedAccessException => "insufficient permissions",
        IOException x => $"file error · {x.Message}",
        _ => e.Message,
    };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch.Stop();
        _blink.Stop();

        // A drop still running holds the dongle on a thread-pool thread. Ask it
        // to stop before disposing what it is talking to.
        _cancellation?.Cancel();

        _dongle.Dispose();
        base.OnFormClosed(e);
    }
}
