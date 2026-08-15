using System.Text.RegularExpressions;
using FrameLink.Agent.Kiosk;
using FrameLink.Agent.Resources;
using FrameLink.Control.Imaging;
using FrameLink.Upstream;

namespace FrameLink.Tests;

/// <summary>
/// §7.1's upstream review ledger — the half that runs offline, on every build.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here reaches the network, and that is the design rather than a limitation.</b> The
/// operator's instruction was that a developer build must never fail because an upstream published
/// something overnight, so the probes live in <c>tools/FrameLink.Upstream</c> and only a person
/// cutting a release runs them. What is asserted here is the other question, which is entirely
/// local: does the committed ledger still describe the pins in this repository? That fails when a
/// human changed one and forgot the other — never because of anything upstream did.
/// </para>
/// <para>
/// The membership rule is asserted too, because it is the thing most likely to erode. The ledger
/// is for versions somebody <i>chose</i>; Debian package versions move on their own under security
/// updates and are recorded by the Fleet Manager as inventory. Two tests keep that boundary from
/// being helpfully blurred later.
/// </para>
/// </remarks>
public sealed class UpstreamLedgerTests
{
    private static string LedgerPath =>
        Path.Combine(GuiFreshnessTests.RepositoryRoot(), UpstreamLedger.FileName);

    private static UpstreamLedger Ledger => UpstreamLedger.Load(LedgerPath);

    [Fact]
    public void The_committed_ledger_is_structurally_sound()
    {
        // The tool refuses to act on an unsound ledger, and discovering that at the moment
        // somebody is trying to cut a release is the worst time to discover it.
        var problems = Ledger.Problems();

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        Assert.NotEmpty(Ledger.Entries);
    }

    [Fact]
    public void The_committed_ledger_is_already_in_the_form_the_tool_writes()
    {
        // So that recording a review is a one-entry diff instead of a whole-file reformat, which
        // is the difference between a reviewable change and one nobody reads.
        var onDisk = File.ReadAllText(LedgerPath).ReplaceLineEndings("\n");

        Assert.Equal(Ledger.ToJson(), onDisk);
    }

    [Fact]
    public void Every_entry_that_names_a_file_names_one_that_exists()
    {
        // `pinnedIn` is the answer to "where do I go to change this", so a stale path is a wrong
        // answer to the only question the field is asked.
        var root = GuiFreshnessTests.RepositoryRoot();

        foreach (var entry in Ledger.Entries.Where(entry => entry.PinnedIn.Contains('/', StringComparison.Ordinal)))
        {
            var path = Path.Combine(root, entry.PinnedIn.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{entry.Id} points at {entry.PinnedIn}, which is not there.");
        }
    }

    [Fact]
    public void The_base_image_pin_and_the_ledger_agree_on_the_release()
    {
        // §3.9's generator refuses to touch an image that does not match BaseImagePin, and the
        // ledger is what says a human looked at that pin. Two records of one decision that can
        // disagree silently is worse than one, so they are tied together here.
        var entry = Ledger.Find("raspios-lite-arm64");
        Assert.NotNull(entry);

        Assert.Equal(BaseImagePin.Current.Release, entry.Pinned);
        Assert.StartsWith(entry.Pinned, BaseImagePin.Current.ImageFileName, StringComparison.Ordinal);
        Assert.Contains(entry.Pinned, BaseImagePin.Current.ArchiveUrl.ToString(), StringComparison.Ordinal);

        // The image is published under a directory dated a day later than the image itself, so the
        // probe's answer and the pin are never equal. Asserted rather than left as a surprise to
        // whoever next reads a report that says `using 2026-06-18  reviewed 2026-06-19`.
        Assert.NotEqual(entry.Pinned, entry.Reviewed.Upstream);
        Assert.Contains(entry.Reviewed.Upstream, BaseImagePin.Current.ArchiveUrl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_immich_kiosk_pin_and_the_ledger_agree_on_the_release()
    {
        // The same tie the base image gets, for the same reason and one step further. §2.1 has the
        // agent fetch this release over the network onto a frame and then run it, so "which version"
        // is not a note in a document — it is a URL, two digests and a length that a frame acts on
        // unattended. Two records of one decision that can disagree silently is worse than one.
        var entry = Ledger.Find("immich-kiosk");
        Assert.NotNull(entry);

        Assert.Equal(KioskReleasePin.Current.Version, entry.Pinned);
        Assert.Contains(KioskReleasePin.Current.Tag, KioskReleasePin.Current.ArchiveUrl.ToString(), StringComparison.Ordinal);
        Assert.Contains(entry.Pinned, KioskReleasePin.Current.AssetFileName + KioskReleasePin.Current.ChecksumsUrl, StringComparison.Ordinal);

        // The probe watches the same repository the pin fetches from, which is the only way a
        // `check` run can answer for this entry at all.
        Assert.Contains("damongolding/immich-kiosk", entry.Probe.Url!.ToString(), StringComparison.Ordinal);
        Assert.Contains("damongolding/immich-kiosk", KioskReleasePin.Current.ArchiveUrl.ToString(), StringComparison.Ordinal);

        // And the digest a human reviews against is in the note, so re-checking the pin is reading
        // one field rather than reconstructing what was looked at.
        Assert.Contains(KioskReleasePin.Current.ArchiveSha256, entry.Reviewed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_target_framework_band_and_the_ledger_agree()
    {
        var entry = Ledger.Find("dotnet-lts-band");
        Assert.NotNull(entry);

        var props = File.ReadAllText(Path.Combine(GuiFreshnessTests.RepositoryRoot(), "Directory.Build.props"));
        var band = Regex.Match(props, @"<TargetFramework>(?<tfm>[^<]+)</TargetFramework>", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.True(band.Success, "Directory.Build.props no longer states one TargetFramework.");
        Assert.Equal(band.Groups["tfm"].Value, entry.Pinned);
    }

    [Fact]
    public void Every_nuget_package_this_repository_references_has_a_ledger_entry()
    {
        // The direction that matters. Adding a dependency is the moment a version enters the
        // artifact without anybody having reviewed it, and §7.1's floats mean the version moves
        // afterwards on its own.
        var reviewed = Ledger.Entries
            .Where(entry => string.Equals(entry.Probe.Kind, UpstreamProbe.NugetPackage, StringComparison.Ordinal))
            .Select(entry => entry.Probe.Url!.Segments[^2].TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in ProjectFiles())
        {
            foreach (Match reference in Regex.Matches(
                File.ReadAllText(project),
                @"<PackageReference\s+Include=""(?<id>[^""]+)""",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)))
            {
                referenced.Add(reference.Groups["id"].Value);
            }
        }

        Assert.NotEmpty(referenced);
        Assert.All(referenced, id => Assert.Contains(id, reviewed, StringComparer.OrdinalIgnoreCase));
        Assert.All(reviewed, id => Assert.Contains(id, referenced, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Debian_package_versions_are_not_ledger_entries()
    {
        // The boundary, asserted rather than described. The apt resources are told to move forward
        // under security updates, and the Fleet Manager records the versions it finds on each
        // frame; recorded inventory is not a chosen version. An entry per Debian package would put
        // a value that changes weekly in front of a gate that runs once per release, which is how
        // a gate stops being performed.
        var ledger = Ledger;

        foreach (var spec in PackageCatalog.Specs)
        {
            Assert.Null(ledger.Find(spec.Package));

            Assert.DoesNotContain(
                ledger.Entries,
                entry => entry.Probe.Url!.ToString().Contains($"/{spec.Package}/", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void There_is_no_probe_kind_for_a_debian_package()
    {
        // The other half of the same boundary: the ledger cannot express an apt package, because
        // the tool has no probe that could answer for one. Somebody widening the ledger has to
        // widen this list first, which is a diff a person reads.
        Assert.Equal(
            ["raspios-images", "github-release", "nuget-package", "dotnet-channel"],
            UpstreamProbe.Kinds);
    }

    [Fact]
    public void Nothing_that_builds_or_publishes_invokes_the_detector()
    {
        // "I do not want the build to fail." The detector reaches four upstreams over the network,
        // so the only way that promise holds is if no build path can reach it. The one reference
        // permitted anywhere is the suite's project reference, which pulls in the ledger types and
        // no probe.
        var root = GuiFreshnessTests.RepositoryRoot();
        var mustNotMention = new List<string>
        {
            Path.Combine(root, "Directory.Build.props"),
            Path.Combine(root, "build", "build.sh"),
            Path.Combine(root, "build", "verify-unit.sh"),
            Path.Combine(root, "build", "Dockerfile"),
            Path.Combine(root, "src", "FrameLink.Agent", "FrameLink.Agent.csproj"),
            Path.Combine(root, "src", "FrameLink.Control", "FrameLink.Control.csproj"),
            Path.Combine(root, "src", "FrameLink.Protocol", "FrameLink.Protocol.csproj"),
        };

        foreach (var path in mustNotMention.Where(File.Exists))
        {
            Assert.DoesNotContain(
                "FrameLink.Upstream",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ProjectFiles()
    {
        var root = GuiFreshnessTests.RepositoryRoot();

        foreach (var directory in (string[])["src", "tests", "tools"])
        {
            var full = Path.Combine(root, directory);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var project in Directory.EnumerateFiles(full, "*.csproj", SearchOption.AllDirectories))
            {
                yield return project;
            }
        }
    }
}

/// <summary>
/// What the detector does with an answer — the pure half of <c>tools/FrameLink.Upstream</c>.
/// </summary>
/// <remarks>
/// The probes themselves are network calls and are exercised by running the tool. The decision
/// they feed is a function of two strings, and every consequence the operator asked for lives in
/// it: what counts as moved, what counts as current, and the refusal to call an unanswered probe
/// either one.
/// </remarks>
public sealed class UpstreamDetectionTests
{
    private static UpstreamEntry Reviewed(string upstream, string verdict = "adopted") => new()
    {
        Id = "example",
        Title = "An upstream",
        PinnedIn = "somewhere",
        Pinned = "1.0.0",
        Probe = new UpstreamProbe { Kind = UpstreamProbe.NugetPackage, Url = new Uri("https://example.invalid/i.json") },
        Reviewed = new UpstreamReview { Utc = new DateOnly(2026, 8, 15), Upstream = upstream, Verdict = verdict, Note = "why" },
    };

    [Fact]
    public void An_upstream_still_serving_what_was_reviewed_is_current()
    {
        var finding = UpstreamProbes.Classify(Reviewed("1.0.0"), new ProbeAnswer("1.0.0"));

        Assert.Equal(UpstreamState.Current, finding.State);
    }

    [Fact]
    public void An_upstream_serving_anything_else_has_moved()
    {
        // Compared against what was *reviewed*, never against what is pinned. Those are different
        // questions, and only this one can tell you something changed while nobody was looking —
        // a deliberate hold is on an old version on purpose, and must not report as drift forever.
        var held = Reviewed("2.0.0", verdict: "held");

        Assert.Equal(UpstreamState.Current, UpstreamProbes.Classify(held, new ProbeAnswer("2.0.0")).State);
        Assert.Equal(UpstreamState.Moved, UpstreamProbes.Classify(held, new ProbeAnswer("2.1.0")).State);
    }

    [Fact]
    public void An_upstream_that_did_not_answer_is_neither_current_nor_moved()
    {
        // "The ledger is current" is a claim about what upstream is serving. An unreachable
        // upstream leaves that unknown, and unknown must not pass a release gate.
        var failed = UpstreamProbes.Classify(Reviewed("1.0.0"), new ProbeAnswer(null, Failure: "no answer"));
        var empty = UpstreamProbes.Classify(Reviewed("1.0.0"), new ProbeAnswer("   "));

        Assert.Equal(UpstreamState.Unreachable, failed.State);
        Assert.Equal(UpstreamState.Unreachable, empty.State);
        Assert.NotNull(failed.Failure);
    }

    [Fact]
    public void A_probe_kind_nothing_can_answer_is_refused_by_the_ledger_itself()
    {
        var entry = Reviewed("1.0.0") with
        {
            Probe = new UpstreamProbe { Kind = "apt", Url = new Uri("https://deb.debian.org/") },
        };

        Assert.Contains(entry.Problems(), problem => problem.Contains("probe kind", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hold_that_is_not_actually_holding_anything_back_is_refused()
    {
        // `held` means "upstream offers this and we are staying where we are". Recorded while the
        // two versions are equal, it is a field somebody set without the reason it stands for.
        var entry = Reviewed("1.0.0", verdict: "held") with { Pinned = "1.0.0" };

        Assert.Contains(entry.Problems(), problem => problem.Contains("held", StringComparison.Ordinal));
    }

    [Fact]
    public void A_review_with_no_reason_recorded_is_refused()
    {
        var entry = Reviewed("1.0.0");
        var blank = entry with { Reviewed = entry.Reviewed with { Note = "  " } };

        Assert.Contains(blank.Problems(), problem => problem.Contains("not a review", StringComparison.Ordinal));
    }

    [Fact]
    public void Recording_a_review_replaces_one_entry_and_leaves_the_rest_alone()
    {
        var ledger = new UpstreamLedger
        {
            Comment = ["a ledger"],
            Entries = [Reviewed("1.0.0") with { Id = "first" }, Reviewed("2.0.0") with { Id = "second" }],
        };

        var updated = ledger.With(ledger.Find("second")! with { Pinned = "2.1.0" });

        Assert.Equal("1.0.0", updated.Find("first")!.Reviewed.Upstream);
        Assert.Equal("2.1.0", updated.Find("second")!.Pinned);
        Assert.Equal(2, updated.Entries.Count);
    }

    [Fact]
    public void Two_entries_with_one_id_are_refused()
    {
        var ledger = new UpstreamLedger { Entries = [Reviewed("1.0.0"), Reviewed("1.0.0")] };

        Assert.Contains(ledger.Problems(), problem => problem.Contains("share this id", StringComparison.Ordinal));
    }
}
