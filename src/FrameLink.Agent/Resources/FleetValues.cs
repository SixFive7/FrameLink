namespace FrameLink.Agent.Resources;

/// <summary>
/// The Fleet Manager's effective settings, as a resource sees them (§3.4).
/// </summary>
/// <remarks>
/// <para>
/// This type is where §2.2's "static logic, dynamic values" is enforced in practice. A resource
/// asks for a named key and gets a string; it cannot ask for a command, a script, a path
/// template or a list of steps, because there is nothing here that returns one. The catalog is
/// compiled in; only the values move.
/// </para>
/// <para>
/// Read through a delegate rather than captured as a dictionary, so a value changed in the
/// Fleet Manager while the frame is running is drift that the next pass corrects, exactly as a
/// value changed on the frame would be. §2.6 makes a pushed desired-value change "maximally
/// serious" conflict drift, and it can only be that if it is observed rather than remembered.
/// </para>
/// </remarks>
public sealed class FleetValues
{
    private readonly Func<string, string?> _lookup;

    /// <summary>Creates a view over <paramref name="lookup"/>.</summary>
    public FleetValues(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
    }

    /// <summary>A view over a fixed dictionary, for tests and for a frame with no server yet.</summary>
    public static FleetValues From(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new FleetValues(key => values.GetValueOrDefault(key));
    }

    /// <summary>An empty view.</summary>
    public static FleetValues None { get; } = new(_ => null);

    /// <summary>The value for <paramref name="key"/>, or null.</summary>
    public string? Find(string key) => _lookup(key) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary>The value for <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
    /// <remarks>
    /// Every resource has a catalog default, so a Fleet Manager that has never been configured
    /// still produces a fully specified frame. §1.2.2's "a frame must provision and self-heal
    /// with the server unreachable" would be untrue otherwise — an unreachable server supplies
    /// no settings at all.
    /// </remarks>
    public string Get(string key, string fallback) => Find(key) ?? fallback;
}
