using FrameLink.Control.Storage;
using FrameLink.Protocol;

namespace FrameLink.Control;

/// <summary>
/// Every comparison the Fleet Manager makes over package inventories.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drift is computed here and never on the frame, and that split is the whole design.</b> A
/// frame behind NAT with no inbound port is left running Debian's security-only automatic updates
/// (Appendix B item 4), so its packages are <i>expected</i> to move — forward, on their own, with
/// nobody pressing anything. An agent that treated that as drift would undo the update and, under
/// §2.6, stop showing photos until it had. So the agent reports and the server decides what the
/// report means, which is also the only place a question like "these two frames differ in
/// fourteen packages" can be answered at all.
/// </para>
/// <para>
/// <b>Two comparisons, and the operator asked for both.</b> Against the reviewed baseline —
/// "this frame is three versions ahead on chromium" — and across the fleet — "these two frames
/// disagree about fourteen packages". The second is the sharper instrument, because it needs no
/// baseline to be right about: two frames built the same way that have stopped matching is a fact
/// about them, not about a reference from months ago.
/// </para>
/// <para>
/// <b>Nothing here returns nine hundred rows.</b> Every method is capped and reports the
/// uncapped total beside the capped list, because §7.4 holds the console to the same bar as the
/// frame and a table of nine hundred rows meets no bar at all.
/// </para>
/// </remarks>
public static class PackageDrift
{
    /// <summary>Installed version equals the reviewed one.</summary>
    public const string StatusSame = "same";

    /// <summary>Installed version is newer. Expected — this is what a security update looks like.</summary>
    public const string StatusAhead = "ahead";

    /// <summary>Installed version is older than the reviewed one. Something is wrong.</summary>
    public const string StatusBehind = "behind";

    /// <summary>The baseline names it and this frame does not have it.</summary>
    public const string StatusMissing = "missing";

    /// <summary>This frame has it and the baseline never named it.</summary>
    public const string StatusExtra = "extra";

    /// <summary>A package appeared.</summary>
    public const string ChangeInstalled = "installed";

    /// <summary>A package went away.</summary>
    public const string ChangeRemoved = "removed";

    /// <summary>A package moved forward.</summary>
    public const string ChangeUpgraded = "upgraded";

    /// <summary>A package moved backward.</summary>
    public const string ChangeDowngraded = "downgraded";

    /// <summary>
    /// The most rows any one list hands the browser.
    /// </summary>
    /// <remarks>
    /// Four hundred is far above the tens a real answer contains and far below the nine hundred
    /// that would make the screen a data dump. It exists for the pathological case — a frame
    /// built from a much newer base image, which is legitimately ahead on hundreds of packages —
    /// where the honest response is "hundreds, here are the first four hundred" rather than
    /// either a truncation nobody is told about or a page that takes a second to lay out.
    /// </remarks>
    public const int MaxRows = 400;

    /// <summary>How one frame's set stands against the reviewed baseline, package by package.</summary>
    /// <remarks>
    /// Ordered worst first — behind, then missing, then extra, then ahead — because the ordering
    /// is the triage. Within a status, ordinal by name.
    /// </remarks>
    public static IReadOnlyList<PackageDeltaView> AgainstBaseline(
        IReadOnlyDictionary<string, string> installed,
        int limit = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var deltas = new List<PackageDeltaView>();

        foreach (var (package, baseline) in PackageBaseline.Versions)
        {
            if (!installed.TryGetValue(package, out var version))
            {
                deltas.Add(new PackageDeltaView
                {
                    Package = package,
                    Status = StatusMissing,
                    Baseline = baseline,
                });
                continue;
            }

            var order = DebianVersion.Compare(version, baseline);
            if (order == 0)
            {
                continue;
            }

            deltas.Add(new PackageDeltaView
            {
                Package = package,
                Status = order > 0 ? StatusAhead : StatusBehind,
                Baseline = baseline,
                Installed = version,
            });
        }

        foreach (var (package, version) in installed)
        {
            if (!PackageBaseline.Versions.ContainsKey(package))
            {
                deltas.Add(new PackageDeltaView
                {
                    Package = package,
                    Status = StatusExtra,
                    Installed = version,
                });
            }
        }

        deltas.Sort(static (left, right) =>
        {
            var byStatus = Severity(left.Status).CompareTo(Severity(right.Status));
            return byStatus != 0
                ? byStatus
                : string.CompareOrdinal(left.Package, right.Package);
        });

        return limit > 0 && deltas.Count > limit ? deltas[..limit] : deltas;
    }

    /// <summary>The five numbers on one frame's row, without materialising the rows.</summary>
    public static PackageSummaryView Summarise(
        DevicePackageSet set,
        string? name,
        bool online)
    {
        ArgumentNullException.ThrowIfNull(set);

        var ahead = 0;
        var behind = 0;
        var missing = 0;

        foreach (var (package, baseline) in PackageBaseline.Versions)
        {
            if (!set.Packages.TryGetValue(package, out var version))
            {
                missing++;
                continue;
            }

            var order = DebianVersion.Compare(version, baseline);
            if (order > 0)
            {
                ahead++;
            }
            else if (order < 0)
            {
                behind++;
            }
        }

        var extra = 0;
        foreach (var package in set.Packages.Keys)
        {
            if (!PackageBaseline.Versions.ContainsKey(package))
            {
                extra++;
            }
        }

        return new PackageSummaryView
        {
            DeviceId = set.DeviceId,
            Name = name,
            Online = online,
            ObservedUtc = set.ObservedUtc,
            ContentHash = set.ContentHash,
            Installed = set.Packages.Count,
            Ahead = ahead,
            Behind = behind,
            Missing = missing,
            Extra = extra,
        };
    }

    /// <summary>
    /// Every package the fleet does not agree on, with the frames grouped under their version.
    /// </summary>
    /// <returns>The capped list and the uncapped total, in that order.</returns>
    /// <remarks>
    /// <para>
    /// A package counts as agreed when every reporting frame has it at the same version — which
    /// means a package one frame lacks entirely is a disagreement, rendered as a group with no
    /// version. That is the case an operator most needs to see and the one a naive
    /// intersection-of-keys would silently drop.
    /// </para>
    /// <para>
    /// With fewer than two frames there is nothing to disagree about and the answer is empty.
    /// </para>
    /// </remarks>
    public static (IReadOnlyList<PackageDisagreementView> Rows, int Total, int Agreed) AcrossFleet(
        IReadOnlyList<DevicePackageSet> sets,
        int limit = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(sets);

        if (sets.Count < 2)
        {
            return ([], 0, sets.Count == 1 ? sets[0].Packages.Count : 0);
        }

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            names.UnionWith(set.Packages.Keys);
        }

        var rows = new List<PackageDisagreementView>();
        var agreed = 0;

        foreach (var package in names)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var absent = new List<string>();

            foreach (var set in sets)
            {
                if (set.Packages.TryGetValue(package, out var version))
                {
                    if (!groups.TryGetValue(version, out var devices))
                    {
                        devices = [];
                        groups[version] = devices;
                    }

                    devices.Add(set.DeviceId);
                }
                else
                {
                    absent.Add(set.DeviceId);
                }
            }

            if (absent.Count == 0 && groups.Count == 1)
            {
                agreed++;
                continue;
            }

            var versions = groups
                .OrderByDescending(group => group.Key, DebianVersionComparer.Instance)
                .Select(group => new PackageVersionGroupView { Version = group.Key, DeviceIds = group.Value })
                .ToList();

            if (absent.Count > 0)
            {
                versions.Add(new PackageVersionGroupView { DeviceIds = absent });
            }

            rows.Add(new PackageDisagreementView
            {
                Package = package,
                Baseline = PackageBaseline.VersionOf(package),
                Versions = versions,
            });
        }

        // Widest disagreement first: a package three frames split three ways is more interesting
        // than one two frames split two ways, and both are more interesting than the rest.
        rows.Sort(static (left, right) =>
        {
            var byWidth = right.Versions.Count.CompareTo(left.Versions.Count);
            return byWidth != 0 ? byWidth : string.CompareOrdinal(left.Package, right.Package);
        });

        var total = rows.Count;
        return (limit > 0 && rows.Count > limit ? rows[..limit] : rows, total, agreed);
    }

    /// <summary>What moved between two of one frame's sets.</summary>
    /// <param name="before">The older set.</param>
    /// <param name="after">The newer set.</param>
    /// <param name="observedUtc">When the newer set was observed.</param>
    /// <param name="limit">Most rows to return.</param>
    public static PackageChangeSetView Between(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        DateTimeOffset observedUtc,
        int limit = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new List<PackageChangeView>();

        foreach (var (package, version) in after)
        {
            if (!before.TryGetValue(package, out var previous))
            {
                changes.Add(new PackageChangeView
                {
                    Package = package,
                    Change = ChangeInstalled,
                    To = version,
                });
                continue;
            }

            var order = DebianVersion.Compare(version, previous);
            if (order != 0)
            {
                changes.Add(new PackageChangeView
                {
                    Package = package,
                    Change = order > 0 ? ChangeUpgraded : ChangeDowngraded,
                    From = previous,
                    To = version,
                });
            }
        }

        foreach (var (package, version) in before)
        {
            if (!after.ContainsKey(package))
            {
                changes.Add(new PackageChangeView
                {
                    Package = package,
                    Change = ChangeRemoved,
                    From = version,
                });
            }
        }

        changes.Sort(static (left, right) =>
        {
            var byChange = ChangeSeverity(left.Change).CompareTo(ChangeSeverity(right.Change));
            return byChange != 0 ? byChange : string.CompareOrdinal(left.Package, right.Package);
        });

        return new PackageChangeSetView
        {
            ObservedUtc = observedUtc,
            Total = changes.Count,
            Changes = limit > 0 && changes.Count > limit ? changes[..limit] : changes,
        };
    }

    /// <summary>Turns a frame's history into one change set per report, newest first.</summary>
    /// <remarks>
    /// The oldest entry in the window produces nothing, because there is nothing before it to
    /// diff against — which is honest rather than a gap: a set with no predecessor in the retained
    /// window is a state, not a change.
    /// </remarks>
    public static IReadOnlyList<PackageChangeSetView> Timeline(
        IReadOnlyList<DevicePackageHistoryEntry> history,
        int limit = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(history);

        var sets = new List<PackageChangeSetView>();
        for (var index = 0; index + 1 < history.Count; index++)
        {
            sets.Add(Between(
                history[index + 1].Packages,
                history[index].Packages,
                history[index].ObservedUtc,
                limit));
        }

        return sets;
    }

    private static int Severity(string status) => status switch
    {
        StatusBehind => 0,
        StatusMissing => 1,
        StatusExtra => 2,
        StatusAhead => 3,
        _ => 4,
    };

    private static int ChangeSeverity(string change) => change switch
    {
        ChangeDowngraded => 0,
        ChangeRemoved => 1,
        ChangeInstalled => 2,
        _ => 3,
    };

    /// <summary>Orders version strings the Debian way, for anything that sorts them.</summary>
    private sealed class DebianVersionComparer : IComparer<string>
    {
        public static DebianVersionComparer Instance { get; } = new();

        public int Compare(string? x, string? y) => DebianVersion.Compare(x, y);
    }
}
