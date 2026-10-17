// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CryptinSDK.Crypto;
using CryptinSDK.Dongle;
using CryptinSDK.Seed;

namespace CryptinCli;

/// <summary>
/// `cryptin` — the command-line client.
///
/// Three rules shape everything here, and they are the difference between a
/// tool a script can use and one it cannot:
///
///   * **stdout carries results, nothing else.** `encrypt` and `decrypt` print
///     one line: the path they produced. So `out=$(cryptin encrypt f)` works.
///     Every message for a human goes to stderr.
///   * **progress is drawn only on a terminal.** Redirected stderr gets no
///     carriage returns, because a log file full of `\r` is worse than no
///     progress at all.
///   * **the PIN never appears in argv.** There is no `--pin` flag and there
///     will not be one: arguments are visible in `ps` to every user on the box
///     and land in shell history. It comes from stdin, prompted without echo
///     when there is a terminal, read as a line when there is a pipe.
///
/// The session lives on the dongle, not in this process — the chip stays
/// unlocked until it loses power or ten idle minutes pass. That is what makes
/// `unlock` worth having as its own command: unlock once, then run as many
/// encrypts as you like from any process.
/// </summary>
internal static class Program
{
    private const int ConnectWaitMs = 3000;

    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Handled rather than fatal: the SDK checks the token between chunks
            // and nothing is written until a file completes, so a Ctrl-C leaves
            // no half-written output. Letting the runtime kill us would too, but
            // noisily and without the exit code a caller can act on.
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return (int)await RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Err("cancelled");
            return (int)ExitCode.Cancelled;
        }
        catch (Exception e)
        {
            return (int)Report(e);
        }
    }

    private static async Task<ExitCode> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage(Console.Out);
            return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        if (args[0] is "--version")
        {
            Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            return ExitCode.Ok;
        }

        return args[0] switch
        {
            "status" => await StatusAsync(),
            "unlock" => Unlock(),
            "reset" => Reset(args),
            "recover" => Recover(args),
            "encrypt" => Transform(args, encrypting: true, ct),
            "decrypt" => Transform(args, encrypting: false, ct),
            "open-offline" => OpenOffline(args),
            _ => Fail($"unknown command '{args[0]}'", ExitCode.Usage, withUsage: true),
        };
    }

    // --- commands ---------------------------------------------------------

    /// <summary>
    /// What is attached, whether it is unlocked, and where the licence stands.
    ///
    /// Deliberately does not unlock anything. `status` is what a script runs to
    /// decide what to do next, and a status command that pops a PIN prompt is
    /// one nobody can put in a cron job.
    /// </summary>
    private static async Task<ExitCode> StatusAsync()
    {
        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0)
        {
            Console.Out.WriteLine("dongle: none");
            Err("no Crypt-in dongle found — plug one in");
            return ExitCode.NoDongle;
        }

        if (attached.Count > 1)
            Err($"{attached.Count} dongles attached, using {attached[0]}");

        using var dongle = new CryptinDongle();
        if (!dongle.Connect(attached[0], ConnectWaitMs))
        {
            Console.Out.WriteLine($"dongle: {attached[0]}");
            Console.Out.WriteLine("state:  unreachable");
            Err("the dongle is visible but will not open — unplug it and plug it back in");
            return ExitCode.DongleError;
        }

        Console.Out.WriteLine($"dongle: {dongle.UsbSerial}");

        // PING is answered before the PIN gate, so it proves the transport
        // without saying anything about the session. GET_PUBKEY is gated, so
        // whether it answers is exactly the "am I unlocked" question.
        if (!dongle.Ping())
        {
            Console.Out.WriteLine("state:  unreachable");
            return ExitCode.DongleError;
        }

        var unlocked = IsUnlocked(dongle);
        Console.Out.WriteLine($"state:  {(unlocked ? "unlocked" : "locked")}");

        if (!unlocked)
        {
            Err("locked — run 'cryptin unlock'");
            return ExitCode.PinRequired;
        }

        Console.Out.WriteLine($"license: {await DescribeLicenceAsync(dongle)}");
        return ExitCode.Ok;
    }

    /// <summary>
    /// Verifies the PIN. The unlocked session then outlives this process,
    /// because it lives on the chip.
    /// </summary>
    private static ExitCode Unlock()
    {
        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0) return Fail("no Crypt-in dongle found", ExitCode.NoDongle);

        using var dongle = new CryptinDongle();
        if (!dongle.Connect(attached[0], ConnectWaitMs))
            return Fail("the dongle will not open — unplug it and plug it back in", ExitCode.DongleError);

        if (IsUnlocked(dongle))
        {
            Err("already unlocked");
            return ExitCode.Ok;
        }

        var pin = Pin.Read();
        try
        {
            var result = dongle.VerifyPin(pin.AsSpan());
            switch (result)
            {
                case PinResult.Ok:
                    Err("unlocked");
                    return ExitCode.Ok;

                case PinResult.Blocked:
                    // No attempt count, here or anywhere: the counter lives in
                    // NVS on the dongle and survives restarts, so this process
                    // cannot know how many are left and must not pretend to.
                    return Fail("dongle locked out — recovery needs your 24-word seed "
                                + "and the UART cable", ExitCode.DongleBlocked);

                case PinResult.NotReady:
                    return Fail("the dongle has not loaded its keys yet — try again in a moment",
                                ExitCode.DongleError);

                default:
                    return Fail("wrong PIN", ExitCode.PinRequired);
            }
        }
        finally
        {
            Array.Clear(pin, 0, pin.Length);
        }
    }

    private static ExitCode Transform(string[] args, bool encrypting, CancellationToken ct)
    {
        if (args.Length != 2)
            return Fail($"usage: cryptin {(encrypting ? "encrypt" : "decrypt")} <path>",
                        ExitCode.Usage);

        var path = args[1];
        if (!File.Exists(path) && !Directory.Exists(path))
            return Fail($"no such file or directory: {path}", ExitCode.Usage);

        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0) return Fail("no Crypt-in dongle found", ExitCode.NoDongle);

        using var dongle = new CryptinDongle();
        if (!dongle.Connect(attached[0], ConnectWaitMs))
            return Fail("the dongle will not open — unplug it and plug it back in", ExitCode.DongleError);

        if (!IsUnlocked(dongle))
            return Fail("dongle is locked — run 'cryptin unlock' first", ExitCode.PinRequired);

        var label = $"{(encrypting ? "encrypting" : "decrypting")} {Path.GetFileName(path)}";
        using var progress = new StderrProgress(label);

        var result = encrypting
            ? FileOps.Encrypt(path, dongle, progress, ct)
            : FileOps.Decrypt(path, dongle, progress, ct);

        progress.Done();
        Console.Out.WriteLine(result);          // the only thing on stdout
        return ExitCode.Ok;
    }

    /// <summary>
    /// Opens a .crin or .cryptin with the 24 recovery words and no dongle: the
    /// way back when the dongle is lost or dead. The words come from stdin,
    /// never from the command line, where history and the process list keep
    /// them. Numbers and punctuation are ignored, so a transcript like
    /// "1. sudden 2. blood" works as typed.
    /// </summary>
    private static ExitCode OpenOffline(string[] args)
    {
        if (args.Length != 2)
            return Fail("usage: cryptin open-offline <file>   (the 24 words on stdin)", ExitCode.Usage);

        var path = args[1];
        if (!File.Exists(path))
            return Fail($"no such file: {path}", ExitCode.Usage);

        if (!Console.IsInputRedirected)
            Err("type the 24 recovery words, then Enter and Ctrl-Z (Windows) or Ctrl-D:");

        var words = Regex.Matches(Console.In.ReadToEnd().ToLowerInvariant(), "[a-z]+")
                         .Select(m => m.Value).ToList();

        byte[] entropy;
        try
        {
            entropy = Bip39.MnemonicToEntropy(words);
        }
        catch (ArgumentException e)
        {
            return Fail($"recovery words not accepted: {e.Message.Split(" (Parameter")[0]}", ExitCode.Usage);
        }

        var fileSecret = SeedDerivation.FileSecret(entropy);
        CryptographicOperations.ZeroMemory(entropy);
        try
        {
            Console.Out.WriteLine(FileOps.DecryptOffline(path, fileSecret));   // the only thing on stdout
            return ExitCode.Ok;
        }
        catch (CorruptedCryptinFileException)
        {
            // Without the dongle there is no K_identity to compare (step 7), so
            // "other dongle" and "damaged" are the same failure here.
            return Fail("these words do not open this file — it belongs to another dongle, "
                        + "or it is damaged", ExitCode.CorruptFile);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileSecret);
        }
    }

    // --- shared -----------------------------------------------------------

    /// <summary>
    /// Asks a gated command and reads the answer as the session state.
    ///
    /// There is no "are you unlocked" opcode, and inventing one client-side —
    /// a cached bool — is what let the desktop app sail past an auto-lock. The
    /// dongle is the only thing that knows, so it gets asked.
    /// </summary>
    private static bool IsUnlocked(CryptinDongle dongle)
    {
        try
        {
            dongle.GetIdentityPubKey();
            return true;
        }
        catch (PinRequiredException)
        {
            return false;
        }
    }

    private static async Task<string> DescribeLicenceAsync(CryptinDongle dongle)
    {
        try
        {
            var status = await dongle.GetLicenseStatusAsync();

            // Every dongle in existence before licence enforcement answers this
            // way. Reporting "no licence" for firmware that has no such concept
            // would be alarming and wrong.
            if (status.Error == DongleLicenseError.Unsupported) return "not reported by this firmware";
            if (status.IsValid) return "valid";

            return status.Error switch
            {
                DongleLicenseError.Expired => "expired (existing files still decrypt)",
                DongleLicenseError.InvalidSignature => "signature not recognised — contact support",
                _ => "none (encryption needs one; decryption never does)",
            };
        }
        catch (CryptinDongleException)
        {
            return "unknown";
        }
    }

    // --- output -----------------------------------------------------------

    /// <summary>
    /// Power-cycles the dongle over the UART bridge. The one command here that
    /// does not speak the protocol: it drives a hardware line, so it works on a
    /// chip whose application is not running and which therefore has no HID at
    /// all. Needs the UART cable — see <see cref="DongleHardReset"/>.
    ///
    /// stdout stays empty. A reset has no value for a script to consume; the
    /// exit code is the whole answer, so `cryptin reset &amp;&amp; cryptin unlock`
    /// reads correctly.
    /// </summary>
    private static ExitCode Reset(string[] args)
    {
        string? port = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--port" or "-p")
            {
                if (i + 1 >= args.Length)
                    return Fail("--port needs a value, e.g. --port COM3", ExitCode.Usage);
                port = args[++i];
            }
            else
            {
                return Fail($"unknown option '{args[i]}' for reset", ExitCode.Usage);
            }
        }

        if (port is null)
        {
            // Only vendor-identified ports are eligible. On Windows that list is
            // always empty, because SerialPort exposes no VID — and picking the
            // machine's only serial port is not identification. Measured on
            // 2026-08-05: with the UART cable out, the sole survivor was COM1,
            // the motherboard's own port, and pulsing RTS there reported a
            // successful reset in one second while the session stayed open.
            var bridges = DongleHardReset.ListBridgePorts();
            if (bridges.Count != 1)
            {
                var all = DongleHardReset.ListAllPorts();
                return Fail(all.Count == 0
                                ? "found no serial port to offer — if the UART cable is connected, "
                                  + "name it anyway with --port (enumeration misses some nodes)"
                                : $"name the port, it cannot be identified here: --port {string.Join(" | --port ", all)}",
                            ExitCode.Usage);
            }

            port = bridges[0];
            Err($"using {port}");
        }

        // Noted before the pulse: proving the reset happened means proving this
        // dongle left the bus. Null when none is attached, which is the wedged
        // chip we are here to recover.
        var target = CryptinDongle.ListSerials().FirstOrDefault();

        try
        {
            DongleHardReset.Reset(port);

            // Announced only now, and with the duration: the reset has actually
            // happened at this point, and the wait looks like a hang otherwise —
            // the chip boots in about two seconds, the host takes roughly thirty
            // to notice.
            Err($"reset on {port} — waiting for the dongle to come back "
                + $"(up to {DongleHardReset.DefaultWaitMs / 1000}s, this is normal)…");

            // Never inferred from "a dongle is present afterwards": one that
            // never left satisfies that trivially, and reporting a dead session
            // that is still open is worse than reporting nothing.
            if (target is not null && !DongleHardReset.WaitForAbsence(target))
                return Fail($"{target} never left the bus — {port} does not reach EN, "
                            + "so nothing was reset and any open session is still open",
                            ExitCode.DongleError);

            if (DongleHardReset.WaitForHid())
            {
                Err("dongle is back, and locked — the session did not survive");
                return ExitCode.Ok;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Fail($"{port} is held by another process — close any serial monitor",
                        ExitCode.IoError);
        }
        catch (IOException e)
        {
            return Fail($"cannot drive {port}: {e.Message}", ExitCode.IoError);
        }

        return Fail("reset done, but no HID came back — the chip may have no valid app "
                    + "to boot, or this port does not reach EN",
                    ExitCode.NoDongle);
    }

    /// <summary>
    /// Reboots a dongle that came up in USB-Serial/JTAG instead of running its
    /// application — the state where `status` says "no dongle" although the
    /// thing is plugged in.
    ///
    /// Separate from `reset` on purpose, because it is a different situation
    /// down to the port: `reset` drives EN through the UART bridge, which a
    /// shipped dongle does not have, while this talks to the chip's own
    /// interface. Merging them would mean guessing which of the two a user
    /// meant at the moment they are least able to tell.
    /// </summary>
    private static ExitCode Recover(string[] args)
    {
        string? port = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--port" or "-p")
            {
                if (i + 1 >= args.Length)
                    return Fail("--port needs a value, e.g. --port COM5", ExitCode.Usage);
                port = args[++i];
            }
            else
            {
                return Fail($"unknown option '{args[i]}' for recover", ExitCode.Usage);
            }
        }

        if (CryptinDongle.ListSerials().Count > 0)
            return Fail("a dongle is already answering — nothing to recover", ExitCode.Usage);

        // Trying every port is safe and is why no --port is needed: anything
        // that is not an ESP32 ROM never answers SYNC, so it is skipped rather
        // than disturbed. Guessing one and reporting on it would be the unsafe
        // half, not the trying.
        var candidates = port is not null ? [port] : DongleHardReset.ListRecoveryCandidates();
        if (candidates.Count == 0)
            return Fail("no serial port to try — is the dongle plugged in?", ExitCode.NoDongle);

        Err($"looking for a wedged dongle on: {string.Join(", ", candidates)}");

        foreach (var candidate in candidates)
        {
            try
            {
                Err($"trying {candidate}…");
                if (DongleHardReset.TryRecoverOverNativePort(candidate))
                {
                    Err($"recovered on {candidate} — the dongle is back, and locked");
                    return ExitCode.Ok;
                }
            }
            catch (UnauthorizedAccessException)
            {
                Err($"{candidate}: held by another process, skipped");
            }
            catch (IOException e)
            {
                Err($"{candidate}: {e.Message}, skipped");
            }
        }

        return Fail("no dongle answered on any port — if it is plugged in, unplug and "
                    + "plug it back, which also clears this state most of the time",
                    ExitCode.NoDongle);
    }

    private static void Err(string message) => Console.Error.WriteLine($"cryptin: {message}");

    private static ExitCode Fail(string message, ExitCode code, bool withUsage = false)
    {
        Err(message);
        if (withUsage) Usage(Console.Error);
        return code;
    }

    /// <summary>
    /// Maps an SDK exception onto the exit code that tells the caller what to do
    /// about it. The message is for a person; the code is the contract.
    /// </summary>
    private static ExitCode Report(Exception e)
    {
        switch (e)
        {
            case PinRequiredException:
                return Fail("dongle is locked — run 'cryptin unlock' first", ExitCode.PinRequired);

            case NoActiveLicenseException:
                return Fail("no active license — encryption needs one, decryption never does",
                            ExitCode.NoLicense);

            case WrongDongleException:
                return Fail("this file was encrypted with a different dongle — it is not damaged",
                            ExitCode.WrongDongle);

            case CorruptedCryptinFileException:
                return Fail("this file is damaged or was tampered with — right dongle, bad data",
                            ExitCode.CorruptFile);

            case CryptinFileTooLargeException x:
                return Fail($"file is over the {x.Maximum / 1_000_000.0:F0} MB per-transfer limit "
                            + "— this limit will rise in a future update", ExitCode.FileTooLarge);

            case InvalidCryptinFileException x:
                return Fail($"not a readable Crypt-in file: {x.Message}", ExitCode.NotCryptinFile);

            case CryptinTimeoutException:
                return Fail("the dongle did not respond in time — unplug it and plug it back in",
                            ExitCode.DongleError);

            case CryptinDongleException x:
                return Fail(x.Message, ExitCode.DongleError);

            case UnauthorizedAccessException:
            case IOException:
                return Fail(e.Message, ExitCode.IoError);

            default:
                return Fail($"{e.GetType().Name}: {e.Message}", ExitCode.DongleError);
        }
    }

    private static void Usage(TextWriter to) => to.WriteLine(
        """
        cryptin — Crypt-in hardware dongle, command line client

        USAGE
          cryptin status              what is attached, and whether it is unlocked
          cryptin unlock              verify the PIN (read from stdin)
          cryptin encrypt <path>      encrypt a file or folder to <path>.crin
          cryptin decrypt <path>      decrypt a .crin or .cryptin back
          cryptin open-offline <file> decrypt with the 24 recovery words, no dongle
                                      (words read from stdin, never the command line)
          cryptin reset [--port P]    power-cycle the dongle over the UART cable
          cryptin recover [--port P]  revive a dongle that boots without its app

        `reset` and `recover` are the two commands that do not go through the
        HID protocol, and they are not interchangeable:

          reset    holds EN low through the UART bridge. Development boards
                   only — a shipped dongle has one socket and no bridge.
          recover  talks to the chip's own USB-Serial/JTAG and reboots it with
                   the RTC watchdog. This is the one for `status` saying "no
                   dongle" while the thing is plainly plugged in. Unplugging
                   and plugging it back in usually clears it too — try that
                   first.

                   The dongle takes a moment to hand USB over from its ROM to
                   its application, and anything that grabs the device during
                   that moment leaves it looking like a serial port instead of
                   a Crypt-in dongle. Virtual machine software is the usual
                   culprit: if VirtualBox, VMware or WSL asks whether to give
                   a new USB device to the guest, and the name it offers is a
                   JTAG or serial debug unit, that is this. Tell it to leave
                   Crypt-in on the host. A heavily loaded machine can stretch
                   the same moment on its own.

        Both end the unlocked session, because it lives in the chip's RAM.

        The unlocked session lives on the dongle, so `unlock` once and every
        later command in any process finds it open — until the dongle loses
        power or sits idle for ten minutes.

        The PIN is never an argument. With a terminal you are prompted without
        echo; otherwise it is read from stdin:

          cryptin unlock < pin.txt
          pass show cryptin/pin | cryptin unlock

        stdout carries only results, so this is safe:

          out=$(cryptin encrypt secret.pdf) && echo "wrote $out"

        EXIT CODES
          0  ok                 7  wrong dongle for this file
          2  usage              8  file damaged or tampered with
          3  no dongle          9  not a Crypt-in file
          4  PIN required      10  file over the transfer limit
          5  dongle locked out 11  dongle error
          6  no license        12  filesystem error
                              130  interrupted

        https://cryptin.lake8.dev
        """);
}
