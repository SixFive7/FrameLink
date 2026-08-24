using System.Globalization;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Resources;
using FrameLink.Protocol;

namespace FrameLink.Agent.Telemetry;

/// <summary>
/// Reads dpkg's whole database and reports it, but only when it has changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a resource.</b> §2.2's unit is "the smallest independently verifiable
/// setting", and the ~930 packages on a frame are not a setting at all — nothing declares them,
/// nothing converges them, and a resource that asserted the closure would report drift every time
/// Debian re-cut a dependency. What the operator asked for is visibility, so this observes and
/// reports and never acts. The fifteen packages the catalog does manage keep their resources and
/// keep their own floor comparison (<see cref="AptPackageSpec.ReviewedVersion"/>); this runs
/// beside them and says nothing about drift.
/// </para>
/// <para>
/// <b>The cadence, and why it is this one.</b> Two facts decide it. The set changes only when apt
/// or dpkg runs, which on a converged frame means <c>unattended-upgrades</c> — a handful of times
/// a month. And §2.4 reboots after every applied resource, so <i>every</i> change the agent
/// itself makes is followed by a fresh process start. That gives the cheap schedule below:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Once at startup</b>, which covers everything the agent installed before its own reboot and
/// covers the first report a newly adopted frame ever sends.
/// </description></item>
/// <item><description>
/// <b>Then every <see cref="IntervalSettingKey"/>, six hours by default</b>, which covers the
/// changes the agent did not make — the overnight security update on a frame that has been up for
/// a month.
/// </description></item>
/// </list>
/// <para>
/// <b>And a content hash decides whether anything goes out at all.</b> Reading dpkg is one
/// process and a few milliseconds; sending is ~30 kB. So the read happens on the schedule and the
/// send happens on change: the hash of the canonical rendering is compared with the last hash
/// this frame delivered, persisted in <see cref="StateFileName"/> so a reboot does not re-send an
/// unchanged set. The steady-state cost of the whole mechanism is therefore four <c>dpkg-query</c>
/// runs a day and no traffic.
/// </para>
/// <para>
/// <b>Offline it behaves like every other picture on the telemetry channel</b> (§4.1): the
/// inventory is written to the bounded on-disk buffer and drains on reconnect. The hash advances
/// on buffering rather than on delivery, which is correct because the buffer holds the message —
/// what must never happen is the hash advancing on a set that was neither sent nor stored.
/// </para>
/// </remarks>
public sealed class PackageInventoryReporter
{
    /// <summary>Where the last delivered hash and sequence are remembered.</summary>
    public const string StateFileName = "package-inventory.state";

    /// <summary>Fleet setting governing how often dpkg is re-read (§3.4).</summary>
    public const string IntervalSettingKey = "packages.reportInterval";

    /// <summary>How often dpkg is re-read when the Fleet Manager has not said otherwise.</summary>
    /// <remarks>
    /// Six hours is chosen against what it is looking for. <c>unattended-upgrades</c> runs on
    /// systemd's <c>apt-daily-upgrade</c> timer, which fires once a day with a randomised delay,
    /// so anything under a day catches it; six hours bounds the reporting lag at a quarter of a
    /// day for four database reads. Shorter buys nothing, because nothing else on a converged
    /// frame moves a package.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    private readonly AptPackages _apt;
    private readonly IPackageTelemetry _telemetry;
    private readonly IStateStore _store;
    private readonly IAgentClock _clock;
    private readonly IAgentLog _log;
    private readonly FleetValues _values;

    /// <summary>Creates a reporter for one device.</summary>
    public PackageInventoryReporter(
        AptPackages apt,
        IPackageTelemetry telemetry,
        IStateStore store,
        IAgentClock clock,
        IAgentLog log,
        FleetValues? values = null)
    {
        ArgumentNullException.ThrowIfNull(apt);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _apt = apt;
        _telemetry = telemetry;
        _store = store;
        _clock = clock;
        _log = log;
        _values = values ?? FleetValues.None;
    }

    /// <summary>The frame this reports for.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The interval in force right now, fleet setting first (§3.4).</summary>
    /// <remarks>
    /// Read per tick rather than captured, for the same reason the countdown is: a value the
    /// operator changes while a frame is running has to take effect on that frame, and a frame
    /// that has not been adopted yet has no settings at all and must still run on the default.
    /// </remarks>
    public TimeSpan Interval =>
        _values.Find(IntervalSettingKey) is { } configured
        && TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var parsed)
        && parsed > TimeSpan.Zero
            ? parsed
            : DefaultInterval;

    /// <summary>Reads dpkg, and reports if the set has moved since the last report.</summary>
    /// <returns>True when an inventory was handed to telemetry.</returns>
    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var installed = await _apt.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (installed.Count == 0)
        {
            // dpkg answering nothing is not "this frame has no packages" — it is a query that
            // failed, and reporting an empty set would delete a real inventory on the server and
            // make every other frame look like it had diverged from this one.
            _log.Warn("dpkg-query listed no installed packages; skipping this package inventory.");
            return false;
        }

        var hash = PackageInventory.HashOf(installed);
        var (lastHash, lastSequence) = ReadState();

        if (string.Equals(hash, lastHash, StringComparison.Ordinal))
        {
            return false;
        }

        var (packages, observed) = Fit(installed);
        if (observed > packages.Count)
        {
            _log.Warn(
                $"This frame has {observed} installed packages, more than the {PackageInventory.MaxPackages} "
                + "one report may carry. The report names the first of them and says how many there were.");
        }

        var sequence = lastSequence + 1;
        await _telemetry.InventoryAsync(
            new PackageInventory
            {
                DeviceId = DeviceId,
                Sequence = sequence,
                GeneratedUtc = _clock.UtcNow,
                ContentHash = hash,
                ObservedCount = observed,
                Packages = packages,
            },
            cancellationToken).ConfigureAwait(false);

        WriteState(hash, sequence);
        _log.Info($"Reported {observed} installed packages to the Fleet Manager (content {hash[..12]}).");
        return true;
    }

    /// <summary>Ticks once, then on the interval, until asked to stop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException
                and not ProcessTimeoutException)
            {
                // Reporting is observation, so a failed tick costs visibility and nothing else.
                // Taking the process down over it would cost the frame its product.
                _log.Warn($"A package inventory tick failed and was skipped: {exception.Message}");
            }

            try
            {
                await _clock.DelayAsync(Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Cuts an oversized set down to what one message may carry.</summary>
    /// <remarks>
    /// The hash is deliberately taken over the <i>whole</i> set before this runs, so a frame that
    /// changes a package beyond the cut-off still reports rather than going quiet. Truncation
    /// keeps the ordinal-first packages, which is arbitrary but stable — an unstable choice would
    /// make the delivered set flap between reports on a frame that never changed.
    /// </remarks>
    private static (IReadOnlyDictionary<string, string> Packages, int Observed) Fit(
        IReadOnlyDictionary<string, string> installed)
    {
        if (installed.Count <= PackageInventory.MaxPackages)
        {
            return (installed, installed.Count);
        }

        var trimmed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in installed.Keys.Order(StringComparer.Ordinal).Take(PackageInventory.MaxPackages))
        {
            trimmed[name] = installed[name];
        }

        return (trimmed, installed.Count);
    }

    /// <summary>Reads the last delivered hash and sequence, or nothing on a fresh frame.</summary>
    private (string Hash, long Sequence) ReadState()
    {
        var text = _store.ReadText(StateFileName);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, 0);
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sequence = lines.Length > 1 && long.TryParse(lines[1], CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        return (lines.Length > 0 ? lines[0] : string.Empty, sequence);
    }

    private void WriteState(string hash, long sequence) =>
        _store.WriteText(
            StateFileName,
            hash + "\n" + sequence.ToString(CultureInfo.InvariantCulture) + "\n");
}
