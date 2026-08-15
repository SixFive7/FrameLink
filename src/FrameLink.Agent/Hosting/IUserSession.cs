namespace FrameLink.Agent.Hosting;

/// <summary>
/// The login user's home directory and their <c>systemd --user</c> manager.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a second seam and not more of <see cref="ISystemControl"/>.</b> The agent runs
/// as <c>root</c> in the system manager (§6.1: <c>fl-agent.service</c> sets <c>User=root</c>, and
/// the agent needs no <c>sudo</c> at all), while the entire kiosk stack — the compositor, the
/// browser unit, labwc's autostart, <c>.bash_profile</c> — lives in one unprivileged user's
/// session. Those are two different systemd managers, two different D-Bus buses and two different
/// filesystem owners, and a call that forgets which one it is talking to fails in the quietest
/// possible way: <c>systemctl --user</c> as root answers about <i>root's</i> user manager, which
/// on a frame does not exist, so <c>is-enabled</c> returns "not-found" for a unit that is enabled
/// and the resource reports permanent false drift.
/// </para>
/// <para>
/// <b>The user is a fleet value, read at call time.</b> The catalog gives
/// <c>boot.autologin.getty-tty1</c> the setting <c>device.user</c>, and every path and unit below
/// hangs off the same name — so it is resolved through one delegate rather than captured, exactly
/// as <see cref="Resources.FleetValues"/> is, and for the same reason: a name changed in the Fleet
/// Manager has to become drift on the next pass rather than at the next process start.
/// </para>
/// </remarks>
public interface IUserSession
{
    /// <summary>The unprivileged login user the kiosk stack belongs to.</summary>
    string UserName { get; }

    /// <summary>That user's home directory.</summary>
    string HomeDirectory { get; }

    /// <summary>Runs a command inside that user's session, as that user.</summary>
    /// <remarks>
    /// "Inside the session" is doing real work: the command gets <c>XDG_RUNTIME_DIR</c>,
    /// <c>DBUS_SESSION_BUS_ADDRESS</c> and <c>WAYLAND_DISPLAY</c>, which is what
    /// <c>systemctl --user</c> needs to find the right manager and what <c>wlr-randr</c> needs to
    /// find the compositor. Without them both tools fail with messages that read like the setting
    /// is wrong rather than like the caller is in the wrong session.
    /// </remarks>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hands a path the agent wrote under the user's home to that user.
    /// </summary>
    /// <remarks>
    /// The agent writes as root, so without this every file it creates in the home directory is
    /// root-owned. Some of them still work that way — bash sources a root-owned
    /// <c>.bash_profile</c> quite happily — and some do not, because the user's own tooling
    /// expects to be able to rewrite them. Ownership is restored on the file <i>and on every
    /// directory the agent had to create to reach it</i>, since a root-owned
    /// <c>~/.config/labwc</c> is the same problem one level up.
    /// </remarks>
    Task GiveToUserAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// The real user session: <c>runuser</c> into the login user, with the session environment set.
/// </summary>
/// <remarks>
/// <para>
/// <c>runuser -u &lt;user&gt; -- env VAR=... command</c> rather than <c>systemctl --user
/// --machine=&lt;user&gt;@.host</c>. Both reach the user manager; only the first also works for
/// <c>wlr-randr</c>, <c>chown</c> and anything else that is not <c>systemctl</c>, so there is one
/// mechanism here instead of one per tool. <c>env</c> is invoked as a program with an argument
/// vector — there is no shell anywhere in this path, which is what keeps §2.2's ban on
/// server-supplied logic true even though a fleet value (the user name) reaches the command line.
/// </para>
/// <para>
/// The uid is resolved once per process and cached. It cannot change under a running agent — a
/// renamed or re-created user is a new session and a new agent start — and the lookup is a process
/// spawn that would otherwise happen on every observation of every user-scoped resource, five
/// minutes apart, forever.
/// </para>
/// </remarks>
public sealed class LoginUserSession : IUserSession
{
    /// <summary>The username the guides and the v1 reference use.</summary>
    public const string DefaultUser = "framelink";

    /// <summary>Fleet setting carrying the login user (§3.4).</summary>
    public const string SettingKey = "device.user";

    /// <summary>Where Raspberry Pi OS puts login users' homes.</summary>
    public const string HomeRoot = "/home";

    /// <summary>The Wayland socket labwc creates, and the only one this build uses.</summary>
    public const string WaylandDisplay = "wayland-0";

    private readonly IProcessRunner _processes;
    private readonly Func<string> _user;
    private readonly Lock _gate = new();
    private (string User, string Uid)? _resolved;

    /// <summary>Creates a session over <paramref name="user"/>.</summary>
    /// <param name="processes">How commands are started.</param>
    /// <param name="user">
    /// The login user, read at call time. Anything empty falls back to
    /// <see cref="DefaultUser"/> — an unadopted frame receives no settings at all (§3.3), and the
    /// kiosk stack still has to come up on it so that §2.7's browser stage can show the
    /// "adopt me" screen.
    /// </param>
    public LoginUserSession(IProcessRunner processes, Func<string>? user = null)
    {
        ArgumentNullException.ThrowIfNull(processes);

        _processes = processes;
        _user = user ?? (() => DefaultUser);
    }

    /// <inheritdoc/>
    public string UserName => _user()?.Trim() is { Length: > 0 } name ? name : DefaultUser;

    /// <inheritdoc/>
    public string HomeDirectory => HomeRoot + "/" + UserName;

    /// <inheritdoc/>
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var user = UserName;
        var uid = await UidAsync(user, cancellationToken).ConfigureAwait(false);

        if (uid is null)
        {
            return new ProcessResult(-1, string.Empty, $"There is no user called '{user}' on this frame.");
        }

        var vector = new List<string>(arguments.Count + 8)
        {
            "-u", user, "--", "env",
            $"XDG_RUNTIME_DIR=/run/user/{uid}",
            $"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/{uid}/bus",
            $"WAYLAND_DISPLAY={WaylandDisplay}",
            executable,
        };

        vector.AddRange(arguments);

        return await _processes.RunAsync("runuser", vector, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task GiveToUserAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var user = UserName;
        var owner = $"{user}:{user}";

        foreach (var target in SelfAndAncestors(path, HomeDirectory))
        {
            await _processes.RunAsync("chown", [owner, target], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <paramref name="path"/> and every directory between it and <paramref name="home"/>.
    /// </summary>
    /// <remarks>
    /// The home directory itself is excluded. It is not the agent's to own — it was created by
    /// <c>useradd</c> long before the agent existed, and rewriting its ownership on every file
    /// write would be a mutation nobody asked for.
    /// </remarks>
    public static IReadOnlyList<string> SelfAndAncestors(string path, string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        var trimmedHome = home.TrimEnd('/');
        var targets = new List<string>(4);

        for (var current = path.TrimEnd('/');
             current.Length > trimmedHome.Length && current.StartsWith(trimmedHome + "/", StringComparison.Ordinal);
             current = current[..current.LastIndexOf('/')])
        {
            targets.Add(current);
        }

        return targets;
    }

    private async Task<string?> UidAsync(string user, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_resolved is { } cached && string.Equals(cached.User, user, StringComparison.Ordinal))
            {
                return cached.Uid;
            }
        }

        var result = await _processes.RunAsync("id", ["-u", user], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.StandardOutput.Trim() is not { Length: > 0 } uid)
        {
            return null;
        }

        lock (_gate)
        {
            _resolved = (user, uid);
        }

        return uid;
    }
}
