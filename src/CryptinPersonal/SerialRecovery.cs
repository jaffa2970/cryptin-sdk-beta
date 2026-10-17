using System.IO.Ports;
using System.Text;

namespace CryptinPersonal;

/// <summary>
/// Drives the firmware's <c>recover</c> console dialogue, so the wizard does it
/// instead of telling the user to.
///
/// Until 2026-09-02 step 3 of <see cref="BackupWizard"/> flashed the chip and
/// then printed instructions: open a serial terminal at 115200, type
/// <c>recover</c>, paste the 24 words. The comment beside it said the flash was
/// automatable and the recovery was not. It is — this class is a port of
/// <c>tools/recover_over_serial.py</c>, which did the whole exchange in thirty
/// lines and worked on the first attempt.
///
/// What made it worth changing is not tidiness. A clean Windows 11 has no
/// serial terminal, and the beta package ships none: measured on a fresh VM,
/// a beta tester could not make a backup at all. The instructions sent them to
/// a program they did not have.
///
/// The dialogue, from <c>firmware/src/main.cpp</c> <c>recoveryFlow()</c>:
/// <code>
///   ->  recover
///   &lt;-  "Recovery mode: paste your 24-word mnemonic and press Enter."
///   ->  &lt;24 words&gt;
///   &lt;-  "Checksum OK. ..." + "Type YES to confirm:"   or "ERROR: invalid mnemonic"
///   ->  YES
///   &lt;-  "Recovery OK." + "Your PIN is unchanged." + the public key
/// </code>
/// </summary>
internal static class SerialRecovery
{
    private const int Baud = 115200;

    private const string PromptMnemonic = "paste your 24-word mnemonic";
    private const string PromptConfirm = "Type YES to confirm";
    private const string Done = "Recovery OK.";

    /// <summary>What <c>completeProvisioning()</c> prints, on either of the two
    /// routes that reach it: firmware built after 2026-09-03 acknowledging the
    /// recovery by itself, or any firmware answering the <c>ok</c> the
    /// first-boot banner has always taken.</summary>
    private const string Acknowledged = "Acknowledged (";
    private const string BadMnemonic = "ERROR: invalid mnemonic";
    private const string Cancelled = "Recovery cancelled";

    /// <summary>What the firmware prints when setup is over and the console is
    /// listening. Waited for rather than guessed at with a delay: esptool ends
    /// with a reset, and for a moment after it nobody is reading. On the bench
    /// the marker arrived 0.4 s after esptool let go — short, but a slower
    /// machine or a VM is exactly where a hard-coded delay would be wrong.</summary>
    private const string Ready = "Type 'recover'";

    internal enum Outcome
    {
        Ok,
        PortUnavailable,
        NoConsole,
        NoAnswer,
        MnemonicRejected,
        Refused,
    }

    /// <param name="HandoverClosed">True once the chip has been told the phrase
    /// is written down, which is what shuts <c>CMD_GET_PROVISIONING</c> and
    /// stops the banner. False is not a failed recovery — it is a chip that
    /// still offers the seed it was just given, and the caller has to say so.
    /// </param>
    internal sealed record Result(
        Outcome Outcome, string Detail, string PublicKeyHex = "", bool HandoverClosed = false);

    /// <param name="words">The 24 words, already validated by <c>Bip39.Validate</c>.</param>
    internal static async Task<Result> RunAsync(
        string portName, string[] words, bool justFlashed, CancellationToken token = default)
        => await Task.Run(() => Run(portName, words, justFlashed, token), token);

    private static Result Run(string portName, string[] words, bool justFlashed, CancellationToken token)
    {
        // Retried because right after esptool exits the port can still be its
        // own for a fraction of a second, and that first failure means nothing.
        SerialPort? port = null;
        for (var attempt = 0; attempt < 20 && port is null; attempt++)
        {
            try
            {
                var candidate = new SerialPort(portName, Baud)
                {
                    ReadTimeout = 200,
                    WriteTimeout = 2000,
                    NewLine = "\n",
                    // Before Open(), and this is the whole trick: on Windows
                    // opening a port pulses DTR/RTS, which resets the dongle.
                    // A reset dongle restarts down the "existing keypair"
                    // branch and the state we were talking to is gone.
                    DtrEnable = false,
                    RtsEnable = false,
                };
                candidate.Open();
                port = candidate;
            }
            catch (Exception) when (attempt < 19)
            {
                Thread.Sleep(250);
            }
            catch (Exception e)
            {
                return new Result(Outcome.PortUnavailable, e.Message);
            }
        }
        if (port is null) return new Result(Outcome.PortUnavailable, $"{portName} never opened");

        using (port)
        {
            if (justFlashed)
            {
                // Read blind. The first-boot banner passes through this window
                // and it carries the seed and the PIN in clear: nothing from
                // here may reach a log, a message box or a variable that
                // outlives the call.
                if (WaitFor(port, [Ready], TimeSpan.FromSeconds(30), token) is null)
                    return new Result(Outcome.NoConsole,
                        "the chip never announced its console after the flash");
            }
            else
            {
                port.DiscardInBuffer();
                Thread.Sleep(300);
                Drain(port);
            }

            port.Write("recover\n");

            // Blind as well: if the chip has not entered recoveryFlow() yet, a
            // banner can still arrive right here.
            if (WaitFor(port, [PromptMnemonic], TimeSpan.FromSeconds(20), token) is null)
                return new Result(Outcome.NoAnswer, "no reply to the recover command");

            port.Write(string.Join(' ', words) + "\n");

            var afterWords = WaitFor(port, [PromptConfirm, BadMnemonic], TimeSpan.FromSeconds(20), token);
            if (afterWords is null)
                return new Result(Outcome.NoAnswer, "no reply after the recovery phrase");
            if (afterWords == BadMnemonic)
                return new Result(Outcome.MnemonicRejected,
                    "the dongle refused the phrase: an unknown word, or a wrong checksum");

            port.Write("YES\n");

            var tail = new StringBuilder();
            var finished = WaitFor(port, [Done, Cancelled], TimeSpan.FromSeconds(20), token, tail);
            if (finished != Done)
                return new Result(Outcome.Refused, "the dongle did not complete the recovery");

            // The public key follows, and unlike everything else on this port it
            // is public: it is the identity the seed produces, and step 4
            // compares it with the main dongle's.
            // A marker that cannot arrive, on purpose: this is a two-second
            // window held open so the key has time to land, not a wait for
            // anything. (It was a raw NUL byte in the source until 2026-09-03,
            // which compiled and worked and made the file binary to grep.)
            WaitFor(port, ["\0"], TimeSpan.FromSeconds(2), token, tail);
            var publicKeyHex = ExtractPublicKey(tail.ToString());

            // Close the handover before letting go of the port. From here the
            // chip carries the MAIN dongle's seed, and an unacknowledged chip
            // hands that seed to anyone who asks: over HID, where
            // CMD_GET_PROVISIONING is exempt from the PIN gate, and over this
            // very console, which reprints the phrase and the PIN every five
            // seconds until it is answered.
            //
            // Firmware built after 2026-09-03 does this at the end of
            // recoveryFlow() and has already said so in the tail just read.
            // Older firmware -- everything already in a tester's hands -- takes
            // the `ok` the first-boot banner has always accepted, and that is
            // why this is done from here as well as there: it is the half of
            // the fix that reaches chips we cannot reflash.
            //
            // Sending it costs nothing on a chip that is already closed: the
            // command is only read while a handover is pending, and falls
            // through the dispatcher otherwise.
            var closed = tail.ToString().Contains(Acknowledged, StringComparison.Ordinal);
            if (!closed)
            {
                port.Write("OK\n");
                closed = WaitFor(port, [Acknowledged], TimeSpan.FromSeconds(5), token) is not null;
            }

            return new Result(Outcome.Ok, "recovered", publicKeyHex, closed);
        }
    }

    /// <summary>
    /// Waits for any of <paramref name="expected"/> to appear anywhere in what
    /// has arrived, and returns which. A substring search over the whole buffer,
    /// not line parsing: the first-boot banner repeats every five seconds and
    /// can land in the middle of a prompt.
    /// </summary>
    private static string? WaitFor(
        SerialPort port, string[] expected, TimeSpan timeout,
        CancellationToken token, StringBuilder? keep = null)
    {
        var buffer = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            string chunk;
            try { chunk = port.ReadExisting(); }
            catch (TimeoutException) { chunk = ""; }

            if (chunk.Length == 0) { Thread.Sleep(50); continue; }

            buffer.Append(chunk);
            keep?.Append(chunk);
            var seen = buffer.ToString();
            foreach (var want in expected)
                if (seen.Contains(want, StringComparison.Ordinal))
                    return want;
        }
        return null;
    }

    private static void Drain(SerialPort port)
    {
        try { _ = port.ReadExisting(); } catch (TimeoutException) { }
    }

    private static string ExtractPublicKey(string tail)
    {
        const string marker = "Public key (hex):";
        var at = tail.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return "";
        var rest = tail[(at + marker.Length)..].TrimStart();
        var end = rest.IndexOfAny(['\r', '\n']);
        return (end < 0 ? rest : rest[..end]).Trim();
    }
}
