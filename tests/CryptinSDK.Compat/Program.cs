using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CryptinSDK.Crypto;
using CryptinSDK.Seed;

namespace CryptinSDK.Compat;

/// <summary>
/// The compatibility gate, ported from tests/fixtures/compat/verify_fixtures.py.
///
/// One difference, and it is the whole reason this port exists. The Python
/// script implements the reader itself, from the specification, so that it
/// proves CRYPTIN_FILE_FORMAT.md is accurate. This one calls
/// <see cref="CryptinCipher.Read"/> instead: it proves the SDK is accurate. A
/// C# gate that re-implemented the reader would be checking itself.
///
/// So a failure here means the SDK does not match the fixtures. A failure in the
/// Python one means the *document* does not match them. Keep both.
///
/// Expected: 23 ok, 0 fail -- 9 legacy cases, 5 in crin-dongle/ (since
/// 2026-09-30), 5 in crin-v2/ (2026-10-09) and, since 2026-10-11, 4 in
/// crin-chip/: v1 as the chip really wrote it, the per-chunk GCM of §2.1.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        DirectoryInfo here;
        try
        {
            here = FixtureLocator.Locate(args);
        }
        catch (DirectoryNotFoundException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        Console.WriteLine($"Fixture: {here.FullName}\n");

        var manifest = JsonSerializer.Deserialize<Manifest>(
                           File.ReadAllBytes(Path.Combine(here.FullName, "manifest.json")))
                       ?? throw new InvalidOperationException("manifest.json vuoto");

        var secret = Convert.FromHexString(manifest.Vectors.FileSecret);
        var pubA = Convert.FromHexString(manifest.Vectors.PubkeyA);

        int ok = 0, fail = 0;
        foreach (var c in manifest.Cases)
        {
            if (Run(here, c, secret, pubA)) ok++;
            else fail++;
        }

        Console.WriteLine($"\n{ok} ok, {fail} fail");
        if (fail > 0)
            Console.WriteLine("Il lettore dell'SDK e le fixture non concordano.");

        var writerFailed = RunWriter(here, manifest, secret);
        var seedFailed = RunSeedVectors(here);
        return fail > 0 || writerFailed || seedFailed ? 1 : 0;
    }

    /// <summary>
    /// The writer, checked the only way worth checking it: not by a round-trip
    /// through our own reader — which would pass even if both halves shared a
    /// mistake — but by rebuilding each .crin fixture byte for byte.
    ///
    /// It works because everything else is deterministic: given the salt, the
    /// nonce and the public key out of the fixture's own header, plus the
    /// synthetic file_secret, the output must be identical to the fixture --
    /// the legacy C# app's for crin/, gen_crin_dongle.py's for crin-dongle/.
    /// No dongle, nothing random, no tolerance.
    ///
    /// .cryptin is skipped by design, not by omission: new code reads that
    /// container and never writes it.
    /// </summary>
    /// <returns>true if anything failed.</returns>
    private static bool RunWriter(DirectoryInfo here, Manifest manifest, byte[] secret)
    {
        // crin-chip/ is read, never written: nothing produces that form any more.
        var crin = manifest.Cases.Where(c => c.Format == "crin" && c.Derivation != "chip").ToList();
        var skipped = manifest.Cases.Count - crin.Count;

        Console.WriteLine($"\n--- writer: ricostruzione byte per byte di .crin v1 " +
                          $"({skipped} .cryptin e crin-chip saltati, non si scrivono) ---\n");

        int ok = 0, fail = 0;
        foreach (var c in crin)
        {
            var want = File.ReadAllBytes(Path.Combine(here.FullName, c.File));
            var plaintext = File.ReadAllBytes(Path.Combine(here.FullName, c.Plaintext));
            var layout = CrinFormat.Crin;

            // Everything the writer cannot invent comes out of the fixture's own
            // header. The public key included: wrong-dongle.crin names a
            // different dongle, and rebuilding it means writing that same key.
            var publicKey = want.AsSpan(layout.PublicKeyOffset, CrinFormat.PublicKeyLength);
            var salt = want.AsSpan(layout.SaltOffset, CrinFormat.SaltLength);
            var nonce = want.AsSpan(layout.NonceOffset, CrinFormat.NonceLength);
            var isFolder = (want[layout.FlagsOffset!.Value] & CrinFormat.FlagFolder) != 0;

            // Each set is rebuilt by the writer of its own derivation, and
            // byte-identical output is what proves the derivation: the two
            // writers share everything except the info string and the AAD.
            byte[] rebuilt;
            try
            {
                rebuilt = c.Derivation switch
                {
                    "v2" => CryptinCipher.WriteCrinV2(plaintext, secret, publicKey, isFolder, salt, nonce),
                    "dongle" => CryptinCipher.WriteCrin(plaintext, secret, publicKey, isFolder, salt, nonce),
                    _ => CryptinCipher.WriteLegacyCrin(plaintext, secret, publicKey, isFolder, salt, nonce),
                };
            }
            catch (Exception e)
            {
                Fail(c.File, $"{e.GetType().Name}: {e.Message}");
                fail++;
                continue;
            }

            if (rebuilt.AsSpan().SequenceEqual(want))
            {
                Ok(c.File, $"{rebuilt.Length} byte identici [{c.Derivation}]" + (isFolder ? ", cartella" : ""));
                ok++;
                continue;
            }

            var at = FirstDifference(rebuilt, want);
            Fail(c.File, at < 0
                ? $"lunghezza {rebuilt.Length}, attesi {want.Length}"
                : $"primo byte diverso a offset {at} ({Where(at, layout)}): "
                  + $"0x{rebuilt[at]:X2} invece di 0x{want[at]:X2}");
            fail++;
        }

        if (!FreshPerFile(here, secret)) fail++; else ok++;
        if (!ChipTamperRejected(here, secret, manifest)) fail++; else ok++;

        Console.WriteLine($"\n{ok} ok, {fail} fail (writer)");
        if (fail > 0)
            Console.WriteLine("Il writer non riproduce i byte delle fixture.");
        return fail > 0;
    }

    /// <summary>
    /// The one property the byte-for-byte test cannot check, because it supplies
    /// the salt and nonce itself: that the public writer generates a fresh pair
    /// every time. Getting this wrong is not a small bug — reusing a nonce under
    /// the same key breaks AES-GCM completely — and it would leave every fixture
    /// test still green.
    /// </summary>
    private static bool FreshPerFile(DirectoryInfo here, byte[] secret)
    {
        var plaintext = File.ReadAllBytes(Path.Combine(here.FullName, "plaintext/text.txt"));
        var pubkey = new byte[CrinFormat.PublicKeyLength];
        Array.Fill(pubkey, (byte)0x40);

        var a = CryptinCipher.WriteCrin(plaintext, secret, pubkey, isFolder: false);
        var b = CryptinCipher.WriteCrin(plaintext, secret, pubkey, isFolder: false);
        var layout = CrinFormat.Crin;

        var sameSalt = a.AsSpan(layout.SaltOffset, CrinFormat.SaltLength)
                        .SequenceEqual(b.AsSpan(layout.SaltOffset, CrinFormat.SaltLength));
        var sameNonce = a.AsSpan(layout.NonceOffset, CrinFormat.NonceLength)
                         .SequenceEqual(b.AsSpan(layout.NonceOffset, CrinFormat.NonceLength));

        if (sameSalt || sameNonce)
            return Fail("salt/nonce freschi",
                sameSalt && sameNonce ? "due scritture hanno salt E nonce identici"
                : sameSalt ? "due scritture hanno lo stesso salt"
                : "due scritture hanno lo stesso nonce");

        // And what came out is readable, which is what closes the loop on the
        // public entry point rather than only on the internal one.
        var back = CryptinCipher.Read(a, secret, pubkey);
        return back.Plaintext.AsSpan().SequenceEqual(plaintext)
            ? Ok("salt/nonce freschi", "due scritture differiscono, e la prima si rilegge")
            : Fail("salt/nonce freschi", "il file scritto non si rilegge uguale");
    }

    /// <summary>
    /// The way back without a dongle: BLAKE2b against Python's hashlib (an
    /// implementation we did not write), and words -> entropy -> file_secret
    /// against seed-vectors.json (generator/gen_seed_vectors.py).
    /// </summary>
    /// <returns>true if anything failed.</returns>
    private static bool RunSeedVectors(DirectoryInfo here)
    {
        Console.WriteLine("\n--- seme: BLAKE2b, parole <-> entropia, file_secret ---\n");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(here.FullName, "seed-vectors.json")));
        int ok = 0, fail = 0;

        var i = 0;
        foreach (var v in doc.RootElement.GetProperty("blake2b").EnumerateArray())
        {
            i++;
            var key = Convert.FromHexString(v.GetProperty("key").GetString()!);
            var msg = Convert.FromHexString(v.GetProperty("msg").GetString()!);
            var want = v.GetProperty("hash").GetString()!;
            var got = Convert.ToHexString(Blake2b.Hash(msg, key, v.GetProperty("out").GetInt32()));
            var label = $"blake2b #{i} (key {key.Length}, msg {msg.Length})";
            if (got.Equals(want, StringComparison.OrdinalIgnoreCase)) { Ok(label, "uguale a hashlib"); ok++; }
            else { Fail(label, $"{got[..16]}… invece di {want[..16]}…"); fail++; }
        }

        i = 0;
        foreach (var s in doc.RootElement.GetProperty("seeds").EnumerateArray())
        {
            i++;
            var entropy = Convert.FromHexString(s.GetProperty("entropy").GetString()!);
            var phrase = s.GetProperty("mnemonic").GetString()!;
            var label = $"seme #{i}";
            var words = phrase.Split(' ');
            var back = Bip39.MnemonicToEntropy(words);
            var secret = Convert.ToHexString(SeedDerivation.FileSecret(entropy));
            if (Bip39.EntropyToMnemonic(entropy) != phrase) { Fail(label, "entropia -> parole diverse"); fail++; }
            else if (!back.AsSpan().SequenceEqual(entropy)) { Fail(label, "parole -> entropia diversa"); fail++; }
            else if (!secret.Equals(s.GetProperty("file_secret").GetString(), StringComparison.OrdinalIgnoreCase))
            { Fail(label, "file_secret diverso"); fail++; }
            else { Ok(label, "parole <-> entropia, file_secret uguale"); ok++; }
        }

        // A wrong word must be refused, not turned into some other seed.
        var bad = doc.RootElement.GetProperty("seeds")[3].GetProperty("mnemonic").GetString()!.Split(' ');
        (bad[5], bad[6]) = (bad[6], bad[5]);
        try
        {
            Bip39.MnemonicToEntropy(bad);
            Fail("parole scambiate", "accettate"); fail++;
        }
        catch (ArgumentException)
        {
            Ok("parole scambiate", "rifiutate dal checksum"); ok++;
        }

        Console.WriteLine($"\n{ok} ok, {fail} fail (seme)");
        return fail > 0;
    }

    /// <summary>
    /// The chip-format reader opens files that standard GCM rejects, so it must
    /// be shown to reject too: one bit of ciphertext flipped in a crin-chip
    /// fixture has to end as "corrupted", not as a plaintext.
    /// </summary>
    private static bool ChipTamperRejected(DirectoryInfo here, byte[] secret, Manifest manifest)
    {
        var blob = File.ReadAllBytes(Path.Combine(here.FullName, "crin-chip/text.crin"));
        blob[CrinFormat.Crin.HeaderLength + 100] ^= 0x01;
        var pubA = Convert.FromHexString(manifest.Vectors.PubkeyA);
        try
        {
            CryptinCipher.Read(blob, secret, pubA);
            return Fail("crin-chip manomesso", "un bit cambiato e il file si apre lo stesso");
        }
        catch (CorruptedCryptinFileException)
        {
            return Ok("crin-chip manomesso", "un bit del ciphertext -> corrupted, nessun plaintext");
        }
        catch (Exception e)
        {
            return Fail("crin-chip manomesso", $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    /// <summary>Names the field an offset falls in, so a failure points at the
    /// mistake instead of at a number.</summary>
    private static string Where(int offset, CryptinLayout layout) => offset switch
    {
        _ when offset < layout.MagicLength => "magic",
        _ when offset == layout.VersionOffset => "versione",
        _ when offset == layout.FlagsOffset => "flags",
        _ when offset < layout.SaltOffset => "pubkey",
        _ when offset < layout.NonceOffset => "salt",
        _ when offset < layout.HeaderLength => "nonce",
        _ => "ciphertext o tag",
    };

    /// <returns>true if the case passed.</returns>
    private static bool Run(DirectoryInfo here, FixtureCase c, byte[] secret, byte[] pubA)
    {
        var label = c.File;
        var blob = File.ReadAllBytes(Path.Combine(here.FullName, c.File));

        if (!Convert.ToHexString(SHA256.HashData(blob)).Equals(c.Sha256, StringComparison.OrdinalIgnoreCase))
            return Fail(label, "sha256 diverso dal manifest (fixture modificata?)");

        CryptinContent content;
        try
        {
            content = CryptinCipher.Read(blob, secret, pubA);
        }
        catch (WrongDongleException e)
        {
            var who = Convert.ToHexString(e.HeaderPublicKey)[..16].ToLowerInvariant();
            return c.Expect == "wrong-dongle"
                ? Ok(label, $"rifiutato come atteso (dongle {who})")
                : Fail(label, "rifiutato per dongle sbagliato, non atteso");
        }
        catch (Exception e)
        {
            return Fail(label, $"{e.GetType().Name}: {e.Message}");
        }

        if (c.Expect == "wrong-dongle")
            return Fail(label, "decifrato, doveva essere rifiutato");

        // Not in the Python gate: the manifest names the container for every
        // case, and dispatching on the magic is exactly what a reader can get
        // wrong while still decrypting correctly.
        var expectedFormat = c.Format == "crin" ? CryptinFileFormat.Crin : CryptinFileFormat.Cryptin;
        if (content.Format != expectedFormat)
            return Fail(label, $"formato {content.Format}, atteso {expectedFormat}");

        var want = File.ReadAllBytes(Path.Combine(here.FullName, c.Plaintext));
        if (!content.Plaintext.AsSpan().SequenceEqual(want))
            return Fail(label, $"plaintext diverso ({content.Plaintext.Length} byte contro {want.Length})");

        if (c.Expect != "folder")
            return Ok(label, $"{content.Plaintext.Length} byte");

        if (!content.IsFolder)
            return Fail(label, "flag cartella non riconosciuto");

        using var zip = new ZipArchive(new MemoryStream(content.Plaintext), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => $"'{e.FullName}'").Order(StringComparer.Ordinal);
        return Ok(label, $"{content.Plaintext.Length} byte, cartella, [{string.Join(", ", names)}]");
    }

    private static bool Ok(string label, string detail)
    {
        Console.WriteLine($"  ok   {label}: {detail}");
        return true;
    }

    private static bool Fail(string label, string detail)
    {
        Console.WriteLine($"  FAIL {label}: {detail}");
        return false;
    }
}
