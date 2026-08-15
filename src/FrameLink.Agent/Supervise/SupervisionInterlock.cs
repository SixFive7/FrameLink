using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Supervise;

/// <summary>One supervision action's window over the resources it disturbs.</summary>
/// <param name="Behaviour">Which of §2.10's behaviours opened it.</param>
/// <param name="Resources">The resource ids whose transient wrongness it excuses.</param>
/// <param name="OpenedUtc">When it opened.</param>
/// <param name="DeadlineUtc">When it stops excusing anything (§2.10 clause 3).</param>
public sealed record SupervisionWindow(
    string Behaviour,
    IReadOnlyList<string> Resources,
    DateTimeOffset OpenedUtc,
    DateTimeOffset DeadlineUtc)
{
    /// <summary>Whether this window still excuses <paramref name="resource"/> at <paramref name="at"/>.</summary>
    public bool Covers(string resource, DateTimeOffset at) =>
        at < DeadlineUtc && Resources.Contains(resource, StringComparer.Ordinal);
}

/// <summary>
/// <b>§2.10's interlock</b> — "the reconciler holds a lock on what it is applying, and a
/// supervision action opens a window on what it touches."
/// </summary>
/// <remarks>
/// <para>
/// One object, two directions, because the failure it prevents is symmetric. Supervision
/// restarting a browser the reconciler is deliberately holding down produces exactly the
/// interference that makes "which change broke it" unanswerable (§1.2 principle 5). The
/// reconciler treating a browser supervision has just restarted as drift produces the other half:
/// §2.6 stops the product, the screen shows a repair, and the frame reboots — for a blink that was
/// the declared state being kept alive.
/// </para>
/// <para>
/// <b>The window is what makes "supervision never stops the product" true rather than intended.</b>
/// Without it, <c>unit.chromium-kiosk.running-matches-content</c> observes "no browser process is
/// running" for the second or two a restart takes, and every 03:00 restart blanks the frame and
/// narrates a repair. §2.10 says that collision is why supervision is not modelled as drift; this
/// is where that separation is enforced rather than described.
/// </para>
/// <para>
/// <b>And the deadline is the boundary, not a safety valve.</b> "Supervision owns the transient,
/// drift owns the persistent." A window that expires stops excusing anything, so a browser that
/// did not come back becomes ordinary drift and everything §2.6 and §2.7 prescribe takes over —
/// the device leaves <c>InSync</c>, the product stops, the screen narrates. Nothing has to
/// <i>decide</i> that; it follows from the window no longer covering the resource.
/// </para>
/// </remarks>
public sealed class SupervisionInterlock
{
    /// <summary>The statuses §2.10 clause 1 names as the reconciler holding a resource.</summary>
    /// <remarks>
    /// <c>Degraded</c>, <c>Escalated</c> and <c>Halted</c> are deliberately <i>not</i> here, and
    /// that is §2.10's list rather than an omission: those three mean the reconciler has stopped
    /// touching the resource, so there is nothing left to race. A frame whose kiosk unit has been
    /// given up on still needs its browser restarted to stay alive — giving up there would mean a
    /// dark frame, which §2.10 gives as the whole reason supervision does not reuse §2.5's ladder.
    /// </remarks>
    public static bool IsHold(ResourceStatusKind kind) => kind
        is ResourceStatusKind.Progressing
        or ResourceStatusKind.AwaitingReboot
        or ResourceStatusKind.Blocked;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly List<SupervisionWindow> _windows = [];
    private string? _applying;

    /// <summary>Replaces the held set from a completed pass's status list.</summary>
    public void PublishHolds(IReadOnlyList<ResourceStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        lock (_gate)
        {
            _held.Clear();
            foreach (var status in statuses)
            {
                if (IsHold(status.Kind))
                {
                    _held.Add(status.Name);
                }
            }
        }
    }

    /// <summary>
    /// Names the resource being acted on right now, or clears it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PublishHolds"/> because an apply is held from the instant before
    /// <c>ActAsync</c> runs, which is earlier than any status list exists for it. The union of the
    /// two is what <see cref="ReconcilerHolds"/> answers.
    /// </remarks>
    public void Applying(string? resource)
    {
        lock (_gate)
        {
            _applying = resource;
        }
    }

    /// <summary>Whether the reconciler is holding <paramref name="resource"/> (§2.10 clause 1).</summary>
    public bool ReconcilerHolds(string resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        lock (_gate)
        {
            return _held.Contains(resource) || string.Equals(_applying, resource, StringComparison.Ordinal);
        }
    }

    /// <summary>Whether the reconciler is holding any of <paramref name="resources"/>.</summary>
    public string? FirstHeld(IReadOnlyList<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var resource in resources)
        {
            if (ReconcilerHolds(resource))
            {
                return resource;
            }
        }

        return null;
    }

    /// <summary>Opens a window over <paramref name="resources"/> (§2.10 clause 2).</summary>
    public SupervisionWindow Open(
        string behaviour,
        IReadOnlyList<string> resources,
        DateTimeOffset now,
        TimeSpan recoveryDeadline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(behaviour);
        ArgumentNullException.ThrowIfNull(resources);

        var window = new SupervisionWindow(behaviour, [.. resources], now, now + recoveryDeadline);

        lock (_gate)
        {
            _windows.Add(window);
        }

        return window;
    }

    /// <summary>Closes a window because the supervised thing is healthy again.</summary>
    public void Close(SupervisionWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            _windows.Remove(window);
        }
    }

    /// <summary>
    /// Whether an open window excuses <paramref name="resource"/> right now (§2.10 clause 2).
    /// </summary>
    public bool Excuses(string resource, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);

        lock (_gate)
        {
            foreach (var window in _windows)
            {
                if (window.Covers(resource, now))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The behaviour excusing <paramref name="resource"/>, for the delta.</summary>
    public string? ExcusedBy(string resource, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);

        lock (_gate)
        {
            foreach (var window in _windows)
            {
                if (window.Covers(resource, now))
                {
                    return window.Behaviour;
                }
            }

            return null;
        }
    }

    /// <summary>Removes and returns every window whose deadline has passed (§2.10 clause 3).</summary>
    public IReadOnlyList<SupervisionWindow> Expire(DateTimeOffset now)
    {
        lock (_gate)
        {
            List<SupervisionWindow>? expired = null;

            for (var index = _windows.Count - 1; index >= 0; index--)
            {
                if (_windows[index].DeadlineUtc <= now)
                {
                    (expired ??= []).Add(_windows[index]);
                    _windows.RemoveAt(index);
                }
            }

            return expired ?? (IReadOnlyList<SupervisionWindow>)[];
        }
    }

    /// <summary>How many windows are open.</summary>
    public int OpenWindows
    {
        get
        {
            lock (_gate)
            {
                return _windows.Count;
            }
        }
    }
}
