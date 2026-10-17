using CryptinSDK.Licensing;

namespace CryptinPersonal;

/// <summary>
/// Which licence server this build of the app talks to.
///
/// The choice cannot be made at runtime from the dongle. Nothing on the wire
/// tells a <c>-dev</c> chip from a <c>-prod</c> one at activation: the firmware
/// never exposes <c>CRYPTIN_ENV_PROD</c> over HID, and K_attestation has the
/// same shape either way. Nor is the URL in the firmware at all — the dongle
/// never speaks to the licence server, it only verifies the JWT the host hands
/// it against the RSA key compiled into <c>keystore.h</c>. So the endpoint is a
/// property of the <b>host</b> build, and this is where it is decided.
///
/// <see cref="LicenseClient.DefaultBaseUrl"/> is deliberately left alone: the
/// SDK is Apache-2.0 and consumed by third parties, so its default has to stay
/// production for everyone. The divergence lives in the app and only in the app.
///
/// Built with <c>-c RedTeam</c> this points at the clone, so a test activation
/// writes a row there instead of into production. Every other configuration —
/// Debug included — points at production: a default that fell the other way
/// would mean a forgotten flag silently *stops* real activations from working,
/// which is discovered by a customer rather than by us.
/// </summary>
internal static class LicenseEndpoint
{
#if CRYPTIN_REDTEAM
    /// <summary>The red-team clone. Never the production database.</summary>
    internal static readonly Uri BaseUrl = new("https://redteam-license.lake8.dev");

    /// <summary>
    /// Whether this is the red-team build.
    ///
    /// <c>static readonly</c> and not <c>const</c>, for the same reason as
    /// <see cref="ActivationForm.BetaActivation"/>: a const makes the other
    /// branch unreachable and the build then carries no warning about it.
    /// </summary>
    internal static readonly bool IsRedTeam = true;
#else
    /// <summary>Production, via the SDK default — one source for the real URL.</summary>
    internal static readonly Uri BaseUrl = LicenseClient.DefaultBaseUrl;

    /// <summary>See the RedTeam branch above for why this is not a const.</summary>
    internal static readonly bool IsRedTeam = false;
#endif

    /// <summary>
    /// A window title with the endpoint stamped on it when it is not production.
    ///
    /// The price of a separate binary is that the two are indistinguishable once
    /// running, and a red-team result mistaken for a production one is worse than
    /// no test at all. This is what tells them apart at a glance. In every
    /// shipping configuration it returns the title unchanged.
    /// </summary>
    internal static string Stamp(string title) =>
        IsRedTeam ? $"{title}  ⚠ RED TEAM — {BaseUrl.Host}" : title;
}
