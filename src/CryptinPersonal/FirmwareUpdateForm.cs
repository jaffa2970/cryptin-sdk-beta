using CryptinSDK.Updater;

namespace CryptinPersonal;

/// <summary>
/// Updates the firmware of the dongle in hand, keeping it the same dongle
/// (decided 2026-10-31).
///
/// Before this the only way to put new firmware on a beta dongle was Transfer
/// license: a full reflash that wipes the keys, the 24 words typed back in, and
/// one of the three yearly transfers spent on an update that was nobody's fault.
/// The updater's <c>update-app</c> writes from 0xE000 on and leaves nvs alone, so
/// the seed, the PIN, K_attestation and the licence token all stay; the server
/// is never asked. Its guards live in the updater, not here: it reads the
/// chip's partition table and nvs and writes nothing unless both say "same
/// layout, already a dongle".
/// </summary>
internal sealed class FirmwareUpdateForm : Form
{
    /// <summary>Same reason as <see cref="LicenseTransferWizard.RestartRequired"/>:
    /// the write resets the chip under this process, and only a new process
    /// talks to the device Windows enumerates afterwards.</summary>
    internal static bool RestartRequired { get; private set; }

    internal const string RestartMessage =
        "Close Crypt-in now and open it again. The dongle is the same one: same 24 words, same " +
        "PIN, same license — nothing has to be typed or moved.\n\n" +
        "Why a restart and not a button: the chip was rewritten and reset while this application " +
        "held it open, and Windows replaced the device underneath. Only ending the process " +
        "clears that.";

    private readonly ComboBox _ports = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _update = new();

    public FirmwareUpdateForm()
    {
        Text = "Crypt-in — update firmware";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 400);

        var heading = new Label
        {
            Text = "Update the firmware of this dongle",
            Location = new Point(16, 14),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
        };

        var detail = new Label
        {
            Location = new Point(16, 44),
            Size = new Size(528, 90),
            Text =
                "This writes the new firmware and keeps everything else: your 24 words, your PIN " +
                "and your license stay as they are. Nothing is asked of the license server and " +
                "no transfer is used.\n\n" +
                "Connect the second cable to the socket marked UART and leave the native one " +
                "where it is. It takes under a minute.",
        };

        _ports.Location = new Point(16, 144);
        _ports.Size = new Size(380, 26);
        _ports.DropDownStyle = ComboBoxStyle.DropDownList;
        // Hover and focus, not only DropDown: see LicenseTransferWizard, where
        // refilling on DropDown alone showed the previous list.
        _ports.MouseEnter += (_, _) => LoadPorts();
        _ports.Enter += (_, _) => LoadPorts();
        _ports.DropDown += (_, _) => LoadPorts();

        var refresh = new Button { Text = "Refresh", Location = new Point(408, 143), Size = new Size(100, 28) };
        refresh.Click += (_, _) => LoadPorts();

        var uartNote = new Label
        {
            Location = new Point(16, 176),
            Size = new Size(528, 40),
            ForeColor = SystemColors.GrayText,
            Text = SerialPorts.OnlyUartNote,
        };

        _update.Text = "Update";
        _update.Location = new Point(16, 222);
        _update.Size = new Size(130, 30);
        _update.Click += async (_, _) => await UpdateAsync();

        _progress.Location = new Point(16, 262);
        _progress.Size = new Size(492, 20);

        var updater = new UpdaterProcess();
        if (!updater.IsInstalled) _update.Enabled = false;

        var fine = new Label
        {
            Location = new Point(16, 292),
            Size = new Size(528, 90),
            ForeColor = SystemColors.GrayText,
            Text =
                (updater.IsInstalled ? "" : "CryptinUpdater.exe is not installed next to this application.\n\n") +
                "If the chip is not a beta dongle with the same layout as this firmware, nothing " +
                "is written and you are told why. Pulling a cable during the write leaves the keys " +
                "where they are: run Update again.",
        };

        var close = new Button { Text = "Close", Location = new Point(444, 222), Size = new Size(100, 30) };
        close.Click += (_, _) => Close();
        CancelButton = close;

        Controls.AddRange([heading, detail, _ports, refresh, uartNote, _update, _progress, fine, close]);
        LoadPorts();
    }

    private void LoadPorts()
    {
        var chosen = (_ports.SelectedItem as SerialPortInfo)?.PortName;

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

        var at = -1;
        for (var i = 0; i < _ports.Items.Count; i++)
            if (_ports.Items[i] is SerialPortInfo info
                && info.PortName.Equals(chosen, StringComparison.OrdinalIgnoreCase))
                at = i;
        _ports.SelectedIndex = at >= 0 ? at : 0;
    }

    private async Task UpdateAsync()
    {
        if (_ports.SelectedItem is not SerialPortInfo port)
        {
            MessageBox.Show(this,
                "No COM port to write to.\n\n" +
                "Connect the cable to the socket marked UART, then open the list above: it " +
                "re-reads the ports every time it opens.",
                "Update firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Looked up again, as the transfer does: the list is filled by UI events
        // and another board can have taken that COM number in the meantime.
        if (!SerialPorts.Programmable().Any(p => p.PortName.Equals(port.PortName, StringComparison.OrdinalIgnoreCase)))
        {
            LoadPorts();
            MessageBox.Show(this,
                $"{port.PortName} is not there any more. Nothing was written: pick the port again.",
                "Update firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _update.Enabled = false;
        _progress.Value = 0;
        try
        {
            var updater = new UpdaterProcess();
            var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
            var result = await updater.UpdateAppAsync(port.PortName, progress);

            if (result != FlashResult.Success)
            {
                // Seen on γ, 2026-11-04: a write cut half-way (cable pulled, process
                // killed) leaves the dongle with no application -- it does not start
                // and shows no dongle interface until Update completes. The keys are
                // not touched (nothing is written below 0xE000). "Not updated" alone
                // reads as "nothing happened", and a dongle that then vanishes reads
                // as a broken one. Say both halves.
                var what = result switch
                {
                    FlashResult.PortNotFound => $"{port.PortName} is not responding: {updater.LastError}",
                    _ => $"The firmware was not updated. {updater.LastError}",
                };
                MessageBox.Show(this,
                    what.TrimEnd() + "\n\n" +
                    "If this stopped during the write, the dongle will not start until an update " +
                    "completes. Your 24 words, your PIN and your license are not touched: check the " +
                    "UART cable and press Update again.",
                    "Update firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _progress.Value = 100;
            RestartRequired = true;
            MessageBox.Show(this, "The firmware is updated.\n\n" + RestartMessage,
                "Firmware updated — one restart left", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (UpdaterNotInstalledException e)
        {
            MessageBox.Show(this, e.Message, "Updater missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!RestartRequired) _update.Enabled = true;
        }
    }
}
