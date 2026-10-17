// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Buffers.Binary;
using System.IO.Ports;

namespace CryptinSDK.Dongle;

/// <summary>
/// The smallest slice of the ESP32 ROM loader protocol that does one job:
/// reboot a chip that came up in USB-Serial/JTAG instead of running the
/// application.
///
/// **This is not an esptool.** It cannot flash, read, or erase anything, and it
/// must not grow those abilities: the whole reason it is safe to point at a
/// customer's dongle is that the only thing it can do is write four registers
/// that start a watchdog. Anything more would put a flash-capable path in a
/// consumer application.
///
/// Why this exists at all: a shipped dongle has one socket. When the
/// application does not take the USB PHY at boot — measured at ~13% of plugs on
/// 2026-08-05 — that socket presents the chip's own USB-Serial/JTAG, where the
/// RTS line does **not** reach EN. Every reset we had went through RTS, so none
/// of them could work there. The RTC watchdog resets from inside the chip and
/// needs no control line, which is why it does.
/// </summary>
internal static class EspRomProtocol
{
    // Framing. SLIP, RFC 1055: 0xC0 delimits, 0xDB escapes.
    private const byte SlipEnd = 0xC0;
    private const byte SlipEsc = 0xDB;
    private const byte SlipEscEnd = 0xDC;
    private const byte SlipEscEsc = 0xDD;

    private const byte CmdSync = 0x08;
    private const byte CmdWriteReg = 0x09;

    // ESP32-S3 RTC_CNTL. Values transcribed from esptool's esp32s3.py target;
    // they are chip constants, not choices.
    private const uint RtcCntlBase = 0x6000_8000;
    private const uint WdtConfig0 = RtcCntlBase + 0x0098;
    private const uint WdtConfig1 = RtcCntlBase + 0x009C;
    private const uint WdtWProtect = RtcCntlBase + 0x00B0;
    private const uint WdtWriteKey = 0x50D8_3AA1;

    /// <summary>Enable, stage 5 (system reset), timeout in stage 0, prescaler 2 —
    /// the same word esptool writes: <c>(1&lt;&lt;31)|(5&lt;&lt;28)|(1&lt;&lt;8)|2</c>.</summary>
    private const uint WdtEnableSystemReset = (1u << 31) | (5u << 28) | (1u << 8) | 2u;

    /// <summary>Ticks before the watchdog fires. Short: the point is to reboot now.</summary>
    private const uint WdtTimeoutTicks = 2000;

    /// <summary>
    /// Talks to the ROM loader on <paramref name="portName"/> and arms the RTC
    /// watchdog, which reboots the chip a few milliseconds later.
    /// </summary>
    /// <returns>false when the ROM never answered SYNC — which is the normal
    /// answer on a port that is not a wedged ESP32, and must not be treated as
    /// a failed reset.</returns>
    /// <exception cref="IOException">The port could not be opened, or failed
    /// before the watchdog was armed. Once it is armed this method does not
    /// throw — see below.</exception>
    /// <remarks>
    /// ⚠️ <b>After the arming write, a port that dies under us is success, not
    /// failure.</b> Measured on Linux 2026-08-06: the chip reboots a few
    /// milliseconds after <c>WdtConfig0</c> is written, its CDC-ACM node
    /// disappears in the same instant, and the trailing re-lock write — or the
    /// port's own <c>Dispose</c> — threw <see cref="ObjectDisposedException"/>.
    /// Nothing caught it, so <c>cryptin recover</c> exited 11 reporting a dead
    /// dongle it had just brought back to life. On the one recovery path a
    /// customer with a single-socket dongle actually has.
    ///
    /// Swallowing those cannot manufacture a false success, and that is what
    /// makes it safe: this method never proves the reboot happened. The caller
    /// does, by watching the HID interface come back
    /// (<see cref="DongleHardReset.TryRecoverOverNativePort"/>). Here the
    /// disappearing port is evidence *for* the reset, which is the same rule
    /// the reset path already lives by — the proof is the disappearance, not
    /// the presence of what was expected afterwards.
    ///
    /// <para><b>Scope, honestly.</b> The absorbing applies on every platform,
    /// not only where it was measured, so a genuine post-arming
    /// <see cref="IOException"/> on Windows is now swallowed where it used to
    /// travel — a real change of behaviour that nobody has re-measured there.
    /// It cannot turn into a false success: the caller's only source of truth
    /// is <c>WaitForHid</c>, so a chip that did not reboot yields no HID, the
    /// caller returns false, and the command reports a failure. What is lost on
    /// Windows is the *diagnostic*, not the verdict — an exception that would
    /// once have named the fault now shows up as "no dongle answered". Verified
    /// on Linux, both branches, twice each (2026-08-06). On Windows only the
    /// unarmed branch is verified; the armed one needs a chip wedged on
    /// Windows, which nobody has arranged.</para>
    /// </remarks>
    public static bool TryWatchdogReboot(string portName, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        var port = new SerialPort(portName, 115200)
        {
            ReadTimeout = 300,
            WriteTimeout = 1000,
        };

        // Set the moment the reboot becomes inevitable. Before it, an exception
        // is a real failure and belongs to the caller; after it, the chip is
        // going to restart whatever happens to this handle.
        var armed = false;

        try
        {
            // Both control lines low before and after opening. On USB-Serial/JTAG
            // these are not modem lines: the ROM reads them as boot-mode controls,
            // so an unattended pulse from opening the port can land the chip
            // somewhere other than where we found it.
            port.DtrEnable = false;
            port.RtsEnable = false;
            port.Open();
            port.DtrEnable = false;
            port.RtsEnable = false;
            port.DiscardInBuffer();

            if (!Sync(port, deadline)) return false;

            // Order matters and comes from the chip's documentation: the protect
            // register has to be unlocked with the magic key first, and locked
            // again afterwards, or the config writes are ignored.
            WriteReg(port, WdtWProtect, WdtWriteKey);
            WriteReg(port, WdtConfig1, WdtTimeoutTicks);
            WriteReg(port, WdtConfig0, WdtEnableSystemReset);
            armed = true;

            // Best effort, and only that. The chip restores this register on
            // reset anyway, so failing to re-lock a chip that is already
            // rebooting costs nothing — while reporting it as a failed recovery
            // costs the user their dongle.
            try { WriteReg(port, WdtWProtect, 0); }
            catch (Exception e) when (PortVanished(e)) { }

            return true;
        }
        catch (Exception e) when (armed && PortVanished(e))
        {
            return true;
        }
        finally
        {
            // Dispose of a handle whose device node is gone throws on Linux, and
            // a throwing finally would replace the answer with an error no
            // matter what happened above.
            try { port.Dispose(); }
            catch (Exception e) when (PortVanished(e)) { }
        }
    }

    /// <summary>
    /// Did this exception come from the device node going away underneath us?
    ///
    /// Listed explicitly rather than caught as one broad type: the point is to
    /// absorb a vanishing port, not to make this method silent about genuine
    /// protocol faults. <see cref="ObjectDisposedException"/> is the one
    /// actually measured on Linux; the others are the same event reaching us
    /// through a different layer, and a driver tearing a node down is not
    /// obliged to be consistent about which.
    /// </summary>
    private static bool PortVanished(Exception e)
        => e is ObjectDisposedException
             or IOException
             or UnauthorizedAccessException
             or InvalidOperationException
             or TimeoutException;

    /// <summary>
    /// SYNC, retried: the ROM discards the first frames after a port opens, so
    /// a single attempt says nothing about whether anything is listening.
    /// </summary>
    private static bool Sync(SerialPort port, DateTime deadline)
    {
        var payload = new byte[36];
        payload[0] = 0x07; payload[1] = 0x07; payload[2] = 0x12; payload[3] = 0x20;
        payload.AsSpan(4).Fill(0x55);

        while (DateTime.UtcNow < deadline)
        {
            port.DiscardInBuffer();
            Send(port, CmdSync, payload);

            if (ReadReply(port, CmdSync, out _)) return true;
        }

        return false;
    }

    private static void WriteReg(SerialPort port, uint address, uint value)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), address);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), value);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0xFFFF_FFFF);  // mask: all bits
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), 0);           // no delay

        Send(port, CmdWriteReg, body);

        // The reply is read but not insisted upon. The last write locks the
        // protect register and arms a watchdog that is already counting: the
        // chip may reboot mid-answer, and a missing reply there means success,
        // not failure.
        ReadReply(port, CmdWriteReg, out _);
    }

    /// <summary>Header is <c>&lt;BBHI&gt;</c>: direction 0x00, opcode, payload
    /// length, checksum. The checksum field is only meaningful for the data
    /// commands, and zero for these two.</summary>
    private static void Send(SerialPort port, byte opcode, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[8 + payload.Length];
        packet[0] = 0x00;
        packet[1] = opcode;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0);
        payload.CopyTo(packet.AsSpan(8));

        var framed = new List<byte>(packet.Length + 8) { SlipEnd };
        foreach (var b in packet)
        {
            switch (b)
            {
                case SlipEnd: framed.Add(SlipEsc); framed.Add(SlipEscEnd); break;
                case SlipEsc: framed.Add(SlipEsc); framed.Add(SlipEscEsc); break;
                default: framed.Add(b); break;
            }
        }
        framed.Add(SlipEnd);

        var bytes = framed.ToArray();
        port.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Reads SLIP frames until one answers <paramref name="expectedOpcode"/>.
    /// Frames for other opcodes are dropped rather than treated as errors: the
    /// ROM emits its own chatter, and a stale frame is not a protocol failure.
    /// </summary>
    private static bool ReadReply(SerialPort port, byte expectedOpcode, out byte[] payload)
    {
        payload = [];

        try
        {
            for (var frames = 0; frames < 8; frames++)
            {
                var frame = ReadFrame(port);
                if (frame is null) return false;

                // direction 0x01, opcode, size, value, then payload.
                if (frame.Length < 8 || frame[0] != 0x01 || frame[1] != expectedOpcode)
                    continue;

                payload = frame[8..];
                return true;
            }
        }
        catch (TimeoutException) { return false; }

        return false;
    }

    private static byte[]? ReadFrame(SerialPort port)
    {
        var buffer = new List<byte>(64);
        var started = false;

        while (true)
        {
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { return null; }
            if (b < 0) return null;

            if (b == SlipEnd)
            {
                // Consecutive delimiters are padding between frames, not an
                // empty frame worth returning.
                if (!started) { started = true; continue; }
                if (buffer.Count == 0) continue;
                return buffer.ToArray();
            }

            if (!started) continue;

            if (b == SlipEsc)
            {
                int esc;
                try { esc = port.ReadByte(); }
                catch (TimeoutException) { return null; }

                buffer.Add(esc switch
                {
                    SlipEscEnd => SlipEnd,
                    SlipEscEsc => SlipEsc,
                    _ => (byte)esc,
                });
                continue;
            }

            buffer.Add((byte)b);
        }
    }
}
