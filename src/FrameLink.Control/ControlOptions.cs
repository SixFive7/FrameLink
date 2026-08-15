using System.Globalization;

namespace FrameLink.Control;

/// <summary>
/// Everything the Fleet Manager needs that is not code: paths, deadlines and the abuse
/// budgets of the open registration path.
/// </summary>
/// <remarks>
/// Read from environment variables rather than a config file, because §3.1 ships one
/// container with one volume — the operator authors a Compose file, not an appsettings
/// tree. Every value has a working default, so the only variable an operator must set is
/// <see cref="Authentication.OperatorCredential.EnvironmentVariable"/>, and even that one is
/// optional at start-up (§3.2).
/// </remarks>
public sealed record ControlOptions
{
    /// <summary>Directory holding the SQLite database. The volume-mapped path in Docker.</summary>
    public string DataDirectory { get; init; } = "/var/lib/fl-control";

    /// <summary>Where the served agent binaries live. A concurrent workstream fills it.</summary>
    public string ReleaseDirectory { get; init; } = "";

    /// <summary>
    /// Proxy addresses whose <c>X-Forwarded-For</c> is believed.
    /// </summary>
    /// <remarks>
    /// Empty by default, and that default is a security decision rather than laziness: the
    /// per-IP rate limiter of §3.3 is the only thing standing between an internet-exposed
    /// registration path and unbounded noise, and trusting an unauthenticated header would
    /// let one attacker present a fresh source address per request. Behind Traefik (§3.8)
    /// the operator names the proxy explicitly.
    /// </remarks>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>Interval between application-level pings on an adopted device's socket.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long a socket may go without inbound traffic before it is torn down.
    /// </summary>
    /// <remarks>
    /// Two missed pings. §3.5: a pulled plug leaves a half-open TCP connection that never
    /// closes, so "presence is the socket" is only true if something actively proves the
    /// socket is still there.
    /// </remarks>
    public TimeSpan PongDeadline { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the handshake may take before the connection is abandoned.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Largest single wire frame accepted, so a hostile peer cannot exhaust memory.</summary>
    public int MaxFrameBytes { get; init; } = 256 * 1024;

    /// <summary>Hard cap on un-adopted device rows (§3.3).</summary>
    /// <remarks>
    /// Enforced by evicting the least recently seen pending rows rather than by refusing the
    /// newcomer. Refusal would mean a jammed queue answers a genuine frame with silence, and
    /// §2.6 is explicit that rejection is an answer and silence is not. An evicted noise row
    /// costs an attacker another connection; an evicted genuine row reappears on that frame's
    /// next reconnect.
    /// </remarks>
    public int PendingDeviceCap { get; init; } = 200;

    /// <summary>Age after which an un-adopted row is deleted (§3.3 auto-expiry).</summary>
    /// <remarks>Measured from last contact, so any frame that is actually running is never
    /// expired — it refreshes the timestamp on every reconnect.</remarks>
    public TimeSpan PendingDeviceTtl { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How often expired pending rows and old events are swept.</summary>
    public TimeSpan ReaperInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long device events and reconciliation history are kept (§3.5, decision 21).
    /// </summary>
    /// <remarks>
    /// One month, and never any photo or call content. Long enough that a fault which showed up
    /// as drift three weeks ago is still explainable; short enough that a single-volume SQLite
    /// file on an operator's server stays a file rather than a database problem.
    /// </remarks>
    public TimeSpan TelemetryRetention { get; init; } = TimeSpan.FromDays(31);

    /// <summary>Handshake attempts allowed from one address per <see cref="RateLimitWindow"/>.</summary>
    public int RateLimitAttempts { get; init; } = 20;

    /// <summary>Width of the per-address rate limiting window (§3.3).</summary>
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Cap on how many client addresses the limiter tracks at once.
    /// </summary>
    /// <remarks>
    /// Without it the limiter is itself the memory-exhaustion vector it exists to prevent.
    /// </remarks>
    public int MaxTrackedAddresses { get; init; } = 20_000;

    /// <summary>Lifetime of an operator's browser session.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Full path of the SQLite database file.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "framelink.db");

    /// <summary>Builds options from the process environment, falling back to the defaults above.</summary>
    public static ControlOptions FromEnvironment()
    {
        var defaults = new ControlOptions();
        return new ControlOptions
        {
            DataDirectory = Read("FRAMELINK_DATA_DIR") ?? defaults.DataDirectory,
            ReleaseDirectory = Read("FRAMELINK_RELEASE_DIR") ?? LocateReleaseDirectory(),
            TrustedProxies = (Read("FRAMELINK_TRUSTED_PROXIES") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            PendingDeviceCap = ReadInt("FRAMELINK_PENDING_CAP", defaults.PendingDeviceCap),
            PendingDeviceTtl = ReadDays("FRAMELINK_PENDING_TTL_DAYS", defaults.PendingDeviceTtl),
            RateLimitAttempts = ReadInt("FRAMELINK_RATE_LIMIT_ATTEMPTS", defaults.RateLimitAttempts),
        };

        static string? Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static int ReadInt(string name, int fallback) =>
            int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : fallback;

        static TimeSpan ReadDays(string name, TimeSpan fallback) =>
            int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? TimeSpan.FromDays(parsed)
                : fallback;
    }

    /// <summary>
    /// Finds the directory the agent binaries are published into.
    /// </summary>
    /// <remarks>
    /// In the container they sit beside the executable in <c>agent/</c>. The walk up towards a
    /// repository <c>build/out</c> is a development convenience only, so that <c>dotnet run</c>
    /// from a source tree serves whatever the agent workstream last built without anyone
    /// having to set a variable.
    /// </remarks>
    private static string LocateReleaseDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "agent");
        if (Directory.Exists(beside))
        {
            return beside;
        }

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && probe is not null; depth++, probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "build", "out");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return beside;
    }
}
