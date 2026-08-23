using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>One channel of one ALSA simple control, as <c>amixer</c> prints it.</summary>
/// <param name="Name">The channel name — <c>Front Left</c>, <c>Front Right</c> or <c>Mono</c>.</param>
/// <param name="Value">The raw step value, on this hardware 0–60.</param>
/// <param name="Switch">Whether it is unmuted, or null where the control has no switch.</param>
/// <param name="Decibels">The gain <c>amixer</c> reported, e.g. <c>0.00dB</c>, or null.</param>
public readonly record struct MixerChannel(string Name, int Value, bool? Switch, string? Decibels);

/// <summary>What one <c>amixer sget</c> answered.</summary>
public sealed record MixerReading
{
    /// <summary>The simple control asked for, e.g. <c>PCM,1</c>.</summary>
    public required string Control { get; init; }

    /// <summary>Every channel the control reported.</summary>
    public required IReadOnlyList<MixerChannel> Channels { get; init; }

    /// <summary>The control's own ceiling, from its <c>Limits:</c> line.</summary>
    public int? Maximum { get; init; }

    /// <summary>Why there is nothing to compare, when <c>amixer</c> could not answer.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// One control the audio block owns, in both the spellings it is known by.
/// </summary>
/// <remarks>
/// <c>amixer</c> addresses <c>PCM,1</c>; <c>/var/lib/alsa/asound.state</c> stores the same thing
/// as <c>PCM Playback Volume</c> with <c>index 1</c>. Two resources compare the two spellings of
/// one setting — deliberately, because the catalog keeps "the running value is wrong" and "the
/// stored value is wrong" as different faults — so the mapping between them is written once here
/// rather than in each of them.
/// </remarks>
public sealed record MixerControlSpec
{
    /// <summary>The simple control's name, without its index.</summary>
    public required string Control { get; init; }

    /// <summary>Its index — the whole subject of the <c>PCM,1</c> trap.</summary>
    public required int Index { get; init; }

    /// <summary>Playback, or capture.</summary>
    public required bool Playback { get; init; }

    /// <summary>The fleet setting carrying this control's level (§3.4).</summary>
    public required string SettingKey { get; init; }

    /// <summary>The catalog default, which is correct on an unadopted frame.</summary>
    public required string DefaultValue { get; init; }

    /// <summary>How <c>amixer</c> is asked for it.</summary>
    public string Selector => Control + "," + Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The direction word both spellings use.</summary>
    public string Direction => Playback ? "Playback" : "Capture";

    /// <summary>How <c>asound.state</c> names this control's level.</summary>
    public string StoredVolumeName => $"{Control} {Direction} Volume";

    /// <summary>How <c>asound.state</c> names this control's switch.</summary>
    public string StoredSwitchName => $"{Control} {Direction} Switch";
}

/// <summary>
/// The frame's ALSA mixer: one card, read and written through <c>amixer</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Card 0 by number, exactly as guide 4 and the catalog spell it</b>, and safe because
/// <see cref="SndUsbAudioIndexResource"/> is a declared dependency of everything here: the pin
/// is what makes "card 0" mean the array rather than whatever enumerated first.
/// </para>
/// <para>
/// <b>The ceiling is a rule, not a hardware fact.</b> Guide 4's measured configuration is +20 dB
/// of loudness spread across two stages, both landing at 0 dB, and it says plainly what must not
/// happen next: "do not push software gain above 0 dB anywhere in the chain — beyond digital full
/// scale there is no loudness left, only clipping". On this array 0 dB is step 60, which the v1
/// reference records as the <c>Limits: Playback 0 - 60</c> of every control in the block. So a
/// fleet value is clamped to <see cref="Ceiling"/> and, where the hardware reports a lower
/// maximum, to that as well — a Fleet Manager cannot make a frame distort by typing a bigger
/// number.
/// </para>
/// </remarks>
public sealed class AlsaMixer
{
    /// <summary>The tool, resolved from <c>PATH</c>.</summary>
    public const string Executable = "amixer";

    /// <summary>The card the array is pinned to.</summary>
    public const string Card = "0";

    /// <summary>
    /// The highest step any control in this block may be set to: 0.00 dB.
    /// </summary>
    /// <remarks>
    /// Transcribed from the v1 reference's <c>ALSA_MIXER</c> capture, where all four controls
    /// read <c>Limits: … 0 - 60</c> and <c>60 [100%] [0.00dB]</c>.
    /// </remarks>
    public const int Ceiling = 60;

    private readonly IProcessRunner _processes;
    private readonly ISystemFiles _files;

    /// <summary>Creates a view over the card.</summary>
    public AlsaMixer(IProcessRunner processes, ISystemFiles files)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(files);

        _processes = processes;
        _files = files;
    }

    /// <summary>Whether this machine has ALSA at all (§5.3's virtual agents do not).</summary>
    public bool HasSoundHardware => _files.FileExists(AlsaCards.CardsPath);

    /// <summary>Reads one simple control.</summary>
    public async Task<MixerReading> ReadAsync(string selector, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var result = await _processes
            .RunAsync(Executable, ["-c", Card, "sget", selector], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return new MixerReading
            {
                Control = selector,
                Channels = [],
                Failure = result.Combined.Length == 0 ? "amixer did not answer" : result.Combined,
            };
        }

        return Parse(selector, result.StandardOutput);
    }

    /// <summary>Sets one simple control to <paramref name="value"/>.</summary>
    public Task<ProcessResult> SetAsync(string selector, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return _processes.RunAsync(Executable, ["-c", Card, "sset", selector, value], cancellationToken);
    }

    /// <summary>The command a person would have typed, for the change text.</summary>
    public static string Command(string selector, string value) =>
        $"{Executable} -c {Card} sset {selector} {value}";

    /// <summary>Clamps a desired step to the block's ceiling and the control's own maximum.</summary>
    public static int Clamp(int desired, int? maximum)
    {
        var limit = maximum is { } hardware && hardware < Ceiling ? hardware : Ceiling;
        return desired < 0 ? 0 : desired > limit ? limit : desired;
    }

    /// <summary>
    /// Parses <c>amixer sget</c> output.
    /// </summary>
    /// <remarks>
    /// Two lines in that output look like channel lines and are not. <c>Limits: Playback 0 - 60</c>
    /// parses as a channel called "Limits" sitting at 0 if it is not excluded, which would report
    /// a correctly-set control as silent; and a bare <c>Mono:</c> header appears above the two
    /// stereo channels of <c>PCM,0</c>. Both are excluded by requiring the bracketed percentage
    /// that a real channel line always carries.
    /// </remarks>
    public static MixerReading Parse(string selector, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(output);

        var channels = new List<MixerChannel>();
        int? maximum = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();

            if (string.Equals(name, "Limits", StringComparison.Ordinal))
            {
                maximum = LastInteger(rest);
                continue;
            }

            if (!rest.Contains('[', StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2
                || (!string.Equals(tokens[0], "Playback", StringComparison.Ordinal)
                    && !string.Equals(tokens[0], "Capture", StringComparison.Ordinal))
                || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            bool? state = null;
            string? decibels = null;

            foreach (var token in tokens)
            {
                if (string.Equals(token, "[on]", StringComparison.Ordinal))
                {
                    state = true;
                }
                else if (string.Equals(token, "[off]", StringComparison.Ordinal))
                {
                    state = false;
                }
                else if (token.StartsWith('[') && token.EndsWith("dB]", StringComparison.Ordinal))
                {
                    decibels = token[1..^1];
                }
            }

            channels.Add(new MixerChannel(name, value, state, decibels));
        }

        return new MixerReading
        {
            Control = selector,
            Channels = channels,
            Maximum = maximum,
            Failure = channels.Count == 0 ? "amixer reported no channels for this control" : null,
        };
    }

    /// <summary>Channels rendered for a delta: <c>Front Left=60, Front Right=60</c>.</summary>
    public static string Describe(MixerReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading.Failure is { } failure)
        {
            return failure;
        }

        var parts = new List<string>(reading.Channels.Count);
        foreach (var channel in reading.Channels)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{channel.Name}={channel.Value}{(channel.Decibels is { } gain ? " " + gain : string.Empty)}{channel.Switch switch { true => " on", false => " MUTED", _ => string.Empty }}"));
        }

        return string.Join(", ", parts);
    }

    private static int? LastInteger(string text)
    {
        int? found = null;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                found = value;
            }
        }

        return found;
    }
}

/// <summary>
/// What the user session's audio stack is doing, recorded beside every mixer observation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mixer has two owners and the guides configure only one.</b>
/// <c>alsa-restore.service</c> applies <c>/var/lib/alsa/asound.state</c> early in boot; then the
/// login session starts and WirePlumber's <c>restore-device</c> policy applies its <i>own</i>
/// stored per-device volume and route from <c>~/.local/state/wireplumber/</c>. Later writer wins,
/// and the symptom of losing that race — a quiet frame — is identical to the hidden <c>PCM,1</c>
/// fault, so it will be misattributed by anyone who has not been told.
/// </para>
/// <para>
/// <b>This is evidence, not a verdict.</b> The catalog lists it as a <i>suspected</i> revert,
/// reasoned from documented upstream behaviour and a boot ordering rather than from an
/// observation, and three of its siblings in that list have since been disproved by measurement.
/// So the agent neither assumes WirePlumber reverts the mixer nor assumes it does not: it records
/// what WirePlumber is doing at the moment of every reading, which makes the answer fall out of
/// ordinary telemetry rather than out of a special investigation. Two facts are cheap and
/// sufficient — whether WirePlumber is running, and whether it holds stored device state at all,
/// which is the thing the v1 inventory never captured.
/// </para>
/// <para>
/// <b>The mixer <i>is</i> gated on the session, and that reverses what this remark used to say.</b>
/// It argued that a resource refusing to conclude until the session was up would act, and therefore
/// reboot, on a frame whose session is broken — which was wrong on its own terms, because
/// <see cref="ResourceObservation.Unevaluable"/> returns before <c>ActAsync</c> is ever reached
/// (decision 65 records exactly that). What the ungated version actually produced is worse than the
/// risk it was avoiding: the post-boot verify runs at boot+10.0–10.6 s and the user manager comes up
/// 0.03–0.7 s later, so the verify was a coin flip on whether it read the agent's value or
/// WirePlumber's, and a verify that won <b>passed and cleared the ledger</b> on a frame that was
/// about to be wrong again. §2.4's whole rule is that "applied" is claimed only from an observation
/// the setting had to survive a boot for, and a reading taken before the other owner of that value
/// has started is not that observation. So the gate is the mixer's too (decision 80).
/// </para>
/// <para>
/// <b>The file names, not just the count.</b> Which files WirePlumber keeps under
/// <c>~/.local/state/wireplumber/</c> is the one read-only fact that separates *it restored a stored
/// volume* from *it applied its own default to a route it has no stored volume for*, and the two
/// have different fixes. Nobody has been able to take that reading by hand, so the agent takes it on
/// every mixer observation and it arrives in ordinary telemetry.
/// </para>
/// </remarks>
public sealed class SessionAudio
{
    /// <summary>The user unit that owns device volumes once the session is up.</summary>
    public const string Unit = "wireplumber.service";

    /// <summary>Where its <c>restore-device</c> policy keeps per-device state.</summary>
    public const string StateSubdirectory = ".local/state/wireplumber";

    /// <summary>How many stored-state file names are listed before the evidence is truncated.</summary>
    /// <remarks>
    /// WirePlumber 0.5 keeps a handful — <c>default-nodes</c>, <c>default-profile</c>,
    /// <c>restore-stream</c> and the route state — so six lists all of them on any frame anybody has
    /// seen while bounding a string that reaches the screen and the wire.
    /// </remarks>
    public const int NamesShown = 6;

    /// <summary>How long one probe is reused for, so a pass does not spawn one per control.</summary>
    public static TimeSpan Freshness { get; } = TimeSpan.FromSeconds(10);

    private readonly IUserSession _session;
    private readonly ISystemFiles _files;
    private readonly IAgentClock _clock;
    private readonly Lock _gate = new();
    private string? _cached;
    private DateTimeOffset _takenUtc;

    /// <summary>Creates the probe.</summary>
    public SessionAudio(IUserSession session, ISystemFiles files, IAgentClock clock)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clock);

        _session = session;
        _files = files;
        _clock = clock;
    }

    /// <summary>One short sentence naming both facts, for an observation's observed text.</summary>
    public async ValueTask<string> EvidenceAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        lock (_gate)
        {
            if (_cached is { } fresh && now - _takenUtc < Freshness)
            {
                return fresh;
            }
        }

        var probe = await _session
            .RunAsync("systemctl", ["--user", "is-active", Unit], cancellationToken)
            .ConfigureAwait(false);

        var answer = probe.StandardOutput.Trim();
        var running = answer.Length == 0
            ? (probe.StandardError.Length == 0 ? "unreachable" : "no session")
            : answer.Split('\n')[0].Trim();

        var directory = _session.HomeDirectory.TrimEnd('/') + "/" + StateSubdirectory;
        var evidence = $"wireplumber {running}, {StoredState(_files, directory)}";

        lock (_gate)
        {
            _cached = evidence;
            _takenUtc = now;
        }

        return evidence;
    }

    /// <summary>
    /// The session gate (decision 65) in front of a mixer observation, or null to go ahead and look.
    /// </summary>
    /// <remarks>
    /// Offered from here rather than by handing every mixer resource its own
    /// <see cref="IUserSession"/>: the session is this class's business, and the reason the mixer
    /// needs the gate is the same reason this class exists — the value has a second owner that lives
    /// inside the session.
    /// </remarks>
    public ValueTask<ResourceObservation?> NotSettledAsync(string expected, CancellationToken cancellationToken) =>
        UserSessionGate.NotSettledAsync(_session, expected, cancellationToken);

    /// <summary>What WirePlumber is keeping under its state directory, named rather than counted.</summary>
    public static string StoredState(ISystemFiles files, string directory)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!files.DirectoryExists(directory))
        {
            return "no stored device state";
        }

        var paths = files.ListFiles(directory);
        if (paths.Count == 0)
        {
            return "0 stored device files";
        }

        var names = new List<string>(NamesShown);
        foreach (var path in paths)
        {
            if (names.Count == NamesShown)
            {
                names.Add("…");
                break;
            }

            var slash = path.LastIndexOfAny(['/', '\\']);
            names.Add(slash < 0 ? path : path[(slash + 1)..]);
        }

        names.Sort(StringComparer.Ordinal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{paths.Count} stored device file{(paths.Count == 1 ? string.Empty : "s")} ({string.Join(", ", names)})");
    }
}

/// <summary>
/// <c>audio.mixer.pcm0-playback-volume</c>, <c>audio.mixer.pcm1-playback-volume</c> and
/// <c>audio.mixer.headset-capture-volume</c> — a level, on one or more controls.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>PCM,1</c> instance is the highest-value resource in guide 4, and the reason this is
/// three resources rather than one.</b> The card exposes two playback stages: the obvious stereo
/// <c>PCM,0</c>, and a second mono stage <c>PCM,1</c> that ships at <c>40/60</c> — −20 dB, one
/// hundredth of full electrical power. Setting only the obvious one, as an earlier revision of
/// guide 4 did, leaves a frame that is merely quiet: everything reports healthy, the amplifier is
/// on, the volume "is" 100%, and the speaker is throttled in software by roughly 18 dB. It is the
/// fault nobody reports and nobody finds, so it gets its own name, its own delta and its own
/// escalation.
/// </para>
/// <para>
/// <b>The values are transcribed from the v1 reference, not chosen here.</b>
/// <c>reference/v1-state-inventory.txt</c>'s <c>ALSA_MIXER</c> section captured the running v1
/// frame: <c>PCM,0</c> at 60 on Front Left and Front Right, <c>PCM,1</c> at 60 on Mono,
/// <c>Headset,0</c> at 60 on both channels and <c>Headset,1</c> at 60 on Mono, every one of them
/// <c>[0.00dB] [on]</c> against <c>Limits: 0 - 60</c>. That capture is the specification.
/// </para>
/// <para>
/// <b>No adoption edge, and the catalog says why.</b> The level is a fleet setting, but the
/// catalog default is correct on an unadopted frame, so this resource never has to guess: it
/// applies 60 now and a later fleet override is ordinary drift that reconciles like any other
/// change.
/// </para>
/// </remarks>
public sealed class MixerVolumeResource : IResource
{
    private readonly AlsaMixer _mixer;
    private readonly SessionAudio _session;
    private readonly FleetValues _values;
    private readonly IReadOnlyList<MixerControlSpec> _specs;
    private readonly bool _requireOn;

    /// <summary>Creates the resource over one or more controls that share a level.</summary>
    /// <param name="name">The catalog id.</param>
    /// <param name="mixer">The card.</param>
    /// <param name="session">The second owner's state, for the observed text.</param>
    /// <param name="values">Fleet settings.</param>
    /// <param name="specs">The controls this resource owns.</param>
    /// <param name="requireOn">
    /// Whether the control's switch is part of this resource. True only for the capture pair,
    /// where the catalog folds "at 60" and "both on" into one resource; the playback switches are
    /// resources in their own right because mute and volume are different diagnoses reached by
    /// different commands.
    /// </param>
    /// <param name="detected">What was detected, in plain language.</param>
    /// <param name="whyItMatters">Why it matters, in one sentence.</param>
    /// <param name="gloss">Plain-language gloss on the change.</param>
    /// <param name="dependsOn">The catalog's declared dependencies.</param>
    public MixerVolumeResource(
        string name,
        AlsaMixer mixer,
        SessionAudio session,
        FleetValues values,
        IReadOnlyList<MixerControlSpec> specs,
        bool requireOn,
        string detected,
        string whyItMatters,
        string gloss,
        IReadOnlyList<string> dependsOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(dependsOn);

        Name = name;
        Detected = detected;
        WhyItMatters = whyItMatters;
        Gloss = gloss;
        DependsOn = dependsOn;

        _mixer = mixer;
        _session = session;
        _values = values;
        _specs = specs;
        _requireOn = requireOn;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn { get; }

    /// <inheritdoc/>
    public string Detected { get; }

    /// <inheritdoc/>
    public string WhyItMatters { get; }

    /// <summary>Plain-language gloss on this resource's change (§2.7 item 3).</summary>
    public string Gloss { get; }

    /// <summary>The controls this resource owns.</summary>
    public IReadOnlyList<MixerControlSpec> Specs => _specs;

    /// <summary>The step this resource converges <paramref name="spec"/> on, clamped.</summary>
    public int DesiredFor(MixerControlSpec spec, int? maximum)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var text = _values.Get(spec.SettingKey, spec.DefaultValue);
        var desired = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : int.Parse(spec.DefaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture);

        return AlsaMixer.Clamp(desired, maximum);
    }

    /// <summary>What this resource wants, before any reading — for the unevaluable case.</summary>
    public string DesiredSummary()
    {
        var wanted = new List<string>(_specs.Count);
        foreach (var spec in _specs)
        {
            wanted.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{spec.Selector}={DesiredFor(spec, maximum: null)}"));
        }

        return string.Join(", ", wanted) + (_requireOn ? ", every channel on" : string.Empty);
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!_mixer.HasSoundHardware)
        {
            return new ResourceObservation(true, "the validated level", "no sound hardware on this machine");
        }

        // Decision 80. The mixer's second owner lives in the login session, so a reading taken
        // before that session exists is a reading of a value that has not finished being decided —
        // and §2.4 forbids claiming a change survived a boot on the strength of one. An unevaluable
        // observation spends no attempt, acts on nothing and reboots for nothing; it is re-read
        // thirty seconds later, by which time WirePlumber has had its turn and the answer is real.
        if (await _session.NotSettledAsync(DesiredSummary(), cancellationToken).ConfigureAwait(false)
            is { } waiting)
        {
            return waiting;
        }

        var expected = new List<string>(_specs.Count);
        var observed = new List<string>(_specs.Count);
        var correct = true;

        foreach (var spec in _specs)
        {
            var reading = await _mixer.ReadAsync(spec.Selector, cancellationToken).ConfigureAwait(false);
            var desired = DesiredFor(spec, reading.Maximum);

            expected.Add(string.Create(CultureInfo.InvariantCulture, $"{spec.Selector}={desired}"));
            observed.Add($"{spec.Selector}: {AlsaMixer.Describe(reading)}");

            if (reading.Failure is not null || reading.Channels.Count == 0)
            {
                correct = false;
                continue;
            }

            foreach (var channel in reading.Channels)
            {
                if (channel.Value != desired || (_requireOn && channel.Switch == false))
                {
                    correct = false;
                }
            }
        }

        var evidence = await _session.EvidenceAsync(cancellationToken).ConfigureAwait(false);

        return new ResourceObservation(
            correct,
            string.Join(", ", expected) + (_requireOn ? ", every channel on" : string.Empty),
            string.Join("; ", observed) + $" [{evidence}]");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var commands = new List<string>(_specs.Count * 2);

        foreach (var spec in _specs)
        {
            var reading = await _mixer.ReadAsync(spec.Selector, cancellationToken).ConfigureAwait(false);
            var desired = DesiredFor(spec, reading.Maximum).ToString(CultureInfo.InvariantCulture);

            var applied = await _mixer.SetAsync(spec.Selector, desired, cancellationToken).ConfigureAwait(false);
            commands.Add(AlsaMixer.Command(spec.Selector, desired)
                + (applied.Succeeded ? string.Empty : $" (refused: {applied.Combined})"));

            if (!_requireOn)
            {
                continue;
            }

            // `cap` rather than `on`: these are capture switches, and the capture verb is the one
            // amixer documents for them.
            var unmuted = await _mixer.SetAsync(spec.Selector, "cap", cancellationToken).ConfigureAwait(false);
            commands.Add(AlsaMixer.Command(spec.Selector, "cap")
                + (unmuted.Succeeded ? string.Empty : $" (refused: {unmuted.Combined})"));
        }

        return new ResourceAction(string.Join(" && ", commands), Gloss);
    }
}

/// <summary>
/// <c>audio.mixer.pcm0-playback-switch</c> and <c>audio.mixer.pcm1-playback-switch</c> — the
/// playback stage is not muted.
/// </summary>
/// <remarks>
/// <para>
/// Set by no guide command: both default on, and <c>alsactl store</c> persists them. They are in
/// the catalog because <b>mute and volume are different diagnoses with the same symptom</b> —
/// silence — reached by different commands, and because the v1 reference records both switches
/// <c>true</c> in its <c>asound.state</c> capture (<c>control.3</c> and <c>control.4</c>, the
/// second carrying <c>index 1</c>). Cheap to observe, so worth having.
/// </para>
/// <para>
/// The volume resource for the same stage depends on the switch, so a muted stage is reported as
/// exactly that rather than as a level that will not take effect.
/// </para>
/// </remarks>
public sealed class MixerSwitchResource : IResource
{
    private readonly AlsaMixer _mixer;
    private readonly SessionAudio _session;
    private readonly MixerControlSpec _spec;

    /// <summary>Creates the resource for one control.</summary>
    public MixerSwitchResource(
        string name,
        AlsaMixer mixer,
        SessionAudio session,
        MixerControlSpec spec,
        string detected,
        string whyItMatters,
        string gloss,
        IReadOnlyList<string> dependsOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(dependsOn);

        Name = name;
        Detected = detected;
        WhyItMatters = whyItMatters;
        Gloss = gloss;
        DependsOn = dependsOn;

        _mixer = mixer;
        _session = session;
        _spec = spec;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn { get; }

    /// <inheritdoc/>
    public string Detected { get; }

    /// <inheritdoc/>
    public string WhyItMatters { get; }

    /// <summary>Plain-language gloss on this resource's change (§2.7 item 3).</summary>
    public string Gloss { get; }

    /// <summary>The control this resource owns.</summary>
    public MixerControlSpec Spec => _spec;

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        if (!_mixer.HasSoundHardware)
        {
            return new ResourceObservation(true, $"{_spec.Selector} unmuted", "no sound hardware on this machine");
        }

        // Decision 80, for the same reason as the level: mute is a route property WirePlumber
        // restores alongside the volume, so the switch has the same second owner and the same
        // race.
        if (await _session.NotSettledAsync($"{_spec.Selector} unmuted on every channel", cancellationToken)
                .ConfigureAwait(false) is { } waiting)
        {
            return waiting;
        }

        var reading = await _mixer.ReadAsync(_spec.Selector, cancellationToken).ConfigureAwait(false);
        var evidence = await _session.EvidenceAsync(cancellationToken).ConfigureAwait(false);

        var unmuted = reading.Failure is null && reading.Channels.Count > 0;
        foreach (var channel in reading.Channels)
        {
            if (channel.Switch == false)
            {
                unmuted = false;
            }
        }

        return new ResourceObservation(
            unmuted,
            $"{_spec.Selector} unmuted on every channel",
            $"{AlsaMixer.Describe(reading)} [{evidence}]");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var result = await _mixer.SetAsync(_spec.Selector, "unmute", cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            AlsaMixer.Command(_spec.Selector, "unmute")
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            Gloss);
    }
}

/// <summary>
/// <c>audio.wireplumber.playback-volume</c> — <b>WirePlumber's own idea of how loud this frame is</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was measured.</b> <c>audio.mixer.pcm0-playback-volume</c> set <c>PCM,0=60</c>, rebooted,
/// verified — and the value observed afterwards was <c>Front Left=37 -23.00dB on, Front Right=37
/// -23.00dB on</c> with <c>wireplumber active</c> beside it. The ALSA control was owned, correctly,
/// by a resource that could not keep it, because something downstream of it was setting it too.
/// </para>
/// <para>
/// <b>What the number says, and this is the strongest evidence in the file.</b> The control's scale
/// is <b>one step per decibel</b> — three independent points agree: 60 is 0.00 dB in the v1
/// inventory, 40 is the −20 dB <c>PCM,1</c> ships at, and 37 read −23.00 dB on the frame. So 37 is
/// not noise, it is a *requested gain* of −23 dB. WirePlumber 0.5's own default sink volume,
/// <c>device.routes.default-sink-volume</c>, is <c>0.064</c> linear, which is
/// 20·log₁₀(0.064) = <b>−23.88 dB</b> — the nearest step at or above that request is exactly 37.
/// <b>This is arithmetic on a documented constant, not a measurement</b>, and it is offered as the
/// leading hypothesis rather than as an established mechanism: three of the catalog's suspected
/// reverts have already been disproved by measurement and this one has not been measured either.
/// What it does do is predict the observed value to within the control's own quantisation, which
/// nothing else on offer does.
/// </para>
/// <para>
/// <b>Why it changes the fix.</b> If 37 is WirePlumber's *default* rather than a *restored* value,
/// then owning <c>~/.local/state/wireplumber/</c> — the catalog's suggestion — repairs nothing,
/// because there is no stored route volume to correct. Setting the volume <i>through WirePlumber</i>
/// is correct under both readings: it overrides a default, and it is what causes
/// <c>restore-device</c> to persist a stored value, so whichever mechanism is really in play ends up
/// agreeing with the frame. That is deliberately the property being aimed at, because the mechanism
/// cannot be measured from this desk.
/// </para>
/// <para>
/// <b>Why not a configuration fragment.</b> The obvious alternative is a
/// <c>~/.config/wireplumber/wireplumber.conf.d/</c> file setting the default to unity, or taking the
/// hardware mixer away from WirePlumber with <c>api.alsa.soft-mixer</c>. Both are plausible and
/// neither can be tested here, and a malformed fragment stops WirePlumber from starting — which
/// takes the camera fragment down with it and leaves the frame with no audio at all.
/// <c>wpctl</c> is the supported interface, cannot break the daemon, and fails visibly: if the call
/// is refused the resource reports drift with the tool's own words and escalates like anything else.
/// </para>
/// <para>
/// <b>It does not replace the ALSA resources, and it cannot.</b> <c>PCM,1</c> is a second hardware
/// gain stage that no PipeWire route volume reaches — it is the −20 dB stage guide 4's loudness fix
/// found — so the hardware mixer stays agent-owned whatever WirePlumber does. This resource makes
/// the two owners *want the same thing* rather than taking the value away from either: both derive
/// from <c>audio.playbackVolume</c>, and the comparison is done in decibels with a half-step
/// tolerance, which is the quantisation the two owners share.
/// </para>
/// <para>
/// <b>A prediction worth checking on the next hardware run:</b> if the route volume is what moved,
/// only <c>PCM,0</c> reverts and <c>PCM,1</c> stays where the agent put it. The delta measured on
/// the frame names <c>PCM,0</c> and says nothing about <c>PCM,1</c>, which is consistent with that
/// and does not establish it.
/// </para>
/// <para>
/// <b>And it runs before the resource it rescues, not after — which is the opposite of how it was
/// first written.</b> It was declared depending on both mixer volumes, so it could not be acted on
/// until <c>audio.mixer.pcm0-playback-volume</c> was <c>InSync</c> — a state that resource cannot
/// reach while WirePlumber is holding the sink at its default, which is the entire fault this one
/// exists for. Measured on the frame 2026-08-16: <c>PCM,0</c> exhausted its budget and escalated,
/// the escalation stopped the pass, and the rescue never executed once. Applying the catalog's own
/// dependency test — <i>would this resource have to guess?</i> — the two edges were never warranted
/// anyway: <c>wpctl get-volume</c> reads WirePlumber's sink and touches no ALSA control, and the
/// desired value comes from the <i>same fleet setting</i> through the sibling resource object, which
/// answers whatever state that sibling is in. What is real is the edge in the other direction, and
/// it now exists: <c>PCM,0</c> cannot hold 60 until WirePlumber has been told to stop asking for
/// −23 dB, so the mixer stage depends on this one.
/// </para>
/// </remarks>
public sealed class WirePlumberVolumeResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "audio.wireplumber.playback-volume";

    /// <summary>WirePlumber's own control tool, shipped by the same package.</summary>
    public const string Executable = "wpctl";

    /// <summary>The sink wpctl resolves to whatever PipeWire is currently playing through.</summary>
    /// <remarks>
    /// Deliberately the default sink rather than a node named for the array. The frame has one
    /// playback device and the browser plays through whichever one PipeWire has chosen, so the
    /// default *is* the thing whose loudness the household hears — and a node name would be a second
    /// spelling of the card pin that <see cref="SndUsbAudioIndexResource"/> already owns.
    /// </remarks>
    public const string Sink = "@DEFAULT_AUDIO_SINK@";

    /// <summary>
    /// How far the two owners may disagree before it is drift: half a step of the shared control.
    /// </summary>
    /// <remarks>
    /// The hardware control is quantised at 1 dB per step (measured: 60 = 0.00 dB, 40 = −20 dB,
    /// 37 = −23.00 dB), and WirePlumber's volume is a continuous fraction, so an exact comparison
    /// would report permanent false drift the moment either side rounded. Half a step is the
    /// tightest tolerance that cannot.
    /// </remarks>
    public const double ToleranceDecibels = 0.5;

    private readonly SessionAudio _session;
    private readonly IUserSession _shell;
    private readonly AlsaMixer _mixer;
    private readonly MixerVolumeResource _stage;

    /// <summary>Creates the resource over the playback stage whose level it must agree with.</summary>
    /// <param name="session">The second owner's state, for the observed text and the gate.</param>
    /// <param name="shell">How <c>wpctl</c> is run inside the login user's session.</param>
    /// <param name="mixer">The card, for "is there any sound hardware here at all".</param>
    /// <param name="stage">
    /// The stereo playback resource. Taken as the resource rather than as a copy of its number so
    /// that a fleet override arriving mid-life moves both owners together — two copies of a desired
    /// value are two things that can disagree, and this resource exists because two owners did.
    /// </param>
    public WirePlumberVolumeResource(
        SessionAudio session,
        IUserSession shell,
        AlsaMixer mixer,
        MixerVolumeResource stage)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(stage);

        _session = session;
        _shell = shell;
        _mixer = mixer;
        _stage = stage;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <summary>The daemon that has to be installed, and the session <c>wpctl</c> has to run in.</summary>
    /// <remarks>
    /// Exactly what Observe and Act need and nothing else. The two mixer-volume edges that used to
    /// be here inverted the ordering against the resource this one rescues — see the type's remarks
    /// — and neither survived the catalog's dependency test: nothing in <c>wpctl get-volume</c> or
    /// <c>wpctl set-volume</c> reads or writes an ALSA control, and the desired level is read from
    /// the shared fleet setting rather than from the sibling's converged state, so this resource
    /// never has to guess.
    /// </remarks>
    public IReadOnlyList<string> DependsOn =>
    [
        PackageResource.Prefix + "wireplumber",
        ConsoleAutologinResource.ResourceName,
    ];

    /// <inheritdoc/>
    public string Detected =>
        "The part of this frame that routes sound is holding the speaker quieter than the frame is set to.";

    /// <inheritdoc/>
    public string WhyItMatters =>
        "It turns the speaker down again every time the frame starts, so the frame looks right and sounds faint.";

    /// <summary>Plain-language gloss on this resource's change (§2.7 item 3).</summary>
    public static string Gloss =>
        "Telling the part of this frame that routes sound to leave the speaker at the level it was tested at.";

    /// <summary>The linear volume one mixer step corresponds to, on the measured 1 dB-per-step scale.</summary>
    /// <remarks>
    /// <paramref name="step"/> 60 is 0.00 dB and therefore 1.00; step 0 is silence and is returned as
    /// exactly zero rather than as 10^−3, because "off" and "very quiet" are different states and
    /// <c>wpctl</c> spells the first one <c>0.00</c>.
    /// </remarks>
    public static double VolumeForStep(int step) =>
        step <= 0 ? 0d : Math.Pow(10d, (step - AlsaMixer.Ceiling) / 20d);

    /// <summary>That volume as <c>wpctl</c> is asked for it, and as it prints it back.</summary>
    public static string Format(double volume) =>
        volume.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Whether two volumes agree to within half a step of the shared control.</summary>
    public static bool Agree(double observed, double desired)
    {
        if (observed <= 0d || desired <= 0d)
        {
            return Math.Abs(observed - desired) < 0.005d;
        }

        return Math.Abs(20d * Math.Log10(observed / desired)) <= ToleranceDecibels;
    }

    /// <summary>
    /// The volume and mute state in one line of <c>wpctl get-volume</c>, or null if it said neither.
    /// </summary>
    /// <remarks>
    /// The output is one line — <c>Volume: 1.00</c>, or <c>Volume: 0.06 [MUTED]</c> — and it is
    /// parsed by finding the label rather than by splitting on position, so a future wpctl that
    /// prints a node name in front of it still reads.
    /// </remarks>
    public static (double Volume, bool Muted)? Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        const string Label = "Volume:";

        var at = output.IndexOf(Label, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = output[(at + Label.Length)..];
        var muted = rest.Contains("[MUTED]", StringComparison.OrdinalIgnoreCase);

        foreach (var token in rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
            {
                return (volume, muted);
            }
        }

        return null;
    }

    /// <summary>The volume this resource converges on, from the same fleet setting as the mixer.</summary>
    public double Desired() => VolumeForStep(_stage.DesiredFor(AudioCatalog.Pcm0, maximum: null));

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var desired = Desired();
        var expected = $"wireplumber holding the sink at {Format(desired)}, unmuted";

        if (!_mixer.HasSoundHardware)
        {
            return new ResourceObservation(true, expected, "no sound hardware on this machine");
        }

        // wpctl needs the session's bus, so without a session there is nothing to ask rather than
        // something wrong (decision 65).
        if (await _session.NotSettledAsync(expected, cancellationToken).ConfigureAwait(false) is { } waiting)
        {
            return waiting;
        }

        var result = await _shell
            .RunAsync(Executable, ["get-volume", Sink], cancellationToken)
            .ConfigureAwait(false);

        var evidence = await _session.EvidenceAsync(cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // A local read that failed has learned something real about this machine, so it is
            // drift rather than unevaluable — CameraNodeResource's rule, and this resource is
            // exactly the kind of place it exists to protect.
            return new ResourceObservation(
                false,
                expected,
                $"{Executable} could not be asked: {(result.Combined.Length == 0 ? "no output" : result.Combined.Trim())} [{evidence}]");
        }

        if (Parse(result.StandardOutput) is not { } reading)
        {
            return new ResourceObservation(
                false,
                expected,
                $"{Executable} answered '{result.StandardOutput.Trim()}', which carries no volume [{evidence}]");
        }

        return new ResourceObservation(
            Agree(reading.Volume, desired) && !reading.Muted,
            expected,
            $"the sink is at {Format(reading.Volume)}{(reading.Muted ? " MUTED" : string.Empty)} [{evidence}]");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var desired = Format(Desired());

        var volume = await _shell
            .RunAsync(Executable, ["set-volume", Sink, desired], cancellationToken)
            .ConfigureAwait(false);

        // Unmuted in the same Act rather than as a resource of its own: unlike the hardware
        // switches, this is one route property set through one tool, and splitting it would give
        // the two halves of a single write two independent escalation ladders.
        var unmute = await _shell
            .RunAsync(Executable, ["set-mute", Sink, "0"], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"{Executable} set-volume {Sink} {desired}"
                + (volume.Succeeded ? string.Empty : $" (refused: {volume.Combined.Trim()})")
                + $" && {Executable} set-mute {Sink} 0"
                + (unmute.Succeeded ? string.Empty : $" (refused: {unmute.Combined.Trim()})"),
            Gloss);
    }
}

/// <summary>One control as <c>/var/lib/alsa/asound.state</c> stores it.</summary>
/// <param name="Name">The kernel control name, e.g. <c>PCM Playback Volume</c>.</param>
/// <param name="Index">Its index; 0 unless the file says otherwise.</param>
/// <param name="Values">Every <c>value</c> / <c>value.N</c> entry, as written.</param>
public readonly record struct StoredControl(string Name, int Index, IReadOnlyList<string> Values);

/// <summary>
/// The file <c>alsa-restore.service</c> replays into the card at every boot.
/// </summary>
/// <remarks>
/// Parsed structurally rather than by pattern, because the v1 reference's capture of this file
/// is <b>truncated part-way through <c>control.4</c></b> — it records the two channel maps and
/// the two playback switches and stops mid-block, so the exact spelling the volume controls are
/// stored under is not in the frozen reference. What is not guessed is the shape: alsa-lib's
/// configuration format is a tree of <c>key value</c> lines and <c>name { … }</c> blocks, and the
/// walk below reads that and nothing else.
/// </remarks>
public static class AsoundState
{
    /// <summary>Where <c>alsactl</c> keeps it.</summary>
    public const string StatePath = "/var/lib/alsa/asound.state";

    /// <summary>Every control stored for <paramref name="card"/>.</summary>
    public static IReadOnlyList<StoredControl> Parse(string? content, string card)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(card);

        var controls = new List<StoredControl>();
        if (string.IsNullOrEmpty(content))
        {
            return controls;
        }

        var stack = new List<string>();
        var values = new List<string>();
        string? name = null;
        var index = 0;
        var inControl = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith('{'))
            {
                var opened = line[..^1].Trim();
                stack.Add(opened);

                if (stack.Count == 2
                    && string.Equals(stack[0], "state." + card, StringComparison.Ordinal)
                    && opened.StartsWith("control.", StringComparison.Ordinal))
                {
                    inControl = true;
                    name = null;
                    index = 0;
                    values.Clear();
                }

                continue;
            }

            if (string.Equals(line, "}", StringComparison.Ordinal))
            {
                if (stack.Count == 2 && inControl)
                {
                    if (name is not null)
                    {
                        controls.Add(new StoredControl(name, index, [.. values]));
                    }

                    inControl = false;
                }

                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            // Only the control's own keys. Everything one level deeper is the `comment` block,
            // which describes the control rather than holding its value.
            if (!inControl || stack.Count != 2)
            {
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            var key = line[..space];
            var value = line[(space + 1)..].Trim().Trim('\'');

            if (string.Equals(key, "name", StringComparison.Ordinal))
            {
                name = value;
            }
            else if (string.Equals(key, "index", StringComparison.Ordinal))
            {
                index = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;
            }
            else if (string.Equals(key, "value", StringComparison.Ordinal)
                || key.StartsWith("value.", StringComparison.Ordinal))
            {
                values.Add(value);
            }
        }

        return controls;
    }

    /// <summary>The stored control with this name and index, or null.</summary>
    public static StoredControl? Find(IReadOnlyList<StoredControl> controls, string name, int index)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(name);

        foreach (var control in controls)
        {
            if (control.Index == index && string.Equals(control.Name, name, StringComparison.Ordinal))
            {
                return control;
            }
        }

        return null;
    }
}

/// <summary>
/// <c>audio.alsa.stored-state</c> — the levels come back by themselves after a restart.
/// </summary>
/// <remarks>
/// <para>
/// From guide 4 step 5. <c>alsa-utils</c> ships <c>alsa-restore.service</c> as a static unit that
/// replays <c>/var/lib/alsa/asound.state</c> early in every boot, so nothing needs installing or
/// enabling — the only missing piece is the file, and <c>alsactl store</c> writes it whole, which
/// makes the Act idempotent by construction.
/// </para>
/// <para>
/// <b>Deliberately not the same observation as reading the live mixer.</b> The running value can
/// be right while the stored value is wrong — nothing has rebooted yet — and the stored value can
/// be right while the running value is wrong, which is what a second owner changing it after boot
/// looks like. Those are two different faults with one symptom, and the catalog keeps both
/// observable rather than collapsing them; that is also what makes this resource the control
/// experiment for the WirePlumber question described on <see cref="SessionAudio"/>.
/// </para>
/// </remarks>
public sealed class AlsaStoredStateResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "audio.alsa.stored-state";

    /// <summary>The tool that writes the file.</summary>
    public const string Executable = "alsactl";

    /// <summary>How <c>asound.state</c> spells "unmuted".</summary>
    public const string StoredOn = "true";

    private readonly IProcessRunner _processes;
    private readonly ISystemFiles _files;
    private readonly IReadOnlyList<MixerVolumeResource> _volumes;

    /// <summary>Creates the resource over the volume resources whose values it persists.</summary>
    /// <remarks>
    /// It takes the resources rather than a copy of their numbers so that a fleet override
    /// arriving mid-life moves both the live value and the stored expectation together. Two
    /// copies of a desired value are two things that can disagree, and this resource exists
    /// precisely to notice disagreements.
    /// </remarks>
    public AlsaStoredStateResource(
        IProcessRunner processes,
        ISystemFiles files,
        IReadOnlyList<MixerVolumeResource> volumes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(volumes);

        _processes = processes;
        _files = files;
        _volumes = volumes;
    }

    /// <summary>Whether the Act waits for WirePlumber's own volume to be settled first.</summary>
    /// <remarks>
    /// <c>alsactl store</c> captures whatever is live at the instant it runs, so on a frame where
    /// WirePlumber also writes the hardware mixer the file would otherwise record whichever owner
    /// happened to write last. Declaring the dependency makes the ordering explicit rather than
    /// lucky. It is an option only because a machine with no sound hardware builds this block
    /// without the WirePlumber resource at all.
    /// </remarks>
    public bool AfterWirePlumber { get; init; } = true;

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn
    {
        get
        {
            var names = new List<string>(_volumes.Count + 3);
            foreach (var volume in _volumes)
            {
                names.Add(volume.Name);
            }

            names.Add(AudioCatalog.Pcm0SwitchResourceName);
            names.Add(AudioCatalog.Pcm1SwitchResourceName);

            if (AfterWirePlumber)
            {
                names.Add(WirePlumberVolumeResource.ResourceName);
            }

            return names;
        }
    }

    /// <inheritdoc/>
    public string Detected => "This frame's sound levels are not saved, so a restart could bring it back too quiet to hear.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without the saved copy the frame starts at whatever level its sound card happens to pick.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_files.FileExists(AlsaCards.CardsPath))
        {
            return ValueTask.FromResult(new ResourceObservation(
                true,
                "the levels saved for the next start",
                "no sound hardware on this machine"));
        }

        var content = _files.ReadText(AsoundState.StatePath);
        if (content is null)
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                $"{AsoundState.StatePath} holding this frame's levels",
                $"{AsoundState.StatePath} does not exist"));
        }

        var stored = AsoundState.Parse(content, AlsaCards.ArrayId);
        var wrong = new List<string>();
        var expected = new List<string>();

        foreach (var volume in _volumes)
        {
            foreach (var spec in volume.Specs)
            {
                // No hardware maximum here: the stored file records the value that was live, and
                // the live value was clamped by the same rule when it was set.
                var desired = volume.DesiredFor(spec, maximum: null).ToString(CultureInfo.InvariantCulture);

                expected.Add($"{spec.StoredVolumeName}[{spec.Index}]={desired}");
                Check(stored, spec.StoredVolumeName, spec.Index, desired, wrong);
                Check(stored, spec.StoredSwitchName, spec.Index, StoredOn, wrong);
            }
        }

        return ValueTask.FromResult(new ResourceObservation(
            wrong.Count == 0,
            string.Join(", ", expected) + ", every switch true",
            wrong.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"all {stored.Count} stored controls agree")
                : string.Join("; ", wrong) + Names(stored)));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        // Whole-file, every card, exactly as guide 4 step 5 does it. The live values it captures
        // are correct by construction: every mixer resource is a declared dependency of this one,
        // so none of them is drifted at the moment this runs.
        var result = await _processes.RunAsync(Executable, ["store"], cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"{Executable} store"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Combined})"),
            "Saving this frame's sound levels so they come back by themselves every time it starts.");
    }

    private static void Check(
        IReadOnlyList<StoredControl> stored,
        string name,
        int index,
        string desired,
        List<string> wrong)
    {
        if (AsoundState.Find(stored, name, index) is not { } control)
        {
            wrong.Add(string.Create(CultureInfo.InvariantCulture, $"'{name}' index {index} is not in the file"));
            return;
        }

        foreach (var value in control.Values)
        {
            if (!string.Equals(value, desired, StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add(string.Create(CultureInfo.InvariantCulture, $"'{name}' index {index} is stored as {value}, not {desired}"));
                return;
            }
        }

        if (control.Values.Count == 0)
        {
            wrong.Add(string.Create(CultureInfo.InvariantCulture, $"'{name}' index {index} is stored with no value"));
        }
    }

    /// <summary>
    /// What the file does hold, when something expected is missing.
    /// </summary>
    /// <remarks>
    /// The first escalation therefore carries the real control names rather than only the ones
    /// that were looked for — which matters here more than elsewhere, because the v1 reference's
    /// capture of this file stops before the volume controls and cannot settle their spelling.
    /// </remarks>
    private static string Names(IReadOnlyList<StoredControl> stored)
    {
        if (stored.Count == 0)
        {
            return $" — the file holds no controls for card '{AlsaCards.ArrayId}'";
        }

        var names = new List<string>();
        foreach (var control in stored)
        {
            var spelled = string.Create(CultureInfo.InvariantCulture, $"'{control.Name}'[{control.Index}]");
            if (!names.Contains(spelled, StringComparer.Ordinal))
            {
                names.Add(spelled);
            }

            if (names.Count == 12)
            {
                break;
            }
        }

        return " — the file holds " + string.Join(", ", names);
    }
}

/// <summary>
/// Guide 4's audio block, in the catalog's own order.
/// </summary>
/// <remarks>
/// <para>
/// Positions 54–61 of the proposed ordering, plus the tool that serves them and the card pin that
/// precedes them. The DFU flash sits immediately ahead of the mixer values because open question
/// 2 resolved the collision between §5.5's "brick-capable last" and guide 4's statement that the
/// levels are validated against firmware 2.0.10: brick-capable is split by <i>recovery cost</i>,
/// and a bricked mic array leaves the Pi bootable and is recoverable by hand at the frame.
/// </para>
/// <para>
/// <b>The +20 dB is a chain, and the chain is what this block encodes.</b> Amplifier on, both
/// playback switches unmuted, <c>PCM,0</c> at 0 dB, <c>PCM,1</c> at 0 dB, and the whole set
/// persisted. Four of those five are invisible in isolation — a frame missing any one of them is
/// "working, but quiet" — which is why each is its own resource with its own delta rather than
/// one "set the volume" step.
/// </para>
/// </remarks>
public static class AudioCatalog
{
    /// <summary>The catalog id of the stereo playback stage's level.</summary>
    public const string Pcm0VolumeResourceName = "audio.mixer.pcm0-playback-volume";

    /// <summary>The catalog id of the second, mono playback stage's level.</summary>
    public const string Pcm1VolumeResourceName = "audio.mixer.pcm1-playback-volume";

    /// <summary>The catalog id of the stereo playback stage's switch.</summary>
    public const string Pcm0SwitchResourceName = "audio.mixer.pcm0-playback-switch";

    /// <summary>The catalog id of the mono playback stage's switch.</summary>
    public const string Pcm1SwitchResourceName = "audio.mixer.pcm1-playback-switch";

    /// <summary>The catalog id of the microphone level.</summary>
    public const string HeadsetCaptureResourceName = "audio.mixer.headset-capture-volume";

    /// <summary>Fleet setting carrying the speaker level (§3.4).</summary>
    public const string PlaybackVolumeKey = "audio.playbackVolume";

    /// <summary>Fleet setting carrying the microphone level (§3.4).</summary>
    public const string CaptureVolumeKey = "audio.captureVolume";

    /// <summary>The level every control in the block converges on: 60, which is 0.00 dB.</summary>
    public const string DefaultLevel = "60";

    /// <summary>The stereo playback stage — the obvious one.</summary>
    public static MixerControlSpec Pcm0 { get; } = new()
    {
        Control = "PCM",
        Index = 0,
        Playback = true,
        SettingKey = PlaybackVolumeKey,
        DefaultValue = DefaultLevel,
    };

    /// <summary>The second, mono playback stage — the one that ships at −20 dB.</summary>
    public static MixerControlSpec Pcm1 { get; } = new()
    {
        Control = "PCM",
        Index = 1,
        Playback = true,
        SettingKey = PlaybackVolumeKey,
        DefaultValue = DefaultLevel,
    };

    /// <summary>The stereo capture control.</summary>
    public static MixerControlSpec Headset0 { get; } = new()
    {
        Control = "Headset",
        Index = 0,
        Playback = false,
        SettingKey = CaptureVolumeKey,
        DefaultValue = DefaultLevel,
    };

    /// <summary>The mono capture control.</summary>
    public static MixerControlSpec Headset1 { get; } = new()
    {
        Control = "Headset",
        Index = 1,
        Playback = false,
        SettingKey = CaptureVolumeKey,
        DefaultValue = DefaultLevel,
    };

    /// <summary>Builds the block, in catalog order.</summary>
    public static IReadOnlyList<IResource> Build(DeviceCatalogContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tool = new XvfHost(context.Files, context.Processes, context.Session);
        var installer = new XvfHostInstaller(
            context.Files,
            context.XvfHostDownload ?? UnreachableXvfHostDownload.Instance,
            context.Log);

        var mixer = new AlsaMixer(context.Processes, context.Files);
        var session = new SessionAudio(context.Session, context.Files, context.Clock);

        var pcm0Switch = new MixerSwitchResource(
            Pcm0SwitchResourceName,
            mixer,
            session,
            Pcm0,
            "This frame's speaker is muted.",
            "A muted speaker is silent however loud the frame is set.",
            "Unmuting this frame's speaker.",
            [SndUsbAudioIndexResource.ResourceName]);

        var pcm1Switch = new MixerSwitchResource(
            Pcm1SwitchResourceName,
            mixer,
            session,
            Pcm1,
            "The second, hidden volume stage of this frame's speaker is muted.",
            "It is a separate switch from the obvious one, and while it is muted the speaker stays silent.",
            "Unmuting the second volume stage of this frame's speaker.",
            [SndUsbAudioIndexResource.ResourceName]);

        var pcm0Volume = new MixerVolumeResource(
            Pcm0VolumeResourceName,
            mixer,
            session,
            context.Values,
            [Pcm0],
            requireOn: false,
            "This frame's speaker is not set to the level it was tested at.",
            "Too quiet and nobody hears the call; louder than this and the sound distorts.",
            "Setting this frame's speaker to the level it was tested at.",
            [
                SndUsbAudioIndexResource.ResourceName,
                Pcm0SwitchResourceName,

                // The measured edge, and the one the catalog had backwards. This control has a
                // second owner in the login session, and while that owner is asking for -23 dB no
                // amount of `amixer sset` keeps 60 across a boot — measured on the frame, three
                // attempts and an escalation. So WirePlumber is told first and this stage is
                // asserted afterwards, which is the only order in which it can hold. `PCM,1` takes
                // no such edge: it is a second hardware stage no route volume reaches, and it was
                // never observed away from 60.
                WirePlumberVolumeResource.ResourceName,
            ]);

        var pcm1Volume = new MixerVolumeResource(
            Pcm1VolumeResourceName,
            mixer,
            session,
            context.Values,
            [Pcm1],
            requireOn: false,
            "The second, easily-missed volume stage of this frame's speaker is turned down.",
            "It costs about eighteen decibels — the difference between a frame you can barely hear and a frame you can.",
            "Turning up the second volume stage of this frame's speaker, the one that is not the obvious control.",
            [
                SndUsbAudioIndexResource.ResourceName,
                Pcm1SwitchResourceName,
            ]);

        var capture = new MixerVolumeResource(
            HeadsetCaptureResourceName,
            mixer,
            session,
            context.Values,
            [Headset0, Headset1],
            requireOn: true,
            "This frame's microphones are not set to the level they were tested at.",
            "If they are turned down, the person at the other end cannot hear anyone in this room.",
            "Setting this frame's microphones to the level they were tested at.",
            [SndUsbAudioIndexResource.ResourceName]);

        return
        [
            // Position 22 of the catalog's ordering, but declared here rather than with the
            // package block: it is not an apt package, and it exists only to serve the two
            // resources below it.
            new XvfHostToolResource(tool, context.Files, installer),

            // Positions 54–61. `firmware.xvf3800.version` used to sit at the head of this block,
            // and decision 90 took it out of the graph entirely: a DFU flash is the only Act that
            // could ever converge it, this product will never perform one unattended, and a
            // resource with no Act that can succeed halts the pass instead of reporting. What it
            // observed is now reported beside the loop by `ArrayFirmwareReporter`.
            new XvfAmplifierResource(tool, context.Files),
            pcm0Switch,
            pcm1Switch,

            // Ahead of the mixer levels, not between them and the file that persists them. The
            // hardware mixer has a second owner living in the login session, and that owner has to
            // be brought to the frame's number *before* the stage it holds down is asserted —
            // otherwise the stage spends its whole budget losing an argument nobody has had yet.
            // The declaration order is only the reading order; the DAG edge on pcm0Volume is what
            // actually enforces it.
            new WirePlumberVolumeResource(session, context.Session, mixer, pcm0Volume),

            pcm0Volume,
            pcm1Volume,
            capture,

            // Last of the audio block, because `alsactl store` records whatever is live and must
            // therefore run after every owner of every stage has written.
            new AlsaStoredStateResource(context.Processes, context.Files, [pcm0Volume, pcm1Volume, capture]),
        ];
    }
}
