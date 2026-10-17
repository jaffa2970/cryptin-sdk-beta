using System.Security.Cryptography;
using System.Text.Json;
using CryptinSDK.Crypto;
using CryptinSDK.Dongle;
using CryptinSDK.Licensing;

namespace CryptinSDK.Hardware;

/// <summary>
/// Fase 1 on real hardware: the sequence from CRYPTIN_SDK_PROMPT_SESSIONE.md §9.
///
/// Two things this refuses to do, both because of a documented near-miss. An
/// earlier version of the session brief listed one dongle's PIN under the other
/// dongle's chip ID; following it would have sent three wrong PINs to the only
/// chip with real eFuses burned and blocked it.
///
///   1. It will not talk to a dongle whose serial is not the one named on the
///      command line. The check happens before a single byte is written.
///   2. It stops dead on a wrong PIN. Three is all there are, ever, and the
///      counter is in NVS: it survives unplugging, rebooting and this program.
///
/// The PIN comes from CRYPTIN_TEST_PIN so it stays off the command line and out
/// of shell history. Nothing here ever prints it, and nothing prints file_secret
/// either — only a hash of it, so two runs can be compared without exposing it.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Which licence server this run talks to.
    ///
    /// Defaults to production, like <see cref="LicenseClient.DefaultBaseUrl"/>,
    /// because that is what every invocation written before this one meant.
    /// Point it somewhere else with CRYPTIN_LICENSE_URL -- the red-team clone,
    /// say.
    ///
    /// <b>-c RedTeam does not reach here.</b> LicenseEndpoint lives in
    /// CryptinPersonal and this project references only the SDK, so the build
    /// configuration steers nothing at all: every configuration, RedTeam
    /// included, talked to production until this variable existed. Worth saying
    /// out loud, because the rest of the tooling teaches the opposite habit.
    /// </summary>
    private static Uri _endpoint = LicenseClient.DefaultBaseUrl;

    /// <summary>
    /// The third thing this program refuses to do: a CRYPTIN_LICENSE_URL that
    /// does not parse is a hard stop, never a quiet fall back to production.
    ///
    /// Falling back is how someone who meant to reach the clone spends one of a
    /// real licence's three recoveries on a typo, and gets it back in a year.
    /// </summary>
    /// <returns>null when the endpoint is settled, or the reason it is not.</returns>
    private static string? ResolveEndpoint()
    {
        var raw = Environment.GetEnvironmentVariable("CRYPTIN_LICENSE_URL");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return $"CRYPTIN_LICENSE_URL non e' un URL assoluto http/https: '{raw}'";

        _endpoint = url;
        return null;
    }

    /// <summary>
    /// Reads the provisioning handover through the SDK, and stops.
    ///
    /// Deliberately does NOT acknowledge: that is the irreversible step, and a
    /// diagnostic that quietly performed it would consume the one handover the
    /// dongle has. Acknowledging belongs to the wizard, after a person has seen
    /// the words.
    /// </summary>
    private static int Provisioning(string expected)
    {
        var attached = CryptinDongle.ListSerials();
        Console.WriteLine($"Dongle collegati: {(attached.Count == 0 ? "nessuno" : string.Join(", ", attached))}");

        if (!attached.Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"  FERMO. Atteso {expected}, che non e' collegato.");
            return 3;
        }

        using var dongle = new CryptinDongle();
        if (!dongle.Connect(requiredUsbSerial: expected, waitMs: 5000))
        {
            Console.Error.WriteLine($"  FERMO. {expected} e' enumerato ma non si apre.");
            return 4;
        }

        var handover = dongle.GetProvisioning();
        if (handover is null)
        {
            Console.WriteLine("  ok   consegna gia' confermata: il seed non e' piu' leggibile.");
            return 0;
        }

        using (handover)
        {
            Console.WriteLine($"  ok   PIN: {handover.Pin}");
            Console.WriteLine($"  ok   parole ({handover.Words.Count}):");
            Console.WriteLine($"       {handover.Mnemonic}");
        }

        Console.WriteLine("\n  Nessun ACK inviato: il dongle riproporra' la consegna.");
        return 0;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "uso: dotnet run --project tests/CryptinSDK.Hardware -- <serial-atteso>\n" +
                "     con il PIN in CRYPTIN_TEST_PIN, letto dentro il comando.\n" +
                "     Senza PIN arriva fino a Ping e si ferma prima del gate.\n" +
                "     --provisioning legge la consegna e si ferma: nessun ACK,\n" +
                "     nessun PIN, il dongle resta esattamente com'era.\n" +
                "\n" +
                "     Server: CRYPTIN_LICENSE_URL, altrimenti la PRODUZIONE.\n" +
                "     -c RedTeam non c'entra e non protegge: questo progetto\n" +
                "     non vede LicenseEndpoint, quindi senza quella variabile\n" +
                "     ogni configurazione scrive in produzione.");
            return 2;
        }

        if (ResolveEndpoint() is { } urlError)
        {
            Console.Error.WriteLine(
                $"\n  FERMO. {urlError}\n" +
                "  Non ripiego sulla produzione: sarebbe il modo di spendere un\n" +
                "  recovery vero su un refuso, e di riaverlo fra 365 giorni.");
            return 6;
        }

        if (_endpoint != LicenseClient.DefaultBaseUrl)
            Console.WriteLine($"\n  *** NON E' LA PRODUZIONE: {_endpoint} ***\n");

        var expected = args[0];

        if (args.Contains("--provisioning")) return Provisioning(expected);

        var attached = CryptinDongle.ListSerials();

        Console.WriteLine($"Dongle collegati: {(attached.Count == 0 ? "nessuno" : string.Join(", ", attached))}");

        if (!attached.Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"\n  FERMO. Atteso {expected}, che non e' collegato.\n" +
                "  Non mando niente a un chip che non e' quello richiesto:\n" +
                "  tre PIN sbagliati bloccano il dongle, e il contatore sta in NVS.");
            return 3;
        }

        using var dongle = new CryptinDongle();
        if (!dongle.Connect(requiredUsbSerial: expected, waitMs: 5000))
        {
            Console.Error.WriteLine($"  FERMO. {expected} e' enumerato ma non si apre.");
            return 4;
        }

        Console.WriteLine($"Aperto {dongle.UsbSerial}\n");

        // 1. PING -- the only command a locked dongle answers besides VERIFY_PIN.
        if (!dongle.Ping())
        {
            Console.Error.WriteLine("  FAIL Ping: nessun magic CRYPTIN\\0");
            return 1;
        }
        Console.WriteLine("  ok   Ping: CRYPTIN\\0");

        var pin = Environment.GetEnvironmentVariable("CRYPTIN_TEST_PIN");
        if (string.IsNullOrEmpty(pin))
        {
            Console.WriteLine("\nCRYPTIN_TEST_PIN non impostata: mi fermo prima del PIN gate.\n" +
                              "Il trasporto HID funziona, il resto e' dietro il gate.");
            return 0;
        }

        // 2. VERIFY_PIN -- one attempt, no retry, ever.
        var result = dongle.VerifyPin(pin);
        if (result != PinResult.Ok)
        {
            Console.Error.WriteLine(
                $"\n  FERMO. VerifyPin -> {result}. NON riprovo.\n" +
                "  Tre tentativi sbagliati bloccano il dongle e solo un recover\n" +
                "  con le 24 parole sulla console UART lo sblocca.\n" +
                "  Controlla di aver letto il PIN dalla sezione giusta di\n" +
                "  dongle-test-mnemonic.md -- una sezione per chip.");
            return 5;
        }
        Console.WriteLine("  ok   VerifyPin: sbloccato per questa sessione USB");

        // 3. The two keys must differ. If they match it is a firmware bug, not a
        //    client bug: one is derived from the seed, the other from hardware RNG.
        var identity = dongle.GetIdentityPubKey();
        var attestation = dongle.GetAttestationPubKey();
        Console.WriteLine($"  ok   K_identity:    {Convert.ToHexString(identity).ToLowerInvariant()}");
        Console.WriteLine($"  ok   K_attestation: {Convert.ToHexString(attestation).ToLowerInvariant()}");

        if (identity.AsSpan().SequenceEqual(attestation))
        {
            Console.Error.WriteLine("\n  FAIL le due chiavi coincidono -- e' un bug del firmware, non del client.");
            return 1;
        }
        Console.WriteLine("  ok   le due chiavi sono diverse, come devono");

        // 4. file_secret is a secret: only a fingerprint of it leaves this process.
        //    Deliberately still called: this harness runs against a -dev dongle,
        //    and 0x04 answering there is part of what it is checking. On -prod
        //    the same call throws ERR_DEPRECATED, by design.
#pragma warning disable CS0618 // GetFileSecret is deprecated on -prod firmware
        var secret = dongle.GetFileSecret();
#pragma warning restore CS0618
        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(secret))[..16].ToLowerInvariant();
            Console.WriteLine($"  ok   file_secret: {secret.Length} byte, sha256 {fingerprint}… (mai stampato in chiaro)");

            // 4b. The host derivation and the chip's must agree. This is the check
            //     that did not exist for two months: the harness read file_secret
            //     and never used it, so nothing noticed that the host derived with
            //     "cryptin-file-v1" + header AAD while the chip used
            //     "cryptin-file-key-v1" with none (measured 2026-09-30).
            var probe = System.Text.Encoding.ASCII.GetBytes("host->chip derivation probe");
            var hostWritten = CryptinCipher.WriteCrin(probe, secret, identity, isFolder: false);
            try
            {
                var back = CryptinCipher.ReadCrin(dongle, hostWritten);
                if (!back.Plaintext.AsSpan().SequenceEqual(probe))
                {
                    Console.Error.WriteLine("\n  FAIL un .crin scritto dall'host si apre sul chip ma il testo e' diverso.");
                    return 1;
                }
                Console.WriteLine("  ok   .crin scritto dall'host con file_secret: il chip lo apre (derivazioni allineate)");
            }
            catch (CorruptedCryptinFileException)
            {
                Console.Error.WriteLine(
                    "\n  FAIL il chip rifiuta un .crin scritto dall'host: le due derivazioni divergono.\n" +
                    "  Vedi CRYPTIN_FILE_FORMAT.md §1 e measure_hkdf_info.py.");
                return 1;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        // 5. CHALLENGE -- the fragmented one. Printed so the signature can be
        //    verified out of band; .NET has no Ed25519 and this is not the place
        //    to add a dependency for one check.
        var nonce = RandomNumberGenerator.GetBytes(32);
        var signature = dongle.Challenge(nonce);
        Console.WriteLine($"  ok   Challenge: {signature.Length} byte su due report");
        Console.WriteLine($"\n  nonce     {Convert.ToHexString(nonce).ToLowerInvariant()}");
        Console.WriteLine($"  signature {Convert.ToHexString(signature).ToLowerInvariant()}");

        var again = dongle.Challenge(RandomNumberGenerator.GetBytes(32));
        if (again.AsSpan().SequenceEqual(signature))
        {
            Console.Error.WriteLine("\n  FAIL due nonce diversi hanno dato la stessa firma.");
            return 1;
        }
        Console.WriteLine("\n  ok   un secondo nonce da' una firma diversa");
        Console.WriteLine("\nFase 1: trasporto, PIN gate, chiavi e challenge OK.");

        if (args.Contains("verify"))
        {
            var code = Verify(dongle);
            if (code != 0 || !args.Contains("activate")) return code;
        }
        else if (!args.Contains("activate") && !args.Contains("activate-b")
                 && !args.Contains("renew") && !args.Contains("register-backup")
                 && !args.Contains("recovery"))
        {
            Console.WriteLine("\n(sottocomandi: 'verify', 'activate', 'activate-b', 'renew',\n" +
                              " 'register-backup --backup-attestation <hex64> [--label X]',\n" +
                              " 'recovery [--reason damaged_device]' — TRANSFER, sposta la licenza.\n" +
                              " Tutti tranne 'verify' scrivono nel DB di produzione.)");
            return 0;
        }

        if (args.Contains("recovery")) return Recovery(dongle, args);
        if (args.Contains("register-backup")) return RegisterBackup(dongle, args);
        if (args.Contains("renew")) return Renew(dongle);
        if (args.Contains("activate-b")) return Activate(dongle, Registered);
        return args.Contains("activate") ? Activate(dongle, null) : 0;
    }

    /// <summary>
    /// /api/recovery. Moves a licence onto the connected chip, which must be the
    /// rebuilt one — recovered from the seed, so it signs with the original
    /// K_identity while carrying its own K_attestation.
    ///
    /// Takes no --old-attestation and no --new-attestation. The old chip's key is
    /// not part of the contract: in a recovery that chip is gone, and the server
    /// schema has no field for it. The new one is read from the dongle on the
    /// wire, because that dongle *is* the new chip -- accepting it as an argument
    /// would only create a way to send a value that contradicts the signature.
    /// </summary>
    private static int Recovery(CryptinDongle dongle, string[] args)
    {
        if (Argument(args, "--old-attestation") is not null)
            Console.WriteLine("  (nota) --old-attestation ignorato: non e' nel contratto, " +
                              "il server non ha quel campo.");

        var reasonText = Argument(args, "--reason") ?? "damaged_device";
        var reason = reasonText switch
        {
            "lost_device" => RecoveryReason.LostDevice,
            "damaged_device" => RecoveryReason.DamagedDevice,
            "stolen_device" => RecoveryReason.StolenDevice,
            _ => (RecoveryReason?)null,
        };
        if (reason is null)
        {
            Console.Error.WriteLine($"\n  --reason '{reasonText}' non valido: " +
                                    "lost_device, damaged_device o stolen_device.");
            return 2;
        }

        Console.WriteLine($"\n--- /api/recovery su {_endpoint} ---");
        Console.WriteLine($"  chip ricostruito: {dongle.UsbSerial}");
        Console.WriteLine($"  motivo:           {reasonText}");
        Console.WriteLine("  ATTENZIONE: e' un TRANSFER. Dopo, la licenza sta su questo chip e " +
                          "su nessun altro.");

        using var client = new LicenseClient(_endpoint);
        try
        {
            var result = client.RecoverAsync(dongle, reason.Value).GetAwaiter().GetResult();

            Console.WriteLine($"  ok   status:     {result.Status}");
            Console.WriteLine($"  ok   expires_at: {result.ExpiresAt:u} (invariata: sposta, non estende)");
            if (result.Message is not null) Console.WriteLine($"  --   message:    {result.Message}");
            Console.WriteLine($"  ok   jwt:        {result.Jwt.Length} caratteri");
            ReportLicenceId(result.Jwt);
            return 0;
        }
        catch (LicenseApiException e)
        {
            Console.Error.WriteLine($"\n  FAIL {e.Message}");
            Console.Error.WriteLine(e.Code switch
            {
                LicenseErrorCodes.AlreadyActivated =>
                    "  La K_attestation di questo chip risulta gia' su un'altra licenza.\n" +
                    "  Se e' registrata come backup altrove, va tolta quella riga prima.",
                LicenseErrorCodes.SameDevice =>
                    "  Il chip e' gia' quello registrato su questa licenza: niente da spostare.",
                LicenseErrorCodes.LicenseNotFound =>
                    "  Nessuna licenza per questa K_identity: il seed caricato non e' quello giusto.",
                // "next_available lo dice il server" e' quello che diceva questa
                // riga fino al 2026-09-03, senza dirlo -- la stessa forma del
                // difetto trovato quella sera nel BackupWizard, dove il campo
                // arrivava dal server e il client lo buttava nel parser. Qui la
                // finestra e' mobile su 365 giorni, quindi la data e' ancora
                // meno ricostruibile a mano.
                LicenseErrorCodes.RecoveryRateLimit =>
                    "  Limite di 3 recovery per licenza ogni 365 giorni, finestra mobile.\n" +
                    (e.NextAvailable is { Length: > 0 } when_
                        ? $"  Prossima possibile: {when_}."
                        : "  Il server non ha detto quando si riapre."),
                LicenseErrorCodes.InvalidReason =>
                    "  Motivo non riconosciuto.",
                _ => "",
            });
            return 11;
        }
    }

    /// <summary>
    /// /api/dongle/register_backup. The dongle on the wire must be the **main**
    /// one: it signs the challenge and its K_attestation proves which chip it
    /// is. The backup signs nothing — only its K_attestation is needed, read
    /// from it beforehand and passed in here as hex.
    ///
    /// Both chips are needed, one at a time, which is why this takes the value
    /// rather than expecting two dongles plugged in at once.
    /// </summary>
    private static int RegisterBackup(CryptinDongle dongle, string[] args)
    {
        var backupHex = Argument(args, "--backup-attestation");
        var label = Argument(args, "--label");

        if (backupHex is null)
        {
            Console.Error.WriteLine(
                "\n  Serve --backup-attestation <64 caratteri hex>.\n" +
                "  Leggila dal chip di backup con:\n" +
                "    dotnet run --project tests/CryptinSDK.Hardware -- <serial-backup>\n" +
                "  e copia la riga K_attestation.");
            return 2;
        }

        backupHex = backupHex.Trim().ToLowerInvariant();
        if (backupHex.Length != 64 || !backupHex.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine($"\n  --backup-attestation non è hex da 64 caratteri ({backupHex.Length}).");
            return 2;
        }

        var backup = Convert.FromHexString(backupHex);

        // Caught here rather than left to the server's SAME_AS_MAIN, because at
        // this point the mistake is obvious and the message can say what to do.
        if (backup.AsSpan().SequenceEqual(dongle.GetAttestationPubKey()))
        {
            Console.Error.WriteLine(
                "\n  FERMO. La K_attestation passata è quella del dongle collegato.\n" +
                "  Il backup deve essere un secondo chip fisico: la K_attestation\n" +
                "  nasce da RNG hardware e non si riproduce da un seed.");
            return 9;
        }

        Console.WriteLine($"\n--- /api/dongle/register_backup su {_endpoint} ---");
        Console.WriteLine($"  principale:  {dongle.UsbSerial}, firma il challenge");
        Console.WriteLine($"  backup:      {backupHex[..16]}… (non firma niente)");
        if (label is not null) Console.WriteLine($"  etichetta:   {label}");

        using var client = new LicenseClient(_endpoint);
        try
        {
            var result = client.RegisterBackupAsync(dongle, backup, label).GetAwaiter().GetResult();

            Console.WriteLine($"  ok   status:          {result.Status}");
            Console.WriteLine($"  ok   trusted_dongles: {result.TrustedDongles} " +
                              "(conta anche il principale, che non ha una riga in dongle_trusted)");
            Console.WriteLine($"  ok   label:           {result.Label ?? "(nessuna)"}");
            Console.WriteLine("\n  Il backup è operativo subito. Verificalo collegandolo e lanciando 'verify':\n" +
                              "  deve dare Valid con lo stesso lid del principale.");
            return 0;
        }
        catch (LicenseApiException e)
        {
            Console.Error.WriteLine($"\n  FAIL {e.Message}");
            Console.Error.WriteLine(e.Code switch
            {
                LicenseErrorCodes.SameAsMain =>
                    "  Il chip indicato è il principale stesso.",
                LicenseErrorCodes.AlreadyRegistered =>
                    "  Quel chip è già un backup — su questa licenza o su un'altra.",
                LicenseErrorCodes.AlreadyActivated =>
                    "  Quel chip è il principale di un'altra licenza.",
                LicenseErrorCodes.BackupLimitReached =>
                    "  Backup dell'anno già registrato. next_available lo dice il server, non si ricalcola.",
                LicenseErrorCodes.LicenseExpired =>
                    "  Licenza scaduta: va rinnovata prima di registrare un backup.",
                LicenseErrorCodes.LicenseNotActive =>
                    "  Licenza non attiva.",
                LicenseErrorCodes.AttestationMismatch when e.Reason == "NOT_MAIN_DONGLE" =>
                    "  Il dongle collegato è un backup già registrato: deve firmare il PRINCIPALE.",
                LicenseErrorCodes.AttestationMismatch =>
                    "  Il dongle collegato non risulta autorizzato su questa licenza.",
                _ => "",
            });
            return 10;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// /api/dongle/renew. Extends a real licence by a year, so it is only ever
    /// pointed at a row that is knowingly a test artefact.
    ///
    /// Renewal is bound to the **main** dongle: a registered backup verifies and
    /// works but cannot renew, and gets 403 ATTESTATION_MISMATCH if it tries.
    /// </summary>
    private static int Renew(CryptinDongle dongle)
    {
        var sessionId = Guid.NewGuid().ToString();

        Console.WriteLine($"\n--- /api/dongle/renew su {_endpoint} ---");
        Console.WriteLine($"  stripe_session_id: {sessionId} (nuovo — ogni rinnovo ne vuole uno)");

        using var client = new LicenseClient(_endpoint);
        try
        {
            var result = client.RenewAsync(dongle, sessionId).GetAwaiter().GetResult();

            Console.WriteLine($"  ok   status:     {result.Status}");
            Console.WriteLine($"  ok   expires_at: {result.ExpiresAt:u}");

            if (result.PreviousExpiresAt is { } previous)
            {
                // max(old, now) + 365, so renewing early adds the remaining days
                // instead of throwing them away. Worth printing both dates: the
                // rule is the kind that surprises people at the wrong moment.
                var added = (result.ExpiresAt - previous).TotalDays;
                Console.WriteLine($"  ok   previous:   {previous:u}");
                Console.WriteLine($"  ok   estesa di:  {added:F0} giorni, da {previous:dd/MM/yyyy} a {result.ExpiresAt:dd/MM/yyyy}");
            }
            else
            {
                Console.WriteLine("  --   previous_expires_at: assente (licenza senza scadenza)");
            }

            Console.WriteLine($"  ok   jwt:        {result.Jwt.Length} caratteri, " +
                              $"{result.Jwt.Count(c => c == '.') + 1} segmenti");
            ReportLicenceId(result.Jwt);
            return 0;
        }
        catch (LicenseApiException e)
        {
            Console.Error.WriteLine($"\n  FAIL {e.Message}");
            Console.Error.WriteLine(e.Code switch
            {
                LicenseErrorCodes.AttestationMismatch =>
                    "  Il rinnovo vuole il dongle PRINCIPALE: un backup registrato non rinnova.",
                LicenseErrorCodes.LicenseNotFound =>
                    "  Nessuna licenza per questa K_identity: va attivata prima.",
                LicenseErrorCodes.AlreadyActivated =>
                    "  Quel session id era gia' stato usato.",
                _ => "",
            });
            return 8;
        }
    }

    /// <summary>
    /// Reads the licence row id out of the JWT's `lid` claim.
    ///
    /// Nothing else exposes it: the activation response carries status, jwt and
    /// expires_at, and there is no endpoint that returns a licence by id. So the
    /// token is the only place a test row's identity can be found without
    /// database access — which is what makes the difference between "an artefact
    /// we know about" and one nobody can find later.
    ///
    /// The signature is NOT verified here. This is bookkeeping, not
    /// authentication: the app verifies the token offline against the embedded
    /// RSA key, and doing it here would prove nothing about the licence.
    /// </summary>
    private static void ReportLicenceId(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            Console.WriteLine("  --   jwt: non ha tre segmenti, id non leggibile");
            return;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;

            var lid = root.TryGetProperty("lid", out var id) ? id.ToString() : "assente";
            var product = root.TryGetProperty("product", out var p) ? p.GetString() : "?";
            Console.WriteLine($"  ok   licenza id:  {lid}  (product {product})");
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            Console.WriteLine("  --   jwt: payload non decodificabile");
        }
    }

    private static void Check(string what, string got, string expected)
        => Console.WriteLine(got == expected
            ? $"  ok   {what}: {got}"
            : $"  FAIL {what}: {got}, atteso {expected}");

    /// <summary>
    /// Mode B test data.
    ///
    /// The VAT number is IT12345678903, not the ...901 in the test plan: 901
    /// fails the Italian check digit, which should be 3. It would be refused by
    /// ActivationValidation and degraded to "N/D" by the server, so asserting
    /// that it comes back intact could never pass. 903 is the same synthetic
    /// number with the correct check digit, and belongs to nobody.
    /// </summary>
    private static ActivationData Registered => new()
    {
        Email = "test@lake8.dev",
        FirstName = "Test",
        LastName = "Crypt-in",
        Company = "lake8.dev",
        VatId = "IT12345678903",
        AcceptTerms = true,
    };

    /// <summary>
    /// The client-side rules, exercised without touching the network. They run
    /// before an activation because that is where they run in the app: the
    /// server never rejects these fields, so this is the only place a typo can
    /// still be caught while the user can still fix it.
    /// </summary>
    private static void ValidationPreflight()
    {
        Console.WriteLine("\n--- validazione client, prima di toccare il server ---");

        (string label, string value, bool accepted)[] cases =
        [
            ("CF persona fisica",        "RSSMRA80A01H501U", true),
            ("P.IVA italiana valida",    "IT12345678903",    true),
            ("P.IVA senza prefisso",     "12345678903",      true),
            ("P.IVA del piano (901)",    "IT12345678901",    false),
            ("VAT europea",              "DE123456789",      true),
            ("valore corto e non VAT",   "12345",            false),
            ("oltre 16 caratteri",       "RSSMRA80A01H501UX", false),
        ];

        foreach (var (label, value, expected) in cases)
        {
            var problem = ActivationValidation.VatId(value);
            var accepted = problem is null;
            Console.WriteLine($"  {(accepted == expected ? "ok  " : "FAIL")} {label,-24} {value,-18} " +
                              (accepted ? "accettato" : $"rifiutato — {problem}"));
        }

        foreach (var (label, value, expected) in new[]
                 {
                     ("email normale", "test@lake8.dev", true),
                     ("email senza @", "test.lake8.dev", false),
                 })
        {
            var problem = ActivationValidation.Email(value);
            var accepted = problem is null;
            Console.WriteLine($"  {(accepted == expected ? "ok  " : "FAIL")} {label,-24} {value,-18} " +
                              (accepted ? "accettata" : $"rifiutata — {problem}"));
        }

        Console.WriteLine(
            "\n  Nota: '12345' e la 901 sono rifiutate QUI. Il server le accetterebbe\n" +
            "  entrambe e le scriverebbe come N/D — non rifiuta mai un campo anagrafico\n" +
            "  dopo un pagamento riuscito, ed è per questo che il controllo sta a monte.");
    }

    /// <summary>
    /// /api/verify. Writes nothing but a refreshed JWT and a ping timestamp, so
    /// it is safe to run as often as you like — and the nonce is ours, not a
    /// single-use one from the server, so a retry costs nothing either.
    /// </summary>
    private static int Verify(CryptinDongle dongle)
    {
        Console.WriteLine($"\n--- /api/verify su {_endpoint} ---");

        using var client = new LicenseClient(_endpoint);
        try
        {
            var status = client.VerifyAsync(dongle).GetAwaiter().GetResult();

            Console.WriteLine($"  ok   stato:      {status.State}");
            Console.WriteLine($"  ok   utilizzabile: {status.IsUsable}");
            if (status.ExpiresAt is not null) Console.WriteLine($"  ok   scadenza:   {status.ExpiresAt:u}");
            if (status.GraceUntil is not null) Console.WriteLine($"  ok   grazia:     {status.GraceUntil:u}");
            if (status.Reason is not null) Console.WriteLine($"  --   reason:     {status.Reason}");
            if (status.Jwt is not null)
            {
                Console.WriteLine($"  ok   jwt:        {status.Jwt.Length} caratteri, rinfrescato");
                ReportLicenceId(status.Jwt);
            }
            return 0;
        }
        catch (LicenseApiException e)
        {
            Console.Error.WriteLine($"\n  FAIL {e.Message}");
            return 7;
        }
    }

    /// <summary>
    /// Fase 3, mode A: anonymous activation against the real licence server.
    ///
    /// This is not a dry run. With the Stripe bypass on in production, a POST
    /// that gets past the signature check creates a live 365-day licence row —
    /// so the harness says what it is about to do before doing it.
    /// </summary>
    private static int Activate(CryptinDongle dongle, ActivationData? data)
    {
        var mode = data is null ? "A (anonima)" : "B (registrata)";
        if (data is not null) ValidationPreflight();

        // Fresh every time: stripe_session_id is UNIQUE, and reusing one comes
        // back as 409 ALREADY_ACTIVATED rather than as a second activation.
        var sessionId = Guid.NewGuid().ToString();

        Console.WriteLine($"\n--- /api/activate, modalita' {mode} su {_endpoint} ---");
        Console.WriteLine($"  stripe_session_id: {sessionId} (nuovo, bypass Stripe attivo)");
        if (data is not null)
            Console.WriteLine($"  anagrafica:        {data.Email}, {data.FirstName} {data.LastName}, " +
                              $"{data.Company}, {data.VatId}, termini={data.AcceptTerms}");

        using var client = new LicenseClient(_endpoint);
        try
        {
            var result = client.ActivateAsync(dongle, sessionId, data).GetAwaiter().GetResult();

            var expectedRegistered = data is not null;
            var days = (result.ExpiresAt - DateTimeOffset.UtcNow).TotalDays;

            Console.WriteLine($"  ok   status:     {result.Status}");
            Console.WriteLine($"  ok   expires_at: {result.ExpiresAt:u} ({days:F0} giorni)");
            Console.WriteLine($"  ok   jwt:        {result.Jwt.Length} caratteri, " +
                              $"{result.Jwt.Count(c => c == '.') + 1} segmenti");
            ReportLicenceId(result.Jwt);

            if (result.Customer is null)
            {
                Console.WriteLine("  --   customer: assente (server precedente al blocco additivo)");
                return 0;
            }

            var c = result.Customer;
            Check("customer.registered", c.Registered.ToString().ToLowerInvariant(),
                expectedRegistered.ToString().ToLowerInvariant());

            if (data is not null)
            {
                Check("customer.email", c.Email, data.Email ?? "");
                Check("customer.company", c.Company, data.Company ?? "");
                // The server normalises a recognised VAT and degrades anything
                // it cannot place to N/D, so both are a pass -- what would not
                // be is silence.
                Console.WriteLine(c.VatId == "N/D"
                    ? $"  --   customer.vat_id: N/D (il server non l'ha riconosciuta)"
                    : $"  ok   customer.vat_id: {c.VatId}");
            }
            return 0;
        }
        catch (LicenseApiException e)
        {
            Console.Error.WriteLine($"\n  FAIL {e.Message}");
            Console.Error.WriteLine(e.Code switch
            {
                LicenseErrorCodes.AlreadyActivated =>
                    "  Questo chip ha gia' una licenza viva, oppure il session id era gia' usato.",
                LicenseErrorCodes.PaymentRequired =>
                    "  Il bypass Stripe non e' attivo sul server, o il session id era vuoto.",
                LicenseErrorCodes.ChallengeExpired =>
                    "  Challenge scaduta o gia' consumata: ne serve una nuova a ogni tentativo.",
                LicenseErrorCodes.InvalidSignature when e.IsMalformedRequest =>
                    "  Corpo malformato: e' un bug del client, non del dongle.",
                LicenseErrorCodes.InvalidSignature =>
                    "  Firma o hex rifiutati: minuscolo, senza 0x, 64/128 caratteri.",
                _ => "",
            });
            return 6;
        }
    }
}
