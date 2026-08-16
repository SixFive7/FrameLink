using System.Globalization;

namespace FrameLink.Control.LiveKit;

/// <summary>Where the call server this Fleet Manager mints tokens for actually is.</summary>
public enum LiveKitMode
{
    /// <summary>The Fleet Manager fetches, configures and supervises its own (§3.7).</summary>
    Bundled,

    /// <summary>An operator's existing LiveKit, pointed at by environment (§3.7's escape hatch).</summary>
    External,

    /// <summary>Switched off. Nothing is supervised and no token is minted.</summary>
    Disabled,
}

/// <summary>
/// Everything §3.7 needs that is not code, read from the environment.
/// </summary>
/// <remarks>
/// <para>
/// Its own record rather than more fields on <c>ControlOptions</c>, because the escape hatch is
/// what makes this a group rather than a scattering: an operator who already runs LiveKit sets
/// three variables and the entire bundled half — the download, the config file, the child
/// process, the secret — stops existing. That is one decision, and it reads as one here.
/// </para>
/// <para>
/// <b>Every value has a working default except one.</b> §3.2's shape is that an operator sets a
/// password and nothing else, and this holds to it: ports, the token lifetime and the renewal
/// threshold are all defaulted, and the key and secret are <i>generated</i> rather than
/// configured (§3.2: "LiveKit's key and secret are generated automatically"). The exception is
/// <see cref="PublicUrl"/>, and it cannot be defaulted honestly — see its own remarks.
/// </para>
/// </remarks>
public sealed record LiveKitOptions
{
    /// <summary>Whether the bundled server runs at all. <c>FRAMELINK_LIVEKIT_ENABLED</c>.</summary>
    public const string EnabledVariable = "FRAMELINK_LIVEKIT_ENABLED";

    /// <summary>The address frames dial. <c>FRAMELINK_LIVEKIT_PUBLIC_URL</c>.</summary>
    public const string PublicUrlVariable = "FRAMELINK_LIVEKIT_PUBLIC_URL";

    /// <summary>An existing LiveKit's address. <c>FRAMELINK_LIVEKIT_URL</c>.</summary>
    public const string ExternalUrlVariable = "FRAMELINK_LIVEKIT_URL";

    /// <summary>That server's API key. <c>FRAMELINK_LIVEKIT_API_KEY</c>.</summary>
    public const string ExternalKeyVariable = "FRAMELINK_LIVEKIT_API_KEY";

    /// <summary>That server's API secret. <c>FRAMELINK_LIVEKIT_API_SECRET</c>.</summary>
    public const string ExternalSecretVariable = "FRAMELINK_LIVEKIT_API_SECRET";

    /// <summary>Directory holding the binary, its configuration and its working files.</summary>
    /// <remarks>
    /// Inside the data directory §3.1 already asks for, so a self-hoster who does nothing gets a
    /// working setup on the one volume they already mapped. Unlike §3.9's images this is small —
    /// a 50 MB executable and a one-kilobyte YAML file — so it does not get a variable of its own.
    /// </remarks>
    public required string Directory { get; init; }

    /// <summary>Which shape of deployment this is.</summary>
    public required LiveKitMode Mode { get; init; }

    /// <summary>
    /// The URL a frame dials, which is not derivable and therefore not defaulted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container knows the addresses it is bound to and nothing whatever about the address a
    /// frame on the other side of a bridge network, a published port and possibly a reverse proxy
    /// will reach it on. §3.7 makes that split explicit — signalling may ride Traefik as a
    /// WebSocket over TLS while media cannot — so the signalling URL is a deployment decision
    /// with at least two correct answers (<c>ws://10.20.30.250:7880</c> on a LAN,
    /// <c>wss://framelink.example.org</c> behind a proxy) and no way to tell which.
    /// </para>
    /// <para>
    /// So it is asked for rather than guessed, and an unset value is handled the §3.2 way: the
    /// server still starts, LiveKit is still supervised, and the status route names the variable.
    /// What is <i>not</i> done is issuing a frame an invented address — <c>app.config.livekit-url</c>
    /// treats an unissued value as "nothing to converge on", so the frame stays green and silent
    /// about calls instead of retrying a URL nobody chose.
    /// </para>
    /// </remarks>
    public string PublicUrl { get; init; } = string.Empty;

    /// <summary>An existing LiveKit's address, when <see cref="Mode"/> is external.</summary>
    public string ExternalUrl { get; init; } = string.Empty;

    /// <summary>That server's API key.</summary>
    public string ExternalKey { get; init; } = string.Empty;

    /// <summary>That server's API secret.</summary>
    public string ExternalSecret { get; init; } = string.Empty;

    /// <summary>The HTTP and WebSocket signalling port. Guide 7's value.</summary>
    public int SignalPort { get; init; } = 7880;

    /// <summary>The TCP media fallback port, for a network that blocks UDP. Guide 7's value.</summary>
    public int TcpMediaPort { get; init; } = 7881;

    /// <summary>First UDP port of the ICE range where call media actually flows.</summary>
    public int UdpPortStart { get; init; } = 50_000;

    /// <summary>
    /// Last UDP port of that range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 50000–50059, sixty ports. LiveKit takes a port from this range per participant connection
    /// and a range that runs out presents as calls that connect for some participants and not
    /// others — the least diagnosable failure in the whole call path — so the number is chosen
    /// with headroom rather than trimmed to fit: guide 13 contemplates several frames per
    /// household and sixty simultaneous participant connections is an order of magnitude past
    /// what a household reaches.
    /// </para>
    /// <para>
    /// <b>Sixty rather than two hundred because a host has to be able to publish them.</b> The
    /// range is published one-to-one — remap it and calls connect for nobody with no error
    /// anywhere — and on Windows the whole of it sits inside the ephemeral range the operating
    /// system lends out (49152 upward, 16384 wide), so any port in it can already be held by
    /// another program when the stack starts. The fix is a persistent reservation, and a
    /// reservation is a fixed, host-wide grant: this workstation's is `50000-50059`, applied at
    /// boot, and Compose refuses to start the whole stack when a single mapping cannot bind. So
    /// the default matches what a host can realistically hold rather than what a call path could
    /// theoretically want, and <c>FRAMELINK_LIVEKIT_UDP_END</c> widens it for a deployment whose
    /// reservation is wider.
    /// </para>
    /// </remarks>
    public int UdpPortEnd { get; init; } = 50_059;

    /// <summary>
    /// How long a minted call token is valid for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A year, and the number is the whole argument of §3.7 against v1's ten. v1's token was
    /// long-lived because nothing could renew it: minting one meant a person at a workstation
    /// with the API secret, so the only defence against expiry was outliving the hardware. The
    /// Fleet Manager holds the secret and speaks to every frame, so a token is now an artifact it
    /// re-mints whenever it likes — which turns the lifetime from a bet on hardware longevity
    /// into an ordinary safety margin.
    /// </para>
    /// <para>
    /// It is a year and not a month because §1.2 principle 2 requires a frame to keep working
    /// with the server unreachable. Renewal happens at <see cref="RenewalFraction"/> of the
    /// remaining life, so a frame in contact carries a token minted within the last four months
    /// and a Fleet Manager that dies leaves the fleet eight months of calling — long enough that
    /// the frames are not what fails first.
    /// </para>
    /// </remarks>
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromDays(365);

    /// <summary>
    /// The fraction of <see cref="TokenLifetime"/> remaining at which a token is re-minted.
    /// </summary>
    /// <remarks>
    /// A third. Renewal is attempted on every reconnect, so the practical effect is that a frame
    /// which has been in contact at any point in the last eight months is carrying a fresh token.
    /// Anything larger renews constantly for no benefit; anything smaller narrows the window in
    /// which an intermittently connected frame gets its chance.
    /// </remarks>
    public double RenewalFraction { get; init; } = 1.0 / 3.0;

    /// <summary>Where the executable lives.</summary>
    public string BinaryPath => Path.Combine(Directory, LiveKitReleasePin.Current.BinaryMemberName);

    /// <summary>Where the generated configuration lives.</summary>
    public string ConfigPath => Path.Combine(Directory, "livekit.yaml");

    /// <summary>Whether a token can be minted at all — a key and secret exist somewhere.</summary>
    public bool IsCallingConfigured => Mode is not LiveKitMode.Disabled;

    /// <summary>How much life a token must have left before it stops being renewed.</summary>
    public TimeSpan RenewalThreshold =>
        TimeSpan.FromTicks((long)(TokenLifetime.Ticks * Math.Clamp(RenewalFraction, 0.0, 1.0)));

    /// <summary>Builds the options from the process environment and a data directory.</summary>
    /// <param name="dataDirectory">The Fleet Manager's volume-mapped data directory.</param>
    public static LiveKitOptions FromEnvironment(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var defaults = new LiveKitOptions { Directory = "", Mode = LiveKitMode.Bundled };

        var externalUrl = Read(ExternalUrlVariable) ?? string.Empty;
        var enabled = ReadBool(EnabledVariable, fallback: true);

        // The escape hatch wins over the bundled path, and it wins by being set rather than by a
        // mode switch nobody would remember to flip. An operator who names an existing LiveKit
        // has said everything needed; making them also say "and do not run your own" would be a
        // second variable whose only job is to agree with the first.
        var mode = !enabled
            ? LiveKitMode.Disabled
            : externalUrl.Length > 0
                ? LiveKitMode.External
                : LiveKitMode.Bundled;

        return new LiveKitOptions
        {
            Directory = Path.Combine(dataDirectory, "livekit"),
            Mode = mode,
            PublicUrl = Read(PublicUrlVariable) ?? string.Empty,
            ExternalUrl = externalUrl,
            ExternalKey = Read(ExternalKeyVariable) ?? string.Empty,
            ExternalSecret = Read(ExternalSecretVariable) ?? string.Empty,
            SignalPort = ReadInt("FRAMELINK_LIVEKIT_PORT", defaults.SignalPort),
            TcpMediaPort = ReadInt("FRAMELINK_LIVEKIT_TCP_PORT", defaults.TcpMediaPort),
            UdpPortStart = ReadInt("FRAMELINK_LIVEKIT_UDP_START", defaults.UdpPortStart),
            UdpPortEnd = ReadInt("FRAMELINK_LIVEKIT_UDP_END", defaults.UdpPortEnd),
            TokenLifetime = ReadDays("FRAMELINK_LIVEKIT_TOKEN_DAYS", defaults.TokenLifetime),
        };

        static string? Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static bool ReadBool(string name, bool fallback) => Read(name) switch
        {
            null => fallback,
            var value when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
            var value when string.Equals(value, "0", StringComparison.Ordinal) => false,
            var value when string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) => false,
            _ => true,
        };

        static int ReadInt(string name, int fallback) =>
            int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed is > 0 and < 65_536
                    ? parsed
                    : fallback;

        static TimeSpan ReadDays(string name, TimeSpan fallback) =>
            int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                    ? TimeSpan.FromDays(parsed)
                    : fallback;
    }

    /// <summary>Everything structurally wrong with these options, in plain sentences.</summary>
    /// <remarks>
    /// Rendered on the status route rather than thrown. §3.2's rule is that an unconfigured
    /// instance explains itself instead of failing silently, and a Fleet Manager whose calling
    /// half is misconfigured is exactly that case: photos, adoption, reconciliation and updates
    /// all still work, and refusing to start would take them down to complain about calls.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Mode is LiveKitMode.Disabled)
        {
            return problems;
        }

        if (Mode is LiveKitMode.External)
        {
            if (ExternalKey.Length == 0 || ExternalSecret.Length == 0)
            {
                problems.Add(
                    $"{ExternalUrlVariable} names an existing LiveKit server, but "
                    + $"{ExternalKeyVariable} and {ExternalSecretVariable} are not both set, so no "
                    + "call token can be signed for it.");
            }
        }
        else
        {
            if (PublicUrl.Length == 0)
            {
                problems.Add(
                    $"{PublicUrlVariable} is not set, so this Fleet Manager does not know which "
                    + "address frames should dial for calls. Set it to the LiveKit signalling URL "
                    + "frames can reach — for example ws://<this-server>:" + SignalPort.ToString(CultureInfo.InvariantCulture)
                    + " on a home network, or wss://<your-domain> behind a reverse proxy.");
            }

            if (UdpPortEnd < UdpPortStart)
            {
                problems.Add("The UDP media port range ends before it starts.");
            }

            if (TcpMediaPort == SignalPort)
            {
                problems.Add("The signalling port and the TCP media fallback port are the same port.");
            }
        }

        return problems;
    }

    /// <summary>The address frames are issued, or empty when none is known.</summary>
    public string EffectiveUrl => Mode switch
    {
        LiveKitMode.External => ExternalUrl,
        LiveKitMode.Bundled => PublicUrl,
        _ => string.Empty,
    };
}
