using FrameLink.Agent.Hosting;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>user.framelink.supplementary-groups</c> — the frame's own account can reach its hardware.
/// </summary>
/// <remarks>
/// <para>
/// From guide 11 step 3 (<c>gpio</c>), guide 9 step 1 (<c>docker</c>, superseded) and the v1
/// inventory's <c>USERS_GROUPS</c>. The parity set is the stock <c>userconf-pi</c> membership plus
/// the two the build added, and <b><c>docker</c> is deliberately not in it</b>: Docker leaves the
/// frame with §2.1, and a naive state-diff against the frozen v1 reference would otherwise demand
/// that membership for ever. A frame that still carries it is not repaired — see below.
/// </para>
/// <para>
/// <b>The comparison is containment, not equality, and that is a decision.</b> Every group listed
/// here must be present; a group that is present and not listed is reported and left alone. Two
/// reasons. Removing a membership is destructive and irreversible by this agent — it cannot know
/// why somebody added it, and the same reasoning already governs
/// <c>boot.cmdline.fbcon-rotate</c> leaving a hand-set rotation alone. And the one membership v2
/// actively wants gone, <c>docker</c>, disappears with the account's reason for having it rather
/// than by being stripped here; stripping it would also be the agent's only destructive act
/// against a user account, which is a large door to open for a group that grants nothing once the
/// daemon is gone.
/// </para>
/// <para>
/// <b>Membership only takes effect in a new login session</b>, which on this frame means a reboot —
/// so §2.4's reboot-per-resource is not ceremony here, it is the mechanism. A frame that is added
/// to <c>gpio</c> and not rebooted has a session that still cannot open a line.
/// </para>
/// <para>
/// <b>Being in the <c>i2c</c> group is not the same as having an i2c bus.</b> The groups exist on a
/// stock Raspberry Pi OS image whether or not the interfaces they gate are enabled — measured on
/// the mule, <c>/dev/i2c-*</c> does not exist at all until something enables it. Nothing may read
/// this resource being <c>InSync</c> as evidence that an i2c device is reachable.
/// </para>
/// </remarks>
public sealed class UserGroupsResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "user.framelink.supplementary-groups";

    /// <summary>
    /// The v1 parity set, less <c>docker</c>.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>reference/v1-state-inventory.txt</c>'s <c>USERS_GROUPS</c> line:
    /// <c>adm dialout cdrom sudo audio video plugdev games users netdev input render spi i2c gpio</c>
    /// — the order there is numeric by gid, and this list keeps the catalog's reading order instead
    /// because it is what a person compares against.
    /// </remarks>
    public static IReadOnlyList<string> Groups { get; } =
    [
        "adm",
        "dialout",
        "cdrom",
        "sudo",
        "audio",
        "video",
        "plugdev",
        "games",
        "users",
        "netdev",
        "input",
        "render",
        "spi",
        "i2c",
        "gpio",
    ];

    /// <summary>The membership v1 had and v2 must never re-add.</summary>
    public const string RetiredGroup = "docker";

    private readonly IProcessRunner _processes;
    private readonly IUserSession _session;

    /// <summary>Creates the resource.</summary>
    public UserGroupsResource(IProcessRunner processes, IUserSession session)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(session);

        _processes = processes;
        _session = session;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame's own account is missing permissions it needs.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without them it cannot reach the button, the sound hardware or the screen.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var user = _session.UserName;
        var result = await _processes
            .RunAsync("id", [user], ProcessDeadline.Local, cancellationToken)
            .ConfigureAwait(false);
        var expected = $"{user} in {string.Join(", ", Groups)}";

        if (!result.Succeeded)
        {
            return new ResourceObservation(
                false,
                expected,
                result.Combined.Length == 0 ? $"there is no account called {user}" : result.Combined.Replace('\n', ' '));
        }

        var held = Membership(result.StandardOutput);
        var missing = Groups.Where(group => !held.Contains(group, StringComparer.Ordinal)).ToList();

        var extra = held.Contains(RetiredGroup, StringComparer.Ordinal)
            ? $"; it is also in {RetiredGroup}, which v2 no longer uses and does not remove"
            : string.Empty;

        return new ResourceObservation(
            missing.Count == 0,
            expected,
            missing.Count == 0
                ? $"{user} is in all {Groups.Count} of them{extra}"
                : $"{user} is not in {string.Join(", ", missing)}{extra}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var user = _session.UserName;
        var current = await _processes
            .RunAsync("id", [user], ProcessDeadline.Local, cancellationToken)
            .ConfigureAwait(false);
        var held = current.Succeeded ? Membership(current.StandardOutput) : [];
        var missing = Groups.Where(group => !held.Contains(group, StringComparer.Ordinal)).ToList();

        if (missing.Count == 0)
        {
            return new ResourceAction(
                $"left {user}'s group membership alone — it already holds every group in the set",
                "This frame's account already has the permissions it needs.");
        }

        // `-a -G` appends. Without `-a`, `usermod -G` *replaces* the whole supplementary set, which
        // on this account would silently drop sudo — one missing letter between a repair and a
        // frame nobody can administer.
        var joined = string.Join(',', missing);
        var result = await _processes
            .RunAsync("usermod", ["-a", "-G", joined, user], ProcessDeadline.Local, cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"usermod -a -G {joined} {user}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined.Replace('\n', ' ')})"),
            $"Giving this frame's own account permission to reach {(missing.Count == 1 ? "one more part" : $"{missing.Count} more parts")} of its hardware.");
    }

    /// <summary>The group names in a line of <c>id</c> output.</summary>
    /// <remarks>
    /// <c>id framelink</c> prints
    /// <c>uid=1000(framelink) gid=1000(framelink) groups=1000(framelink),4(adm),...</c>. Only the
    /// <c>groups=</c> field is read, and only the names inside the parentheses: the numeric gids
    /// differ between images and asserting them would report drift on a frame that is correct.
    /// </remarks>
    public static IReadOnlyList<string> Membership(string idOutput)
    {
        ArgumentNullException.ThrowIfNull(idOutput);

        const string Marker = "groups=";
        var names = new List<string>();

        var start = idOutput.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return names;
        }

        var field = idOutput[(start + Marker.Length)..];
        var end = field.IndexOfAny([' ', '\n', '\r']);
        if (end >= 0)
        {
            field = field[..end];
        }

        foreach (var entry in field.Split(','))
        {
            var open = entry.IndexOf('(', StringComparison.Ordinal);
            var close = entry.LastIndexOf(')');

            if (open >= 0 && close > open)
            {
                names.Add(entry[(open + 1)..close]);
            }
        }

        return names;
    }
}

/// <summary>
/// One line as <c>gpioinfo</c> describes it.
/// </summary>
/// <param name="Chip">The chip it belongs to.</param>
/// <param name="Offset">Its offset on that chip.</param>
/// <param name="Description">Everything <c>gpioinfo</c> printed after the offset, verbatim.</param>
public readonly record struct GpioInfoLine(string Chip, int Offset, string Description);

/// <summary>
/// Reading <c>gpioinfo</c>, tolerantly.
/// </summary>
/// <remarks>
/// libgpiod printed one shape in v1 (<c>line 17: unnamed "consumer" input active-high [used]</c>)
/// and another in v2 (<c>line 17: "GPIO17" input consumer="…" bias=pull-up</c>), and Trixie ships
/// 2.2.1. Rather than parse either shape exactly, this keeps the two things that are stable across
/// both — which chip a line is on and what its offset is — and hands the rest of the text back
/// untouched, so the resource matches on substrings and the delta shows a person the real line.
/// </remarks>
public static class GpioInfo
{
    /// <summary>Every line <c>gpioinfo</c> listed, in order.</summary>
    public static IReadOnlyList<GpioInfoLine> Lines(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var lines = new List<GpioInfoLine>();
        var chip = string.Empty;

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(raw[0]))
            {
                // `gpiochip0 - 54 lines:` — the header that opens a chip's block.
                chip = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
                continue;
            }

            var trimmed = raw.Trim();
            if (!trimmed.StartsWith("line", StringComparison.Ordinal))
            {
                continue;
            }

            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var number = trimmed[4..colon].Trim();
            if (int.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                lines.Add(new GpioInfoLine(chip, offset, trimmed[(colon + 1)..].Trim()));
            }
        }

        return lines;
    }

    /// <summary>How many chips <c>gpioinfo</c> listed at all.</summary>
    /// <remarks>
    /// Zero is the answer that matters: a machine with no GPIO chip is not a frame with a broken
    /// button, and the two must not produce the same verdict.
    /// </remarks>
    public static int ChipCount(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var chips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            chips.Add(line.Chip);
        }

        return chips.Count;
    }
}

/// <summary>
/// <c>gpio.button.line</c> — the agent is holding the line the call button is wired to.
/// </summary>
/// <remarks>
/// <para>
/// From guide 11 steps 2 and 3, and it is one resource rather than three because the claim, the
/// bias and the pin number are a single line-request operation that cannot be acted on
/// independently. The two failure signatures the catalog names are both visible in the same
/// output: a <b>wrong pin</b> shows the configured line unused, and a <b>contended line</b> shows
/// it held by a different consumer.
/// </para>
/// <para>
/// <b>Observe differs from Verify here, and the catalog says so outright.</b> That the line is
/// claimed with the right bias is observable on a freshly booted frame; that a <i>button</i> is
/// wired to it is only provable by a physical press. Guide 11 makes exactly this split — its step 4
/// simulates a press and exercises everything except the wire, and step 5 is the human who closes
/// the gap. So this resource verifies the claim, and correct wiring stays a human-confirmed
/// checkpoint.
/// </para>
/// <para>
/// <b>A frame with no button is InSync, and that is the point of the split.</b> Nothing about a
/// missing button changes what <c>gpioinfo</c> reports: the agent holds the line, the internal
/// pull-up keeps it high, and no edge ever arrives. Reporting drift for it would send every frame
/// that has not had its button fitted yet up §2.5's ladder — retry, escalate, and stop the whole
/// frame — over a state no software on it can change. The press count is carried in
/// the observed text instead, so "this frame has never seen a press" is visible to an operator
/// without being a fault.
/// </para>
/// <para>
/// <b>What does escalate is a backend that is not there.</b> v1's scar is the reason: without
/// <c>python3-lgpio</c>, gpiozero silently swapped in a mock pin factory and the daemon reported
/// healthy for ever while the button did nothing. A claim the agent could not make is drift here,
/// with the exact command and the exact refusal in the delta.
/// </para>
/// <para>
/// <b>A machine with no GPIO chip at all stands down instead</b>, the same way
/// <c>boot.autologin.getty-tty1</c> stands down where there is no <c>tty1</c> and
/// <c>cpu.governor.performance</c> where there are no cpufreq policies. That is not the mock-pin
/// failure in disguise: the mock claimed success on hardware that existed, while this names the
/// absence in the observed text and applies only where the kernel offers no GPIO at all.
/// </para>
/// </remarks>
public sealed class GpioButtonLineResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "gpio.button.line";

    /// <summary>The tool that reports who holds a line.</summary>
    public const string Executable = "gpioinfo";

    /// <summary>The bias the request must carry, as <c>gpioinfo</c> spells it.</summary>
    public const string Bias = "pull-up";

    private readonly IProcessRunner _processes;
    private readonly FleetValues _values;
    private readonly ButtonWatch? _button;

    /// <summary>Creates the resource.</summary>
    /// <param name="processes">How <c>gpioinfo</c> is run.</param>
    /// <param name="values">Where <c>button.gpioPin</c> comes from.</param>
    /// <param name="button">
    /// The agent's own claim, or null in a catalog built without one — a test graph, or any
    /// composition where nothing is holding a line. Null is reported as the failure it is rather
    /// than treated as an absent feature.
    /// </param>
    public GpioButtonLineResource(IProcessRunner processes, FleetValues values, ButtonWatch? button)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(values);

        _processes = processes;
        _values = values;
        _button = button;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [UserGroupsResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "Nothing on this frame is watching the call button.";

    /// <inheritdoc/>
    public string WhyItMatters => "Pressing the button would do nothing, so a call could only be started from the screen.";

    /// <summary>The line this frame is configured for.</summary>
    public int Pin => _button?.Pin
        ?? (int.TryParse(_values.Find(ButtonWatch.SettingKey), System.Globalization.CultureInfo.InvariantCulture, out var pin) && pin >= 0
            ? pin
            : ButtonWatch.DefaultPin);

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var pin = Pin;
        var state = _button?.State() ?? ButtonState.None;
        var expected = $"line {pin} claimed by {ButtonWatch.ConsumerName} with {Bias} bias";

        var result = await _processes
            .RunAsync(Executable, [], ProcessDeadline.Local, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // The tools are part of the stock `gpiod` package, so this is either a frame whose
            // package set was cut down or a gap in the catalog. Both need a person, and the delta
            // names the tool so that person does not have to guess which.
            return new ResourceObservation(
                false,
                expected,
                $"{Executable} could not be run ({Condense(result.Combined)}); {state.Describe()}");
        }

        var lines = GpioInfo.Lines(result.StandardOutput);

        if (lines.Count == 0)
        {
            return new ResourceObservation(
                true,
                expected,
                $"this machine has no GPIO chip at all, so there is no line to claim; {state.Describe()}");
        }

        var ours = lines.Where(line => line.Description.Contains(ButtonWatch.ConsumerName, StringComparison.Ordinal)).ToList();
        var claimed = ours.FirstOrDefault(line => line.Offset == pin);

        if (claimed != default)
        {
            var biased = claimed.Description.Contains(Bias, StringComparison.Ordinal);

            return new ResourceObservation(
                biased,
                expected,
                biased
                    ? $"line {pin} on {claimed.Chip} is {claimed.Description}; {state.Describe()}"
                    : $"line {pin} on {claimed.Chip} is claimed without {Bias} bias: {claimed.Description}");
        }

        if (ours.Count > 0)
        {
            // The wrong-pin signature, seen from the other side: the agent is holding a line, just
            // not the one the setting names. Worth its own sentence, because the frame looks
            // entirely healthy from the agent's side while the button is on a pin nobody watches.
            return new ResourceObservation(
                false,
                expected,
                $"the agent is holding line {ours[0].Offset} on {ours[0].Chip}, not line {pin}");
        }

        var configured = lines.FirstOrDefault(line => line.Offset == pin);

        return new ResourceObservation(
            false,
            expected,
            configured == default
                ? $"no chip on this frame has a line {pin}; {state.Describe()}"
                : $"line {pin} on {configured.Chip} is {configured.Description}; {state.Describe()}");
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_button is null)
        {
            return ValueTask.FromResult(new ResourceAction(
                "nothing in this agent is watching a button, so there is no claim to make",
                "This frame has no way to watch its call button."));
        }

        var pin = _button.Pin;

        return ValueTask.FromResult(_button.ReArm()
            ? new ResourceAction(
                $"dropped the agent's claim on line {pin} so it is requested again with {Bias} bias and a {(int)ButtonWatch.Debounce.TotalMilliseconds} ms debounce",
                "Taking hold of the call button's wire again.")
            : new ResourceAction(
                $"asked the agent to claim line {pin}; its next attempt is already due ({_button.State().Describe()})",
                "Waiting for this frame's next try at taking hold of the call button's wire."));
    }

    private static string Condense(string output) =>
        output.Length == 0 ? "no output" : output.Replace('\n', ' ').Trim();
}
