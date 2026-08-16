using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FrameLink.Upstream;

/// <summary>
/// <c>upstream-review.json</c> — the record of which upstream versions a human has looked at
/// (§7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The ledger is for versions somebody chose.</b> A pin in source, or a floating dependency
/// whose resolved version a person has actually seen. That is the whole membership rule, and it
/// is what keeps Debian package versions out: the apt resources move forward on their own through
/// security updates by design, the Fleet Manager records what it finds on each frame, and
/// recorded inventory is not a decision. An entry per Debian package would convert a
/// once-per-release question into a daily one, which is the reliable way to produce a gate nobody
/// operates.
/// </para>
/// <para>
/// <b>What the detector compares is <see cref="UpstreamReview.Upstream"/>, not
/// <see cref="UpstreamEntry.Pinned"/>.</b> Those are different questions. "What do we use" is
/// answered by source; "what was upstream serving when somebody last looked" is answered here,
/// and only the second one can tell you that something moved overnight. It also makes both
/// resolutions of a move expressible with one field: adopting the new version and deliberately
/// staying on the old one both end with a person recording the version they saw.
/// </para>
/// <para>
/// <b>Nothing in here fails a build.</b> The operator's instruction was detection, then a pin
/// decision or an upgrade-and-validate before a release — so the probes live in a tool that no
/// build, test or publish invokes, and the only always-on check is
/// <see cref="Problems"/> plus the source-agreement assertions in the suite. Those fail when a
/// human changed a pin and forgot the ledger, never because an upstream published something
/// while everyone was asleep.
/// </para>
/// </remarks>
public sealed record UpstreamLedger
{
    /// <summary>The file, at the repository root.</summary>
    public const string FileName = "upstream-review.json";

    /// <summary>The prose header, carried verbatim so the file explains itself where it lives.</summary>
    [JsonPropertyName("$comment")]
    public IReadOnlyList<string> Comment { get; init; } = [];

    /// <summary>Every reviewed upstream, in no significant order.</summary>
    public IReadOnlyList<UpstreamEntry> Entries { get; init; } = [];

    /// <summary>Reads a ledger from disk.</summary>
    /// <exception cref="JsonException">The file is not a ledger.</exception>
    public static UpstreamLedger Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return JsonSerializer.Deserialize(File.ReadAllText(path), UpstreamJson.Default.UpstreamLedger)
            ?? throw new JsonException($"{path} deserialised to nothing at all.");
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding the solution.</summary>
    /// <remarks>
    /// The same walk the suite uses. It means the tool finds the one ledger in the repository it
    /// was built from, whatever directory the operator happened to be standing in.
    /// </remarks>
    public static string LocateRepositoryRoot(string start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);

        var probe = new DirectoryInfo(start);
        for (var depth = 0; depth < 10 && probe is not null; depth++, probe = probe.Parent)
        {
            if (File.Exists(Path.Combine(probe.FullName, "FrameLink.slnx")))
            {
                return probe.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No FrameLink.slnx at or above {start}.");
    }

    /// <summary>The entry with this id, or null.</summary>
    public UpstreamEntry? Find(string id) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

    /// <summary>This ledger with one entry replaced.</summary>
    public UpstreamLedger With(UpstreamEntry replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        return this with
        {
            Entries = [.. Entries.Select(entry =>
                string.Equals(entry.Id, replacement.Id, StringComparison.Ordinal) ? replacement : entry)],
        };
    }

    /// <summary>
    /// Serialisation settings that keep this file legible to the person who has to read it.
    /// </summary>
    /// <remarks>
    /// The relaxed encoder is the whole reason for copying the context's options rather than using
    /// them directly: the default escapes an apostrophe to <c>'</c>, and a ledger whose every
    /// note is written in that is a file nobody reviews. Only the encoder changes; the resolver is
    /// still the source-generated one, so nothing here needs reflection.
    /// </remarks>
    private static readonly JsonTypeInfo Canonical = MakeCanonicalTypeInfo();

    /// <summary>The canonical text of this ledger — what <see cref="Save"/> writes.</summary>
    /// <remarks>
    /// Newlines are normalised to <c>\n</c> because <c>.gitattributes</c> puts LF in the working
    /// tree on every OS, and a tool that wrote CRLF here would show the whole file as changed on
    /// Windows. The suite asserts the committed file already equals this, so a recorded review is
    /// always a one-entry diff rather than a reformat.
    /// </remarks>
    public string ToJson() =>
        JsonSerializer.Serialize(this, Canonical).ReplaceLineEndings("\n") + "\n";

    /// <summary>Writes this ledger back over the file it came from.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>
    /// Everything structurally wrong with this ledger, in plain sentences. Empty means sound.
    /// </summary>
    /// <remarks>
    /// Shared by the tool and the suite on purpose. A ledger the tool would refuse to act on must
    /// be a red test rather than a surprise at the moment somebody is trying to cut a release.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (Entries.Count == 0)
        {
            problems.Add("The ledger has no entries at all.");
        }

        foreach (var entry in Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                problems.Add("An entry has no id.");
                continue;
            }

            if (!seen.Add(entry.Id))
            {
                problems.Add($"{entry.Id}: two entries share this id.");
            }

            problems.AddRange(entry.Problems());
        }

        return problems;
    }

    private static JsonTypeInfo MakeCanonicalTypeInfo()
    {
        var options = new JsonSerializerOptions(UpstreamJson.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.MakeReadOnly();
        return options.GetTypeInfo(typeof(UpstreamLedger));
    }
}

/// <summary>One reviewed upstream dependency.</summary>
public sealed record UpstreamEntry
{
    /// <summary>Stable identifier, and what the tool's <c>review</c> verb names.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What it is, for somebody reading the report rather than the code.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Where in the repository the choice actually lives.</summary>
    public string PinnedIn { get; init; } = string.Empty;

    /// <summary>The version this project uses today.</summary>
    public string Pinned { get; init; } = string.Empty;

    /// <summary>How to ask upstream what it is serving now.</summary>
    public UpstreamProbe Probe { get; init; } = new();

    /// <summary>What the last human review saw, and decided.</summary>
    public UpstreamReview Reviewed { get; init; } = new();

    /// <summary>Whether this entry deliberately stays behind what upstream offers.</summary>
    [JsonIgnore]
    public bool IsHeld => string.Equals(Reviewed.Verdict, UpstreamReview.Held, StringComparison.Ordinal);

    /// <summary>Everything structurally wrong with this entry.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Title))
        {
            problems.Add($"{Id}: no title, so a report about it would say nothing.");
        }

        if (string.IsNullOrWhiteSpace(PinnedIn))
        {
            problems.Add($"{Id}: does not say where in the repository the version is chosen.");
        }

        if (string.IsNullOrWhiteSpace(Pinned))
        {
            problems.Add($"{Id}: does not say which version this project uses.");
        }

        if (!UpstreamProbe.Kinds.Contains(Probe.Kind, StringComparer.Ordinal))
        {
            problems.Add(
                $"{Id}: probe kind '{Probe.Kind}' is not one of {string.Join(", ", UpstreamProbe.Kinds)}.");
        }

        if (Probe.Url is null || !Probe.Url.IsAbsoluteUri)
        {
            problems.Add($"{Id}: the probe has no absolute URL.");
        }

        if (string.Equals(Probe.Kind, UpstreamProbe.DotnetChannel, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(Probe.Channel))
        {
            problems.Add($"{Id}: a {UpstreamProbe.DotnetChannel} probe needs a channel.");
        }

        // A path-commit probe with no path watches the default branch, which moves for reasons that
        // have nothing to do with the artifact. It would answer, it would answer differently every
        // week, and every answer would be noise — the one failure mode a release gate cannot carry,
        // because a gate that always says "moved" is a gate nobody reads.
        if (string.Equals(Probe.Kind, UpstreamProbe.GithubPathCommit, StringComparison.Ordinal)
            && Probe.Url?.Query.Contains("path=", StringComparison.Ordinal) != true)
        {
            problems.Add(
                $"{Id}: a {UpstreamProbe.GithubPathCommit} probe needs a path= in its query, "
                + "or it watches the whole branch.");
        }

        if (string.IsNullOrWhiteSpace(Reviewed.Upstream))
        {
            problems.Add($"{Id}: no reviewed upstream version, so nothing can be compared against it.");
        }

        if (!UpstreamReview.Verdicts.Contains(Reviewed.Verdict, StringComparer.Ordinal))
        {
            problems.Add(
                $"{Id}: verdict '{Reviewed.Verdict}' is not one of {string.Join(", ", UpstreamReview.Verdicts)}.");
        }

        if (string.IsNullOrWhiteSpace(Reviewed.Note))
        {
            problems.Add($"{Id}: a review with no reason recorded is not a review.");
        }

        if (Reviewed.Utc == default)
        {
            problems.Add($"{Id}: no review date.");
        }

        // A hold is the one verdict that has to disagree with itself to make sense: it says the
        // project is on something other than what upstream serves, on purpose. Recording it while
        // the two versions match means somebody set the field and not the reason.
        if (IsHeld && string.Equals(Pinned, Reviewed.Upstream, StringComparison.Ordinal))
        {
            problems.Add($"{Id}: verdict is 'held' while the pin already equals the reviewed upstream version.");
        }

        return problems;
    }
}

/// <summary>How to ask an upstream what it is serving.</summary>
public sealed record UpstreamProbe
{
    /// <summary>The published Raspberry Pi OS image directories.</summary>
    public const string RaspiosImages = "raspios-images";

    /// <summary>A GitHub repository's latest release.</summary>
    public const string GithubRelease = "github-release";

    /// <summary>
    /// The newest commit touching one path in a GitHub repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For an upstream that publishes no releases at all.</b>
    /// <see cref="GithubRelease"/> reads <c>tag_name</c> from <c>/releases/latest</c>, and against
    /// <c>respeaker/reSpeaker_XVF3800_USB_4MIC_ARRAY</c> that endpoint answers <b>404</b> — zero
    /// releases, zero tags, loose files on a moving default branch. Registering that upstream under
    /// a probe kind which cannot answer for it would have been worse than leaving it out: §7.1 makes
    /// unreachable block a release exactly as a move does, so every future <c>check</c> would have
    /// stopped on a probe that could never succeed, and the honest reading of the failure would have
    /// been drowned by an entry crying wolf.
    /// </para>
    /// <para>
    /// <b>A path, never a branch.</b> <c>commits?path=…&amp;per_page=1</c> answers 200 with the
    /// newest commit that touched that path, which is precisely the event worth a human's attention:
    /// <i>upstream rebuilt the artifact this project fetches</i>. Watching the branch head instead
    /// would report "moved" on every unrelated push — this repository's head has moved several times
    /// since the pinned directory last did — so the missing <c>path=</c> is a defect
    /// <see cref="UpstreamEntry.Problems"/> refuses rather than a detail of the URL.
    /// </para>
    /// </remarks>
    public const string GithubPathCommit = "github-path-commit";

    /// <summary>A NuGet package's published versions.</summary>
    public const string NugetPackage = "nuget-package";

    /// <summary>A .NET release channel.</summary>
    public const string DotnetChannel = "dotnet-channel";

    /// <summary>
    /// Every probe this tool can perform — and, deliberately, the boundary of the ledger.
    /// </summary>
    /// <remarks>
    /// There is no apt or Debian kind here, and that absence is load-bearing rather than a gap
    /// waiting to be filled. A Debian package version is not something anybody chose: it moves
    /// forward on its own under security updates, which is what this project wants it to do, and
    /// the Fleet Manager records the versions it observes on each frame. Adding a kind for it
    /// would put a value that changes weekly in front of a gate that runs once per release.
    /// </remarks>
    public static IReadOnlyList<string> Kinds { get; } =
        [RaspiosImages, GithubRelease, GithubPathCommit, NugetPackage, DotnetChannel];

    /// <summary>Which of <see cref="Kinds"/> this is.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>What the probe reads.</summary>
    public Uri? Url { get; init; }

    /// <summary>Which channel, for <see cref="DotnetChannel"/>.</summary>
    public string? Channel { get; init; }
}

/// <summary>What a human saw, and what they decided about it.</summary>
public sealed record UpstreamReview
{
    /// <summary>The project uses what upstream serves.</summary>
    public const string Adopted = "adopted";

    /// <summary>The project deliberately stays behind what upstream serves.</summary>
    public const string Held = "held";

    /// <summary>The two verdicts a review can reach.</summary>
    public static IReadOnlyList<string> Verdicts { get; } = [Adopted, Held];

    /// <summary>The day of the review.</summary>
    public DateOnly Utc { get; init; }

    /// <summary>What upstream was serving that day. The only field the detector compares.</summary>
    public string Upstream { get; init; } = string.Empty;

    /// <summary>Whether the reviewer took it or stayed put.</summary>
    public string Verdict { get; init; } = Adopted;

    /// <summary>Why. A review with no reason is a timestamp.</summary>
    public string Note { get; init; } = string.Empty;
}
