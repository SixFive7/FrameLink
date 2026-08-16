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

    /// <summary>Whether this user's login session exists yet, and if not, what is missing.</summary>
    /// <remarks>
    /// The one probe behind <see cref="Resources.UserSessionGate"/>. It lives on this seam rather
    /// than in a resource because the fact it reports — whether there is a session at all — is a
    /// property of the session, not of any one thing living inside it, and eleven resources need
    /// the answer.
    /// </remarks>
    Task<SessionReadiness> ReadinessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Whether the login user's session is up, and what is missing when it is not.
/// </summary>
/// <param name="Ready">
/// True once the session exists and its bus is answering. False is <b>not</b> a fault: it is the
/// ordinary state of the first ten seconds of every boot.
/// </param>
/// <param name="Why">
/// What was missing, in the register <see cref="Reconcile.ResourceObservation.Unevaluable"/> puts
/// where an observed value would have gone. Empty when <paramref name="Ready"/> is true.
/// </param>
public readonly record struct SessionReadiness(bool Ready, string Why)
{
    /// <summary>The session is up.</summary>
    public static SessionReadiness Up { get; } = new(true, string.Empty);
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

    /// <summary>Where <c>logind</c> creates each live session's runtime directory.</summary>
    public const string RuntimeDirectoryRoot = "/run/user";

    /// <summary>The user bus socket inside that directory.</summary>
    public const string BusSocketName = "bus";

    private readonly IProcessRunner _processes;
    private readonly ISystemFiles _files;
    private readonly Func<string> _user;
    private readonly Lock _gate = new();
    private (string User, string Uid)? _resolved;
    private string? _flashed;

    /// <summary>Creates a session over <paramref name="user"/>.</summary>
    /// <param name="processes">How commands are started.</param>
    /// <param name="user">
    /// The login user from the fleet setting, read at call time. When it says nothing — which is
    /// the whole of a pending frame's experience (§3.3) — the name falls back to <b>the account
    /// the image was flashed with, read off the frame itself</b>.
    /// </param>
    /// <param name="files">
    /// How <see cref="ReadinessAsync"/> looks at <c>/run/user</c>. Defaulted, because every caller
    /// that predates the readiness gate wants the host filesystem and nothing else.
    /// </param>
    public LoginUserSession(IProcessRunner processes, Func<string>? user = null, ISystemFiles? files = null)
    {
        ArgumentNullException.ThrowIfNull(processes);

        _processes = processes;
        _user = user ?? (() => string.Empty);
        _files = files ?? HostSystemFiles.Instance;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The fallback is read off the frame, not hard-coded, and the catalog is explicit about
    /// why.</b> <c>boot.autologin.getty-tty1</c> "does not gate on adoption, and that is
    /// load-bearing rather than a technicality: this file is the root of the whole user-unit
    /// layer, so an adoption edge here would block the session, labwc and the browser — and §2.7's
    /// browser stage would then be unavailable to exactly the pending frame that is supposed to be
    /// rendering its own fingerprint on it." Not gating on adoption only helps if there is a value
    /// to converge on before adoption, and the frame has one: the account the image was flashed
    /// with, which on Raspberry Pi OS is the first ordinary user, uid 1000. <see cref="DefaultUser"/>
    /// is the last resort, for a machine that has no such account at all.
    /// </remarks>
    public string UserName => _user()?.Trim() is { Length: > 0 } name ? name : FlashedAccount();

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
    /// <remarks>
    /// <para>
    /// <b>Two facts, one probe, and the second one is the symptom that was actually measured.</b>
    /// <c>/run/user/&lt;uid&gt;</c> is created by <c>pam_systemd</c> when <c>logind</c> opens the
    /// session, so its absence means no session exists. The bus socket inside it is created a
    /// moment later by the user manager, and until it is there every <c>systemctl --user</c> and
    /// <c>busctl --user</c> call fails with <i>Failed to connect to user scope bus … No such file
    /// or directory</i> — which is the exact wording three of the four measured resources reported
    /// as drift. A gate that passed on the directory alone would leave those three still lying.
    /// </para>
    /// <para>
    /// <b>No process is spawned.</b> Two <c>stat</c>-shaped questions against a tmpfs, on a seam
    /// the tests already fake, rather than the <c>loginctl</c> call the alternative would need —
    /// this runs before every observation of eleven resources, on a five-minute sweep, forever.
    /// </para>
    /// <para>
    /// <b>A uid that will not resolve reports ready.</b> There is no session to wait for on a frame
    /// with no such account, and the resources behind this gate then report what they genuinely
    /// find, which is the visible failure. Failing towards silence here would hide a misconfigured
    /// <c>device.user</c> behind "not settled yet" for ever — the same direction
    /// <c>boot.autologin.getty-tty1</c>'s settle window is careful to fail away from.
    /// </para>
    /// </remarks>
    public async Task<SessionReadiness> ReadinessAsync(CancellationToken cancellationToken)
    {
        var uid = await UidAsync(UserName, cancellationToken).ConfigureAwait(false);

        if (uid is null)
        {
            return SessionReadiness.Up;
        }

        var runtime = $"{RuntimeDirectoryRoot}/{uid}";

        if (!_files.DirectoryExists(runtime))
        {
            return new SessionReadiness(
                false,
                $"the login session has not started yet ({runtime} does not exist)");
        }

        var bus = $"{runtime}/{BusSocketName}";

        return _files.FileExists(bus)
            ? SessionReadiness.Up
            : new SessionReadiness(
                false,
                $"the login session is starting but its message bus is not up yet ({bus} does not exist)");
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

    /// <summary>The name behind uid 1000, or <see cref="DefaultUser"/>.</summary>
    /// <remarks>
    /// <para>
    /// <c>getent passwd 1000</c> rather than a scan of <c>/home</c>: a home directory is a
    /// guess — a leftover one from a previous owner reads exactly like a live account — while the
    /// passwd database is the thing the login the drop-in performs will actually consult.
    /// </para>
    /// <para>
    /// Resolved once per process. It cannot change under a running agent, and it is consulted on
    /// every observation of every user-scoped resource on a five-minute sweep.
    /// </para>
    /// </remarks>
    public string FlashedAccount()
    {
        lock (_gate)
        {
            if (_flashed is { } cached)
            {
                return cached;
            }
        }

        var result = _processes
            .RunAsync("getent", ["passwd", "1000"], CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var name = result.Succeeded && result.StandardOutput.Split(':') is { Length: > 1 } fields
            && fields[0].Trim() is { Length: > 0 } account
                ? account
                : DefaultUser;

        lock (_gate)
        {
            _flashed = name;
        }

        return name;
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
