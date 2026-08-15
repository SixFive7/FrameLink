using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FrameLink.Control.Authentication;

/// <summary>
/// Browser sessions for the single operator.
/// </summary>
/// <remarks>
/// <para>
/// In memory, deliberately. There is one operator and one process (§3.1), so a session table
/// would be persistence for its own sake — and losing every session on a container restart is
/// the correct behaviour anyway, since a restart is also how the password is rotated.
/// </para>
/// <para>
/// Tokens are 32 random bytes, so a session identifier is not guessable and never carries the
/// password. The dictionary is capped for the same reason the rate limiter is (§3.3): an
/// internet-exposed login route must not be able to grow server memory.
/// </para>
/// </remarks>
public sealed class OperatorSessions(ControlOptions options, TimeProvider clock)
{
    /// <summary>Cookie the GUI carries its session in.</summary>
    public const string CookieName = "fl_session";

    private const int MaxSessions = 64;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    /// <summary>Issues a session token valid for <see cref="ControlOptions.SessionLifetime"/>.</summary>
    public (string Token, DateTimeOffset ExpiresUtc) Create()
    {
        Sweep();

        if (_sessions.Count >= MaxSessions)
        {
            // Oldest expiry first: whichever session is closest to dying is the least costly
            // one to end early.
            var oldest = _sessions.OrderBy(entry => entry.Value).First().Key;
            _sessions.TryRemove(oldest, out _);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expiry = clock.GetUtcNow() + options.SessionLifetime;
        _sessions[token] = expiry;
        return (token, expiry);
    }

    /// <summary>True if the token names a session that has not expired.</summary>
    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var expiry))
        {
            return false;
        }

        if (expiry > clock.GetUtcNow())
        {
            return true;
        }

        _sessions.TryRemove(token, out _);
        return false;
    }

    /// <summary>Ends one session.</summary>
    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _sessions.TryRemove(token, out _);
        }
    }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in _sessions)
        {
            if (entry.Value <= now)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }
}
