using FrameLink.Agent.Hosting;
using FrameLink.Agent.Identity;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>agent.version</c> — the installed binary is the one the Fleet Manager serves.
/// </summary>
/// <remarks>
/// <para>
/// §2.8: "The applied version is an ordinary resource — the root of the DAG, with the same
/// verification and escalation machinery as everything else." This is that resource, and the
/// catalog schedules it at position 1, ahead of even the display carve-out.
/// </para>
/// <para>
/// <b>It matches; it never compares.</b> There is no "is the served version newer" here, and there
/// must not be: reverting the container tag has to revert the fleet within the hour, so a
/// downgrade is ordinary convergence rather than an error to refuse. That rule lives in
/// <see cref="Update.UpdateService"/> and is restated here because this resource is the other place
/// somebody would be tempted to add version ordering.
/// </para>
/// <para>
/// <b>Silence is not a mismatch.</b> A frame whose Fleet Manager has never answered does not know
/// what version it should be running, and saying "expected x, observed y" of a frame that could
/// not ask is the false diagnosis <see cref="ObservationOutcome.Unevaluable"/> exists to prevent.
/// So an unknown served version is unevaluable: the loop leaves the attempt budget alone, nothing
/// reboots, and the frame goes on converging everything else — which is §1.2.2, a frame must
/// provision with the server unreachable.
/// </para>
/// <para>
/// <b>Being first in the catalog is not the same as blocking everything behind it.</b> No other
/// resource declares an edge on this one, because the catalog's <c>—</c> already <i>means</i>
/// "depends on <c>agent.version</c> and nothing else" and the implementation spells that as an
/// empty <see cref="IResource.DependsOn"/>. Materialising the edges would mean a frame that cannot
/// reach its Fleet Manager reports all seventy-eight other resources as
/// <see cref="ResourceStatusKind.Blocked"/> and provisions nothing at all — the exact opposite of
/// what §1.2.2 requires. Declaration order gives it position 1; the DAG gives it no veto.
/// </para>
/// <para>
/// <b>The Act asks rather than does, and that is the design.</b> Fetching, verifying and swapping
/// the binary belongs to the out-of-band hourly loop (§2.8), which is the <i>mechanism</i>; this
/// Act brings the next check forward, exactly as the handshake does. If the request is lost the
/// hourly tick still converges the frame, so the resource can never be the thing that makes an
/// update fail to happen.
/// </para>
/// <para>
/// <b>What is deliberately not here: an "updates are switched off" branch.</b> §2.8 makes updates
/// operator-disableable, but nothing on this frame reads such a setting yet — the out-of-band loop
/// runs unconditionally — and a resource that reported "in sync, updates are off" while the loop
/// went on updating would be a lie on the frame's own screen. The two move together or not at all.
/// </para>
/// </remarks>
public sealed class AgentVersionResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "agent.version";

    private readonly string _running;
    private readonly Func<string?> _served;
    private readonly Action _converge;

    /// <summary>Creates the resource.</summary>
    /// <param name="runningVersion">The version of the binary that is executing.</param>
    /// <param name="servedVersion">
    /// What the Fleet Manager's versionless update endpoint last reported, or null if it has never
    /// answered. Read at observation time rather than captured, so a fleet rolled back between two
    /// passes is noticed on the second one.
    /// </param>
    /// <param name="converge">Brings the next out-of-band check forward.</param>
    public AgentVersionResource(string runningVersion, Func<string?> servedVersion, Action converge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningVersion);
        ArgumentNullException.ThrowIfNull(servedVersion);
        ArgumentNullException.ThrowIfNull(converge);

        _running = runningVersion;
        _served = servedVersion;
        _converge = converge;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame is running different software from the one its Fleet Manager has.";

    /// <inheritdoc/>
    public string WhyItMatters => "The two have to be the same version to understand each other at all.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_served() is not { Length: > 0 } served)
        {
            return ValueTask.FromResult(ResourceObservation.Unevaluable(
                _running,
                "the Fleet Manager has not said which version it serves"));
        }

        return ValueTask.FromResult(new ResourceObservation(
            string.Equals(served, _running, StringComparison.Ordinal),
            served,
            _running));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _converge();

        return ValueTask.FromResult(new ResourceAction(
            $"ask the update loop to converge from {_running} to {_served() ?? "the served version"} now",
            "Fetching the version of this frame's software that its Fleet Manager has."));
    }
}

/// <summary>
/// <c>agent.keypair</c> — this frame still has the identity it was born with.
/// </summary>
/// <remarks>
/// <para>
/// §2.9 and §3.3: the keypair is generated on first boot, lives in a root-only file under
/// <c>/var/lib/fl-agent</c>, and its public-key fingerprint <i>is</i> the immutable device id.
/// Adoption binds that key to a record, and every later reconnect authenticates with it.
/// </para>
/// <para>
/// <b>Three things are observed, and the third is the one worth having.</b> The file being present
/// is the obvious check. The mode being owner-only is §2.9's actual requirement. But
/// <i>fingerprint stable across boots</i> is what catches the failure that costs something: a
/// frame whose identity changed is a frame that has silently become a different device, dropped
/// out of its Fleet Manager record and reappeared in the adoption queue — and every symptom of
/// that reads as "the server forgot me" rather than as "I forgot myself". Recording the
/// fingerprint beside the key turns it from an inference into a comparison.
/// </para>
/// <para>
/// <b>A missing key file is refused, not repaired, and that is deliberate.</b>
/// <see cref="DeviceKeyStore"/> already refuses to regenerate over a damaged key for the same
/// reason, and the private half of <see cref="DeviceKey"/> is unreachable by construction so
/// nothing here could rewrite the original even if it wanted to. Generating a fresh keypair would
/// be a new identity wearing the old frame's name — §3.3 makes that a confirmed, destructive
/// decommission a person takes, never something a repair pass does at three in the morning. So the
/// Act says what is wrong, the escalation ladder tells an operator, and a human decides.
/// </para>
/// <para>
/// <b>The mode is read and written through one seam, not two.</b>
/// <see cref="ISystemFiles.ModeOf"/> and <see cref="ISystemFiles.SetMode"/> are the pair that
/// exists because "a mode bit is a setting in its own right" — the same pair
/// <c>labwc.autostart.executable</c> uses. Reading through one interface and repairing through
/// another would mean an Act whose effect its own Verify cannot see, which is the shape of failure
/// §2.3 makes Observe and Verify one method to prevent.
/// </para>
/// <para>
/// <b>And it is quiet where it cannot see.</b> <see cref="ISystemFiles.ModeOf"/> answers null when
/// the filesystem cannot say, and null is treated as "not observable here" rather than as drift,
/// because a resource that reported a permission fault it had not actually measured would be
/// exactly the write-only optimism §2.4 refuses, pointed the other way. On a frame it can always
/// tell.
/// </para>
/// </remarks>
public sealed class AgentKeypairResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "agent.keypair";

    /// <summary>Where the fingerprint of the key this frame booted with is recorded.</summary>
    public const string FingerprintFileName = "device-fingerprint";

    /// <summary>§2.9's "root-only file": owner read and write, nothing else.</summary>
    public const UnixFileMode SecretMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly IStateStore _store;
    private readonly ISystemFiles _files;
    private readonly Func<string> _deviceId;

    /// <summary>Creates the resource.</summary>
    /// <param name="store">The agent's own state directory, where the key lives.</param>
    /// <param name="files">How the key file's mode is read back and re-applied.</param>
    /// <param name="deviceId">The identity this process is actually running as.</param>
    public AgentKeypairResource(IStateStore store, ISystemFiles files, Func<string> deviceId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(deviceId);

        _store = store;
        _files = files;
        _deviceId = deviceId;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "This frame's own identity is missing, readable by others, or is not the one it had before.";

    /// <inheritdoc/>
    public string WhyItMatters => "It is how your Fleet Manager knows this frame is this frame and not another one.";

    /// <summary>The path of the private key file.</summary>
    public string KeyPath => _store.PathOf(DeviceKeyStore.KeyFileName);

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = _deviceId();
        var expected = $"{current} in {KeyPath}, owner-only, unchanged since the last boot";

        if (!_store.Exists(DeviceKeyStore.KeyFileName))
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                expected,
                $"{KeyPath} is not there, so this frame's identity is only in memory"));
        }

        var wrong = new List<string>(2);

        // A mode wider than 0600, and only that. Narrower is somebody being careful, and null is a
        // filesystem that cannot say — neither is a fault this resource has measured.
        if (_files.ModeOf(KeyPath) is { } mode && (mode & ~SecretMode) != 0)
        {
            wrong.Add($"the key file is {Describe(mode)}, not owner-only");
        }

        var recorded = _store.ReadText(FingerprintFileName)?.Trim();
        if (recorded is null or { Length: 0 })
        {
            wrong.Add("no fingerprint has been recorded yet, so nothing can prove the identity has not moved");
        }
        else if (!string.Equals(recorded, current, StringComparison.Ordinal))
        {
            // The serious one. Said in full rather than as a diff, because whoever reads this has
            // to be able to tell which of the two identities their Fleet Manager knows about.
            wrong.Add($"this frame booted as {recorded} and is now {current}");
        }

        return ValueTask.FromResult(new ResourceObservation(
            wrong.Count == 0,
            expected,
            wrong.Count == 0 ? expected : string.Join("; ", wrong)));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_store.Exists(DeviceKeyStore.KeyFileName))
        {
            return ValueTask.FromResult(new ResourceAction(
                $"refused to recreate {KeyPath} — a new keypair would be a new device, not this one",
                "This frame has lost the file that proves who it is. A new one cannot be invented, so someone has to decide whether to give this frame a fresh identity."));
        }

        var current = _deviceId();
        var recorded = _store.ReadText(FingerprintFileName)?.Trim();

        _files.SetMode(KeyPath, SecretMode);

        if (recorded is { Length: > 0 } && !string.Equals(recorded, current, StringComparison.Ordinal))
        {
            // Recording the new one would erase the only evidence of the change, which is the
            // whole value of the file. The observation stays drifted, the ladder escalates, and an
            // operator adopts the frame under its new id or restores the old key.
            return ValueTask.FromResult(new ResourceAction(
                $"locked the mode on {KeyPath}; left the recorded fingerprint at {recorded} rather than overwriting it with {current}",
                "This frame's identity has changed. It has not quietly accepted the new one — someone needs to look."));
        }

        _store.WriteText(FingerprintFileName, current);

        return ValueTask.FromResult(new ResourceAction(
            $"chmod 0600 {KeyPath} and record {current} in {_store.PathOf(FingerprintFileName)}",
            "Locking this frame's identity file down so only it can read it, and writing down which identity it is."));
    }

    /// <summary>A mode as <c>0640</c>, for a delta a person can read.</summary>
    private static string Describe(UnixFileMode mode) => "0" + Convert.ToString((int)mode & 0x1FF, 8).PadLeft(3, '0');
}
