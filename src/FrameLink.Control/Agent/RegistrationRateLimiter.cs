using System.Collections.Concurrent;

namespace FrameLink.Control.Agent;

/// <summary>
/// The handshake budget on the open registration path (§3.3), in two halves.
/// </summary>
/// <remarks>
/// <para>
/// The device route is fully open and internet-exposed by design — pointing a frame at the
/// URL has to be enough to make it appear, so there is nothing to authenticate <i>before</i>
/// the handshake. That makes the abuse controls mandatory rather than hardening-later, and
/// this is the outermost one: the unidentified half runs before the WebSocket upgrade, so a
/// refused attempt costs one HTTP response and never reaches the crypto or the database.
/// </para>
/// <para>
/// <b>An address is the wrong thing to charge a frame for, and charging one was a real
/// fault</b> (decision 87, fixed by decision 92). Every frame in a household leaves through one NAT, and a
/// containerised Fleet Manager sees its whole fleet arrive as the bridge gateway, so a single
/// per-address budget is one budget for every frame the operator owns — invisible with one
/// frame, and with six it lets one frame in a reconnect loop spend its five healthy siblings'
/// allowance. Trusted-proxy configuration cannot recover the real address either: the agent
/// opens a bare <c>ClientWebSocket</c> and sets no forwarded header, and layer-4 NAT cannot
/// add one.
/// </para>
/// <para>
/// So the budget is keyed on the identity §3.3 already has — the keypair. The address window
/// still exists, but it now counts only <i>unidentified</i> attempts: the pre-upgrade charge
/// is provisional, and <see cref="TryAdmitDevice"/> releases it the moment a proof binds the
/// connection to a device the operator has acted on. A fleet of healthy frames therefore
/// spends nothing on the shared window at all, and cannot starve itself.
/// </para>
/// <para>
/// <b>What bounds the path before an identity exists.</b> Nothing is released for an attempt
/// that never proves a key, that proves a key this server has never met, or that proves one
/// still sitting in the adoption queue — all three stay charged to the address, at exactly the
/// budget that applied before this split. That is deliberately where the strictest bound sits,
/// because it is where an attacker sits: an anonymous flood, a forged proof and a stranger's
/// freshly minted keypair are all unidentified traffic and all share one address window. The
/// device window cannot be reached by any of them, which is also what keeps its dictionary
/// bounded by the operator's own fleet rather than by attacker input.
/// </para>
/// <para>
/// Both tracking dictionaries are capped. A limiter that grows one entry per key is the
/// memory-exhaustion vector it was added to prevent, and an attacker choosing a fresh address
/// per request is precisely the case it has to survive.
/// </para>
/// </remarks>
public sealed class RegistrationRateLimiter(ControlOptions options, TimeProvider clock)
{
    /// <summary>Key every attempt that carries no usable source address is counted under.</summary>
    /// <remarks>
    /// No remote address means a transport that cannot be attributed. Counting them all under
    /// one key keeps the budget enforced rather than bypassed.
    /// </remarks>
    private const string UnattributedAddress = "<unknown>";

    private readonly FixedWindows _addresses = new(
        options.MaxTrackedAddresses,
        options.RateLimitAttempts,
        options.RateLimitWindow);

    private readonly FixedWindows _devices = new(
        options.MaxTrackedDevices,
        options.DeviceRateLimitAttempts,
        options.RateLimitWindow);

    /// <summary>
    /// Records an attempt from <paramref name="address"/> that has proved nothing yet, and says
    /// whether to allow it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spent before the WebSocket upgrade, when the only thing known about the peer is where the
    /// packets came from. The charge is provisional: it is released again by
    /// <see cref="TryAdmitDevice"/> if the handshake goes on to prove an identity this server
    /// already knows.
    /// </para>
    /// <para>
    /// A fixed window rather than a token bucket: an agent's reconnect discipline is capped
    /// exponential backoff (§4.1), so a legitimate frame makes at most a handful of attempts
    /// per window even when a server is down, and a window is far easier to reason about when
    /// reading a rejection in the log.
    /// </para>
    /// </remarks>
    public bool TryAdmitUnidentified(string? address) =>
        _addresses.TryAcquire(KeyFor(address), clock.GetUtcNow());

    /// <summary>
    /// Moves an attempt off the shared address window and onto the device's own, and says
    /// whether the device may proceed.
    /// </summary>
    /// <param name="deviceId">
    /// The public-key fingerprint the proof established. Never a claimed one — a hello is
    /// unauthenticated, so charging a claimed id would let anyone spend a frame's budget by
    /// naming it.
    /// </param>
    /// <param name="address">The address the provisional charge was made against.</param>
    /// <remarks>
    /// <para>
    /// <b>The release is unconditional and the refusal is not, which is the whole point.</b> If
    /// an over-budget device were left charged to its address, one frame in a hard loop would
    /// drain the shared window and its siblings would be refused before the upgrade — with no
    /// chance to prove who they are and so no chance of ever being released. That is the
    /// original fault wearing a different hat, so the shared window is given back first and the
    /// device is then refused on its own budget alone.
    /// </para>
    /// <para>
    /// The residue is accepted deliberately: an adopted frame in a hard loop still costs this
    /// server an upgrade, one signature verification and one indexed read per attempt, because
    /// there is no way to recognise it any earlier than the proof. What it no longer costs is
    /// anything belonging to another frame, and everything expensive past this point — the row
    /// write, the fleet event, the settings resolve, the call-token review, the socket and its
    /// ping timer — is skipped.
    /// </para>
    /// </remarks>
    public bool TryAdmitDevice(string deviceId, string? address)
    {
        _addresses.Release(KeyFor(address));
        return _devices.TryAcquire(deviceId, clock.GetUtcNow());
    }

    /// <summary>Drops windows that have run out, so the dictionaries track only live keys.</summary>
    public void Sweep(DateTimeOffset now)
    {
        _addresses.Sweep(now);
        _devices.Sweep(now);
    }

    private static string KeyFor(string? address) =>
        string.IsNullOrEmpty(address) ? UnattributedAddress : address;

    /// <summary>One keyed set of fixed windows, with a ceiling on how many it will track.</summary>
    private sealed class FixedWindows(int maxKeys, int attempts, TimeSpan width)
    {
        private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

        /// <summary>Charges one attempt to <paramref name="key"/> and says whether to allow it.</summary>
        public bool TryAcquire(string key, DateTimeOffset now)
        {
            if (_windows.Count >= maxKeys)
            {
                Sweep(now);
                if (_windows.Count >= maxKeys && !_windows.ContainsKey(key))
                {
                    // Full of live windows and this key is not one of them. Refusing is the
                    // safe direction: the alternative is unbounded growth under exactly the
                    // attack the limiter exists for.
                    return false;
                }
            }

            var window = _windows.AddOrUpdate(
                key,
                _ => new Window(now, 1),
                (_, existing) => now - existing.StartedUtc >= width
                    ? new Window(now, 1)
                    : existing with { Attempts = existing.Attempts + 1 });

            return window.Attempts <= attempts;
        }

        /// <summary>Gives one charged attempt back, if the key still has a live window.</summary>
        /// <remarks>
        /// A compare-and-swap loop rather than a lock: two frames behind one NAT can be released
        /// concurrently, and a lost update here would leave the shared window permanently short
        /// by one — the slow version of the bug this whole class exists to remove.
        /// </remarks>
        public void Release(string key)
        {
            while (_windows.TryGetValue(key, out var existing))
            {
                if (existing.Attempts <= 0)
                {
                    return;
                }

                if (_windows.TryUpdate(key, existing with { Attempts = existing.Attempts - 1 }, existing))
                {
                    return;
                }
            }
        }

        /// <summary>Removes every window that has run out.</summary>
        public void Sweep(DateTimeOffset now)
        {
            foreach (var entry in _windows)
            {
                if (now - entry.Value.StartedUtc >= width)
                {
                    _windows.TryRemove(entry);
                }
            }
        }

        private sealed record Window(DateTimeOffset StartedUtc, int Attempts);
    }
}
