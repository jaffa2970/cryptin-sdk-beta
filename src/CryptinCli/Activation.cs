// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 lake8.dev
using System.Globalization;
using CryptinSDK.Dongle;
using CryptinSDK.Licensing;

namespace CryptinCli;

/// <summary>
/// `cryptin activate` -- the beta licence from the command line (2026-11-04).
///
/// Until this existed a Linux user needed a Windows machine once, to activate
/// the dongle with CryptinPersonal. What it does is what the app's anonymous
/// activation does, through the same SDK call: challenge, signature by the
/// dongle, POST, and the returned token written to the chip
/// (<see cref="LicenseClient.ActivateAsync"/> does all six steps).
///
/// Deliberately narrower than the app:
///   * **beta SKU only, anonymous only.** No email, no payment: the beta has
///     nothing to pay for and a registered activation needs terms accepted in
///     a form, which a CLI is the wrong place to fake.
///   * **an existing valid licence is not replaced unless asked** (--replace).
///     The app asks in a dialog; a script cannot answer one, so the default has
///     to be the harmless answer.
/// </summary>
internal static class Activation
{
#if CRYPTIN_REDTEAM
    // The clone. Only in a `-c RedTeam` build, never in a published binary.
    internal static readonly Uri Server = new("https://redteam-license.lake8.dev");
#else
    internal static readonly Uri Server = LicenseClient.DefaultBaseUrl;
#endif

    internal static async Task<ExitCode> RunAsync(string[] args, int connectWaitMs, CancellationToken ct)
    {
        var replace = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--replace") replace = true;
            else return Program.Fail($"unknown option '{args[i]}' for activate", ExitCode.Usage);
        }

        var attached = CryptinDongle.ListSerials();
        if (attached.Count == 0) return Program.Fail("no Crypt-in dongle found", ExitCode.NoDongle);

        using var dongle = new CryptinDongle();
        if (!dongle.Connect(attached[0], connectWaitMs))
            return Program.Fail("the dongle will not open — unplug it and plug it back in", ExitCode.DongleError);

        if (!Program.IsUnlocked(dongle))
            return Program.Fail("dongle is locked — run 'cryptin unlock' first", ExitCode.PinRequired);

        // The chip is asked, as the app does: what activation is about to
        // overwrite is what the chip holds, whatever the server thinks.
        // An unreadable state is not a refusal.
        if (!replace)
        {
            try
            {
                var existing = await dongle.GetLicenseStatusAsync();
                if (existing.IsValid)
                {
                    var until = existing.ExpiresAt is { } e
                        ? $" until {e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                        : "";
                    return Program.Fail($"this dongle already has a valid license{until} — "
                                        + "nothing was changed (use --replace to activate anyway)",
                                        ExitCode.Usage);
                }
            }
            catch (CryptinDongleException)
            {
                // Firmware without the command, a transport hiccup: go ahead.
            }
        }

        Program.Err($"activating with {Server.Host} …");
        try
        {
            using var client = new LicenseClient(Server);
            var result = await client.ActivateAsync(
                dongle, ActivateRequest.BetaSessionId, data: null, ActivateRequest.BetaSku, ct);

            Program.Err("activated — the license is on the dongle");
            // Same wording as `status`, so a script can match either.
            Console.Out.WriteLine(
                $"license: valid until {result.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH':'mm", CultureInfo.InvariantCulture)} UTC");
            return ExitCode.Ok;
        }
        catch (LicenseApiException x)
        {
            return Program.Fail(Describe(x), ExitCode.ServerRefused);
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Program.Fail($"license server unreachable ({Server.Host}) — check the connection and try again",
                                ExitCode.ServerUnreachable);
        }
    }

    /// <summary>The app's wording (ActivationForm.Describe), minus the buttons.</summary>
    private static string Describe(LicenseApiException x) => x.Code switch
    {
        _ when x.Code == LicenseErrorCodes.AlreadyActivated
               && x.Reason == LicenseErrorCodes.ReasonIdentityRekeyed =>
            "this recovery phrase has been replaced: its license was moved to a new identity. "
            + "A dongle carrying these words cannot be activated again — use the dongle with the new words",
        LicenseErrorCodes.AlreadyActivated =>
            "this dongle already has an active license on the server — nothing was changed",
        LicenseErrorCodes.IdentityRevoked =>
            "the license tied to this recovery phrase has been revoked — only support can lift it",
        LicenseErrorCodes.BetaLimitReached =>
            "the beta is full: every place has been taken — nothing was changed",
        LicenseErrorCodes.BetaBusy =>
            "another beta activation was being processed at the same moment — try again in a few seconds",
        LicenseErrorCodes.BetaCapUnavailable =>
            "the activation service is temporarily unavailable — try again later",
        LicenseErrorCodes.ChallengeExpired =>
            "the activation session expired — try again",
        LicenseErrorCodes.InvalidSignature =>
            "the server rejected the dongle's signature — unplug it, plug it back in and retry",
        _ when x.IsMalformedRequest =>
            "internal error composing the request — please report it, this is not your fault",
        _ => $"the license server refused: {x.Message}",
    };
}
