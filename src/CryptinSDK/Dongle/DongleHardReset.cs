// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.IO.Ports;

namespace CryptinSDK.Dongle;

/// <summary>
/// Power-cycles the dongle over the UART bridge, by holding EN low.
///
/// **Deliberately not a method on <see cref="CryptinDongle"/>.** That class is
/// an open HID connection, and the whole point of this one is to work when there
/// is no HID to open: a chip sitting in USB-Serial/JTAG mode
/// (<c>VID_303A&amp;PID_1001</c>) is not running the application, so nothing
/// answers the protocol and no opcode can reach it. Hanging this off a connected
/// dongle would promise the opposite of what it does.
///
/// This drives a hardware line, so it works on a chip that is wedged, unflashed
/// or mid-boot. What it needs in exchange is the **UART cable**: EN is reachable
/// only through the RTS pin of the CH343 bridge
/// (<c>VID_1A86&amp;PID_55D3</c>). Driving RTS on the ESP32's own
/// USB-Serial/JTAG port does nothing — that is the same reason esptool's "hard
/// resetting via RTS pin" does not get a chip out of download mode there.
///
/// **A reset ends the unlocked session.** The session lives in the chip's RAM,
/// so the dongle comes back LOCKED and the PIN is needed again. That is a
/// feature where it matters: <c>CryptinDongle.Disconnect()</c> only closes the
/// host handle and leaves the session alive on the chip until the ten-minute
/// auto-lock, which is the gap behind the "Lock button does not lock the chip"
/// entry in STATO_LAVORI.md §6. This does lock it.
/// </summary>
public static class DongleHardReset
{
    /// <summary>How long EN is held low. Half a second, not a pulse: the USB PHY
    /// has to look *disconnected* to the host rather than merely unresponsive,
    /// and a host that never sees a disconnect will not re-enumerate on the way
    /// back. Inherited from tools/soft_reset.py, which is where it was measured.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long to wait for the HID interface to come back.
    ///
    /// **Sixty seconds, and it is not padding.** The chip boots in about two,
    /// but the host is the slow half: measured on Windows 2026-08-05, the
    /// interface reappeared **31,0 s** after the line was released. A first cut
    /// at 15 s reported failure on a reset that had worked perfectly — and the
    /// natural reading of that, "the enumeration list is stale inside a
    /// long-lived process", is wrong: the same process saw the device, it simply
    /// had to wait. Do not lower this without measuring again on the platform
    /// you are lowering it for.
    /// </summary>
    public const int DefaultWaitMs = 60_000;

    /// <summary>How long the dongle has to leave the bus before we accept that
    /// nothing was reset. EN is held for half a second and the host notices the
    /// removal well inside this, while a genuine reset keeps it away for tens of
    /// seconds — so this only has to be longer than the notice, not the boot.</summary>
    public const int VanishTimeoutMs = 8_000;

    /// <summary>
    /// Ports positively identified as the CH343 bridge, by vendor.
    ///
    /// **Empty is the normal answer on Windows**, which exposes no VID/PID
    /// through <see cref="SerialPort"/>: identification is impossible there, and
    /// this returns nothing rather than guessing. Only Linux can answer, where
    /// <c>/dev/serial/by-id/</c> spells the vendor into the name.
    ///
    /// Empty therefore means "you must name the port", never "there is no
    /// bridge". Use <see cref="ListAllPorts"/> to offer the user a choice.
    /// Nothing here opens a port or touches a line.
    /// </summary>
    public static IReadOnlyList<string> ListBridgePorts()
    {
        const string byId = "/dev/serial/by-id";
        if (!OperatingSystem.IsLinux() || !Directory.Exists(byId))
            return [];

        var found = new List<string>();
        foreach (var link in Directory.EnumerateFileSystemEntries(byId))
        {
            var name = Path.GetFileName(link);

            // The CH343 announces itself as 1a86. Espressif's own USB-Serial/JTAG
            // shows up here too and must not be offered: RTS does not reach EN
            // from there, so a reset aimed at it silently does nothing.
            if (name.Contains("1a86", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CH343", StringComparison.OrdinalIgnoreCase))
                found.Add(Path.GetFullPath(link));
        }

        return found;
    }

    /// <summary>
    /// Every serial port on the machine, unfiltered — for showing a human what
    /// they could pass. Most of these have nothing to do with a dongle.
    ///
    /// ⚠️ <see cref="SerialPort.GetPortNames"/> alone is not enough on Linux:
    /// measured 2026-08-05 on Ubuntu 26.04, it returned <c>ttyACM</c> nodes but
    /// **not** an existing, openable <c>/dev/ttyS0</c> that pyserial had driven
    /// minutes earlier. On its own the caller then reported "no serial port on
    /// this machine", sending the user to check the one thing already fine and
    /// hiding the only route that worked. So on Linux the well-known device
    /// nodes are globbed and merged in.
    /// </summary>
    public static IReadOnlyList<string> ListAllPorts()
    {
        var names = new SortedSet<string>(SerialPort.GetPortNames(), StringComparer.Ordinal);
        if (!OperatingSystem.IsLinux()) return [.. names];

        foreach (var pattern in (string[])["ttyS*", "ttyUSB*", "ttyACM*"])
        {
            // Enumerating, not probing a guessed range of numbers: the kernel
            // decides which nodes exist.
            //
            // EnumerateFileSystemEntries rather than EnumerateFiles, and the
            // difference is not stylistic. A tty node is a character device, and
            // the entry-level call is the one that was *measured* to return all
            // 32 of them on Ubuntu (2026-08-06). EnumerateFiles ought to include
            // character devices too — it excludes directories, not devices — but
            // "ought to" is what this file keeps getting caught by, and the
            // version that has run on hardware wins over the version that reads
            // better.
            try
            {
                foreach (var node in Directory.EnumerateFileSystemEntries("/dev", pattern))
                    names.Add(node);
            }
            catch (DirectoryNotFoundException) { /* no /dev: nothing to add */ }
            catch (UnauthorizedAccessException) { /* keep what GetPortNames gave */ }
        }

        return [.. names];
    }

    /// <summary>
    /// Holds EN low on <paramref name="portName"/>, then releases it. Returns as
    /// soon as the line is released — the chip is still booting at that point;
    /// use <see cref="ResetAndWaitForHid"/> to wait for it to come back.
    /// </summary>
    /// <param name="portName">The CH343 bridge: <c>COM3</c>, <c>/dev/ttyACM0</c>.
    /// Not the dongle's HID path, and not the ESP32's own serial port.</param>
    /// <param name="hold">Defaults to <see cref="DefaultHold"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="portName"/> is blank.</exception>
    /// <exception cref="IOException">The port does not exist or cannot be opened —
    /// on Linux this is usually group membership (<c>dialout</c>), not absence.</exception>
    /// <exception cref="UnauthorizedAccessException">Another process holds the port,
    /// typically a serial monitor left open.</exception>
    public static void Reset(string portName, TimeSpan? hold = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        using var port = new SerialPort(portName, 115200);

        // Both lines low *before* Open(): opening a port tends to pulse them, and
        // a pulse here would reset the board at an uncontrolled moment — which is
        // the bug tools/listen_passive.py exists to avoid when only reading.
        port.DtrEnable = false;
        port.RtsEnable = false;
        port.Open();

        // DTR stays low for the whole sequence. Raising it grounds IO0, and the
        // board would come back in the serial bootloader with no USB at all —
        // trading one dead state for a worse one.
        port.DtrEnable = false;

        port.RtsEnable = true;                       // EN low: held in reset
        Thread.Sleep(hold ?? DefaultHold);
        port.RtsEnable = false;                      // released: boots
    }

    /// <summary>
    /// <see cref="Reset"/>, then waits for the dongle's HID interface to come
    /// back. The dongle returns LOCKED — the session did not survive.
    /// </summary>
    /// <param name="portName">The CH343 bridge. See <see cref="Reset"/>.</param>
    /// <param name="requiredUsbSerial">Wait for this serial specifically. Useful
    /// with more than one dongle attached; note it is a label, not an identity
    /// (<see cref="CryptinDongle.UsbSerial"/>).</param>
    /// <param name="waitMs">How long to keep looking after the line is released.</param>
    /// <returns>true when the HID interface reappeared inside the deadline. false
    /// means the reset happened and the application did not come back — an empty
    /// or invalid app partition looks exactly like this.</returns>
    public static bool ResetAndWaitForHid(string portName,
                                          string? requiredUsbSerial = null,
                                          int waitMs = DefaultWaitMs)
    {
        // Noted before the pulse, because proving the reset happened means
        // proving *this* dongle left the bus. Null when nothing is attached,
        // which is the wedged-chip case: there is no HID to lose, and coming
        // back is the whole signal.
        var target = requiredUsbSerial ?? CryptinDongle.ListSerials().FirstOrDefault();

        Reset(portName);

        // A dongle that never left was never reset. Without this the method
        // reports success for a pulse on an unrelated port — measured on
        // 2026-08-05, where an auto-picked COM1 returned "session did not
        // survive" in one second while the session was still open.
        if (target is not null && !WaitForAbsence(target, VanishTimeoutMs))
            return false;

        return WaitForHid(requiredUsbSerial, waitMs);
    }

    /// <summary>
    /// Reboots a dongle that came up in USB-Serial/JTAG instead of running the
    /// application — the state where it shows no HID at all and looks, to
    /// <see cref="CryptinDongle.ListSerials"/> and to the user, exactly like a
    /// dongle that is not plugged in.
    ///
    /// **This is the recovery a customer can actually perform**, and the reason
    /// it had to exist: a shipped dongle has one socket, so
    /// <see cref="Reset"/> — which drives EN through the CH343's RTS line — is
    /// unavailable to them. On the chip's own USB-Serial/JTAG interface RTS
    /// never reaches EN, so no line-driven reset can work there. This arms the
    /// RTC watchdog instead, which reboots from inside the chip.
    ///
    /// Measured on 2026-08-05: about 13% of plugs land in that state, and it is
    /// a boot race, not a bad cable and not a too-short unplug — both were
    /// tested and ruled out.
    /// </summary>
    /// <param name="portName">The serial port the wedged chip exposes:
    /// <c>COM5</c>, <c>/dev/ttyACM0</c>. This is the chip's own interface, not
    /// the CH343 bridge — which is the opposite of what <see cref="Reset"/>
    /// wants, and the two must not be confused.</param>
    /// <param name="waitMs">How long to wait for the HID interface afterwards.</param>
    /// <returns>true when the dongle came back as a working HID device. false
    /// when nothing on that port answered the ROM loader — the usual answer for
    /// a port that is not a wedged dongle, and not an error.</returns>
    /// <exception cref="IOException">The port could not be opened.</exception>
    public static bool TryRecoverOverNativePort(string portName, int waitMs = DefaultWaitMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);

        if (!EspRomProtocol.TryWatchdogReboot(portName)) return false;

        return WaitForHid(waitMs: waitMs);
    }

    /// <summary>
    /// Every port a wedged dongle might be sitting on. On Linux the chip's
    /// USB-Serial/JTAG appears as CDC-ACM, so <c>ttyACM*</c> is where to look;
    /// on Windows there is no vendor information on a serial port at all, so
    /// this is everything and the caller has to try them.
    ///
    /// Deliberately **not** filtered down to one guess: pointing a reboot at
    /// the wrong port is harmless here — a port that is not an ESP32 ROM simply
    /// never answers SYNC — but silently picking one and reporting on it as if
    /// it were the dongle is not.
    /// </summary>
    public static IReadOnlyList<string> ListRecoveryCandidates()
    {
        if (!OperatingSystem.IsLinux()) return ListAllPorts();

        // The bridge, if present, is not a candidate: it is the other route.
        var bridges = new HashSet<string>(ListBridgePorts(), StringComparer.Ordinal);
        return [.. ListAllPorts().Where(p => !bridges.Contains(p))];
    }

    /// <summary>
    /// Waits for <paramref name="usbSerial"/> to leave the bus, which is what
    /// makes "it was reset" an observation rather than an assumption.
    /// </summary>
    /// <returns>false when it never went away, i.e. nothing was reset.</returns>
    public static bool WaitForAbsence(string usbSerial, int timeoutMs = VanishTimeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        do
        {
            if (!CryptinDongle.ListSerials().Any(
                    s => string.Equals(s, usbSerial, StringComparison.OrdinalIgnoreCase)))
                return true;

            Thread.Sleep(100);
        } while (Environment.TickCount64 < deadline);

        return false;
    }

    /// <summary>
    /// Waits for a dongle's HID interface to appear. Split out from
    /// <see cref="ResetAndWaitForHid"/> so a caller can tell the user that the
    /// long wait has started only once the reset has actually happened —
    /// announcing it before <see cref="Reset"/> claims an action that a bad port
    /// name means never took place.
    /// </summary>
    /// <param name="requiredUsbSerial">Wait for this serial specifically, or any
    /// dongle when null.</param>
    /// <param name="waitMs">See <see cref="DefaultWaitMs"/> before shortening it.</param>
    public static bool WaitForHid(string? requiredUsbSerial = null, int waitMs = DefaultWaitMs)
    {
        var deadline = Environment.TickCount64 + waitMs;
        do
        {
            foreach (var serial in CryptinDongle.ListSerials())
            {
                if (requiredUsbSerial is null ||
                    string.Equals(serial, requiredUsbSerial, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            Thread.Sleep(250);
        } while (Environment.TickCount64 < deadline);

        return false;
    }
}
