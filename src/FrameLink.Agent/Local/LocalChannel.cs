using System.Text.Json.Serialization;

namespace FrameLink.Agent.Local;

/// <summary>What the page sends the agent over the local channel.</summary>
/// <remarks>
/// Deliberately tiny. This is not a second control protocol — §4.1's four channels are the wire
/// contract and they run over the socket to the Fleet Manager. What travels here is the two facts
/// nothing else on the frame can know: that the page is alive, and what configuration it is
/// actually using.
/// </remarks>
public sealed record PageMessage
{
    /// <summary>The page has loaded and is rendering.</summary>
    public const string KindHello = "hello";

    /// <summary>The page is still rendering. The liveness heartbeat of §2.10.</summary>
    public const string KindAlive = "alive";

    /// <summary>A call has ended — the event trigger for the camera recycle (§2.10).</summary>
    public const string KindCallEnded = "call-ended";

    /// <summary>Somebody pressed "Reboot now" on the repair screen (§2.7 item 4).</summary>
    public const string KindRebootNow = "reboot-now";

    /// <summary>
    /// Somebody pressed "Try again" on the repair screen — §2.5 rung 5, decision 72.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame's own half of §2.5 rung 3's retry, for the person standing in front of it rather
    /// than the operator holding a Fleet Manager. It carries no arguments on purpose: the press
    /// means <i>everything that gave up, try again</i>, which is the device-wide form
    /// <see cref="Protocol.RetryRequest"/> already spells as a null resource. Naming a resource
    /// would require the page to know which one, and the person pressing it does not care.
    /// </para>
    /// <para>
    /// <b>It resets the budget through the same path the Fleet Manager's retry uses.</b> There is
    /// one reset in the agent with two callers, so a retry pressed at the frame and a retry pressed
    /// in a browser two hundred kilometres away cannot come to mean different things.
    /// </para>
    /// </remarks>
    public const string KindRetry = "retry";

    /// <summary>Which of the kinds above this is.</summary>
    public required string Kind { get; init; }

    /// <summary>The LiveKit identity the app is configured with.</summary>
    public string? Identity { get; init; }

    /// <summary>The room the app is configured with.</summary>
    public string? Room { get; init; }

    /// <summary>The LiveKit address the app is configured with.</summary>
    public string? LivekitUrl { get; init; }

    /// <summary>The slideshow URL the app is configured with.</summary>
    public string? ImmichKioskUrl { get; init; }

    /// <summary>Whether the app holds a token, never the token itself.</summary>
    /// <remarks>
    /// §2.3's <c>app.config.livekit-token</c> is marked <b>secret</b>. The agent already knows the
    /// token — it issued it — so the only thing the page can add is whether it received one, and
    /// sending the value back would put a credential in a message that is trivially logged.
    /// </remarks>
    public bool HasToken { get; init; }
}

/// <summary>What the agent pushes to the page — §2.7's repair screen, rendered in the browser.</summary>
public sealed record StageMessage
{
    /// <summary>Which rung of §2.6's ladder the device is on.</summary>
    public required string Condition { get; init; }

    /// <summary>Whether the product may run (§2.6).</summary>
    public required bool ProductRuns { get; init; }

    /// <summary>The ladder's headline, for a reader with no computer experience.</summary>
    public string? Headline { get; init; }

    /// <summary>The ladder's second line.</summary>
    public string? Detail { get; init; }

    /// <summary>§2.7 item 1 — what was detected.</summary>
    public string? Detected { get; init; }

    /// <summary>§2.7 item 2 — why it matters.</summary>
    public string? WhyItMatters { get; init; }

    /// <summary>§2.7 item 3 — the exact change being made.</summary>
    public string? Action { get; init; }

    /// <summary>§2.7 item 3 — its plain-language gloss.</summary>
    public string? ActionGloss { get; init; }

    /// <summary>The resource being worked on.</summary>
    public string? Resource { get; init; }

    /// <summary>§2.7 item 5 — attempt number.</summary>
    public int Attempt { get; init; }

    /// <summary>§2.7 item 5 — the budget it counts against.</summary>
    public int AttemptBudget { get; init; }

    /// <summary>§2.7 item 4 — seconds left before the verifying reboot, when one is running.</summary>
    public int? CountdownSeconds { get; init; }

    /// <summary>The short device id, for bench matching (§3.3).</summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// §2.7 item 5 — <c>item x attempt 1 of 3</c>, or null when nothing is in progress.
    /// </summary>
    /// <remarks>
    /// A composed sentence rather than the page assembling one from <see cref="Resource"/>,
    /// <see cref="Attempt"/> and <see cref="AttemptBudget"/>. Those three stay for compatibility
    /// with a page that predates this, but the wording is decided once, in
    /// <see cref="State.ReconcileVoice"/>, so the browser and the console cannot say different
    /// things about the same frame.
    /// </remarks>
    public string? ProgressLine { get; init; }

    /// <summary>
    /// §2.7 item 7 — <c>item z failed after 3 tries, expected a but got b</c>, or null.
    /// </summary>
    /// <remarks>
    /// <b>Non-null is the page's whole signal that the frame has stopped.</b> It is what turns the
    /// attempt counter and its animation off and the retry button on, so a page that renders this
    /// field cannot show a stopped frame as a working one.
    /// </remarks>
    public string? StoppedLine { get; init; }

    /// <summary>Whether anybody has been told yet (§2.7 item 7), or null.</summary>
    public string? EscalationLine { get; init; }

    /// <summary>§2.7 item 8 — who to contact, present whenever the frame has given up.</summary>
    public string? ContactLine { get; init; }

    /// <summary>
    /// §2.7 item 9 — whether "Try again" should be offered on this screen (decision 72).
    /// </summary>
    /// <remarks>
    /// True exactly when something has given up. A retry with a full budget already available
    /// would reset nothing and teach the person that the button does nothing, which is the same
    /// harm as a button that is not wired up.
    /// </remarks>
    public bool CanRetry { get; init; }

    /// <summary>
    /// §2.10's annotation, rendered only at fault level.
    /// </summary>
    /// <remarks>
    /// "Below fault level it is operator-facing only. At fault level it also renders on the frame
    /// as the small persistent overlay §2.6 gives <c>NoContact</c>." So this field is null for the
    /// ordinary case of a frame that restarted its browser once last night, and carries a sentence
    /// for a frame that is visibly blinking every ten minutes.
    /// </remarks>
    public string? SupervisionOverlay { get; init; }

    /// <summary>
    /// An instruction to the product, rather than anything about the device's condition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null on every ordinary frame. It carries <c>toggle</c> when the call button is pressed —
    /// v1's <c>{"cmd":"toggle"}</c>, which used to arrive over a WebSocket server the GPIO daemon
    /// ran on <c>127.0.0.1:8889</c>. The catalog retires that port outright ("an internal detail of
    /// the v1 split between daemon and SPA; with both inside one binary there is no port"), so the
    /// press rides the one local origin the page is already connected to.
    /// </para>
    /// <para>
    /// It travels on a full, current stage frame rather than in a message of its own so that a page
    /// which does not understand commands still renders the truth instead of a default condition.
    /// </para>
    /// </remarks>
    public string? Command { get; init; }
}

/// <summary>The configuration document the app fetches from the local origin (§2.1).</summary>
/// <remarks>
/// Field-for-field the five keys of v1's <c>app/config.json</c>, because the app that reads it is
/// unchanged. What moved is where the values come from: the catalog's "Guide 10 step 2's
/// <c>config.json</c> file — superseded by Fleet-Manager-supplied values held in
/// <c>/var/lib/fl-agent</c>; the five fields survive as the five <c>app.config.*</c> resources."
/// </remarks>
public sealed record AppConfigDocument
{
    /// <summary>The frame's LiveKit participant identity.</summary>
    public required string Identity { get; init; }

    /// <summary>The room every household device joins.</summary>
    public required string Room { get; init; }

    /// <summary>The LiveKit WebSocket address.</summary>
    public required string LivekitUrl { get; init; }

    /// <summary>The slideshow URL, with its full display query string.</summary>
    public required string ImmichKioskUrl { get; init; }

    /// <summary>The LiveKit access token. <b>Secret.</b></summary>
    [JsonPropertyName("token")]
    public required string Token { get; init; }
}

/// <summary>What the page last told the agent it is using.</summary>
/// <param name="At">When it said so.</param>
/// <param name="Identity">Its LiveKit identity.</param>
/// <param name="Room">Its room.</param>
/// <param name="LivekitUrl">Its LiveKit address.</param>
/// <param name="ImmichKioskUrl">Its slideshow URL.</param>
/// <param name="HasToken">Whether it holds a token.</param>
public readonly record struct AppReport(
    DateTimeOffset At,
    string? Identity,
    string? Room,
    string? LivekitUrl,
    string? ImmichKioskUrl,
    bool HasToken);

/// <summary>
/// <b>The local channel</b> — the page's only way of saying it is alive, and the agent's only way
/// of putting §2.7's narration into the browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why silence here is the honest liveness signal (§2.10).</b> An OOM-killed renderer leaves an
/// "Aw, Snap!" tab while systemd still reports <c>chromium-kiosk.service</c> <c>active</c>, so
/// <c>Restart=</c> never fires and every unit-level check says the browser is fine. The page is
/// gone; only the page can say so. Measured on v1: a SIGKILLed renderer healed in exactly 90 s
/// against this signal.
/// </para>
/// <para>
/// <b>No sockets in here.</b> The channel is state and fan-out; <see cref="LocalOrigin"/> owns the
/// transport and registers a send delegate per connected page. That split is what lets the
/// liveness rule, the fallback rule and the whole of supervision be asserted without a listening
/// port, and it is the same seam discipline every other Linux surface in the agent has.
/// </para>
/// <para>
/// v1's equivalent was a WebSocket server on <c>127.0.0.1:8889</c> inside the GPIO daemon. The
/// catalog retires the port outright — "an internal detail of the v1 split between daemon and SPA;
/// with both inside one binary there is no port" — so this rides the one local origin instead, and
/// the browser sees one scheme, host and port for the app, the repair screen and the channel.
/// </para>
/// </remarks>
public sealed class LocalChannel
{
    private readonly Lock _gate = new();
    private readonly List<Peer> _peers = [];
    private long _checkIns;

    /// <summary>Raised when the page reports a call has ended (§2.10's event trigger).</summary>
    public event Action? CallEnded;

    /// <summary>Raised when somebody presses "Reboot now" on the repair screen (§2.7 item 4).</summary>
    public event Action? RebootRequested;

    /// <summary>Raised when somebody presses "Try again" at the frame (§2.5 rung 5).</summary>
    public event Action? RetryRequested;

    /// <summary>When the page last said anything at all.</summary>
    /// <remarks>
    /// Null until the first check-in of this process. That null is load-bearing for §2.7's
    /// fallback rule: "the page has never checked in" and "the page checked in and went quiet" are
    /// different faults, and only the first one means the browser never rendered.
    /// </remarks>
    public DateTimeOffset? LastCheckInUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastCheckInUtc;
            }
        }
    }

    /// <summary>What the page last reported about its own configuration.</summary>
    public AppReport? LastReport
    {
        get
        {
            lock (_gate)
            {
                return _lastReport;
            }
        }
    }

    /// <summary>How many pages are connected right now.</summary>
    public int Peers
    {
        get
        {
            lock (_gate)
            {
                return _peers.Count;
            }
        }
    }

    /// <summary>How many messages the page has sent since this process started.</summary>
    public long CheckIns => Interlocked.Read(ref _checkIns);

    private DateTimeOffset? _lastCheckInUtc;
    private AppReport? _lastReport;

    /// <summary>Registers a connected page. Disposing the handle unregisters it.</summary>
    public IDisposable Attach(Func<StageMessage, CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);

        var peer = new Peer(send);

        lock (_gate)
        {
            _peers.Add(peer);
        }

        return new Attachment(this, peer);
    }

    /// <summary>Records one message from the page.</summary>
    public void Receive(PageMessage message, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _lastCheckInUtc = at;

            if (message.Identity is not null
                || message.Room is not null
                || message.LivekitUrl is not null
                || message.ImmichKioskUrl is not null)
            {
                _lastReport = new AppReport(
                    at,
                    message.Identity,
                    message.Room,
                    message.LivekitUrl,
                    message.ImmichKioskUrl,
                    message.HasToken);
            }
        }

        Interlocked.Increment(ref _checkIns);

        switch (message.Kind)
        {
            case PageMessage.KindCallEnded:
                CallEnded?.Invoke();
                break;
            case PageMessage.KindRebootNow:
                RebootRequested?.Invoke();
                break;
            case PageMessage.KindRetry:
                RetryRequested?.Invoke();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Forgets every check-in, as if no page had ever connected.
    /// </summary>
    /// <remarks>
    /// Called when the graphical session is torn down (§2.7's fallback rule) and when it is
    /// brought back. Without it the next arming would measure its deadline against a check-in from
    /// the <i>previous</i> browser, and a session that never renders at all would look healthy on
    /// the strength of the one before it.
    /// </remarks>
    public void Forget()
    {
        lock (_gate)
        {
            _lastCheckInUtc = null;
            _lastReport = null;
        }
    }

    /// <summary>Pushes one narration frame to every connected page.</summary>
    /// <remarks>
    /// A page whose send throws is dropped rather than retried. It is a browser that has gone
    /// away, and the liveness rule above is what notices — a send loop that kept trying would be
    /// the v1 LiveKit retry leak in a new place.
    /// </remarks>
    public async Task PublishAsync(StageMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Peer[] peers;
        lock (_gate)
        {
            peers = [.. _peers];
        }

        foreach (var peer in peers)
        {
            try
            {
                await peer.Send(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or ObjectDisposedException
                or InvalidOperationException
                or System.Net.WebSockets.WebSocketException)
            {
                Detach(peer);
            }
        }
    }

    private void Detach(Peer peer)
    {
        lock (_gate)
        {
            _peers.Remove(peer);
        }
    }

    private sealed class Peer(Func<StageMessage, CancellationToken, Task> send)
    {
        public Func<StageMessage, CancellationToken, Task> Send { get; } = send;
    }

    private sealed class Attachment(LocalChannel channel, Peer peer) : IDisposable
    {
        private Peer? _peer = peer;

        public void Dispose()
        {
            var held = Interlocked.Exchange(ref _peer, null);
            if (held is not null)
            {
                channel.Detach(held);
            }
        }
    }
}
