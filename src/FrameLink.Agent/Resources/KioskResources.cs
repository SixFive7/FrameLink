using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Kiosk;
using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// <c>kiosk.binary.pinned-release</c> — the pinned Immich Kiosk executable is on this frame.
/// </summary>
/// <remarks>
/// <para>
/// Guide 9 step 3's <c>image: ghcr.io/damongolding/immich-kiosk:0.39.3</c>, with the container
/// taken out from under it. §2.1 keeps Immich Kiosk upstream — it is a mature product with a team
/// behind it and v2 does not reimplement it — and changes only how it arrives: a pinned release,
/// checksum-verified, supervised as a child process. The v1 pin was <c>0.39.3</c> and this one is
/// <c>0.42.0</c>, because the catalog pin is a release decision rather than a copy of v1;
/// <c>deploy/immich-kiosk/compose.yaml</c> still names the v1 version and is a v1 artifact.
/// </para>
/// <para>
/// <b>Fetched, never vendored, and that is a licensing decision with a technical consequence.</b>
/// Immich Kiosk is AGPL-3.0. Fetching from upstream rather than redistributing keeps the
/// source-offer obligation with the publisher, off this project and off every self-hoster — which
/// is why this resource has a URL in it at all, and why the Fleet-Manager mirror §2.1 mentions is a
/// later operator setting rather than the default.
/// </para>
/// <para>
/// <b>Observe hashes the file, every pass.</b> The catalog asks for "file present, <c>sha256sum</c>
/// matches" and the hash is the whole of it: a note recording that an install succeeded would
/// survive a boot the file itself did not, which is exactly the claim §2.4 refuses. The catalog
/// also lists <c>&lt;binary&gt; --version</c> and that half is deliberately not implemented — the
/// v0.42.0 executable has no such flag (it uses Go's standard <c>flag</c> package and the string
/// <c>--version</c> does not occur in it), and a digest match already answers the question a
/// version flag would have asked, tautologically and without executing an unverified file.
/// </para>
/// </remarks>
public sealed class KioskBinaryResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "kiosk.binary.pinned-release";

    private readonly KioskInstaller _installer;

    /// <summary>Creates the resource.</summary>
    public KioskBinaryResource(KioskInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        _installer = installer;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public string Detected => "The photo slideshow program is missing from this frame, or is the wrong one.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the frame has nothing to show pictures with.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var pin = _installer.Pin;
        var expected = $"Immich Kiosk {pin.Version} at {_installer.TargetPath}, sha256 {pin.BinarySha256}";
        var digest = await _installer.InstalledDigestAsync(cancellationToken).ConfigureAwait(false);

        if (digest is null)
        {
            return new ResourceObservation(false, expected, $"nothing at {_installer.TargetPath}");
        }

        if (!string.Equals(digest, pin.BinarySha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ResourceObservation(false, expected, $"a different binary, sha256 {digest}");
        }

        return await _installer.IsInstalledAsync(cancellationToken).ConfigureAwait(false)
            ? new ResourceObservation(true, expected, $"Immich Kiosk {pin.Version}, sha256 {digest}")
            : new ResourceObservation(false, expected, $"the right binary, sha256 {digest}, but it is not executable");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var pin = _installer.Pin;
        var result = await _installer.InstallAsync(cancellationToken).ConfigureAwait(false);

        var change = $"fetch {pin.ArchiveUrl}, verify sha256 {pin.ArchiveSha256}, install {_installer.TargetPath}";

        return new ResourceAction(
            result is KioskInstallResult.Installed or KioskInstallResult.AlreadyInstalled
                ? change
                : $"{change} (refused: {result})",
            "Downloading the photo slideshow program and checking, byte for byte, that it arrived intact.");
    }
}

/// <summary>
/// <c>kiosk.offline-cache.dir</c> — there is somewhere for the cached photos to go.
/// </summary>
/// <remarks>
/// <para>
/// Guide 9 step 2's <c>mkdir -p ~/immich-kiosk/offline-assets</c>. The v1
/// <c>chown -R 65532:65532</c> that follows it is a <b>Docker artifact</b> — 65532 is the
/// container's non-root user — and does not carry over; under v2 the requirement is whatever uid
/// the agent runs the child as, which is the agent's own. Transcribing 65532 onto a frame with no
/// container would produce a directory the child cannot write and a cache that silently never
/// fills.
/// </para>
/// <para>
/// <b>The path is not a setting because Kiosk does not have one.</b> The v0.42.0 executable holds
/// the literal <c>./offline-assets</c> — relative — so the cache lands beside the child's working
/// directory and nowhere else. Under Docker that resolved to <c>/offline-assets</c> and the Compose
/// file bind-mounted the real directory over it; here the working directory <i>is</i> the real
/// directory, which is simpler and has one fewer thing to disagree.
/// </para>
/// <para>
/// <b>A write probe, not a <c>test -d</c>.</b> The catalog asks for both, and the second is the one
/// that catches the failure worth catching: a directory that exists and is not writable is exactly
/// what the 65532 <c>chown</c> would leave behind, and Kiosk's response to it is to carry on
/// serving live photos and cache nothing — a frame that looks perfect until the day Immich is
/// unreachable.
/// </para>
/// </remarks>
public sealed class KioskOfflineCacheResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "kiosk.offline-cache.dir";

    /// <summary>The file the write probe creates and removes.</summary>
    public const string ProbeName = ".fl-agent-write-probe";

    private readonly ISystemFiles _files;
    private readonly KioskProcess _kiosk;

    /// <summary>Creates the resource.</summary>
    public KioskOfflineCacheResource(ISystemFiles files, KioskProcess kiosk)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(kiosk);

        _files = files;
        _kiosk = kiosk;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [KioskBinaryResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "This frame has nowhere to keep its own copy of the photos.";

    /// <inheritdoc/>
    public string WhyItMatters => "Without it the screen goes blank whenever the photo server cannot be reached.";

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = _kiosk.OfflineCachePath;
        var expected = $"{path} exists and is writable";

        if (!_files.DirectoryExists(path))
        {
            return ValueTask.FromResult(new ResourceObservation(false, expected, "it is not there"));
        }

        var probe = Path.Combine(path, ProbeName);

        try
        {
            _files.WriteText(probe, string.Empty);
            _files.DeleteFile(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                expected,
                $"it is there but cannot be written: {exception.Message}"));
        }

        return ValueTask.FromResult(new ResourceObservation(true, expected, $"{path} exists and took a test write"));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _files.EnsureDirectory(_kiosk.OfflineCachePath);

        return ValueTask.FromResult(new ResourceAction(
            $"create {_kiosk.OfflineCachePath}",
            "Making the folder where this frame keeps its own copy of the photos."));
    }
}

/// <summary>One of the four settings guide 9's Compose file carried.</summary>
public sealed record KioskConfigSpec
{
    /// <summary>The catalog id.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The file inside the state store that records the value.</summary>
    public required string FileName { get; init; }

    /// <summary>The Compose environment variable this became.</summary>
    public required string Variable { get; init; }

    /// <summary>The fleet setting that supplies it (§3.4).</summary>
    public required string SettingKey { get; init; }

    /// <summary>The catalog default, or empty when only the Fleet Manager can name one.</summary>
    public string Fallback { get; init; } = string.Empty;

    /// <summary>What was detected, for a reader with no computer experience (§2.7 item 1).</summary>
    public required string Detected { get; init; }

    /// <summary>Why it matters, in one short sentence (§2.7 item 2).</summary>
    public required string WhyItMatters { get; init; }

    /// <summary>Plain-language gloss on the change (§2.7 item 3).</summary>
    public required string Gloss { get; init; }

    /// <summary>Ids that must be in sync first.</summary>
    public required IReadOnlyList<string> DependsOn { get; init; }

    /// <summary>Whether the value must never appear in a delta, a log or on a screen.</summary>
    public bool Secret { get; init; }
}

/// <summary>
/// <c>kiosk.config.*</c> — the four Compose settings, held by the agent instead of by a YAML file.
/// </summary>
/// <remarks>
/// <para>
/// Guide 9 step 2's Compose file is superseded (§2.1): the file is gone, the container is gone, and
/// its four <c>environment:</c> entries survive as four resources whose values the Fleet Manager
/// supplies and the agent records under <c>/var/lib/fl-agent</c>. What the child runs on is its own
/// environment block, which <see cref="KioskCatalog.SettingsFrom"/> renders from these records — so
/// there is one copy of each value on the frame, and it is the copy the reconciler owns.
/// </para>
/// <para>
/// <b>Observe compares two things, and only one of them can fail the resource.</b> The recorded
/// value against the issued value is the resource: that is what drifts and what an Act can fix. The
/// <i>running child's own environment</i>, read from <c>/proc/&lt;pid&gt;/environ</c>, is the
/// cross-check the catalog asks for, and it is asymmetric on purpose — an environment that
/// <i>disagrees</i> is drift, because the slideshow is demonstrably running on a value nothing
/// issued, while <i>no child at all</i> is not, because a slideshow that has not started yet says
/// nothing about whether the value is right. Failing on absence would make all four unfixable on
/// exactly the frame that needs them, which is the same asymmetry <c>app.config.*</c> makes about
/// the page's report.
/// </para>
/// <para>
/// <b>Two of the four are gated on adoption and two are not</b>, and the catalog's rule decides it
/// rather than a preference: a resource names <c>agent.adoption</c> when the frame would otherwise
/// have to <i>guess</i>. The address of somebody's photo server and the key that reads it are
/// values this project cannot hold — and §3.3 means the key literally, since a pending device
/// receives no token and an API key is one. Offline mode and its asset count have catalog defaults
/// that are right on an unadopted frame, so they apply them and a later override is ordinary drift.
/// </para>
/// </remarks>
public sealed class KioskConfigResource : IResource
{
    private readonly IStateStore _store;
    private readonly FleetValues _values;
    private readonly ISystemFiles _files;
    private readonly KioskProcess _kiosk;
    private readonly KioskConfigSpec _spec;

    /// <summary>Creates the resource for <paramref name="spec"/>.</summary>
    public KioskConfigResource(
        IStateStore store,
        FleetValues values,
        ISystemFiles files,
        KioskProcess kiosk,
        KioskConfigSpec spec)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(spec);

        _store = store;
        _values = values;
        _files = files;
        _kiosk = kiosk;
        _spec = spec;
    }

    /// <inheritdoc/>
    public string Name => _spec.ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => _spec.DependsOn;

    /// <inheritdoc/>
    public string Detected => _spec.Detected;

    /// <inheritdoc/>
    public string WhyItMatters => _spec.WhyItMatters;

    /// <summary>The value the Fleet Manager has issued, or the catalog default.</summary>
    public string Desired => _values.Get(_spec.SettingKey, _spec.Fallback).Trim();

    /// <summary>The value this frame has recorded, or empty.</summary>
    public string Recorded => _store.ReadText(_spec.FileName)?.Trim() ?? string.Empty;

    /// <inheritdoc/>
    public ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = Desired;
        var recorded = Recorded;

        if (desired.Length == 0)
        {
            return ValueTask.FromResult(new ResourceObservation(
                true,
                $"no {_spec.SettingKey} issued by the Fleet Manager",
                "nothing issued, so nothing to converge on"));
        }

        if (!string.Equals(recorded, desired, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                Describe(desired),
                recorded.Length == 0 ? "nothing recorded on this frame" : Describe(recorded)));
        }

        if (KioskChildEnvironment.Read(_files, _kiosk.Pid) is { } environment
            && environment.TryGetValue(_spec.Variable, out var inUse)
            && !string.Equals(inUse, desired, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new ResourceObservation(
                false,
                Describe(desired),
                $"the running slideshow is using {Describe(inUse)}"));
        }

        return ValueTask.FromResult(new ResourceObservation(true, Describe(desired), Describe(recorded)));
    }

    /// <inheritdoc/>
    public ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var desired = Desired;

        if (_spec.Secret)
        {
            _store.WriteSecretAtomic(_spec.FileName, Encoding.UTF8.GetBytes(desired));
        }
        else
        {
            _store.WriteText(_spec.FileName, desired);
        }

        return ValueTask.FromResult(new ResourceAction(
            $"record {_spec.Variable} = {Describe(desired)} in {_store.PathOf(_spec.FileName)}",
            _spec.Gloss));
    }

    /// <summary>The value as it may be written down.</summary>
    /// <remarks>
    /// A delta travels to the journal, the frame's own screen and the Fleet Manager's device
    /// history, so a secret is described by what can be checked about it and never by its value.
    /// What can be checked about an API key is that it is there and that two keys are the same key,
    /// which a truncated digest answers without carrying the key anywhere.
    /// </remarks>
    private string Describe(string value) => _spec.Secret ? SecretFingerprint.Of(value) : value;
}

/// <summary>A key described by what can be checked about it, never by its value (§2.9).</summary>
public static class SecretFingerprint
{
    /// <summary>How many hex characters of the digest are shown.</summary>
    public const int Length = 12;

    /// <summary>A phrase naming the secret's standing and identity.</summary>
    public static string Of(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (secret.Length == 0)
        {
            return "no key";
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return $"a key, sha256:{digest[..Length]}";
    }
}

/// <summary>The environment a running child was started with.</summary>
/// <remarks>
/// <c>/proc/&lt;pid&gt;/environ</c> is the kernel's own record of the block a process was
/// <c>execve</c>d with, which is why the catalog names it: it is the one reading that cannot be
/// stale. A recorded value and a running process can disagree — a settings push that arrived after
/// the child started is exactly that case — and only this side of the comparison knows.
/// </remarks>
public static class KioskChildEnvironment
{
    /// <summary>Reads the block, or null when there is no child or it cannot be read.</summary>
    public static IReadOnlyDictionary<string, string>? Read(ISystemFiles files, int? pid)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (pid is not { } id)
        {
            return null;
        }

        var raw = files.ReadText(string.Create(CultureInfo.InvariantCulture, $"/proc/{id}/environ"));
        if (raw is null)
        {
            return null;
        }

        var block = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split > 0)
            {
                block[entry[..split]] = entry[(split + 1)..];
            }
        }

        return block;
    }
}

/// <summary>
/// <c>kiosk.listen-address</c> — the slideshow is answering where the frame's browser looks for it.
/// </summary>
/// <remarks>
/// <para>
/// Guide 9 step 2's <c>ports: "127.0.0.1:3000:3000"</c>. The port is fixed by the catalog rather
/// than settable, and has to be: <c>app.config.immich-kiosk-url</c> names it, so a settable port
/// would be a value that has to agree with itself in two places, on two sides of a reboot.
/// </para>
/// <para>
/// <b>⚠ The "and nowhere else" half of the catalog's wording is not achievable against Immich Kiosk
/// v0.42.0, and this resource does not pretend otherwise.</b> Upstream's <c>main.go</c> at tag
/// <c>v0.42.0</c> starts its server with <c>Address: fmt.Sprintf(":%v", baseConfig.Kiosk.Port)</c>
/// and its configuration struct carries a <c>port</c> field and no host or bind field at all — so
/// the process binds every interface, and the loopback restriction in v1 was being performed by
/// <b>Docker's port publishing</b>, not by Kiosk. Removing Docker removes it. What is left inside
/// this resource's reach is the port and the reachability, and both are asserted; the actual bind
/// set is read back and written verbatim into the observation on every pass, including when the
/// resource is in sync, so a wildcard binding is a fact on the screen and in the Fleet Manager's
/// history rather than a silence. Closing it needs something outside Kiosk — a packet filter, or an
/// upstream bind setting — and that is a decision with its own resource, not a line added here.
/// </para>
/// <para>
/// The frame has no inbound ports from outside the household (§3.6), so the exposure is to the
/// local network rather than to the internet. That bounds it; it does not make it the property the
/// catalog described.
/// </para>
/// </remarks>
public sealed class KioskListenAddressResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "kiosk.listen-address";

    private readonly IProcessRunner _processes;
    private readonly KioskProcess _kiosk;

    /// <summary>Creates the resource.</summary>
    public KioskListenAddressResource(IProcessRunner processes, KioskProcess kiosk)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(kiosk);

        _processes = processes;
        _kiosk = kiosk;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn => [KioskBinaryResource.ResourceName];

    /// <inheritdoc/>
    public string Detected => "The photo slideshow is not answering at the address this frame looks for it at.";

    /// <inheritdoc/>
    public string WhyItMatters => "The screen shows a spinner where the photos should be.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var port = _kiosk.Port;
        var expected = string.Create(CultureInfo.InvariantCulture, $"the slideshow answering on 127.0.0.1:{port}");

        if (_kiosk.Pid is not { } pid)
        {
            return new ResourceObservation(false, expected, "no slideshow process is running");
        }

        var listening = await _processes
            .RunAsync("ss", ["-tlnp"], cancellationToken)
            .ConfigureAwait(false);

        if (!listening.Succeeded)
        {
            return new ResourceObservation(
                false,
                expected,
                $"the listening sockets could not be read: {listening.Combined}");
        }

        var bound = ListenSockets.OwnedBy(listening.StandardOutput, pid, port);

        if (bound.Count == 0)
        {
            return new ResourceObservation(
                false,
                expected,
                string.Create(CultureInfo.InvariantCulture, $"pid {pid} is not listening on port {port}"));
        }

        var status = await LoopbackProbe.StatusAsync(port, "/", cancellationToken).ConfigureAwait(false);
        var where = string.Join(", ", bound);

        // The bind set is named whether or not the resource passes. §1.2 principle 3 forbids
        // repairing invisibly, and a wildcard bind this resource cannot close is exactly the kind
        // of abnormal condition that must be said out loud rather than assumed away.
        return status == 200
            ? new ResourceObservation(true, expected, $"LISTEN {where} (pid {pid}), answering HTTP 200 on loopback")
            : new ResourceObservation(
                false,
                expected,
                status is null
                    ? $"LISTEN {where} (pid {pid}) but the loopback connection failed"
                    : $"LISTEN {where} (pid {pid}), answering HTTP {status} on loopback");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var started = await _kiosk.RestartAsync(cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            string.Create(CultureInfo.InvariantCulture, $"restart the slideshow with KIOSK_PORT={_kiosk.Port}")
                + (started ? string.Empty : $" (refused: {_kiosk.LastFailure})"),
            "Restarting the photo slideshow so it answers where this frame's browser looks for it.");
    }
}

/// <summary>Reads <c>ss -tlnp</c> output.</summary>
/// <remarks>
/// The catalog names <c>ss -tlnp</c> specifically, and it is the right tool: it is the only reading
/// that ties a listening socket to the process that owns it, which is the whole question here — a
/// port answering is not evidence that <i>this</i> child is the one answering it.
/// </remarks>
public static class ListenSockets
{
    /// <summary>Every local address <paramref name="pid"/> is listening on <paramref name="port"/> at.</summary>
    public static IReadOnlyList<string> OwnedBy(string output, int pid, int port)
    {
        ArgumentNullException.ThrowIfNull(output);

        var owner = string.Create(CultureInfo.InvariantCulture, $"pid={pid},");
        var suffix = string.Create(CultureInfo.InvariantCulture, $":{port}");
        var found = new List<string>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(owner, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var field in fields)
            {
                if (field.EndsWith(suffix, StringComparison.Ordinal))
                {
                    found.Add(field);
                    break;
                }
            }
        }

        return found;
    }
}

/// <summary>
/// <c>kiosk.process.supervised</c> — the slideshow is running and serving.
/// </summary>
/// <remarks>
/// <para>
/// Guide 9 steps 3 and 4 together: the container's <c>restart: always</c> policy and the
/// <c>HTTP 200</c> check that proved it was more than merely up. Under v2 both are the agent's, and
/// <b>this is the resource that takes Docker off the frame</b> — the Engine, the Compose plugin,
/// containerd, the <c>docker0</c> bridge and <c>docker-selfheal</c> all exist to keep this one
/// process running, and a parent process does it without any of them.
/// </para>
/// <para>
/// <b>Two observations, one diagnosis boundary.</b> "Alive" and "answering" are folded into one
/// resource because the agent owns the lifetime and its response to either being false is the same:
/// start the child again. Splitting them would produce two resources with one Act, which is the
/// granularity rule (§2.2) read backwards. The failure this replaces is the reason Docker leaves —
/// a dead slideshow port drove the browser-renderer leak measured at ~50 MB/min, ending in an OOM
/// kill.
/// </para>
/// <para>
/// <b>The child's own relaunches are not this resource's drift.</b> <see cref="KioskProcess"/>
/// opens a §2.10 interlock window over this resource while it is bringing a child back, so a
/// reconcile pass that lands in the gap between an exit and a relaunch sees an excused transient
/// rather than a reason to stop the product and reboot. When the window's
/// <c>supervision.recoveryDeadline</c> expires the excuse ends and this becomes ordinary drift,
/// exactly as §2.10 clause 3 prescribes — supervision owns the transient, drift owns the persistent.
/// </para>
/// </remarks>
public sealed class KioskSupervisedResource : IResource
{
    /// <summary>The catalog id.</summary>
    public const string ResourceName = "kiosk.process.supervised";

    private readonly KioskProcess _kiosk;

    /// <summary>Creates the resource.</summary>
    public KioskSupervisedResource(KioskProcess kiosk)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        _kiosk = kiosk;
    }

    /// <inheritdoc/>
    public string Name => ResourceName;

    /// <inheritdoc/>
    public IReadOnlyList<string> DependsOn =>
    [
        KioskListenAddressResource.ResourceName,
        "kiosk.config.immich-url",
        "kiosk.config.immich-api-key",
    ];

    /// <inheritdoc/>
    public string Detected => "The photo slideshow is not running.";

    /// <inheritdoc/>
    public string WhyItMatters => "The frame has no pictures to show until it is.";

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var port = _kiosk.Port;
        var expected = string.Create(
            CultureInfo.InvariantCulture,
            $"the slideshow running and answering HTTP 200 on http://127.0.0.1:{port}/");

        if (_kiosk.Pid is not { } pid)
        {
            return new ResourceObservation(
                false,
                expected,
                _kiosk.LastFailure is { Length: > 0 } failure
                    ? $"it is not running — {failure}"
                    : "it is not running");
        }

        var status = await LoopbackProbe.StatusAsync(port, "/", cancellationToken).ConfigureAwait(false);

        return new ResourceObservation(
            status == 200,
            expected,
            status is null
                ? string.Create(CultureInfo.InvariantCulture, $"running as pid {pid} but not answering")
                : string.Create(CultureInfo.InvariantCulture, $"running as pid {pid}, answering HTTP {status}"));
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var started = await _kiosk.RestartAsync(cancellationToken).ConfigureAwait(false);

        return new ResourceAction(
            "restart the Immich Kiosk child process"
                + (started ? string.Empty : $" (refused: {_kiosk.LastFailure})"),
            "Starting the photo slideshow again so the frame has pictures to show.");
    }
}

/// <summary>The eight resources of the catalog's guide 9 block, in catalog order.</summary>
/// <remarks>
/// Docker is removed from the frame entirely (§2.1); what guide 9's Compose file configured
/// survives as these eight. Three of guide 9's four steps have no resource at all and that is the
/// point of the block: step 1 installed Docker Engine, step 2's Compose file and
/// <c>chown 65532</c> described a container, and step 3 started one. Step 4's <c>HTTP 200</c> check
/// is the only part that describes device state, and it is <c>kiosk.process.supervised</c>.
/// </remarks>
public static class KioskCatalog
{
    /// <summary>Fleet setting carrying the Immich server address.</summary>
    public const string ImmichUrlSettingKey = "immich.serverUrl";

    /// <summary>Fleet setting carrying the read-only Immich API key.</summary>
    public const string ImmichApiKeySettingKey = "immich.apiKey";

    /// <summary>Fleet setting carrying whether Kiosk caches for offline use.</summary>
    public const string OfflineModeSettingKey = "slideshow.offlineMode";

    /// <summary>Fleet setting carrying how many assets that cache holds.</summary>
    public const string OfflineAssetCountSettingKey = "slideshow.offlineAssetCount";

    /// <summary>Guide 9's measured value.</summary>
    public const string DefaultOfflineAssetCount = "200";

    /// <summary>The four <c>kiosk.config.*</c> specs, in the order the catalog lists them.</summary>
    public static IReadOnlyList<KioskConfigSpec> Specs { get; } =
    [
        new KioskConfigSpec
        {
            ResourceName = "kiosk.config.immich-url",
            FileName = "kiosk.immich-url",
            Variable = "KIOSK_IMMICH_URL",
            SettingKey = ImmichUrlSettingKey,
            DependsOn = [KioskBinaryResource.ResourceName, AdoptionResource.ResourceName],
            Detected = "This frame does not know where its photos are kept.",
            WhyItMatters = "Without it there are no pictures to show at all.",
            Gloss = "Telling this frame the address of the computer that holds the family photos.",
        },
        new KioskConfigSpec
        {
            ResourceName = "kiosk.config.immich-api-key",
            FileName = "kiosk.immich-api-key",
            Variable = "KIOSK_IMMICH_API_KEY",
            SettingKey = ImmichApiKeySettingKey,
            Secret = true,
            DependsOn = [KioskBinaryResource.ResourceName, AdoptionResource.ResourceName],
            Detected = "This frame is not allowed to read the photos yet.",
            WhyItMatters = "The photo server turns the frame away, and the screen stays empty.",
            Gloss = "Storing this frame's read-only pass for the photo library, where only the frame can read it.",
        },
        new KioskConfigSpec
        {
            ResourceName = "kiosk.config.offline-mode-enabled",
            FileName = "kiosk.offline-mode",
            Variable = "KIOSK_OFFLINE_MODE_ENABLED",
            SettingKey = OfflineModeSettingKey,
            Fallback = "true",
            DependsOn = [KioskBinaryResource.ResourceName],
            Detected = "This frame is not keeping its own copy of the photos.",
            WhyItMatters = "The screen would go blank whenever the photo server cannot be reached.",
            Gloss = "Telling the slideshow to keep its own copy of recent photos.",
        },
        new KioskConfigSpec
        {
            ResourceName = "kiosk.config.offline-asset-count",
            FileName = "kiosk.offline-asset-count",
            Variable = "KIOSK_OFFLINE_MODE_NUMBER_OF_ASSETS",
            SettingKey = OfflineAssetCountSettingKey,
            Fallback = DefaultOfflineAssetCount,
            DependsOn = ["kiosk.config.offline-mode-enabled"],
            Detected = "This frame has not been told how many photos to keep a copy of.",
            WhyItMatters = "Too few and the slideshow repeats itself offline; too many and the card fills up.",
            Gloss = "Setting how many photos this frame keeps its own copy of.",
        },
    ];

    /// <summary>Builds the eight resources, in catalog order.</summary>
    public static IReadOnlyList<IResource> Build(DeviceCatalogContext context, KioskInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(installer);

        var kiosk = context.Kiosk
            ?? throw new InvalidOperationException("The kiosk block needs a KioskProcess in the catalog context.");

        var resources = new List<IResource>(8)
        {
            new KioskBinaryResource(installer),
            new KioskOfflineCacheResource(context.Files, kiosk),
        };

        foreach (var spec in Specs)
        {
            resources.Add(new KioskConfigResource(context.Store, context.Values, context.Files, kiosk, spec));
        }

        resources.Add(new KioskListenAddressResource(context.Processes, kiosk));
        resources.Add(new KioskSupervisedResource(kiosk));

        return resources;
    }

    /// <summary>
    /// The environment the child runs with, from what the reconciler has <i>recorded</i>.
    /// </summary>
    /// <remarks>
    /// Recorded rather than issued, and the ordering is §2.6's: the recorded values are the ones the
    /// reconciler has verified, so a settings push that has not been through a pass does not reach
    /// the child ahead of the resource that owns it. It is also what keeps the slideshow running
    /// through an outage, since a frame that was green when contact dropped keeps its values.
    /// </remarks>
    public static KioskProcessSettings SettingsFrom(IStateStore store, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var offlineMode = store.ReadText("kiosk.offline-mode")?.Trim();
        var count = store.ReadText("kiosk.offline-asset-count")?.Trim();

        return new KioskProcessSettings
        {
            WorkingDirectory = workingDirectory,
            ImmichUrl = store.ReadText("kiosk.immich-url")?.Trim() ?? string.Empty,
            ImmichApiKey = Encoding.UTF8.GetString(store.ReadBytes("kiosk.immich-api-key") ?? []).Trim(),
            OfflineModeEnabled = !bool.TryParse(offlineMode, out var enabled) || enabled,
            OfflineAssetCount =
                int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                    ? parsed
                    : 200,
            Port = KioskProcess.DefaultPort,
        };
    }
}
