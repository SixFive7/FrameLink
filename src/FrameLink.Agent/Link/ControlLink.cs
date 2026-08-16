using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.State;
using FrameLink.Protocol;

namespace FrameLink.Agent.Link;

/// <summary>
/// §4.1's reconnect loop: capped exponential backoff, <b>retry forever</b>, cleanup per failed
/// attempt.
/// </summary>
/// <remarks>
/// <para>
/// The loop itself holds almost nothing. Everything with a lifetime belongs to
/// <see cref="ConnectionAttempt"/>, which is created and destroyed inside one iteration, so
/// "does the retry loop leak" reduces to "does one attempt release what it took" — a question
/// with an answer a test can check.
/// </para>
/// <para>
/// <see cref="CompletedAttempts"/> and <see cref="MaximumConcurrentAttempts"/> exist for that
/// test. The second one is the guard against the subtler failure: not a resource left undisposed,
/// but a second attempt starting while the first is still unwinding, which turns a bounded leak
/// into an unbounded one.
/// </para>
/// </remarks>
public sealed class ControlLink
{
    private readonly IControlTransportFactory _transports;
    private readonly AgentStatusHub _hub;
    private readonly DeviceKey _identity;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly Backoff _backoff;
    private readonly Func<IReadOnlyList<Uri>> _endpoints;
    private readonly Func<HandshakeResult, CancellationToken, Task>? _onVerdict;

    private int _liveAttempts;
    private int _maximumConcurrentAttempts;

    /// <summary>Creates the loop.</summary>
    /// <param name="transports">How to open a connection.</param>
    /// <param name="hub">The shared status holder.</param>
    /// <param name="identity">The device keypair that signs each handshake proof.</param>
    /// <param name="clock">Source of the backoff waits.</param>
    /// <param name="log">Where failures are narrated.</param>
    /// <param name="endpoints">
    /// Read on every iteration rather than captured, so an endpoint list that arrives after the
    /// loop has started is picked up without restarting anything.
    /// </param>
    /// <param name="backoff">The reconnect schedule.</param>
    /// <param name="onVerdict">
    /// Invoked once per completed handshake, before the session pump runs — see
    /// <see cref="AttemptContext.OnVerdict"/>, which is where it is actually called, precisely so
    /// that the ordering is a property of the attempt rather than of this loop. This is how the
    /// update service is triggered early (§2.8, "the handshake is an optimisation, not a
    /// mechanism") — and nothing here depends on it succeeding.
    /// </param>
    public ControlLink(
        IControlTransportFactory transports,
        AgentStatusHub hub,
        DeviceKey identity,
        IAgentClock clock,
        IAgentLog log,
        Func<IReadOnlyList<Uri>> endpoints,
        Backoff? backoff = null,
        Func<HandshakeResult, CancellationToken, Task>? onVerdict = null)
    {
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(endpoints);

        _transports = transports;
        _hub = hub;
        _identity = identity;
        _clock = clock;
        _log = log;
        _endpoints = endpoints;
        _backoff = backoff ?? new Backoff();
        _onVerdict = onVerdict;
    }

    /// <summary>Board serial sent in the hello.</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>Free-text self-report sent in the hello (§4.2).</summary>
    /// <remarks>
    /// Read on every attempt rather than captured, for the same reason the endpoint list is
    /// (see the constructor) and with a sharper consequence: a self-report fixed at construction
    /// is a claim about a loop that had not run yet, repeated on every connect for the life of the
    /// process. <c>AgentStatusReporter.Hello</c> is what the agent passes here, so the hello
    /// carries what the loop is at the moment it is sent — and that reporter also pushes the value
    /// again when it changes without a reconnect, which is the case a converged frame is in.
    /// </remarks>
    public Func<string?>? AgentStatusText { get; init; }

    /// <summary>
    /// Where an attempt publishes its transport so the reconciler can send telemetry (§4.1).
    /// </summary>
    /// <remarks>
    /// Optional, and nothing in this loop depends on it. The attachment lives and dies inside
    /// one attempt's ownership stack, so a loop with an uplink leaks exactly as much as one
    /// without: nothing.
    /// </remarks>
    public AgentUplink? Uplink { get; init; }

    /// <summary>Invoked when the Fleet Manager pushes effective settings (§3.4).</summary>
    public Action<SettingsPush>? OnSettings { get; init; }

    /// <summary>Invoked when the operator presses retry on this frame (§2.5 rung 3).</summary>
    public Action<RetryRequest>? OnRetry { get; init; }

    /// <summary>Invoked when the Fleet Manager pushes who to contact (§2.7 item 8).</summary>
    public Action<OperatorContact>? OnOperatorContact { get; init; }

    /// <summary>How long connect plus handshake may take.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How many attempts have finished.</summary>
    public int CompletedAttempts { get; private set; }

    /// <summary>The high-water mark of simultaneously live attempts. Must never exceed one.</summary>
    public int MaximumConcurrentAttempts => _maximumConcurrentAttempts;

    /// <summary>Runs until cancelled. Never returns because of a failure.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var endpointIndex = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var endpoints = _endpoints();

            if (endpoints.Count == 0)
            {
                consecutiveFailures++;
                PublishSilence(
                    "No Fleet Manager address has been configured for this frame yet.",
                    consecutiveFailures,
                    endpoint: null);
                if (!await WaitAsync(consecutiveFailures, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            var endpoint = endpoints[endpointIndex % endpoints.Count];
            var outcome = await RunOneAsync(endpoint, cancellationToken).ConfigureAwait(false);
            CompletedAttempts++;

            if (outcome.Result == AttemptResult.Cancelled)
            {
                return;
            }

            if (outcome.Result == AttemptResult.Session)
            {
                // Only an `ok` verdict restarts the schedule. It means the frame was adopted and
                // held a real session, so the next failure should not inherit the wait from an
                // outage days ago.
                //
                // Every other verdict is answered and then closed by the server, because §3.3
                // requires a pending record to allocate nothing on an internet-exposed route. That
                // makes a non-`ok` handshake structurally identical to a failed one from this
                // loop's point of view: resetting on it would put an unadopted frame into a
                // one-second reconnect loop against the very endpoint the rule exists to protect.
                // Letting the schedule climb to its cap is what §3.3 means by "the frame learns of
                // its adoption on its next backoff reconnect".
                var reason = outcome.Reason ?? "The connection closed.";

                if (outcome.Handshake is { } verdict && !IsAdopted(verdict))
                {
                    consecutiveFailures++;

                    // Not silence. The server answered — "adopt me", "you are blocked", "I am not
                    // set up yet" — and then closed, and §2.6 is explicit that rejection is an
                    // answer while silence is not. Publishing NoContact here would replace the one
                    // sentence the operator needs with "cannot reach the Fleet Manager" for the
                    // whole of every backoff wait, so the frame would spend most of its time
                    // denying it had been told anything.
                    PublishWaiting(verdict, reason, consecutiveFailures, endpoint);
                }
                else
                {
                    consecutiveFailures = 1;

                    // A green frame whose socket dropped really has lost contact, so this is the
                    // silence rung — and because the last authoritative condition was InSync, §2.6
                    // lets the product keep running through it.
                    PublishSilence(reason, consecutiveFailures, endpoint);
                }
            }
            else
            {
                consecutiveFailures++;

                // Rotate only on failure: §4.3's list is ordered by preference, so the public URL
                // is retried first every time and the LAN address is the fallback, not a peer.
                endpointIndex++;
                _log.Warn($"Attempt {consecutiveFailures} to {endpoint} failed: {outcome.Reason}");
                PublishSilence(outcome.Reason ?? "The Fleet Manager did not answer.", consecutiveFailures, endpoint);
            }

            if (!await WaitAsync(consecutiveFailures, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<AttemptOutcome> RunOneAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var live = Interlocked.Increment(ref _liveAttempts);
        if (live > _maximumConcurrentAttempts)
        {
            _maximumConcurrentAttempts = live;
        }

        try
        {
            return await ConnectionAttempt.RunAsync(
                new AttemptContext
                {
                    Endpoint = endpoint,
                    Transports = _transports,
                    Identity = _identity,
                    Hub = _hub,
                    HardwareSerial = HardwareSerial,
                    AgentStatusText = AgentStatusText?.Invoke(),
                    HandshakeTimeout = HandshakeTimeout,
                    OnVerdict = _onVerdict,
                    Uplink = Uplink,
                    OnSettings = OnSettings,
                    OnRetry = OnRetry,
                    OnOperatorContact = OnOperatorContact,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AttemptOutcome { Result = AttemptResult.Cancelled };
        }
        finally
        {
            Interlocked.Decrement(ref _liveAttempts);
        }
    }

    private static bool IsAdopted(HandshakeResult? verdict) =>
        string.Equals(verdict?.Status, HandshakeStatus.Ok, StringComparison.Ordinal);

    /// <summary>
    /// Publishes the silence rung, naming the endpoint that was actually tried.
    /// </summary>
    /// <remarks>
    /// The endpoint matters on screen: §4.3's list is tried in order, so on a frame with both a
    /// public URL and a LAN address the answer to "what is it even talking to" changes between
    /// attempts, and showing the first entry regardless would quietly misreport it.
    /// </remarks>
    private void PublishSilence(string reason, int consecutiveFailures, Uri? endpoint)
    {
        var delay = _backoff.Delay(consecutiveFailures);

        _hub.Publish(status =>
        {
            var condition = DeviceStateLadder.NoContact(status.LastAuthoritative, reason);

            return status with
            {
                Condition = condition,
                Connected = false,
                CurrentEndpoint = endpoint ?? status.CurrentEndpoint,
                Attempt = consecutiveFailures,
                BackoffTotal = delay,
                BackoffEndsAt = _clock.UtcNow + delay,
                Narration = new Narration
                {
                    Detected = condition.Headline,
                    WhyItMatters = condition.Detail,
                    Action = reason,
                },
            };
        });
    }

    /// <summary>
    /// Holds an authoritative verdict on screen while the frame waits to ask again.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="PublishSilence"/> for the case where the Fleet Manager did
    /// answer and then hung up. What changes between the session and the wait is only the backoff
    /// state; what the frame says about itself must not, or a pending frame's screen alternates
    /// between its own fingerprint and an untrue claim that nothing answered.
    /// </remarks>
    private void PublishWaiting(HandshakeResult verdict, string reason, int consecutiveFailures, Uri? endpoint)
    {
        var delay = _backoff.Delay(consecutiveFailures);
        var condition = DeviceStateLadder.FromHandshake(verdict);

        _hub.Publish(status => status with
        {
            Condition = condition,
            LastAuthoritative = condition,
            Connected = false,
            CurrentEndpoint = endpoint ?? status.CurrentEndpoint,
            Attempt = consecutiveFailures,
            BackoffTotal = delay,
            BackoffEndsAt = _clock.UtcNow + delay,
            Narration = new Narration
            {
                Detected = condition.Headline,
                WhyItMatters = condition.Detail,
                Action = verdict.Message ?? reason,
            },
        });
    }

    private async Task<bool> WaitAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        try
        {
            await _clock.DelayAsync(_backoff.Delay(consecutiveFailures), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
