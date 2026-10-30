using System.Text.Json;
using System.Text.Json.Serialization;
using CryptinSDK.Licensing;

namespace CryptinPersonal;

/// <summary>
/// The few things the app has to remember between runs.
///
/// Deliberately tiny, and deliberately not holding anything sensitive: no PIN
/// (it cannot be cached — the dongle's unlocked state is RAM only and the PIN
/// itself is never stored anywhere), no file_secret, no JWT. A backup reminder
/// date, a flag, and the three public values that let an interrupted backup be
/// picked up again.
/// </summary>
internal sealed class AppSettings
{
    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Crypt-in", "settings.json");

    /// <summary>Set once the setup wizard has run to the end, so it does not
    /// reappear on every launch.</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>When to ask about a backup dongle again. Null means never asked.</summary>
    public DateTimeOffset? BackupReminderAfter { get; set; }

    /// <summary>Set once a backup has actually been registered, so the reminder
    /// stops for good rather than being postponed forever.</summary>
    public bool BackupRegistered { get; set; }

    /// <summary>A backup whose chip is written but whose registration never
    /// happened. Null when there is nothing to finish.</summary>
    public BackupResume? PendingBackup { get; set; }

    /// <summary>A licence transfer whose chip is rebuilt but which never reached
    /// the server. Null when there is nothing to finish. Separate from
    /// <see cref="PendingBackup"/> rather than a shared "pending chip": the two
    /// resume into different wizards and offering the wrong one would send
    /// someone with a broken dongle into a screen that asks for the dongle.</summary>
    public TransferResume? PendingTransfer { get; set; }

    /// <summary>A Checkout Session opened and not yet turned into a licence. Null
    /// when there is none. See <see cref="CheckoutResume"/>.</summary>
    public CheckoutResume? PendingCheckout { get; set; }

    /// <summary>A move onto a new identity that has been started on the server
    /// and not finished. Null when there is none. See <see cref="RekeyResume"/>.</summary>
    public RekeyResume? PendingRekey { get; set; }

    /// <summary>
    /// True when it is time to offer a backup again. Asking on every launch is
    /// how a useful prompt becomes one people dismiss without reading.
    /// </summary>
    public bool ShouldOfferBackup(DateTimeOffset now)
        => !BackupRegistered && (BackupReminderAfter is null || now >= BackupReminderAfter);

    public void ClearPendingBackup()
    {
        PendingBackup = null;
        Save();
    }

    public void ClearPendingTransfer()
    {
        PendingTransfer = null;
        Save();
    }

    public void ClearPendingRekey()
    {
        PendingRekey = null;
        Save();
    }

    /// <summary>Forgets the pending checkout, but only if it is still the one
    /// named: a stale caller must not wipe a newer payment.</summary>
    public void ClearPendingCheckout(string sessionId)
    {
        if (PendingCheckout?.SessionId != sessionId) return;
        PendingCheckout = null;
        Save();
    }

    public void RemindInThirtyDays(DateTimeOffset now)
    {
        BackupReminderAfter = now.AddDays(30);
        Save();
    }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings are not worth an error dialog: the defaults are
            // the same ones a first run gets.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a reminder date is not worth interrupting anyone over.
        }
    }
}

/// <summary>
/// Enough to re-enter the backup wizard at step 4 after the application has
/// been closed and reopened -- which is not a workaround but the procedure:
/// the chip is rewritten and reset while the app holds it, and only ending the
/// process frees what Windows leaves behind (measured 2026-09-02, and
/// reconnecting from inside was tried first and does not do it).
///
/// **Nothing here is a secret, and that is a constraint rather than a
/// coincidence.** The 24 words are not stored: they were typed at step 1 and
/// the chip already has them, so nothing after step 3 needs them again. What is
/// kept is a USB serial and two public keys -- the same values that travel to
/// the licence server in the clear.
///
/// The alternative was to leave the user with half a backup: a chip that opens
/// their files and that the server has never heard of, which <c>/api/verify</c>
/// answers CLONE_OR_UNREGISTERED. That failure surfaces on the day the backup
/// is needed.
/// </summary>
internal sealed class BackupResume
{
    /// <summary>USB serial of the main dongle: step 5 asks for this one back by
    /// name, and step 4 uses it to tell the two chips apart.</summary>
    public string MainSerial { get; set; } = "";

    /// <summary>The main dongle's K_identity, hex. Step 4 compares it with what
    /// the backup answers, and that comparison is the proof the seed took.</summary>
    public string MainIdentityHex { get; set; } = "";

    /// <summary>The identity the recovery printed on the destination chip, hex.
    /// Not a gate -- the HID comparison is -- but the value worth showing when
    /// the two disagree.</summary>
    public string BackupIdentityHex { get; set; } = "";

    /// <summary>When step 3 finished, so the offer can say which backup it
    /// means rather than asking about an unnamed one.</summary>
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>
/// A Checkout Session the app opened and has not yet seen turn into a licence.
///
/// Written the moment the server hands the id back — before the browser opens,
/// before anyone pays. The case it exists for: the customer pays, closes the
/// browser before the redirect, or the app crashes, or the network drops on
/// <c>/api/activate</c>. The money is taken and the licence is not issued, and
/// without this id the only way back is support. With it, the next attempt
/// offers the same payment again, and the server allows that: its anti-replay
/// only refuses a session that has already <em>produced</em> a licence.
///
/// **The one value in this file that is worth something to a thief**, and that
/// is a trade made on purpose. A paid, unused session id activates a licence on
/// whatever dongle presents it first. It is kept only as long as it is unspent,
/// removed as soon as the server says it produced a licence, and sits in the
/// user's own profile, where reading it takes code already running as that
/// user. Losing the payment to a closed browser tab is the likelier harm by far.
///
/// Lives on the one <see cref="AppSettings"/> instance <c>MainForm</c> hands
/// around, never on a fresh <see cref="AppSettings.Load"/>: the wizards save
/// that shared instance, and a second copy written behind its back would be
/// overwritten — this value with it — by the next unrelated save.
/// </summary>
internal sealed class CheckoutResume
{
    /// <summary><c>cs_…</c>, the <c>stripe_session_id</c> to send.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The SKU the session was created for. A pending payment is only
    /// offered back for the same SKU: the server would refuse any other.</summary>
    public string ProductSku { get; set; } = "";

    /// <summary>What it was opened for. A renewal's session can technically
    /// activate a new licence of the same SKU and vice versa, but offering one
    /// in place of the other would surprise the person who paid.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CheckoutPurpose Purpose { get; set; }

    /// <summary>Where the payment page is, so it can be reopened.</summary>
    public string CheckoutUrl { get; set; } = "";

    /// <summary>When it was opened, so the offer can name it.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

internal enum CheckoutPurpose { Activation, Renewal }

/// <summary>
/// Enough to re-enter the licence transfer at its last step after the
/// application has been closed and reopened. The restart is not a workaround
/// but the procedure -- see <see cref="BackupResume"/>, which carries the same
/// scar for the same reason: the chip is rewritten and reset while the app
/// holds it, and only ending the process frees what Windows leaves behind.
///
/// **Nothing here is a secret.** Not the 24 words -- they were typed at step 2
/// and the chip has them now, so nothing afterwards needs them again -- and not
/// the PIN, which is derived from the seed and never stored anywhere.
///
/// Less is kept than for a backup, and the difference says what the two
/// procedures are. A backup keeps the MAIN dongle's serial and identity because
/// both chips exist and have to be told apart. In a transfer the other chip is
/// gone: the only identity there is to remember is the one the recovery just
/// wrote.
/// </summary>
internal sealed class TransferResume
{
    /// <summary>The identity the serial recovery printed on the rebuilt chip,
    /// hex. The last step compares it with what the dongle answers over HID,
    /// which catches the wrong chip in the socket -- not wrong words. Only the
    /// server can tell those, and it does.</summary>
    public string RebuiltIdentityHex { get; set; } = "";

    /// <summary>What was said about the old dongle at step 1, so the resumed
    /// wizard does not ask again. As a string on disk, not a number: the three
    /// values are a wire contract with the server, and a numeric ordinal would
    /// quietly turn "stolen" into "lost" the day one is inserted.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecoveryReason Reason { get; set; } = RecoveryReason.DamagedDevice;

    /// <summary>When the chip was rebuilt, so the offer can say which transfer
    /// it means rather than asking about an unnamed one.</summary>
    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>
/// A move onto a new identity (advisory G2) between its two halves. It lives
/// across restarts twice over: the user decrypts their files between the halves,
/// which can take days, and the reflash in the middle needs the same restart as
/// every other wizard that rewrites a chip under the process.
///
/// **The ticket is a bearer secret**, the second value in this file worth
/// something to a thief after <see cref="CheckoutResume.SessionId"/>, and for
/// the same trade: whoever presents it with a chip of their own takes the
/// licence (not the files — the licence). It is kept only until the ticket is
/// spent or expires, it sits in the user's own profile, and the server allows
/// one open ticket per licence for a few days. Losing it to a crash instead
/// would strand the user between an erased identity and a licence still bound
/// to it, which is the likelier harm.
/// </summary>
internal sealed class RekeyResume
{
    /// <summary>From <c>rekey/start</c>, presented once by <c>rekey/finish</c>.</summary>
    public string Ticket { get; set; } = "";

    /// <summary>When the server stops accepting it.</summary>
    public DateTimeOffset TicketExpiresAt { get; set; }

    /// <summary>The identity the licence is on before the reflash, hex. The last
    /// step refuses a chip that still answers with it: that chip was never
    /// reflashed, and finishing on it would ask the server to move the licence
    /// onto the identity it is already on.</summary>
    public string OldIdentityHex { get; set; } = "";

    /// <summary>Set when the chip carries the new identity — reflashed, or
    /// (with <see cref="OnChip"/>) committed in place. Before it, resuming goes
    /// back to the decrypt-then-program steps; after it, straight to the last.</summary>
    public bool Reflashed { get; set; }

    /// <summary>The firmware changes identity in place (CMD_REKEY_BEGIN/COMMIT,
    /// 2026-10-30): no UART, no reflash, no restart. Decided once, at the start,
    /// by asking the chip.</summary>
    public bool OnChip { get; set; }

    /// <summary>When the ticket was taken, so the offer can name it.</summary>
    public DateTimeOffset StartedAt { get; set; }
}
