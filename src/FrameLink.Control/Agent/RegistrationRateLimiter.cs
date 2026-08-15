using System.Collections.Concurrent;

namespace FrameLink.Control.Agent;

/// <summary>
/// Per-address budget on the open registration path (§3.3).
/// </summary>
/// <remarks>
/// <para>
/// The device route is fully open and internet-exposed by design — pointing a frame at the
/// URL has to be enough to make it appear, so there is nothing to authenticate <i>before</i>
/// the handshake. That makes the abuse controls mandatory rather than hardening-later, and
/// this is the outermost one: it runs before the WebSocket upgrade, so a refused attempt
/// costs one HTTP response and never reaches the crypto or the database.
/// </para>
/// <para>
/// The tracking dictionary is itself capped. A limiter that grows one entry per source
/// address is the memory-exhaustion vector it was added to prevent, and an attacker choosing
/// a fresh address per request is precisely the case it has to survive.
/// </para>
/// </remarks>
public sealed class RegistrationRateLimiter(ControlOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>Records an attempt from <paramref name="address"/> and says whether to allow it.</summary>
    /// <remarks>
    /// A fixed window rather than a token bucket: an agent's reconnect discipline is capped
    /// exponential backoff (§4.1), so a legitimate frame makes at most a handful of attempts
    /// per window even when a server is down, and a window is far easier to reason about when
    /// reading a rejection in the log.
    /// </remarks>
    public bool TryAcquire(string? address)
    {
        // No remote address means a transport that cannot be attributed. Counting them all
        // under one key keeps the budget enforced rather than bypassed.
        var key = string.IsNullOrEmpty(address) ? "<unknown>" : address;
        var now = clock.GetUtcNow();

        if (_windows.Count >= options.MaxTrackedAddresses)
        {
            Sweep(now);
            if (_windows.Count >= options.MaxTrackedAddresses && !_windows.ContainsKey(key))
            {
                // Full of live windows and this address is not one of them. Refusing is the
                // safe direction: the alternative is unbounded growth under exactly the
                // attack the limiter exists for.
                return false;
            }
        }

        var window = _windows.AddOrUpdate(
            key,
            _ => new Window(now, 1),
            (_, existing) => now - existing.StartedUtc >= options.RateLimitWindow
                ? new Window(now, 1)
                : existing with { Attempts = existing.Attempts + 1 });

        return window.Attempts <= options.RateLimitAttempts;
    }

    /// <summary>Drops windows that have run out, so the dictionary tracks only live addresses.</summary>
    public void Sweep(DateTimeOffset now)
    {
        foreach (var entry in _windows)
        {
            if (now - entry.Value.StartedUtc >= options.RateLimitWindow)
            {
                _windows.TryRemove(entry);
            }
        }
    }

    private sealed record Window(DateTimeOffset StartedUtc, int Attempts);
}
