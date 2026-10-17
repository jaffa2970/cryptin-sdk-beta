using CryptinSDK.Dongle;

namespace CryptinPersonal;

/// <summary>
/// Showing a new dongle's recovery phrase, from wherever the app first opens one.
///
/// This lives on its own rather than inside <see cref="SetupWizard"/> because
/// the wizard is not the only door. It runs once and then sets
/// <c>FirstRunCompleted</c>, so a dongle provisioned later — a replacement, a
/// second chip, a reflashed one — reaches <see cref="MainForm"/> without ever
/// passing through it. Wiring the handover to the wizard alone would mean those
/// users are asked for a PIN they were never told, on a dongle whose seed is
/// still waiting to be read out and is being thrown away silently.
///
/// So both callers go through here, and both do it immediately before asking
/// for the PIN — the one point where the app is about to require a secret the
/// user can only have if the handover already happened.
/// </summary>
internal static class ProvisioningHandoverUi
{
    /// <summary>
    /// Hands the seed over if this dongle has never done it. Returns false only
    /// when the user backed out, in which case the caller must not continue to
    /// the PIN prompt.
    /// </summary>
    public static bool EnsureHandedOver(IWin32Window owner, CryptinDongle dongle)
    {
        ProvisioningHandover? handover;
        try
        {
            handover = dongle.GetProvisioning();
        }
        catch (CryptinDongleException e) when (e is CryptinTimeoutException || e.InnerException is not null)
        {
            // The transport, not the firmware -- and until 2026-09-02 both came
            // out of the same catch wearing the same words. A tester on a clean
            // machine moved the cable from the UART socket to the native one,
            // exactly as the wizard had just instructed, and was told his
            // firmware was too old. The message was false and plausible at once,
            // which is the worst pair: the obvious response to it is to reflash,
            // which starts the whole thing over and erases the NVS on the way.
            //
            // The two markers are a timeout, and an exception that wraps an
            // operating-system error -- which is how CryptinDongle.Write reports
            // a failed write. Neither is perfect. But an old firmware answers
            // with a status byte, so its exchange completes and it wraps
            // nothing, and the cost of getting this wrong is now a message
            // instead of a dead end.
            MessageBox.Show(owner,
                $"The dongle {dongle.UsbSerial} did not answer.\n\n" +
                "This says nothing about your firmware or your dongle: the exchange " +
                "did not complete. It usually happens after a cable has been moved " +
                "between the two sockets.\n\n" +
                "Unplug the dongle, plug it back in, and try again. Your recovery " +
                "phrase is still on the dongle and will be offered again.\n\n" +
                $"({e.Message})",
                "Dongle not responding", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // False, so the caller stops here. Both call sites already do the
            // right thing with it -- the rule they state in their comments is
            // that a PIN must never be asked for before the seed has been handed
            // over, and a failed handover is precisely that case.
            return false;
        }
        catch (CryptinDongleException e)
        {
            // An older firmware answers STATUS_UNKNOWN_CMD. That dongle cannot
            // hand anything over, and saying so beats blocking setup with a
            // protocol error nobody can act on. This branch is now the one it
            // was always meant to be: a real, rare, benign case.
            MessageBox.Show(owner,
                "This dongle cannot hand over the recovery code: " +
                "its firmware predates that feature.\n\n" +
                "If you have just programmed it, the 24 words and the PIN were shown " +
                "once only on the serial console, on the UART socket.\n\n" +
                $"({e.Message})",
                "Handover", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        // Null means already acknowledged: an ordinary dongle that has been
        // through setup, not a fault.
        if (handover is null) return true;

        using (handover)
        {
            using var form = new HandoverForm(handover, dongle.UsbSerial);
            if (form.ShowDialog(owner) != DialogResult.OK) return false;
        }

        // Last, and only after the user confirmed. If anything above bailed out,
        // the dongle still has the handover pending and will offer it again --
        // which is what makes an interrupted setup survivable.
        try
        {
            dongle.AckProvisioning();
        }
        catch (CryptinDongleException e)
        {
            MessageBox.Show(owner,
                "The dongle did not confirm the handover, so it will offer it again " +
                "the next time you connect it. The words you wrote down remain valid.\n\n" +
                $"({e.Message})",
                "Handover", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        return true;
    }
}
