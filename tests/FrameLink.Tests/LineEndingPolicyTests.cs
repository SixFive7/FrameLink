using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace FrameLink.Tests;

/// <summary>
/// Whether every tracked file's line endings on disk are the ones <c>.gitattributes</c> declares
/// for it — in both directions.
/// </summary>
/// <remarks>
/// <para>
/// CRLF drift is the one defect git is structurally unable to report. <c>.gitattributes</c> opens
/// with <c>* text=auto eol=lf</c>, so a working-tree file holding CRLF <em>cleans back to the
/// committed LF blob</em>: <c>git status</c> calls it unmodified, <c>git diff</c> prints nothing,
/// and no ordinary command ever mentions it. Every tool that opens the file, meanwhile, reads
/// different bytes than the repository contains.
/// </para>
/// <para>
/// <b>What that cost was not lost time.</b> Three mutation-testing runs applied no patches at all
/// and reported success — the patches are LF and did not match a CRLF working tree, so the suite
/// went green over guards that had never once been exercised. A green suite that is testing
/// nothing is the worst thing a suite can produce. The same drift made
/// <see cref="GuiFreshnessTests"/> pass for whoever built the bundle and fail for everybody else,
/// and it rewrote three shell scripts into files a Pi's <c>/bin/sh</c> will not run.
/// </para>
/// <para>
/// Prevention is two files, and this is the second of them. <c>.editorconfig</c> pins
/// <c>end_of_line</c> under <c>[*]</c>, which is what stops editors, <c>dotnet format</c> and
/// prettier writing the drift in the first place; it used to name only shell scripts, which
/// covered none of the C#, Python, TypeScript, Svelte, JSON or markdown that actually drifted.
/// This is the gate for whatever still gets past that, and it has to be a test, because git will
/// not raise one.
/// </para>
/// <para>
/// The policy is never written down here. <c>git ls-files --eol</c> resolves <c>.gitattributes</c>
/// per path and reports what is on disk beside it, so adding a file, or a rule, needs no edit to
/// this file. A check that has to be maintained is a check that gets deleted by the third person
/// who hits it.
/// </para>
/// </remarks>
public sealed class LineEndingPolicyTests
{
    /// <summary>
    /// Below this many examined paths, assume the check itself broke rather than that the
    /// repository shrank. A git call that returns nothing must not read as a clean run — that is
    /// the same silent no-op this whole file exists to make impossible.
    /// </summary>
    private const int FewestPlausiblePaths = 400;

    /// <summary>
    /// The Svelte console's sources. A CRLF here does not merely sit on disk: it is compiled into
    /// the committed <c>wwwroot</c> bundle, which is why this prefix appears twice below.
    /// </summary>
    private const string GuiSources = "src/FrameLink.Control/gui/";

    /// <summary>The extensions that tree is written in, none of which had a rule before today.</summary>
    private static readonly string[] GuiExtensions = [".svelte", ".ts", ".css", ".html"];

    /// <summary>
    /// One record of <c>git ls-files --eol</c>: the path, the <c>text</c>/<c>eol</c> attributes
    /// git resolved for it, and the line endings git found in the working tree.
    /// </summary>
    /// <param name="Path">Repository-relative, forward slashes, as git prints it.</param>
    /// <param name="Attributes">The <c>attr/</c> column — e.g. <c>text=auto eol=lf</c>.</param>
    /// <param name="OnDisk">
    /// The <c>w/</c> column: <c>lf</c>, <c>crlf</c>, <c>mixed</c>, <c>none</c>, <c>-text</c>, or
    /// empty when the path is tracked but absent from the working tree.
    /// </param>
    private readonly record struct TrackedPath(string Path, string Attributes, string OnDisk);

    /// <summary>Read once: four facts ask the same question of the same five hundred paths.</summary>
    private static readonly Lazy<List<TrackedPath>> Everything = new(Read);

    [Fact]
    public void Every_tracked_file_holds_the_line_endings_gitattributes_declares()
    {
        var root = GuiFreshnessTests.RepositoryRoot();
        var wrong = new List<string>();
        var offenders = new List<string>();
        var examined = 0;

        foreach (var tracked in Everything.Value)
        {
            var (binary, declared) = Policy(tracked.Attributes);

            // The `binary` macro expands to `-text`. Those paths are bytes, not lines: a woff2 and
            // a firmware image both contain CR LF pairs that say nothing about line endings, and
            // reading them as text is how a check like this starts reporting fonts. An empty
            // `w/` column is a path git tracks but the working tree no longer has, which
            // `git status` is already loud about.
            if (binary || declared is null || tracked.OnDisk.Length == 0)
            {
                continue;
            }

            // git reports `-text` for a file whose *content* trips its NUL-byte heuristic even
            // where the attribute says text — tests/FrameLink.Tests/ProtocolVersionOrderingTests.cs
            // really does hold a NUL inside a string literal. Reading the bytes ourselves in that
            // case is load-bearing: otherwise one NUL anywhere buys a file a permanent exemption
            // from the policy, which is the shape of hole this file exists to close.
            var found = string.Equals(tracked.OnDisk, "-text", StringComparison.Ordinal)
                ? Classify(File.ReadAllBytes(
                    Path.Combine(root, tracked.Path.Replace('/', Path.DirectorySeparatorChar))))
                : tracked.OnDisk;

            examined++;

            // `none` is a file with no line feed in it at all — empty, or one line with no
            // terminator. It cannot disagree with a policy it never exercises.
            if (string.Equals(found, "none", StringComparison.Ordinal)
                || string.Equals(found, declared, StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add(tracked.Path);
            wrong.Add(
                $"  {tracked.Path} — on disk {found.ToUpperInvariant()}, "
                + $".gitattributes declares eol={declared}");
        }

        Assert.True(
            examined >= FewestPlausiblePaths,
            $"Only {examined} path(s) were examined, fewer than this repository can plausibly hold "
            + $"({FewestPlausiblePaths}). `git ls-files --eol` returning little or nothing has to "
            + "fail here rather than pass quietly — a check that silently stops checking is the "
            + "failure this test was written for.");

        var named = offenders.Count is > 0 and <= 20 ? string.Join(' ', offenders) : ".";
        var sweep = offenders.Count is > 0 and <= 20 ? string.Join(' ', offenders) : "-a";

        // Converting the file is only half the repair when the file is a GUI source, because the
        // committed bundle was compiled from the CRLF copy and stays poisoned after the source is
        // fixed. Saying so here is the difference between a repair that works and one that leaves
        // a wwwroot nobody else can reproduce.
        var rebuild = offenders.Exists(path => path.StartsWith(GuiSources, StringComparison.Ordinal))
            ? "\n\nAt least one of these is a GUI source, so converting it is only half the "
                + "repair: the committed wwwroot bundle was built from the CRLF copy. Run "
                + "`dotnet build src/FrameLink.Control` afterwards and commit wwwroot and "
                + "gui-build.stamp with it. A CR inside a Svelte text node reaches the output as "
                + "a two-character \\r escape rather than a raw CR, so neither the freshness "
                + "stamp nor a fresh clone can tell you the artifact is poisoned — see a73eec4."
            : string.Empty;

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} of {examined} tracked file(s) hold line endings .gitattributes does "
            + "not declare:\n\n"
            + string.Join('\n', wrong)
            + "\n\nNone of this shows up in `git status`. A CRLF working tree cleans back to the "
            + "committed LF blob, so git calls these files unmodified while every tool that opens "
            + "them reads different bytes than the repository holds — which is how three mutation "
            + "runs applied no patches at all and still went green.\n\n"
            + "Repair, from the repository root. The first command stages the content unchanged, "
            + "the second rewrites the files with the endings .gitattributes declares:\n\n"
            + $"  git add --renormalize -- {named} && git checkout-index -f -- {sweep}\n\n"
            + "Then fix whatever wrote them. .editorconfig pins end_of_line for every extension; "
            + "a tool that does not read it needs telling explicitly — Python's write_text emits "
            + "CRLF on Windows unless it is passed newline=\"\"."
            + rebuild);
    }

    [Fact]
    public void The_GUI_sources_that_poison_the_committed_bundle_are_inside_the_checked_set()
    {
        // a73eec4, proven constructively rather than theorised: one .svelte source with CRLF moved
        // three files in the committed wwwroot bundle and changed two of their content-hashed
        // names. Nothing could see it from either side. Svelte preserves a raw CR inside a
        // multi-line text node and emits it as a two-character \r escape, so GuiFreshnessTests'
        // newline normalisation has no CR to strip on the output side; and on the source side that
        // same normalisation makes the CRLF and LF copies of the file hash identically. The stamp
        // recorded no source change while the output changed, so the bundle committed cleanly,
        // stayed green on a fresh clone, and went red the moment anybody rebuilt.
        //
        // The check above is the only thing that could have caught it, and only if it reaches
        // these extensions — which are exactly the ones .editorconfig gave no rule until [*] got
        // one. That makes their coverage worth asserting rather than assuming: a rename of this
        // directory would otherwise leave the whole tree unexamined and the suite still green.
        var sources = Everything.Value
            .Where(tracked => tracked.Path.StartsWith(GuiSources, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            sources.Count >= 40,
            $"Only {sources.Count} tracked path(s) sit under {GuiSources}. The console is written "
            + "in more files than that, so this prefix has gone stale and the tree it names is no "
            + "longer being checked.");

        var extensions = sources
            .ConvertAll(tracked => Path.GetExtension(tracked.Path))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var extension in GuiExtensions)
        {
            Assert.Contains(extension, extensions);
        }

        var exempt = new List<string>();

        foreach (var tracked in sources)
        {
            var (binary, declared) = Policy(tracked.Attributes);
            if (binary || !string.Equals(declared, "lf", StringComparison.Ordinal))
            {
                exempt.Add($"  {tracked.Path} — resolved attributes: '{tracked.Attributes}'");
            }
        }

        Assert.True(
            exempt.Count == 0,
            $"{exempt.Count} console source(s) are not held to eol=lf, so a CRLF in them would "
            + "compile into the committed bundle unnoticed:\n\n"
            + string.Join('\n', exempt));
    }

    [Fact]
    public void Every_tracked_text_file_has_an_eol_policy_to_be_checked_against()
    {
        // The check above can only compare against a policy that exists. `* text=auto eol=lf` is
        // the first rule in .gitattributes precisely so that no path can arrive without one, and
        // this is what notices if that stops being true — an unpoliced file would otherwise be
        // skipped in silence and look identical to a file that passed.
        var unpoliced = new List<string>();

        foreach (var tracked in Everything.Value)
        {
            var (binary, declared) = Policy(tracked.Attributes);
            if (!binary && declared is null)
            {
                unpoliced.Add($"  {tracked.Path} — resolved attributes: '{tracked.Attributes}'");
            }
        }

        Assert.True(
            unpoliced.Count == 0,
            $"{unpoliced.Count} tracked path(s) are text but have no eol declared, so nothing "
            + "checks their line endings:\n\n"
            + string.Join('\n', unpoliced)
            + "\n\nEither give them an eol in .gitattributes or mark them binary. The baseline "
            + "rule `* text=auto eol=lf` has to stay first in that file: a later rule setting "
            + "`text` alone leaves the inherited eol in place, but `!text` or `-eol` removes it "
            + "and takes the path out of every check below.");
    }

    [Fact]
    public void The_policy_is_read_per_path_and_names_both_endings()
    {
        // Without this, "no CRLF anywhere" would look like a working check while being the wrong
        // rule. tools/harness/fl.cmd is REQUIRED to be CRLF — .gitattributes says so and cmd.exe
        // is why — so an LF fl.cmd is a failure exactly as a CRLF .cs file is. Both values are
        // read out of .gitattributes, which is what makes this an assertion about the derivation
        // rather than about a constant that happens to match.
        Assert.Equal("crlf", EolFor("tools/harness/fl.cmd"));
        Assert.Equal("lf", EolFor(".gitattributes"));

        // And the working tree is held to the CRLF half in practice, not merely permitted it.
        Assert.Equal("crlf", Find("tools/harness/fl.cmd").OnDisk);

        // A path the binary macro claims is never opened as text at all.
        var image = Everything.Value.First(
            tracked => tracked.Path.EndsWith(".png", StringComparison.Ordinal));

        Assert.True(Policy(image.Attributes).Binary, $"{image.Path} resolved as text.");
    }

    [Fact]
    public void The_editorconfig_tells_editors_what_gitattributes_requires()
    {
        // .gitattributes decides what git stores; .editorconfig decides what an editor,
        // `dotnet format` and prettier write back. When the two disagree, every save re-creates
        // the drift and the check above only reports it forever. Both expected values are read
        // out of .gitattributes so that the two files cannot quietly diverge.
        var settings = EditorConfig();

        var everywhere = settings.FindIndex(
            setting => setting.Section is "[*]" && setting.Key is "end_of_line");

        Assert.True(
            everywhere >= 0,
            "[*] in .editorconfig declares no end_of_line. Declaring it for a handful of named "
            + "extensions instead is what let C#, Python, TypeScript, Svelte, JSON and markdown "
            + "drift to CRLF while every shell script stayed correct.");

        Assert.Equal(EolFor(".gitattributes"), settings[everywhere].Value);

        var batch = settings.FindIndex(setting =>
            setting.Key is "end_of_line"
            && setting.Section.Contains("bat", StringComparison.Ordinal)
            && setting.Section.Contains("cmd", StringComparison.Ordinal));

        Assert.True(
            batch > everywhere,
            "No .editorconfig section after [*] gives *.bat and *.cmd an end_of_line of their "
            + "own, so an editor would save tools/harness/fl.cmd as LF against what "
            + ".gitattributes requires.");

        Assert.Equal(EolFor("tools/harness/fl.cmd"), settings[batch].Value);
    }

    [Theory]
    [InlineData("first\nsecond\n", "lf")]
    [InlineData("first\r\nsecond\r\n", "crlf")]
    [InlineData("first\r\nsecond\n", "mixed")]
    [InlineData("first\nsecond\r\n", "mixed")]
    [InlineData("one line, no terminator", "none")]
    [InlineData("", "none")]
    // A lone CR is content, not a line ending. git strips CR only where it precedes LF, and
    // matching that exactly is what makes this the same question git is answering.
    [InlineData("one\rtwo\n", "lf")]
    [InlineData("\r", "none")]
    public void A_files_endings_are_classified_the_way_git_classifies_them(string content, string expected) =>
        Assert.Equal(expected, Classify(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// The <c>text</c>/<c>eol</c> policy git resolved from <c>.gitattributes</c> for one path.
    /// </summary>
    /// <returns>
    /// <c>Binary</c> when the path is <c>-text</c> — which the <c>binary</c> macro expands to — and
    /// must not be read as text; otherwise the declared <c>eol</c>, or <see langword="null"/> when
    /// the path is text but no <c>eol</c> was declared for it.
    /// </returns>
    private static (bool Binary, string? Eol) Policy(string attributes)
    {
        var binary = false;
        string? eol = null;

        foreach (var token in attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "-text")
            {
                binary = true;
            }
            else if (token.StartsWith("eol=", StringComparison.Ordinal))
            {
                eol = token["eol=".Length..];
            }
        }

        return (binary, eol);
    }

    /// <summary>
    /// What line endings a run of bytes holds, in git's own vocabulary, so that a failure message
    /// speaks the same words as <c>git ls-files --eol</c>.
    /// </summary>
    private static string Classify(ReadOnlySpan<byte> bytes)
    {
        var lf = false;
        var crlf = false;

        for (var at = 0; at < bytes.Length;)
        {
            var next = bytes[at..].IndexOf((byte)'\n');
            if (next < 0)
            {
                break;
            }

            var newline = at + next;
            if (newline > 0 && bytes[newline - 1] == (byte)'\r')
            {
                crlf = true;
            }
            else
            {
                lf = true;
            }

            if (lf && crlf)
            {
                return "mixed";
            }

            at = newline + 1;
        }

        return crlf ? "crlf" : lf ? "lf" : "none";
    }

    /// <summary>The <c>eol</c> that <c>.gitattributes</c> declares for one tracked path.</summary>
    private static string EolFor(string path)
    {
        var (binary, eol) = Policy(Find(path).Attributes);

        Assert.False(binary, $"{path} resolves as binary, so it states no line-ending policy.");
        Assert.True(eol is not null, $"{path} resolves as text with no eol declared.");

        return eol!;
    }

    /// <summary>The one tracked entry for a path this test names by hand.</summary>
    private static TrackedPath Find(string path)
    {
        var matches = Everything.Value
            .Where(tracked => string.Equals(tracked.Path, path, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"git tracks {matches.Count} path(s) called '{path}'; this test names it directly "
            + "because .gitattributes gives it a policy of its own. If it moved, point this test "
            + "at the new path — the exception it stands for did not move with it.");

        return matches[0];
    }

    /// <summary>
    /// Every <c>key = value</c> in <c>.editorconfig</c>, in file order, tagged with its section.
    /// </summary>
    private static List<(string Section, string Key, string Value)> EditorConfig()
    {
        var settings = new List<(string Section, string Key, string Value)>();
        var section = string.Empty;

        foreach (var raw in File.ReadLines(
            Path.Combine(GuiFreshnessTests.RepositoryRoot(), ".editorconfig")))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                settings.Add((section, line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }
        }

        return settings;
    }

    /// <summary>
    /// Asks git for every tracked path, the attributes it resolves for that path, and the line
    /// endings the working-tree copy holds — one process, one pass, roughly a quarter of a second
    /// over five hundred paths.
    /// </summary>
    /// <remarks>
    /// <c>--eol</c> is deliberately git's answer rather than a reimplementation. The <c>attr/</c>
    /// column is <c>.gitattributes</c> resolved by the same code that decides what a checkout
    /// writes — the <c>binary</c> macro, last-rule-wins precedence and nested attribute files
    /// included — so this check cannot drift away from the policy it is checking. <c>-z</c> stops
    /// git quoting paths it considers unusual.
    /// </remarks>
    private static List<TrackedPath> Read()
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = GuiFreshnessTests.RepositoryRoot(),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            ArgumentList = { "ls-files", "--eol", "-z" },
        };

        Process? git;
        try
        {
            git = Process.Start(start);
        }
        catch (Win32Exception missing)
        {
            throw new InvalidOperationException(
                "git is not on PATH, so the line-ending policy cannot be read. This check reads "
                + "the repository rather than the build output, and git is what resolves "
                + ".gitattributes per path.",
                missing);
        }

        Assert.NotNull(git);

        using (git)
        {
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();

            Assert.True(git.ExitCode == 0, $"`git ls-files --eol -z` exited {git.ExitCode}.");

            var tracked = new List<TrackedPath>();

            foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // i/<eolinfo><pad>w/<eolinfo><pad>attr/<eolattr><pad><TAB><path>
                var tab = record.IndexOf('\t');
                Assert.True(tab > 0, $"Unparseable `git ls-files --eol` record: '{record}'.");

                var columns = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                Assert.True(
                    columns.Length >= 3
                    && columns[1].StartsWith("w/", StringComparison.Ordinal)
                    && columns[2].StartsWith("attr/", StringComparison.Ordinal),
                    $"`git ls-files --eol` changed shape: '{record[..tab]}'.");

                tracked.Add(new TrackedPath(
                    record[(tab + 1)..],
                    string.Join(' ', columns[2..])["attr/".Length..],
                    columns[1]["w/".Length..]));
            }

            return tracked;
        }
    }
}
