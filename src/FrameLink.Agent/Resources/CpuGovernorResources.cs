using FrameLink.Agent.Hosting;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>unit.cpu-performance.content</c> — the oneshot unit that pins the CPU governor.
/// </summary>
/// <remarks>
/// From guide 12 step 7. A <b>system</b> unit rather than a user unit — the only one of the
/// frame's own units that is — because it has to run before any session exists.
/// </remarks>
public sealed class CpuGovernorUnitResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.cpu-performance.content";

    /// <summary>The unit's name, as systemd knows it.</summary>
    public const string UnitName = "cpu-performance.service";

    /// <summary>Where an operator-installed system unit goes.</summary>
    public const string UnitPath = "/etc/systemd/system/" + UnitName;

    private readonly ISystemFiles _files;
    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public CpuGovernorUnitResource(ISystemFiles files, ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(systemControl);

        _files = files;
        _systemControl = systemControl;
    }

    /// <summary>The unit text this resource converges on, verbatim from guide 12 step 7.</summary>
    public static string DesiredContent =>
        "[Unit]\n"
        + "Description=FrameLink: pin the CPU governor to performance\n"
        + "After=multi-user.target\n"
        + "\n"
        + "[Service]\n"
        + "Type=oneshot\n"
        + "ExecStart=/bin/sh -c 'echo performance | tee /sys/devices/system/cpu/cpufreq/policy*/scaling_governor'\n"
        + "\n"
        + "[Install]\n"
        + "WantedBy=multi-user.target\n";

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "The setting that keeps this frame running at full speed is missing.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the frame slows itself down and video calls stutter.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var actual = _files.ReadText(UnitPath);
        var matches = string.Equals(
            JournalStorageResource.ShortHash(actual ?? string.Empty),
            JournalStorageResource.ShortHash(DesiredContent),
            StringComparison.Ordinal);

        return ValueTask.FromResult(new ResourceObservation(
            matches,
            $"{UnitPath} {JournalStorageResource.ShortHash(DesiredContent)}",
            actual is null ? $"{UnitPath} absent" : $"{UnitPath} {JournalStorageResource.ShortHash(actual)}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        _files.WriteText(UnitPath, DesiredContent);

        // systemd will not notice a new unit file on its own, and the enable resource that
        // depends on this one would fail against a unit systemd has never read.
        await _systemControl.RunAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            $"write {UnitPath} and run systemctl daemon-reload",
            "Adding the instruction that tells this frame to run at full speed from the moment it starts.");
    }
}

/// <summary>
/// <c>unit.cpu-performance.enabled</c> — the unit is wired into the boot.
/// </summary>
/// <remarks>
/// A separate resource from the file, per §2.2's granularity rule: a unit that exists and is not
/// enabled is a different differential diagnosis from a unit that is missing, and the fix is a
/// different command.
/// </remarks>
public sealed class CpuGovernorUnitEnabledResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "unit.cpu-performance.enabled";

    private readonly ISystemControl _systemControl;

    /// <summary>Creates the resource.</summary>
    public CpuGovernorUnitEnabledResource(ISystemControl systemControl)
    {
        ArgumentNullException.ThrowIfNull(systemControl);
        _systemControl = systemControl;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [CpuGovernorUnitResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The full-speed setting exists but is switched off.";

    /// <inheritdoc/>
    public string WhyItMatters => "A setting that is not switched on does nothing when the frame starts.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var result = await _systemControl
            .RunAsync(["is-enabled", CpuGovernorUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        // `is-enabled` exits non-zero for `disabled` and for `not-found` alike, and its stdout
        // is the answer in both cases. Reading the text rather than the exit code is what keeps
        // "disabled" and "there is no such unit" two different observed values.
        var observed = result.Output.Trim();
        observed = observed.Length == 0 ? "no answer from systemctl" : observed.Split('\n')[0].Trim();

        return new ResourceObservation(
            string.Equals(observed, "enabled", StringComparison.Ordinal),
            "enabled",
            observed);
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var result = await _systemControl
            .RunAsync(["enable", CpuGovernorUnitResource.UnitName], cancellationToken)
            .ConfigureAwait(false);

        return new ResourceAction(
            $"systemctl enable {CpuGovernorUnitResource.UnitName}"
                + (result.Succeeded ? string.Empty : $" (refused: {result.Output})"),
            "Switching on the full-speed setting so it takes effect every time the frame starts.");
    }
}

/// <summary>
/// <c>cpu.governor.performance</c> — every CPU is actually running at the chosen governor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The archetype for the whole catalog.</b> §2.4 cites this exact case as the reason every
/// resource reboots: on v1 the <c>cpufreq.default_governor=performance</c> kernel parameter
/// reached <c>/proc/cmdline</c> and the governor still came up <c>ondemand</c>. The setting and
/// its post-boot effect are two resources precisely because they can disagree, and this is the
/// one that reads the effect.
/// </para>
/// <para>
/// <b>The Act is not what fixes it.</b> Writing the governor into sysfs takes effect
/// immediately and is erased by the next boot; what makes it stick is the enabled unit this
/// resource depends on. So the write is a way of making the frame correct <i>now</i>, and the
/// reboot that follows is the test of whether the unit does its job. A unit that is enabled but
/// broken shows up here as a resource that passes its Act every time and fails its Verify every
/// time, walks the escalation ladder, and reaches an operator — which is exactly what should
/// have happened in v1 and did not.
/// </para>
/// <para>
/// Every policy is read, not just <c>policy0</c>: a partial application is a distinct fault and
/// a glob that stopped at the first CPU would hide it.
/// </para>
/// </remarks>
public sealed class CpuGovernorResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "cpu.governor.performance";

    /// <summary>Fleet setting carrying the governor (§3.4).</summary>
    public const string SettingKey = "power.cpuGovernor";

    /// <summary>Mains-powered kiosk, no battery to protect.</summary>
    public const string DefaultGovernor = "performance";

    /// <summary>Where the kernel publishes one directory per frequency policy.</summary>
    public const string PolicyRoot = "/sys/devices/system/cpu/cpufreq";

    private readonly ISystemFiles _files;
    private readonly FleetValues _values;

    /// <summary>Creates the resource.</summary>
    public CpuGovernorResource(ISystemFiles files, FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(values);

        _files = files;
        _values = values;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [CpuGovernorUnitEnabledResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame is not running at full speed.";

    /// <inheritdoc/>
    public string WhyItMatters => "A frame that slows itself down drops frames during a video call.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = _values.Get(SettingKey, DefaultGovernor);
        var policies = Policies();

        if (policies.Count == 0)
        {
            // No cpufreq at all — a container, a virtual agent, a workstation. Reporting drift
            // would put a virtual agent into a permanent repair loop over hardware it does not
            // have; reporting a specific observed value keeps that legible in telemetry.
            return ValueTask.FromResult(new ResourceObservation(
                true,
                desired,
                "no cpufreq policies on this machine"));
        }

        var wrong = new List<string>();
        foreach (var policy in policies)
        {
            var actual = _files.ReadText(policy + "/scaling_governor")?.Trim();
            if (!string.Equals(actual, desired, StringComparison.Ordinal))
            {
                wrong.Add($"{policy[(policy.LastIndexOf('/') + 1)..]}={actual ?? "unreadable"}");
            }
        }

        return ValueTask.FromResult(new ResourceObservation(
            wrong.Count == 0,
            $"{desired} on all {policies.Count} policies",
            wrong.Count == 0 ? $"{desired} on all {policies.Count} policies" : string.Join(", ", wrong)));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = _values.Get(SettingKey, DefaultGovernor);
        var written = 0;

        foreach (var policy in Policies())
        {
            _files.WriteText(policy + "/scaling_governor", desired);
            written++;
        }

        return ValueTask.FromResult(new ResourceAction(
            $"write '{desired}' to {written} scaling_governor files under {PolicyRoot}",
            $"Setting every processor on this frame to '{desired}' now — the restart afterwards is what proves it stays that way."));
    }

    private List<string> Policies()
    {
        var policies = new List<string>();
        foreach (var directory in _files.ListDirectories(PolicyRoot))
        {
            var name = directory[(directory.LastIndexOf('/') + 1)..];
            if (name.StartsWith("policy", StringComparison.Ordinal))
            {
                policies.Add(directory);
            }
        }

        return policies;
    }
}
