using System.Text.Json;
using FrameLink.Control.Storage;
using FrameLink.Parity;

namespace FrameLink.Tests;

/// <summary>
/// The parity harness — milestone Mn+3's first bar, exercised end to end with no hardware.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every fixture in this file is derived from the frozen reference itself</b>, never written
/// by hand. The baseline case builds an observation whose output for each facet <i>is</i> the
/// corresponding block of <c>reference/v1-state-inventory.txt</c> — a frame that replays v1
/// exactly — and every other case is that one with a named mutation applied. Nothing is
/// synthesised, so a test can only fail because the comparison is wrong, never because somebody
/// invented a plausible-looking probe output that a real frame would never produce.
/// </para>
/// <para>
/// <b>What this file cannot do, and says so:</b> it never proves a probe returns what the parser
/// expects on a live frame. The probes are read-only shell commands written to reproduce the
/// shape of the capture, and until one runs against a real Pi that is an argument rather than a
/// measurement. The tests below fix the half that can be fixed here — the parsers, the differ,
/// the ledger, the coverage arithmetic and the verdict — and one of them asserts every probe is
/// at least incapable of writing to the frame it runs on.
/// </para>
/// </remarks>
public sealed class ParityHarnessTests
{
    private static string Root => GuiFreshnessTests.RepositoryRoot();

    // -------------------------------------------------------------------------------------------
    // Totality: nothing may be dropped in silence
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_captured_section_has_a_facet_and_every_facet_has_a_captured_section()
    {
        // The whole anti-silent-truncation guard. The reference is a captured artifact and the
        // only defensible relationship to it is one that accounts for every block — including the
        // blocks nothing can compare, which are declared uncovered with a reason. A section added
        // to the capture turns this red rather than disappearing from the comparison.
        var captured = ReferenceInventory.Load(Root).Keys.Order(StringComparer.Ordinal).ToList();
        var claimed = ParityFacets.All.Select(facet => facet.Section).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(captured, claimed);
        Assert.Equal(29, captured.Count);
        Assert.Equal(ParityFacets.All.Count, ParityFacets.All.Select(facet => facet.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_covered_facet_has_a_probe_and_an_uncovered_one_has_a_reason()
    {
        foreach (var facet in ParityFacets.All)
        {
            if (facet.IsCovered)
            {
                Assert.False(string.IsNullOrWhiteSpace(facet.Probe), $"{facet.Id} is compared but has no probe.");
                Assert.NotEqual(FacetCoverage.None, facet.Coverage);
            }
            else
            {
                Assert.Null(facet.Probe);
                Assert.Equal(FacetCoverage.None, facet.Coverage);
            }

            if (!string.Equals(facet.Coverage, FacetCoverage.Full, StringComparison.Ordinal))
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(facet.Limitation),
                    $"{facet.Id} does not reach full coverage and does not say why.");
            }
        }
    }

    [Fact]
    public void Exactly_one_probe_needs_root_and_it_is_the_one_the_catalog_says_needs_it()
    {
        // Pinned because the count is stated in prose in four places — the subcommand's help, the
        // collector's docstring, the facet table's own remarks and decision 59 — and prose that
        // counts something is prose that goes quietly wrong the moment the something changes.
        var elevated = ParityFacets.All.Where(facet => facet.Elevated).Select(facet => facet.Id).ToList();

        Assert.Equal(["audio.xvf3800.firmware"], elevated);
        Assert.All(
            ParityFacets.All.Where(facet => facet.Elevated),
            facet => Assert.False(string.IsNullOrWhiteSpace(facet.Limitation)));
    }

    [Fact]
    public void Every_ignored_key_carries_the_reason_it_is_ignored()
    {
        // A volatile field has to be dropped or every run reports it. Dropping it silently is the
        // failure; this is the same drop with the reason attached and the coverage report carrying
        // it into the artifact.
        foreach (var facet in ParityFacets.All)
        {
            foreach (var (key, reason) in facet.IgnoredKeys)
            {
                Assert.False(string.IsNullOrWhiteSpace(key));
                Assert.True(reason.Length > 40, $"{facet.Id}.{key} is dropped with no real reason given.");
            }
        }
    }

    [Fact]
    public void No_probe_can_write_to_the_frame_it_runs_on()
    {
        // Crude on purpose. It cannot prove a command is read-only, but it can prove nobody has
        // quietly added an `apt install` or a `sed -i` to something a parity check runs against a
        // frame it is meant to be measuring rather than changing (CLAUDE.md §1.8).
        string[] forbidden =
        [
            "apt ", "apt-get", "dpkg -i", "sed -i", "tee ", "rm ", "mv ", "cp ", "install ",
            "chmod", "chown", "mkdir", "truncate", "dd ", "systemctl start", "systemctl stop",
            "systemctl enable", "systemctl disable", "systemctl restart", "reboot", "modprobe ",
            "amixer set", "alsactl store", "raspi-config", "rpi-eeprom-update",
        ];

        foreach (var facet in ParityFacets.All.Where(facet => facet.Probe is not null))
        {
            foreach (var verb in forbidden)
            {
                Assert.DoesNotContain(verb, facet.Probe!, StringComparison.Ordinal);
            }

            // A redirection into anything but /dev/null or another descriptor writes a file. The
            // scan tracks single quotes, because one probe carries a sed expression containing
            // <REDACTED> and a naive search for '>' reads that as a redirection into a file called
            // `"/g'` — which is exactly the sort of false alarm that gets a guard deleted.
            foreach (var target in Redirections(facet.Probe!))
            {
                Assert.True(
                    target is "/dev/null" or "&1" or "&2",
                    $"{facet.Id}'s probe redirects output somewhere that is not /dev/null: {target}");
            }
        }
    }

    // -------------------------------------------------------------------------------------------
    // The ledger
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_committed_ledger_is_sound()
    {
        var ledger = ExpectedDifferenceLedger.Load(Root);

        Assert.Empty(ledger.Problems());
        Assert.NotEmpty(ledger.Entries);
        Assert.NotEmpty(ledger.Comment);
    }

    [Theory]
    [InlineData("facet", "no facet")]
    [InlineData("kind", "is not a difference kind")]
    [InlineData("matcher", "no key matcher")]
    [InlineData("both", "One of them is not doing anything")]
    [InlineData("reason", "too short to be one")]
    [InlineData("authority", "no authority")]
    public void An_unsound_entry_says_what_is_wrong_with_it(string flaw, string complaint)
    {
        var entry = new ExpectedDifference
        {
            Id = "probe",
            Facet = flaw == "facet" ? "no-such-facet" : "packages",
            Kinds = [flaw == "kind" ? "sideways" : ParityDifferenceKinds.Missing],
            Keys = flaw is "matcher" ? [] : ["curl"],
            AllKeys = flaw == "both",
            Reason = flaw == "reason"
                ? "short"
                : "A reason long enough to be an actual explanation of why this difference is correct.",
            Authority = flaw == "authority" ? " " : "version2.md decision 40",
            RecordedUtc = new DateOnly(2026, 8, 16),
        };

        Assert.Contains(entry.Problems(), problem => problem.Contains(complaint, StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // Catalog coverage: the gap a state diff hides by construction
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_catalog_resource_has_a_recorded_place_its_v1_evidence_lives_or_does_not()
    {
        // Total by construction and enforced here: a resource added to the catalog turns this red
        // until somebody records where its v1 state was captured — or records that it was not.
        // Without that, "the diff is empty" and "the harness was not looking" are the same output.
        var ids = CatalogDocument.Ids(Root);
        var evidence = CatalogEvidenceMap.For(ids);

        Assert.Equal(81, ids.Count);
        Assert.Equal(ids.Count, evidence.Count);
        Assert.All(evidence, item => Assert.False(string.IsNullOrWhiteSpace(item.Note)));
        Assert.Contains(evidence, item => item.Facet is null);
        Assert.Contains(evidence, item => item.Facet is not null);
    }

    [Fact]
    public void Every_evidence_rule_names_a_facet_that_exists()
    {
        foreach (var facet in CatalogEvidenceMap.ReferencedFacets)
        {
            Assert.NotNull(ParityFacets.Find(facet));
        }
    }

    [Fact]
    public void The_resource_count_matches_the_catalog_s_own_arithmetic()
    {
        // Measured against the document rather than remembered, the same way progress.json reads
        // the number: a hard-coded 80 that nobody re-derives is exactly the recorded claim that
        // outlives its cause.
        var text = File.ReadAllText(Path.Combine(Root, "reference", "resource-catalog.md"));
        var stated = System.Text.RegularExpressions.Regex.Match(text, @"\|\s*\*\*Total\*\*\s*\|\s*\*\*(\d+)\*\*\s*\|");

        Assert.True(stated.Success, "The catalog's Counts table no longer states a total.");
        Assert.Equal(int.Parse(stated.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            CatalogDocument.Ids(Root).Count);
    }

    [Fact]
    public void The_parity_tool_and_the_suite_read_the_catalog_the_same_way()
    {
        // Two parsers of one document is a difference generator. They are kept apart on purpose —
        // the tool needs its own at runtime and the suite's predates it — so this is what stops
        // them disagreeing quietly.
        Assert.Equal(ResourceCatalogDocument.Ids(), CatalogDocument.Ids(Root));
    }

    [Fact]
    public void Every_hashed_file_in_the_capture_is_compared_by_some_other_facet()
    {
        // The files.hashes facet is declared uncovered on the argument that every path in it has
        // its *content* compared elsewhere. This is that argument, checked rather than asserted:
        // a file added to the KEY_FILE_HASHES block with no facet carrying it turns this red.
        var reference = ReferenceInventory.Load(Root);
        var hashed = reference["KEY_FILE_HASHES"]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..].Trim())
            .Where(path => path.Length > 0)
            .ToList();

        Assert.Equal(7, hashed.Count);

        var carried = new HashSet<string>(StringComparer.Ordinal) { "/boot/firmware/config.txt", "/boot/firmware/cmdline.txt" };
        foreach (var facet in ParityFacets.All.Where(facet =>
            string.Equals(facet.Kind, FacetKinds.FileSet, StringComparison.Ordinal)))
        {
            carried.UnionWith(FacetParser.Parse(facet, reference[facet.Section]).Keys);
        }

        foreach (var path in hashed)
        {
            Assert.Contains(path, carried);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Parsing the real capture
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_covered_facet_finds_something_in_the_real_capture()
    {
        // A parser that silently returns an empty map would make its facet permanently agree with
        // any frame at all, and nothing else in this suite would notice.
        var reference = ReferenceInventory.Load(Root);

        foreach (var facet in ParityFacets.All.Where(facet => facet.IsCovered))
        {
            var parsed = FacetParser.Parse(facet, reference[facet.Section]);
            Assert.True(parsed.Count > 0, $"{facet.Id} parsed the captured {facet.Section} block into nothing.");
        }
    }

    [Fact]
    public void The_package_block_is_the_same_929_the_Fleet_Manager_measures_drift_against()
    {
        // The harness compares packages by handing them to PackageDrift, which measures against
        // FrameLink.Control's embedded baseline rather than against the file this tool reads. That
        // is the reuse decision 55 asks for, and this is what keeps the two from being different
        // sets while looking like one.
        var packages = FacetParser.Parse(ParityFacets.Find("packages")!, ReferenceInventory.Load(Root)["PACKAGES"]);

        Assert.Equal(929, packages.Count);
        Assert.Equal(PackageBaseline.Versions.Count, packages.Count);
        foreach (var (package, version) in packages)
        {
            Assert.Equal(PackageBaseline.VersionOf(package), version);
        }
    }

    [Theory]
    [InlineData("boot.cmdline", "fbcon=rotate:1", "×1")]
    [InlineData("boot.config", "[all] dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a", "×1")]
    [InlineData("boot.config", "[cm5] dtoverlay=dwc2,dr_mode=host", "×1")]
    [InlineData("eeprom.config", "BOOT_ORDER", "0xf461")]
    [InlineData("units.system.enabled", "docker.service", "enabled enabled")]
    [InlineData("units.user.enabled", "chromium-kiosk.service", "enabled enabled")]
    [InlineData("alsa.mixer", "PCM,0 Front Left Playback", "60 [100%] [0.00dB] [on]")]
    [InlineData("alsa.mixer", "PCM,1 Mono Playback", "60 [100%] [0.00dB] [on]")]
    [InlineData("users.groups", "member of docker", "yes")]
    [InlineData("users.groups", "group audio", "framelink")]
    [InlineData("network", "hostname", "framelink-douwe")]
    [InlineData("network", "interface docker0", "DOWN")]
    [InlineData("journald", "Storage", "persistent")]
    [InlineData("journald", "SystemMaxUse", "64M")]
    [InlineData("identity", "kernel", "6.12.75+rpt-rpi-2712")]
    [InlineData("modprobe.d", "/etc/modprobe.d/blacklist-8192cu.conf", "blacklist 8192cu")]
    [InlineData("app.config", "room", "family")]
    [InlineData("system.governor-zram-tmpfs", "performance", "×1")]
    public void The_capture_parses_to_the_values_a_reader_can_see_in_it(string facetId, string key, string value)
    {
        var facet = ParityFacets.Find(facetId)!;
        var parsed = FacetParser.Parse(facet, ReferenceInventory.Load(Root)[facet.Section]);

        Assert.True(parsed.ContainsKey(key), $"{facetId} has no key '{key}'. It has: {string.Join(", ", parsed.Keys.Take(8))}");
        Assert.Equal(value, parsed[key]);
    }

    [Fact]
    public void The_alsa_card_list_stops_at_the_marker_the_capture_was_cut_off_after()
    {
        // The block holds the card list and then an alsactl dump the capture truncated mid-control.
        // Reading past the marker would compare a complete v2 dump against a partial v1 one and
        // report every control below the cut as missing — noise the capture invented.
        var facet = ParityFacets.Find("alsa.cards")!;
        var parsed = FacetParser.Parse(facet, ReferenceInventory.Load(Root)[facet.Section]);

        Assert.Contains(parsed.Keys, key => key.Contains("reSpeaker XVF3800", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed.Keys, key => key.Contains("control.1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_line_that_appears_twice_is_not_the_same_state_as_a_line_that_appears_once()
    {
        // The catalog repeatedly asks for a directive to be present *exactly once* — `grep -c`,
        // not `grep -q` — because a non-idempotent write history is the failure it guards. A set
        // comparison would call one occurrence and two identical.
        var facet = ParityFacets.Find("boot.config")!;
        var once = FacetParser.Parse(facet, "[all]\ndtoverlay=x\n");
        var twice = FacetParser.Parse(facet, "[all]\ndtoverlay=x\ndtoverlay=x\n");

        Assert.Equal("×1", once["[all] dtoverlay=x"]);
        Assert.Equal("×2", twice["[all] dtoverlay=x"]);
    }

    // -------------------------------------------------------------------------------------------
    // The comparison
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_frame_that_replays_the_reference_verbatim_has_nothing_to_explain()
    {
        // The baseline the rest of the cases mutate. It also proves the two sides go through one
        // parser: if the reference reader and the probe reader disagreed about so much as a
        // trailing space, this would not come back clean.
        var fixture = new ParityFixture(Root);
        var report = fixture.Judge();

        Assert.Equal(ParityOutcomes.Parity, report.Outcome);
        Assert.Equal(0, report.Findings);
        Assert.Equal(0, report.Expected);
        Assert.Equal(0, report.Tolerated);
        Assert.Equal(0, report.Unresolved);

        // Every ledger entry describes a way v2 differs from v1, so a frame that *is* v1 makes all
        // of them stale — which is the report saying so rather than the run looking clean.
        Assert.Equal(report.LedgerEntries, report.StaleLedgerEntries.Count);
        Assert.Equal(0, report.LedgerEntriesUsed);
    }

    [Fact]
    public void A_v2_shaped_frame_is_at_parity_and_every_difference_carries_its_reason()
    {
        // The case the whole design exists for: a frame that is deliberately not byte-identical to
        // v1 and is nonetheless correct. Each mutation below is one the catalog's own "does not
        // become a device resource" section or an Appendix A decision requires.
        var report = V2Frame().Judge();

        Assert.Equal(ParityOutcomes.Parity, report.Outcome);
        Assert.Equal(0, report.Findings);
        Assert.True(report.Expected >= 25, $"only {report.Expected} differences were explained");

        // Nothing in the ledger is dead weight, and nothing on this frame is unexplained.
        Assert.Empty(report.StaleLedgerEntries);
        Assert.Equal(report.LedgerEntries, report.LedgerEntriesUsed);

        foreach (var difference in report.Facets.SelectMany(facet => facet.Differences))
        {
            Assert.NotEqual(ParityVerdicts.Finding, difference.Verdict);
            Assert.False(string.IsNullOrWhiteSpace(difference.Reason));
        }
    }

    [Fact]
    public void A_unit_that_v1_had_and_this_frame_does_not_is_a_finding()
    {
        var report = V2Frame()
            .RemoveLines("units.system.enabled", line => line.StartsWith("cpu-performance.service", StringComparison.Ordinal))
            .Judge();

        Assert.Equal(ParityOutcomes.Differs, report.Outcome);
        Assert.Equal(1, report.Findings);

        var finding = Findings(report).Single();
        Assert.Equal("units.system.enabled", finding.Facet);
        Assert.Equal(ParityDifferenceKinds.Missing, finding.Kind);
        Assert.Equal("cpu-performance.service", finding.Key);
        Assert.Null(finding.Reason);
    }

    [Fact]
    public void A_package_that_moved_forward_is_tolerated_and_one_that_moved_back_is_a_finding()
    {
        // Decision 55's whole point, and the one drift the operator accepts. Forward is a security
        // update on a frame with no inbound ports; backward is nothing legitimate at all.
        var ahead = V2Frame().Replace("packages", "openssl 3.5.5-1~deb13u2", "openssl 3.5.6-1~deb13u1").Judge();
        Assert.Equal(ParityOutcomes.Parity, ahead.Outcome);
        Assert.Equal(1, ahead.Tolerated);
        Assert.Equal(0, ahead.Findings);

        var behind = V2Frame().Replace("packages", "openssl 3.5.5-1~deb13u2", "openssl 3.5.4-1~deb13u1").Judge();
        Assert.Equal(ParityOutcomes.Differs, behind.Outcome);
        Assert.Equal(ParityDifferenceKinds.Behind, Findings(behind).Single().Kind);
    }

    [Fact]
    public void A_kernel_that_moved_forward_is_tolerated_the_same_way_a_package_is()
    {
        var report = V2Frame()
            .Replace("identity", "6.12.75+rpt-rpi-2712", "6.12.80+rpt-rpi-2712")
            .Judge();

        Assert.Equal(ParityOutcomes.Parity, report.Outcome);
        Assert.Equal(ParityDifferenceKinds.Ahead,
            report.Facets.Single(facet => facet.Facet == "identity").Differences.Single().Kind);
    }

    [Fact]
    public void A_package_v1_never_had_is_an_addition_and_is_a_finding_until_somebody_records_why()
    {
        // The hard half of the three kinds: an addition may be an improvement or a regression, and
        // no mechanism can tell them apart. It is a finding until a person writes down which.
        var report = V2Frame().Append("packages", "telnetd 0.17-1").Judge();

        var finding = Findings(report).Single();
        Assert.Equal(ParityDifferenceKinds.Extra, finding.Kind);
        Assert.Equal("telnetd", finding.Key);
    }

    [Fact]
    public void A_changed_file_reports_which_lines_moved_rather_than_that_it_changed()
    {
        var report = V2Frame()
            .Replace("units.user.files", "Description=Chromium Kiosk Browser", "Description=Kiosk")
            .Judge();

        var finding = Findings(report).Single();
        Assert.Equal(ParityDifferenceKinds.Changed, finding.Kind);
        Assert.Equal("/home/framelink/.config/systemd/user/chromium-kiosk.service", finding.Key);
        Assert.Contains("- Description=Chromium Kiosk Browser", finding.Detail);
        Assert.Contains("+ Description=Kiosk", finding.Detail);
    }

    [Fact]
    public void A_duplicated_directive_is_a_difference_even_though_the_line_is_present()
    {
        var report = V2Frame().Append("boot.config", "dtoverlay=vc4-kms-dsi-waveshare-panel-v2,10_1_inch_a").Judge();

        var finding = Findings(report).Single();
        Assert.Equal(ParityDifferenceKinds.Changed, finding.Kind);
        Assert.Equal("×1", finding.Reference);
        Assert.Equal("×2", finding.Observed);
    }

    // -------------------------------------------------------------------------------------------
    // Not looking is not the same as agreeing
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_probe_that_failed_makes_the_run_incomplete_and_never_parity()
    {
        var report = V2Frame().Fail("eeprom.config", 127, "rpi-eeprom-config: command not found").Judge();

        Assert.Equal(ParityOutcomes.Incomplete, report.Outcome);
        Assert.Equal(0, report.Findings);
        Assert.Equal(1, report.Unresolved);

        var facet = report.Facets.Single(facet => facet.Facet == "eeprom.config");
        Assert.Equal(FacetStates.ProbeFailed, facet.State);
        Assert.Contains("command not found", facet.Limitation, StringComparison.Ordinal);
        Assert.Contains("eeprom.config", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_facet_nobody_collected_is_incomplete_and_is_named()
    {
        var report = V2Frame().Drop("modprobe.d").Judge();

        Assert.Equal(ParityOutcomes.Incomplete, report.Outcome);
        Assert.Equal(FacetStates.NotCollected, report.Facets.Single(facet => facet.Facet == "modprobe.d").State);
    }

    [Fact]
    public void An_elevated_probe_skipped_for_want_of_root_is_reported_rather_than_assumed()
    {
        // What an ordinary unprivileged `fl.py parity` produces. It must not read as parity: the
        // array's firmware version was never looked at, and a harness that returned 0 for "I could
        // not look" would be worse than no harness.
        var report = V2Frame()
            .Skip("audio.xvf3800.firmware", "Needs root, and this run was unprivileged.")
            .Judge();

        Assert.Equal(ParityOutcomes.Incomplete, report.Outcome);
        Assert.Equal(0, report.Findings);

        var facet = report.Facets.Single(facet => facet.Facet == "audio.xvf3800.firmware");
        Assert.Equal(FacetStates.NotCollected, facet.State);
        Assert.Contains("root", facet.Limitation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_collection_is_incomplete_and_not_a_frame_that_matches_nothing()
    {
        var report = ParityJudge.Judge(
            ReferenceInventory.Load(Root),
            new ParityObservationSet(),
            ExpectedDifferenceLedger.Load(Root),
            CatalogDocument.Ids(Root),
            DateTimeOffset.UtcNow);

        Assert.Equal(ParityOutcomes.Incomplete, report.Outcome);
        Assert.Equal(0, report.Findings);
        Assert.Equal(ParityFacets.All.Count(facet => facet.IsCovered), report.Unresolved);
    }

    // -------------------------------------------------------------------------------------------
    // The artifacts
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_coverage_report_states_both_gaps_and_their_sizes()
    {
        var report = V2Frame().Judge();

        Assert.Equal(5, report.Coverage.UncoveredSections.Count);
        Assert.NotEmpty(report.Coverage.PartialSections);
        Assert.All(report.Coverage.UncoveredSections, facet => Assert.False(string.IsNullOrWhiteSpace(facet.Limitation)));
        Assert.All(report.Coverage.PartialSections, facet => Assert.False(string.IsNullOrWhiteSpace(facet.Limitation)));

        Assert.Equal(81, report.Coverage.CatalogResources);
        Assert.Equal(
            report.Coverage.CatalogResources,
            report.Coverage.ResourcesWithReference.Count + report.Coverage.ResourcesWithoutReference.Count);
        Assert.NotEmpty(report.Coverage.ResourcesWithoutReference);
        Assert.NotEmpty(report.Coverage.IgnoredKeys);
    }

    [Fact]
    public void The_human_summary_leads_with_the_verdict_and_ends_with_the_coverage()
    {
        var report = V2Frame()
            .RemoveLines("units.system.enabled", line => line.StartsWith("cpu-performance.service", StringComparison.Ordinal))
            .Judge();

        var text = ParitySummary.Render(report);

        Assert.Contains("verdict     DIFFERS", text, StringComparison.Ordinal);
        Assert.Contains("missing   cpu-performance.service", text, StringComparison.Ordinal);
        Assert.Contains("catalog resources with no v1 state to compare against", text, StringComparison.Ordinal);
        Assert.Contains("system.timezone", text, StringComparison.Ordinal);
        Assert.Contains("pipewire.graph", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_survives_its_own_json()
    {
        // The machine-readable artifact is the half another session reads. A field that does not
        // round-trip is a field that quietly becomes null in whatever reads it next.
        var report = V2Frame().Judge();
        var json = JsonSerializer.Serialize(report, ParityJson.Default.ParityReport);
        var back = JsonSerializer.Deserialize(json, ParityJson.Default.ParityReport);

        Assert.NotNull(back);
        Assert.Equal(report.Outcome, back.Outcome);
        Assert.Equal(report.Findings, back.Findings);
        Assert.Equal(report.Expected, back.Expected);
        Assert.Equal(report.Facets.Count, back.Facets.Count);
        Assert.Equal(
            report.Coverage.ResourcesWithoutReference.Count,
            back.Coverage.ResourcesWithoutReference.Count);
    }

    // -------------------------------------------------------------------------------------------
    // Fixtures
    // -------------------------------------------------------------------------------------------

    /// <summary>Every shell redirection target in a command, ignoring single-quoted text.</summary>
    private static IEnumerable<string> Redirections(string command)
    {
        var quoted = false;

        for (var index = 0; index < command.Length; index++)
        {
            if (command[index] == '\'')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted || command[index] != '>')
            {
                continue;
            }

            var scan = index + 1;
            if (scan < command.Length && command[scan] == '>')
            {
                scan++;
            }

            while (scan < command.Length && command[scan] == ' ')
            {
                scan++;
            }

            var start = scan;
            if (scan < command.Length && command[scan] == '&')
            {
                scan++;
            }

            while (scan < command.Length
                && !char.IsWhiteSpace(command[scan])
                && command[scan] is not (';' or '|' or '&'))
            {
                scan++;
            }

            yield return command[start..scan];
        }
    }

    private static IEnumerable<ParityDifference> Findings(ParityReport report) =>
        report.Facets
            .SelectMany(facet => facet.Differences)
            .Where(difference => string.Equals(difference.Verdict, ParityVerdicts.Finding, StringComparison.Ordinal));

    /// <summary>
    /// The v1 replay with every difference a correct v2 frame is supposed to have.
    /// </summary>
    /// <remarks>
    /// Each mutation is one the catalog's "Does not become a device resource" section or an
    /// Appendix A decision requires, and between them they exercise every entry in the committed
    /// ledger — which is what the parity case asserts by finding no stale entries.
    /// </remarks>
    private static ParityFixture V2Frame() =>
        new ParityFixture(Root)
            // Decisions 40 and 41: Docker leaves, and everything that existed because of it.
            .Clear("docker")
            .Clear("kiosk.compose")
            .RemoveLines("packages", line =>
                line.StartsWith("docker-", StringComparison.Ordinal)
                || line.StartsWith("containerd.io ", StringComparison.Ordinal))
            .RemoveLines("apt.sources", line => line.StartsWith("docker.list", StringComparison.Ordinal))
            .RemoveLines("units.system.enabled", line =>
                line.StartsWith("docker", StringComparison.Ordinal)
                || line.StartsWith("containerd.service", StringComparison.Ordinal))
            .RemoveLines("units.system.files", _ => false)
            .RemoveFile("units.system.files", "/etc/systemd/system/docker-selfheal.service")
            .Replace("users.groups", ",985(docker)", string.Empty)
            .RemoveLines("users.groups", line => line.StartsWith("docker:x:", StringComparison.Ordinal))
            .RemoveLines("network", line =>
                line.StartsWith("docker0", StringComparison.Ordinal)
                || line.StartsWith("br-", StringComparison.Ordinal))
            // A testbed diagnostic the guides never described.
            .RemoveLines("units.system.enabled", line => line.StartsWith("sshd-mute-monitor", StringComparison.Ordinal))
            .RemoveFile("units.system.files", "/etc/systemd/system/sshd-mute-monitor.service")
            // Decision 39: the app ships inside the agent, so the httpd unit and the clone go.
            .RemoveLines("units.user.enabled", line => line.StartsWith("framelink-spa", StringComparison.Ordinal))
            .RemoveFile("units.user.files", "/home/framelink/.config/systemd/user/framelink-spa.service")
            .RemoveLines("packages", line =>
                line.StartsWith("git ", StringComparison.Ordinal) || line.StartsWith("git-man ", StringComparison.Ordinal))
            // The GPIO daemon moves inside the binary, with its three python packages.
            .RemoveLines("units.user.enabled", line => line.StartsWith("framelink-gpio", StringComparison.Ordinal))
            .RemoveFile("units.user.files", "/home/framelink/.config/systemd/user/framelink-gpio.service")
            .RemoveLines("packages", line =>
                line.StartsWith("python3-gpiozero ", StringComparison.Ordinal)
                || line.StartsWith("python3-lgpio ", StringComparison.Ordinal)
                || line.StartsWith("python3-websockets ", StringComparison.Ordinal))
            // Decision 47: the restart and the watchdog become supervision, not units.
            .RemoveLines("units.user.enabled", line => line.StartsWith("chromium-restart", StringComparison.Ordinal)
                || line.StartsWith("chromium-watchdog", StringComparison.Ordinal))
            .RemoveFile("units.user.files", "/home/framelink/.config/systemd/user/chromium-restart.service")
            .RemoveFile("units.user.files", "/home/framelink/.config/systemd/user/chromium-restart.timer")
            .RemoveFile("units.user.files", "/home/framelink/.config/systemd/user/chromium-watchdog.service")
            .RemoveFile("units.user.files", "/home/framelink/.config/systemd/user/chromium-watchdog.timer")
            // The agent itself, which v1 had no equivalent of.
            .Append("units.system.enabled", "fl-agent.service enabled enabled")
            // Per-device and per-card facts nothing sets.
            .Replace("network", "framelink-douwe", "framelink-mule")
            .Replace("boot.cmdline", "root=PARTUUID=f870549c-02", "root=PARTUUID=aa11bb22-02")
            .Replace("boot.cmdline", " ds=nocloud;i=rpi-imager-1776005232619", string.Empty)
            // guide 5's zram, which the capture has no line for either way.
            .Append("system.governor-zram-tmpfs", "/dev/zram0 partition 512M");

    /// <summary>
    /// An observation built out of the frozen reference, with named mutations applied.
    /// </summary>
    /// <remarks>
    /// Nothing here is invented: a facet's output starts as the exact text of the block it will be
    /// compared against, so the only way a test fails is that the comparison is wrong. The mutators
    /// operate on that text as lines, which is how a real frame differs from the capture too.
    /// </remarks>
    private sealed class ParityFixture
    {
        private readonly Dictionary<string, string> _stdout = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Status, string Stderr)> _failures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _skipped = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> _reference;
        private readonly string _root;

        public ParityFixture(string root)
        {
            _root = root;
            _reference = ReferenceInventory.Load(root);

            foreach (var facet in ParityFacets.All.Where(facet => facet.IsCovered))
            {
                _stdout[facet.Id] = _reference[facet.Section];
            }
        }

        public ParityFixture Replace(string facet, string from, string to)
        {
            _stdout[facet] = _stdout[facet].Replace(from, to, StringComparison.Ordinal);
            return this;
        }

        public ParityFixture Append(string facet, string line)
        {
            _stdout[facet] = _stdout[facet] + "\n" + line;
            return this;
        }

        public ParityFixture Clear(string facet)
        {
            _stdout[facet] = string.Empty;
            return this;
        }

        public ParityFixture RemoveLines(string facet, Func<string, bool> predicate)
        {
            _stdout[facet] = string.Join('\n', _stdout[facet]
                .Split('\n')
                .Where(line => !predicate(line)));
            return this;
        }

        /// <summary>Removes one <c>##### path</c> section and its body from a file-set facet.</summary>
        public ParityFixture RemoveFile(string facet, string path)
        {
            var kept = new List<string>();
            var skipping = false;

            foreach (var line in _stdout[facet].Split('\n'))
            {
                if (line.StartsWith(FacetParser.FileMarker, StringComparison.Ordinal))
                {
                    skipping = string.Equals(line[FacetParser.FileMarker.Length..].Trim(), path, StringComparison.Ordinal);
                }

                if (!skipping)
                {
                    kept.Add(line);
                }
            }

            _stdout[facet] = string.Join('\n', kept);
            return this;
        }

        public ParityFixture Fail(string facet, int status, string stderr)
        {
            _failures[facet] = (status, stderr);
            return this;
        }

        public ParityFixture Skip(string facet, string reason)
        {
            _skipped[facet] = reason;
            return this;
        }

        public ParityFixture Drop(string facet)
        {
            _dropped.Add(facet);
            return this;
        }

        public ParityReport Judge() => ParityJudge.Judge(
            _reference,
            Build(),
            ExpectedDifferenceLedger.Load(_root),
            CatalogDocument.Ids(_root),
            DateTimeOffset.UtcNow);

        private ParityObservationSet Build() => new()
        {
            Collector = "ParityHarnessTests",
            Host = "fixture",
            CollectedUtc = DateTimeOffset.UtcNow,
            Elevated = true,
            Observations =
            [
                .. _stdout
                    .Where(entry => !_dropped.Contains(entry.Key))
                    .Select(entry => new ParityObservation
                    {
                        Facet = entry.Key,
                        Command = "(fixture)",
                        ExitStatus = _failures.TryGetValue(entry.Key, out var failure) ? failure.Status : 0,
                        Stdout = entry.Value,
                        Stderr = _failures.TryGetValue(entry.Key, out var complaint) ? complaint.Stderr : string.Empty,
                        Skipped = _skipped.GetValueOrDefault(entry.Key),
                    }),
            ],
        };
    }
}
