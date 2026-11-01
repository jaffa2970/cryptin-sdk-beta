using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CryptinUpdater;

/// <summary>
/// ═══════════════════════════════════════════════════════════════════════════
///  TEST BUILD. This is NOT the CryptinUpdater that ships.
/// ═══════════════════════════════════════════════════════════════════════════
///
/// It flashes whatever file it is handed, and verifies nothing. No signature
/// check, no anti-rollback, no idea where the image came from. That is
/// acceptable for pointing at a development chip with no eFuses burned, on a
/// bench, and is unacceptable for anything else.
///
/// The production CryptinUpdater.exe, which is proprietary and signed:
///   1. downloads the release from GitHub,
///   2. verifies its SHA-256,
///   3. verifies the Secure Boot v2 signature against the lake8.dev public key
///      compiled into it -- not configurable from outside, or a hostile fork
///      replaces it,
///   4. and only then calls esptool.
/// See Krypt/CRYPTIN_ARCHITETTURA_CS.md §3.
///
/// Why an open one exists at all: the architecture says the public repository
/// carries "everything needed to rebuild an alternative updater". A working
/// reference implementation is that, and it keeps the boundary honest -- anyone
/// can see exactly what the closed binary is expected to do and check that the
/// contract is sufficient.
///
/// ── Protocol ───────────────────────────────────────────────────────────────
/// One JSON object per line on stdout:
///   {"type":"progress","percent":42}
///   {"type":"done","result":"Success"}
///   {"type":"error","message":"..."}
/// `result` is a FlashResult name. The exit code repeats it -- 0 for Success --
/// so a caller that cannot parse still learns whether it worked.
/// </summary>
internal static class Program
{
    /// <summary>
    /// From platformio.ini. NOT `detect`: with detect, esptool rewrites bytes
    /// 2-3 of whatever sits at offset 0 -- the bootloader's flash header -- to
    /// match the chip it found. On an N16R8 that means 16MB against the 8MB this
    /// project builds for, which breaks Secure Boot images outright and is the
    /// condition under which the development board hangs in the bootloader.
    /// `keep` writes the image exactly as built.
    /// </summary>
    private const string FlashSize = "keep";

    private const string Baud = "921600";      // upload_speed in platformio.ini
    private const string Chip = "esp32s3";

    /// <summary>
    /// Where the dongle's keys live, from <c>partitions/cryptin_8mb.csv</c>:
    /// nvs at 0x9000, 20K. Hardcoded rather than read from the image, because
    /// the question is about the chip's current contents and not about the
    /// layout we are bringing.
    /// </summary>
    private const int NvsOffset = 0x9000;
    private const int NvsSize = 0x5000;

    /// <summary>The partition table, same source. Read back from the chip by
    /// <c>update-app</c> and compared with the image's own.</summary>
    private const int PartitionTableOffset = 0x8000;
    private const int PartitionTableSize = 0x1000;

    /// <summary>
    /// Where <c>update-app</c> starts writing: otadata (0xE000), then app0
    /// (0x10000) and whatever the image carries after it. Everything below --
    /// bootloader, partition table, nvs -- stays as it is on the chip. nvs ends
    /// at 0xE000, which is the whole point of the number.
    /// </summary>
    private const int AppRegionOffset = 0xE000;

    /// <summary>
    /// The image this build is meant to write, by content rather than by name.
    ///
    /// **This is what makes a package possible.** <see cref="RejectNonDev"/>
    /// judges the path, and inside a folder a tester downloaded the path is
    /// whatever they unzipped it to -- measured 2026-08-31, when the assembled
    /// package refused its own firmware because `dist/cryptin-beta/firmware/`
    /// contains no "dev". A hash does not care where the file sits, and it is a
    /// stronger statement than the heuristic it replaces: not "this looks like a
    /// dev build" but "this is the exact artefact we tested".
    ///
    /// Kept in sync by scripts/publish-beta.ps1, which refuses to build a
    /// package whose firmware does not match this constant. Same arrangement as
    /// the other mirrors in this repo: duplicated deliberately, and checked.
    /// </summary>
    private const string BundledImageSha256 =
        // SHA-256 of a published artefact, not key material.
        // 2e1bbef (on-chip New identity, CMD_REKEY 0x11/0x12), built 2026-10-31,
        // seen on γ and β; replaces 90d0cd9 (f66a17cd…), archived in dist/firmware-archive.
        "b33bb3976b2a8a5e2a9118938a96dc8f2439c1498204756fac5c9f0b54a25f7a";  // cryptin-secret-ok

    /// <summary>Beside the executable, which is where the package puts it.</summary>
    private static string BundledImagePath =>
        Path.Combine(AppContext.BaseDirectory, "firmware", "firmware.factory.bin");

    private static int Main(string[] args)
    {
        // The verb. It used to be decorative -- the parent always sent
        // first-flash and nothing read it -- so a missing one still means that.
        var verb = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : "first-flash";
        if (verb is not ("first-flash" or "update-app"))
            return Fail($"unknown command '{verb}': expected first-flash or update-app");

        var port = Argument(args, "--port");

        // IUpdater.FirstFlashAsync takes a port and nothing else, because the
        // production updater fetches its own image from the release it verified.
        // This build cannot do that, so it is told which file to write -- by
        // argument, or by environment when the caller is the app, which has no
        // way to pass one and should not grow one.
        // The package needs no argument: the image travels with the executable.
        // --image and the environment variable stay ahead of it, because on the
        // bench the whole point is pointing at a build you just made.
        var image = Argument(args, "--image")
                    ?? Environment.GetEnvironmentVariable("CRYPTIN_TEST_FIRMWARE")
                    ?? (File.Exists(BundledImagePath) ? BundledImagePath : null);

        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.Error.WriteLine(
                "CryptinUpdater (build di test, nessuna verifica di firma)\n\n" +
                "  CryptinUpdater first-flash --port COM5 [--image <firmware.factory.bin>]\n" +
                "  CryptinUpdater update-app  --port COM5 [--image <firmware.factory.bin>]\n\n" +
                "update-app scrive solo da 0xE000 in poi (otadata e applicazione): la nvs con\n" +
                "seme, PIN e K_attestation resta. Solo su un dongle -dev gia' configurato e con\n" +
                "la stessa tabella delle partizioni dell'immagine; altrimenti non scrive nulla.\n\n" +
                "Senza --image usa firmware/firmware.factory.bin accanto all'eseguibile,\n" +
                "che e' dove lo mette il pacchetto, e ne verifica lo SHA-256 fissato.\n\n" +
                "L'immagine dev'essere quella UNITA prodotta dal build, di solito\n" +
                ".pio/build/esp32-s3-n16r8-dev/firmware.factory.bin: contiene bootloader,\n" +
                "tabella delle partizioni e applicazione ai propri offset e si scrive a 0x0.\n" +
                "firmware.bin da solo e' la sola applicazione e a 0x0 non parte.");
            return (int)FlashResult.FlashFailed;
        }

        if (port is null) return Fail("missing --port");
        if (image is null)
            return Fail("no firmware image: firmware/firmware.factory.bin is not next to the " +
                        "updater. Pass --image, or set CRYPTIN_TEST_FIRMWARE. This build " +
                        "downloads nothing by itself.");
        if (!File.Exists(image)) return Fail($"firmware image not found: {image}");

        // The hash first, and the path only if the hash does not answer. A file
        // that matches the pin is the artefact this build ships, wherever it
        // happens to be sitting; a file that does not is judged by the old
        // heuristic, which is still the right one on a bench full of builds.
        if (!MatchesBundledImage(image) && RejectNonDev(image) is { } refusal)
            return Fail(refusal);

        if (Reject(image) is { } problem) return Fail(problem);

        if (verb == "update-app") return UpdateApp(port, image);

        // Last, because it is the only check that costs a round trip to the chip,
        // and because there is no point asking the hardware about an image we
        // have already refused.
        if (!args.Contains("--force") && NotBlank(port) is { } occupied)
            return Refuse(occupied);

        return Flash(port, image, 0);
    }

    /// <summary>
    /// Updates the firmware of a dongle that is already set up, keeping it the
    /// same dongle (decided 2026-10-31).
    ///
    /// first-flash writes the merged image from 0x0, and its 0xFF padding over
    /// nvs wipes the seed and K_attestation -- which is why updating used to mean
    /// a Transfer, and a transfer spent. Writing only from 0xE000 leaves nvs where
    /// it is: same seed, same PIN, same K_attestation, same token, so the licence
    /// does not move and the server is never asked.
    ///
    /// Two guards, both read from the chip, and nothing is written unless both pass:
    ///
    /// **The partition table must match the image's, byte for byte.** Writing an
    /// app over a layout it was not built for is how a chip ends up booting into
    /// nothing. It is also the -dev check: under flash encryption the table reads
    /// back as ciphertext and cannot match -- and an encrypted chip must never
    /// get plaintext written to it.
    ///
    /// **nvs must carry the "cryptin" namespace.** A chip that is not a dongle yet
    /// has nothing to keep, and the right command for it is first-flash, which
    /// also provisions it.
    /// </summary>
    private static int UpdateApp(string port, string image)
    {
        var bytes = File.ReadAllBytes(image);
        if (bytes.Length <= AppRegionOffset)
            return Fail("the image is too short to carry an application.");

        var table = ReadFlash(port, PartitionTableOffset, PartitionTableSize);
        if (table is null)
            return Fail($"could not read the chip on {port}: nothing was written. Is the cable on " +
                        "the socket marked UART?");

        if (!table.AsSpan().SequenceEqual(bytes.AsSpan(PartitionTableOffset, PartitionTableSize)))
            return Fail("Nothing was written: the chip's partition table is not the one this " +
                        "firmware was built for, or the chip's flash is encrypted. Updating in " +
                        "place is only for a beta dongle with the same layout.");

        var nvs = ReadFlash(port, NvsOffset, NvsSize);
        if (nvs is null)
            return Fail($"could not read the chip on {port}: nothing was written.");
        if (!Encoding.ASCII.GetString(nvs).Contains("cryptin", StringComparison.Ordinal))
            return Fail("Nothing was written: this chip is not a Crypt-in dongle yet, so there is " +
                        "nothing to keep. Set it up from the start instead.");

        var region = Path.Combine(Path.GetTempPath(), $"cryptin-app-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(region, bytes[AppRegionOffset..]);
            return Flash(port, region, AppRegionOffset);
        }
        finally
        {
            try { File.Delete(region); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads a region of the chip's flash, or null when it cannot -- an
    /// unreadable chip is not evidence of anything, so every caller decides
    /// what "could not tell" means for itself.</summary>
    private static byte[]? ReadFlash(string port, int offset, int size)
    {
        var dump = Path.Combine(Path.GetTempPath(), $"cryptin-read-{Guid.NewGuid():N}.bin");
        try
        {
            var startInfo = Esptool();
            foreach (var argument in new[]
                     {
                         "--chip", Chip,
                         "--port", port,
                         "--baud", Baud,
                         "read-flash",
                         $"0x{offset:X}", $"0x{size:X}", dump,
                     })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(dump)) return null;

            var data = File.ReadAllBytes(dump);
            return data.Length == size ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                     or InvalidOperationException
                                     or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try { File.Delete(dump); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Is this byte-for-byte the image this build ships?
    ///
    /// Unreadable counts as "no": the answer feeds a refusal, and a probe that
    /// failed must never widen what gets written.
    /// </summary>
    private static bool MatchesBundledImage(string image)
    {
        try
        {
            using var stream = File.OpenRead(image);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            return digest.Equals(BundledImageSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses anything that is not a -dev build.
    ///
    /// This build flashes over UART on a bench, and the image it is handed comes
    /// from CRYPTIN_TEST_FIRMWARE -- an environment variable, set by hand, whose
    /// value nothing else in the chain inspects. A -prod or -staging image has
    /// CONFIG_SECURE_BOOT_V2_ENABLED and flash encryption in RELEASE mode, so it
    /// burns eFuses on its first boot and the chip stops being reflashable. One
    /// mistyped path is the whole failure mode, and it is not recoverable.
    ///
    /// The test is on the path, not on the file, because that is where the
    /// mistake happens: the two artefacts sit in sibling directories that differ
    /// by one word. A path check is trivially defeated by a -prod image copied
    /// somewhere else, and it is not the only line of defence -- <see
    /// cref="Reject"/> also turns out to refuse the -prod artefact, whose
    /// partition table is encrypted and so fails the AA 50 test. That is
    /// incidental rather than designed, which is exactly why this check is
    /// stated explicitly instead of being left to it.
    /// </summary>
    private static string? RejectNonDev(string image)
    {
        // The full path: a relative one hides the build directory that carries
        // the environment name, which is the thing being tested.
        var path = Path.GetFullPath(image).Replace('\\', '/');

        foreach (var forbidden in new[] { "prod", "staging" })
            if (path.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                return "Refusing to flash non-dev firmware on unverified hardware: " +
                       $"the path contains \"{forbidden}\". This build writes -dev images " +
                       "only: a -prod or -staging image burns the eFuses on first boot, and " +
                       $"the chip can never be reflashed.\n  {path}";

        if (!path.Contains("dev", StringComparison.OrdinalIgnoreCase))
            return "Refusing to flash non-dev firmware on unverified hardware: " +
                   "the path does not contain \"dev\" and the file is not the image this " +
                   "build ships. Expected the package's own image, or one under " +
                   ".pio/build/esp32-s3-n16r8-dev/.\n  " + path;

        return null;
    }

    /// <summary>
    /// Refuses an image that is not the merged one, which is the mistake the
    /// usage text warns about and the one that leaves a chip that will not boot.
    ///
    /// Size does not separate them -- a bare application is bigger than the
    /// 0x10000 it should start at. What separates them is the partition table:
    /// in the merged image offset 0x8000 holds it, and its magic is 0x50AA, on
    /// disk the bytes AA 50. In a bare application that offset is arbitrary
    /// code. Checked against the real artefacts of this project's own build
    /// before being relied on.
    /// </summary>
    private static string? Reject(string image)
    {
        byte[] head;
        using (var stream = File.OpenRead(image))
        {
            head = new byte[0x8002];
            var read = stream.Read(head);
            if (read < head.Length)
                return $"l'immagine e' di {read} byte: troppo corta per contenere " +
                       "una tabella delle partizioni. Serve firmware.factory.bin.";
        }

        // 0xE9 is the ESP image magic. A bootloader and an application both
        // start with it, so this only rules out something that is not an image
        // at all.
        if (head[0] != 0xE9)
            return "the image does not start with the 0xE9 magic: it is not an ESP image.";

        if (head[0x8000] != 0xAA || head[0x8001] != 0x50)
            return "no partition table at offset 0x8000 (magic AA 50): this is the bare " +
                   "application, not the merged image. Written at 0x0 the chip does not " +
                   "boot. firmware.factory.bin is needed.";

        return null;
    }

    /// <summary>
    /// Looks at the chip's nvs partition and answers only when it is not blank.
    ///
    /// **Why this exists — measured 2026-08-31.** `firmware.factory.bin` spans
    /// 0x0 to 0x94E50 and the 20480 bytes of it that land on nvs are 0xFF
    /// padding, so writing it erases the partition. A chip that was already a
    /// dongle came back from a reflash with a brand-new seed and the handover
    /// pending again, and its K_attestation -- which the licence server knows it
    /// by, and which the 24 words cannot regenerate -- was gone with it. Nothing
    /// in the flow said a word.
    ///
    /// Two kinds of "not blank" get different text because they deserve
    /// different answers, and only one of them is a catastrophe: an actual
    /// dongle carries the "cryptin" namespace in plain sight (no flash
    /// encryption on -dev), while a factory image from the board vendor
    /// carries something else and is safe to overwrite.
    ///
    /// Returns null when it cannot tell -- an unreadable chip is not evidence of
    /// a blank one, but refusing on a failed probe would block every flash the
    /// moment esptool changes a message.
    /// </summary>
    private static string? NotBlank(string port)
    {
        var nvs = ReadFlash(port, NvsOffset, NvsSize);
        if (nvs is null || Array.TrueForAll(nvs, b => b == 0xFF)) return null;

        return Encoding.ASCII.GetString(nvs).Contains("cryptin", StringComparison.Ordinal)
            // English, like everything else the user reads. It was written in
            // Italian and stayed that way until a tester met it on 2026-09-02:
            // the one message in the product that only the author could read,
            // and it appears at the moment a chip is about to be overwritten.
            ? "this chip is ALREADY a Crypt-in dongle: its nvs carries the "
              + "\"cryptin\" namespace. Rewriting it erases the seed, file_secret, "
              + "the PIN attempt counter and K_attestation -- and the 24 words do "
              + "NOT bring K_attestation back, so the dongle loses the identity the "
              + "license server knows it by. Updating the firmware? Do not add --force: "
              + "use Update firmware in Crypt-in Personal (or the update-app command), "
              + "which keeps the seed, the PIN and the license where they are."
            : "this chip's nvs is not blank, but it does not carry the \"cryptin\" "
              + "namespace: in all likelihood it is the manufacturer's factory "
              + "firmware, and overwriting it costs nothing.";
    }

    /// <summary>
    /// Not <see cref="Fail"/>: nothing was attempted and nothing is broken. The
    /// caller is being asked a question, and the exit code has to say so or a
    /// script would read a refusal as a failed write.
    /// </summary>
    private static int Refuse(string message)
    {
        Emit(new { type = "error", message });
        Emit(new { type = "done", result = nameof(FlashResult.AlreadyProvisioned) });
        return (int)FlashResult.AlreadyProvisioned;
    }

    /// <summary>
    /// Where esptool comes from, in the order that matters.
    ///
    /// **A copy beside this executable first.** That is what a tester has, and it
    /// is the only arrangement that works on a machine which never installed
    /// Python -- which is every machine except this bench. The package puts
    /// esptool.exe here; see scripts/publish-beta.ps1.
    ///
    /// **`python -m esptool` second**, and not as a courtesy: it is what kept the
    /// flash step working while the package did not exist, and a developer who
    /// has just changed esptool wants their own rather than the frozen one we
    /// ship. A checkout with no vendored binary still flashes.
    ///
    /// PYTHONIOENCODING is forced on that second path. esptool v5.3.0 died with a
    /// UnicodeEncodeError printing its own progress bar to a cp1252 console --
    /// mid write, looking exactly like a flash failure and not being one.
    /// </summary>
    private static ProcessStartInfo Esptool()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "esptool.exe");
        var haveBundled = File.Exists(bundled);

        var startInfo = new ProcessStartInfo(haveBundled ? bundled : "python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!haveBundled)
        {
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("esptool");
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        }

        return startInfo;
    }

    private static int Flash(string port, string image, int offset)
    {
        var startInfo = Esptool();
        foreach (var argument in new[]
                 {
                     "--chip", Chip,
                     "--port", port,
                     "--baud", Baud,
                     "write-flash",
                     "--flash-size", FlashSize,
                     $"0x{offset:X}", image,
                 })
            startInfo.ArgumentList.Add(argument);

        Emit(new { type = "progress", percent = 0 });

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("process did not start");
        }
        catch (Exception e)
        {
            return Fail($"esptool could not be run: {e.Message}");
        }

        var stderr = new List<string>();
        using (process)
        {
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Add(e.Data); };
            process.BeginErrorReadLine();

            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (TryReadPercent(line, out var percent))
                    Emit(new { type = "progress", percent });
            }

            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Emit(new { type = "progress", percent = 100 });
                Emit(new { type = "done", result = nameof(FlashResult.Success) });
                return (int)FlashResult.Success;
            }

            var text = string.Join("\n", stderr);
            var result = Classify(text);
            Emit(new { type = "error", message = Excerpt(text) });
            Emit(new { type = "done", result = result.ToString() });
            return (int)result;
        }
    }

    /// <summary>
    /// esptool prints "Writing at 0x00010000... (37 %)". Only the percentage is
    /// forwarded; the rest is its own logging and the parent ignores what it
    /// cannot parse, so a future esptool that words things differently degrades
    /// to a progress bar that does not move rather than to a failure.
    /// </summary>
    private static bool TryReadPercent(string line, out int percent)
    {
        var match = Regex.Match(line, @"\((\d{1,3})\s*%\)");
        percent = match.Success
            ? Math.Clamp(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100)
            : 0;
        return match.Success;
    }

    /// <summary>
    /// A missing port and a failed write need different advice, so they are not
    /// collapsed. SignatureInvalid cannot occur here -- nothing is verified --
    /// and is never returned, rather than being faked to look complete.
    /// </summary>
    private static FlashResult Classify(string stderr)
        => stderr.Contains("could not open port", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("SerialException", StringComparison.Ordinal)
            ? FlashResult.PortNotFound
            : FlashResult.FlashFailed;

    private static int Fail(string message)
    {
        Emit(new { type = "error", message });
        Emit(new { type = "done", result = nameof(FlashResult.FlashFailed) });
        return (int)FlashResult.FlashFailed;
    }

    private static void Emit(object message)
    {
        Console.WriteLine(JsonSerializer.Serialize(message));
        Console.Out.Flush();
    }

    private static string Excerpt(string text)
        => text.Length <= 400 ? text : text[^400..];

    private static string? Argument(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Mirrors CryptinSDK.Updater.FlashResult. Duplicated on purpose:
    /// this process shares no assembly with its parent, and the wire contract is
    /// the string name, not the type.</summary>
    private enum FlashResult
    {
        Success = 0,
        PortNotFound = 1,
        SignatureInvalid = 2,
        FlashFailed = 3,

        // Nothing was written: the chip is not blank. Appended, never inserted --
        // these are exit codes as well as names.
        AlreadyProvisioned = 4,
    }
}
