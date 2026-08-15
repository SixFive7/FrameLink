using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>app.http.local-origin</c> — the agent is serving the product app and the repair screen.
/// </summary>
/// <remarks>
/// <para>
/// The v2 replacement for guide 10 step 3's <c>framelink-spa.service</c> running
/// <c>busybox httpd</c> over a git checkout. Both the unit and its enablement disappear with the
/// embedded app (§2.1), and what is left is a single resource asserting that the port is answering
/// — because §2.7 requires the repair screen and the product to share <b>one</b> local origin,
/// which is why this is one server rather than two.
/// </para>
/// <para>
/// <b>Observe is a real request, not a look at an in-process flag.</b> The catalog asks for
/// <c>ss -tlnp</c> plus a <c>curl</c> that gets <c>200</c>, and the reason for the second half is
/// that a bound socket is not a working server: a listener whose accept loop has died still shows
/// in <c>ss</c>. So this opens a loopback connection and reads the status line, which is the same
/// evidence <c>curl</c> produces without depending on <c>curl</c> being installed. The in-process
/// flag is reported alongside it, because "not listening" and "listening and answering wrongly"
/// are different faults with different fixes.
/// </para>
/// <para>
/// The port is fixed by the catalog rather than settable, and has to be: the kiosk unit's
/// <c>ExecStart</c> URL and its <c>curl</c> readiness guard both name it, so a settable port would
/// be a value that has to agree with itself in three places.
/// </para>
/// </remarks>
public sealed class LocalOriginResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "app.http.local-origin";

    private readonly LocalOrigin _origin;

    /// <summary>Creates the resource.</summary>
    public LocalOriginResource(LocalOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _origin = origin;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is not serving the page it is supposed to show.";

    /// <inheritdoc/>
    public string WhyItMatters => "The browser has nothing to open, so the screen stays empty.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var expected = $"HTTP 200 from http://127.0.0.1:{_origin.RequestedPort}/";

        if (!_origin.IsListening)
        {
            return new ResourceObservation(
                false,
                expected,
                _origin.LastFailure is { Length: > 0 } failure
                    ? $"not listening on 127.0.0.1:{_origin.RequestedPort} — {failure}"
                    : $"not listening on 127.0.0.1:{_origin.RequestedPort}");
        }

        var status = await LoopbackProbe.StatusAsync(_origin.Port, "/", cancellationToken).ConfigureAwait(false);

        return new ResourceObservation(
            status == 200,
            expected,
            status is null
                ? $"listening on 127.0.0.1:{_origin.Port} but the connection failed"
                : $"HTTP {status} from http://127.0.0.1:{_origin.Port}/");
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var started = _origin.Start();

        return ValueTask.FromResult(new ResourceAction(
            $"serve the embedded app on http://127.0.0.1:{_origin.RequestedPort}/"
                + (started ? string.Empty : $" (refused: {_origin.LastFailure})"),
            "Starting the little web server inside this frame that hands the photos page to its own browser."));
    }
}

/// <summary>One loopback HTTP request, with no client library behind it.</summary>
/// <remarks>
/// <c>HttpClient</c> would work and would cost a connection pool, a handler chain and a DNS path
/// this never needs; the request here is nine ASCII bytes of head to a socket on this machine. It
/// exists so <see cref="LocalOriginResource"/> can produce the <c>curl</c> evidence the catalog
/// asks for on a frame where <c>curl</c> may not be installed — the kiosk unit's readiness guard
/// uses <c>curl</c>, but the guard runs in the user session where the browser's dependencies are.
/// </remarks>
public static class LoopbackProbe
{
    /// <summary>The HTTP status <paramref name="path"/> answers with, or null if it did not.</summary>
    public static async Task<int?> StatusAsync(int port, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);

            var stream = client.GetStream();
            var request = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            var line = Encoding.ASCII.GetString(buffer, 0, read);
            var fields = line.Split(' ');

            return fields.Length >= 2
                && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status)
                    ? status
                    : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>One of the five values guide 10's <c>config.json</c> used to hold.</summary>
public sealed record AppConfigSpec
{
    /// <summary>The catalog id.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The file inside the state store that records the issued value.</summary>
    public required string FileName { get; init; }

    /// <summary>The fleet setting that supplies it (§3.4).</summary>
    public required string SettingKey { get; init; }

    /// <summary>The catalog default, or empty when only the Fleet Manager can name one.</summary>
    public string Fallback { get; init; } = string.Empty;

    /// <summary>What was detected, for a reader with no computer experience (§2.7 item 1).</summary>
    public required string Detected { get; init; }

    /// <summary>Why it matters, in one short sentence (§2.7 item 2).</summary>
    public required string WhyItMatters { get; init; }

    /// <summary>Plain-language gloss on the change being made (§2.7 item 3).</summary>
    public required string Gloss { get; init; }

    /// <summary>Ids that must be in sync first.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [AdoptionResource.ResourceName];

    /// <summary>Whether the value must never appear in a delta, a log or on a screen.</summary>
    public bool Secret { get; init; }

    /// <summary>What the running app reported for this value, if it has reported at all.</summary>
    public Func<AppReport, string?>? ReportedBy { get; init; }
}

/// <summary>
/// <c>app.config.*</c> — the five values the product app runs on, held by the agent.
/// </summary>
/// <remarks>
/// <para>
/// Guide 10 step 2's <c>config.json</c> is superseded: the file is gone, and the five fields
/// survive as five resources whose values the Fleet Manager supplies and the agent keeps under
/// <c>/var/lib/fl-agent</c>. What the app reads is <c>/config.json</c> on the local origin, which
/// the agent renders from these records — so there is one copy of each value on the frame, and it
/// is the copy the reconciler owns.
/// </para>
/// <para>
/// <b>Observe compares two different things, and only one of them can fail the resource.</b> The
/// recorded value against the issued value is the resource: that is what drifts, and that is what
/// an Act can fix. The <i>app's own report</i> over the local channel is the cross-check the
/// catalog asks for, and it is treated asymmetrically on purpose — a report that <i>disagrees</i>
/// is drift, because the page is demonstrably running on a value nothing issued, while <i>no
/// report at all</i> is not, because a browser that has not started yet says nothing about
/// whether the value is right. Failing on silence would make every one of these five resources
/// unfixable on a frame whose browser is down, which is precisely when they need to be applied.
/// </para>
/// <para>
/// <b>A value the Fleet Manager has not issued is not drift either.</b> §3.3 gives a pending
/// device nothing, and these resources are blocked behind <c>agent.adoption</c> for that case; but
/// an adopted fleet that has simply never set <c>call.livekitUrl</c> leaves nothing to converge
/// on, and inventing a value would be worse than leaving it alone. That branch reports in sync
/// with an observed value naming the omission, exactly as <c>identity.hostname</c> does for a
/// frame nobody has named.
/// </para>
/// </remarks>
public sealed class AppConfigResource : IResource
{
    private readonly IStateStore _store;
    private readonly FleetValues _values;
    private readonly LocalChannel _channel;
    private readonly IAgentClock _clock;
    private readonly AppConfigSpec _spec;

    /// <summary>Creates the resource for <paramref name="spec"/>.</summary>
    public AppConfigResource(
        IStateStore store,
        FleetValues values,
        LocalChannel channel,
        IAgentClock clock,
        AppConfigSpec spec)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(spec);

        _store = store;
        _values = values;
        _channel = channel;
        _clock = clock;
        _spec = spec;
    }

    /// <inheritdoc/>
    public string Name => _spec.ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => _spec.DependsOn;

    /// <inheritdoc/>
    public string Detected => _spec.Detected;

    /// <inheritdoc/>
    public string WhyItMatters => _spec.WhyItMatters;

    /// <summary>The value the Fleet Manager has issued, or empty when it has issued none.</summary>
    public string Desired => _values.Get(_spec.SettingKey, _spec.Fallback).Trim();

    /// <summary>The value this frame has recorded, or empty.</summary>
    public string Recorded => _store.ReadText(_spec.FileName)?.Trim() ?? string.Empty;

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = Desired;
        var recorded = Recorded;

        if (desired.Length == 0)
        {
            return ValueTask.FromResult(new ResourceObservation(
                true,
                $"no {_spec.SettingKey} issued by the Fleet Manager",
                "nothing issued, so nothing to converge on"));
        }

        if (!string.Equals(recorded, desired, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                Describe(desired),
                recorded.Length == 0 ? "nothing recorded on this frame" : Describe(recorded)));
        }

        if (_spec.Secret && JwtExpiry.HasExpired(recorded, _clock.UtcNow))
        {
            // The July-23 failure, made visible before it bites. The Act cannot repair this —
            // rewriting an expired token produces an expired token — so the resource walks §2.5's
            // ladder and reaches an operator, which is the whole point: v1's version of this was a
            // frame that went from working to degrading on every boot with nothing to say why.
            return ValueTask.FromResult(new ResourceObservation(
                false,
                "a token that has not expired",
                $"the recorded token expired on {JwtExpiry.Of(recorded)?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
        }

        if (_spec.ReportedBy is { } reported
            && _channel.LastReport is { } report
            && reported(report) is { Length: > 0 } inUse
            && !string.Equals(inUse, desired, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                Describe(desired),
                $"the page is running on {Describe(inUse)}"));
        }

        return ValueTask.FromResult(new ResourceObservation(true, Describe(desired), Describe(recorded)));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = Desired;

        if (_spec.Secret)
        {
            _store.WriteSecretAtomic(_spec.FileName, Encoding.UTF8.GetBytes(desired));
        }
        else
        {
            _store.WriteText(_spec.FileName, desired);
        }

        return ValueTask.FromResult(new ResourceAction(
            $"record {_spec.SettingKey} = {Describe(desired)} in {_store.PathOf(_spec.FileName)}",
            _spec.Gloss));
    }

    /// <summary>
    /// The value as it may be written down.
    /// </summary>
    /// <remarks>
    /// §2.3 marks the LiveKit token <b>secret</b>, and a delta travels to the journal, the screen
    /// and the Fleet Manager's device history. So a secret is described by what can be checked
    /// about it — that it is there, and when it stops being valid — and never by its value. The
    /// expiry is the half that matters: it is the direct descendant of the July-23 post-mortem,
    /// where a token that silently aged out took the frame from working to degrading on every boot
    /// with nothing on screen to say why.
    /// </remarks>
    private string Describe(string value) => _spec.Secret
        ? $"a token, {JwtExpiry.Describe(value)}"
        : value;
}

/// <summary>Reads a JWT's expiry without verifying anything about it.</summary>
/// <remarks>
/// <b>Deliberately not a validation.</b> The agent is not the party that verifies this token — the
/// LiveKit server is, and it holds the secret. What the agent needs is the one claim that turns a
/// silent future failure into a visible present one, and reading it costs a base64 decode. Anything
/// that cannot be parsed is reported as such rather than as valid, which is the safe direction.
/// </remarks>
public static class JwtExpiry
{
    /// <summary>The <c>exp</c> claim, or null if there is not one to read.</summary>
    public static DateTimeOffset? Of(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(decoded);
            return document.RootElement.TryGetProperty("exp", out var expiry) && expiry.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A phrase describing the token's standing, never its value.</summary>
    public static string Describe(string token)
    {
        if (token.Length == 0)
        {
            return "absent";
        }

        return Of(token) is { } expiry
            ? "expiring " + expiry.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "with no readable expiry";
    }

    /// <summary>Whether the token has already expired at <paramref name="now"/>.</summary>
    public static bool HasExpired(string token, DateTimeOffset now) =>
        token.Length > 0 && Of(token) is { } expiry && expiry <= now;
}

/// <summary>The five <c>app.config.*</c> resources, in catalog order.</summary>
public static class AppConfigCatalog
{
    /// <summary>The Immich Kiosk display parameters guide 10 fixed, minus the album and duration.</summary>
    /// <remarks>
    /// <c>use_offline_mode=true</c> is present here and <b>absent from the v1 running frame</b>:
    /// the inventory's <c>APP_CONFIG</c> lacks it while <c>app/config.example.json</c> carries it.
    /// The catalog calls that "a live drift in the parity reference, worth resolving before parity
    /// is declared", and it is resolved in the example's favour — the parameter is the serve half
    /// of the offline pair, and a frame that keeps showing photos through an Immich outage is the
    /// behaviour the pair exists for.
    /// </remarks>
    public const string SlideshowBase =
        "http://127.0.0.1:3000/?disable_ui=true&hide_cursor=true&disable_navigation=true&frameless=true"
        + "&image_fit=cover&transition=fade&background_blur=false&show_more_info=false&use_offline_mode=true";

    /// <summary>Fleet setting carrying the seconds each photo is shown.</summary>
    public const string IntervalSettingKey = "slideshow.interval";

    /// <summary>Fleet setting carrying the album to show.</summary>
    public const string AlbumSettingKey = "slideshow.album";

    /// <summary>Guide 10's measured value.</summary>
    public const string DefaultInterval = "30";

    /// <summary>The specs, in the order the catalog lists them.</summary>
    public static IReadOnlyList<AppConfigSpec> Specs { get; } =
    [
        new AppConfigSpec
        {
            ResourceName = "app.config.identity",
            FileName = "app.identity",
            SettingKey = "call.identity",
            Detected = "This frame does not know what to call itself in a video call.",
            WhyItMatters = "Two frames with the same name are treated as one, and calls go to the wrong place.",
            Gloss = "Giving this frame its own name for video calls.",
            ReportedBy = report => report.Identity,
        },
        new AppConfigSpec
        {
            ResourceName = "app.config.room",
            FileName = "app.room",
            SettingKey = "call.room",
            Fallback = "family",
            Detected = "This frame does not know which video call to join.",
            WhyItMatters = "Without it the frame never reaches the people it is meant to reach.",
            Gloss = "Telling this frame which family video call it belongs to.",
            ReportedBy = report => report.Room,
        },
        new AppConfigSpec
        {
            ResourceName = "app.config.livekit-url",
            FileName = "app.livekit-url",
            SettingKey = "call.livekitUrl",
            Detected = "This frame does not know where to find the video call service.",
            WhyItMatters = "The photos keep working and the calls never connect, with nothing on screen to say why.",
            Gloss = "Telling this frame where its video calls are handled.",
            ReportedBy = report => report.LivekitUrl,
        },
        new AppConfigSpec
        {
            ResourceName = "app.config.livekit-token",
            FileName = "app.livekit-token",
            SettingKey = "call.token",
            Secret = true,
            DependsOn =
            [
                "app.config.identity",
                "app.config.room",
                "app.config.livekit-url",
            ],
            Detected = "This frame's pass for joining video calls is missing or out of date.",
            WhyItMatters = "Without a valid pass the frame is turned away from every call it tries to join.",
            Gloss = "Storing this frame's pass for joining video calls, kept where only the frame itself can read it.",
        },
        new AppConfigSpec
        {
            ResourceName = "app.config.immich-kiosk-url",
            FileName = "app.immich-kiosk-url",
            SettingKey = "slideshow.url",

            // The catalog gives this resource one dependency, `kiosk.listen-address`, and no
            // adoption edge — the base URL is fixed and `slideshow.interval` has a catalog default
            // that is correct before adoption, which is exactly the condition the catalog's
            // dependsOn rule uses to decide the question. `kiosk.listen-address` belongs to the
            // Immich Kiosk block and is not compiled in yet; the DAG refuses a dependency on
            // something that does not exist — deliberately, since it could never be satisfied — so
            // that edge arrives with that block rather than being approximated by a different one.
            DependsOn = [],
            Detected = "This frame does not know where to find its photos.",
            WhyItMatters = "The screen shows a spinner instead of the slideshow.",
            Gloss = "Telling this frame where its photo slideshow comes from.",
            ReportedBy = report => report.ImmichKioskUrl,
        },
    ];

    /// <summary>The slideshow URL for the current settings.</summary>
    /// <remarks>
    /// Composed rather than stored whole, so <c>slideshow.interval</c> and <c>slideshow.album</c>
    /// stay ordinary fleet settings an operator can move without anybody hand-editing a query
    /// string. The base is fixed by the catalog because every one of its parameters is a display
    /// decision guide 9 and guide 10 already made.
    /// </remarks>
    public static string SlideshowUrl(FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var url = SlideshowBase + "&duration=" + values.Get(IntervalSettingKey, DefaultInterval);

        return values.Find(AlbumSettingKey) is { Length: > 0 } album
            ? url + "&album=" + Uri.EscapeDataString(album)
            : url;
    }

    /// <summary>Builds the five resources, in catalog order.</summary>
    public static IReadOnlyList<IResource> Build(
        IStateStore store,
        FleetValues values,
        LocalChannel channel,
        IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(clock);

        var resources = new List<IResource>(Specs.Count);
        foreach (var spec in Specs)
        {
            // The slideshow URL is the one value the catalog composes rather than takes whole, so
            // its "setting" is a computed view over two real ones.
            var source = string.Equals(spec.SettingKey, "slideshow.url", StringComparison.Ordinal)
                ? new FleetValues(key => string.Equals(key, "slideshow.url", StringComparison.Ordinal)
                    ? SlideshowUrl(values)
                    : values.Find(key))
                : values;

            resources.Add(new AppConfigResource(store, source, channel, clock, spec));
        }

        return resources;
    }

    /// <summary>
    /// The document the app fetches, or null when this frame has not been issued enough to run.
    /// </summary>
    /// <remarks>
    /// Built from what the agent has <i>recorded</i>, not from what the Fleet Manager most
    /// recently said, and that ordering is §2.6: the recorded values are the ones the reconciler
    /// has verified, so a settings push that has not been through a reconcile pass does not reach
    /// the page ahead of the resource that owns it. It is also what keeps the page working through
    /// an outage, since a frame that was green when contact dropped keeps its values.
    /// </remarks>
    public static AppConfigDocument? Issued(IStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var identity = store.ReadText("app.identity")?.Trim() ?? string.Empty;
        var room = store.ReadText("app.room")?.Trim() ?? string.Empty;
        var livekitUrl = store.ReadText("app.livekit-url")?.Trim() ?? string.Empty;
        var slideshow = store.ReadText("app.immich-kiosk-url")?.Trim() ?? string.Empty;
        var token = store.ReadText("app.livekit-token")?.Trim() ?? string.Empty;

        if (identity.Length == 0 && room.Length == 0 && slideshow.Length == 0)
        {
            return null;
        }

        return new AppConfigDocument
        {
            Identity = identity,
            Room = room,
            LivekitUrl = livekitUrl,
            ImmichKioskUrl = slideshow,
            Token = token,
        };
    }
}
