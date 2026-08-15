using FrameLink.Agent.Hosting;

namespace FrameLink.Agent.Resources;

/// <summary>
/// What dpkg's own database says about one package.
/// </summary>
/// <remarks>
/// The three states a naive check collapses are kept apart here, because they are three
/// different diagnoses. <see cref="Installed"/> is the only one that means the software is
/// usable; <see cref="ConfigFilesOnly"/> is dpkg's <c>rc</c>, which <c>dpkg -l</c> lists on a
/// line of its own and which an implementer scanning for "is it in the list" reads as present;
/// and <see cref="Partial"/> is an interrupted install that has files on disk and is not
/// configured.
/// </remarks>
public enum PackageState
{
    /// <summary>dpkg has never heard of it, or records it as removed and purged.</summary>
    NotInstalled,

    /// <summary>Unpacked and configured. The only state that counts as installed.</summary>
    Installed,

    /// <summary>Removed but not purged — dpkg's <c>rc</c>. The program's files are gone.</summary>
    ConfigFilesOnly,

    /// <summary>Unpacked, half-installed, half-configured, or awaiting triggers.</summary>
    Partial,

    /// <summary>dpkg-query could not be asked, or answered something unrecognised.</summary>
    Unreadable,
}

/// <summary>One package's state and version, as <c>dpkg-query</c> reports them.</summary>
/// <param name="State">Which of the five states dpkg is describing.</param>
/// <param name="Raw">dpkg's own word for it, verbatim, so an unrecognised state still travels.</param>
/// <param name="Version">The installed version, or empty when there is none.</param>
/// <remarks>
/// <b>The version is carried and never compared.</b> §7.1 is "everything floats, the build
/// freezes it", and the catalog says the same in the package block's own words: it "pins the
/// <i>presence</i>, not the version". So the version exists to be reported — in the observed
/// value, in telemetry and in the state diff against the v1 reference — and there is deliberately
/// no code path anywhere that turns a version difference into drift.
/// </remarks>
public readonly record struct PackageStatus(PackageState State, string Raw, string Version)
{
    /// <summary>Whether the package is actually usable.</summary>
    public bool IsInstalled => State is PackageState.Installed;

    /// <summary>
    /// Whether the package's files are on disk in any form.
    /// </summary>
    /// <remarks>
    /// The negative of <see cref="IsInstalled"/> is not this: <c>rc</c> means the files were
    /// removed and only configuration was kept, so a package in that state is absent in the sense
    /// <c>pkg.libspa-0.2-libcamera.absent</c> cares about.
    /// </remarks>
    public bool IsPresent => State is PackageState.Installed or PackageState.Partial;

    /// <summary>The state in the words an operator reads in a delta.</summary>
    public string Describe() => State switch
    {
        PackageState.Installed => Version.Length == 0 ? "installed" : $"installed {Version}",
        PackageState.ConfigFilesOnly => "removed, with its configuration left behind (dpkg 'rc')",
        PackageState.NotInstalled => "not installed",
        PackageState.Partial => $"only partly installed (dpkg says '{Raw}')",
        _ => $"could not be read from dpkg ({Raw})",
    };
}

/// <summary>
/// Why an <c>apt-get</c> run did not do what was asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so that an escalation names the right thing.</b> §2.5 requires the exact
/// expected-versus-observed delta to reach a person, and "labwc is not installed" is a true but
/// useless sentence when the cause is that the frame cannot reach <c>deb.debian.org</c>. The
/// classification is carried in the action text, so the operator reads the cause rather than the
/// symptom.
/// </para>
/// <para>
/// <b>None of these is <see cref="Reconcile.ObservationOutcome.Unevaluable"/>, and that is
/// deliberate.</b> Observe for a package is <c>dpkg-query</c> against a local database, so it
/// always has an answer, even on a frame with no network at all. There is nothing here that
/// "could not be determined" — the frame genuinely does not have the software, whatever the
/// reason, and <see cref="Reconcile.ObservationOutcome.Unevaluable"/> is documented as reserved
/// for an off-device authority that did not answer and as something that must never become the
/// place a real failure goes to be quiet. What a transient outage gets instead is §2.5's
/// exponential backoff, which is the mechanism that already exists for "try again in a while".
/// </para>
/// </remarks>
public enum AptFailure
{
    /// <summary>Nothing in the output describes any of the modes below.</summary>
    None,

    /// <summary>The package archive could not be reached or fetched from.</summary>
    ArchiveUnreachable,

    /// <summary>The archive answered, and does not offer this package.</summary>
    NotInArchive,

    /// <summary>Something else holds the dpkg or apt lock — usually unattended-upgrades.</summary>
    Locked,

    /// <summary>apt-get refused for a reason this classifier does not recognise.</summary>
    Other,
}

/// <summary>The result of one apt operation.</summary>
/// <param name="Succeeded">Whether apt-get exited zero.</param>
/// <param name="Failure">Why not, when it did not.</param>
/// <param name="Command">The command line that ran, verbatim and copy-pasteable.</param>
/// <param name="Detail">apt's own error text, reduced to the lines that say something.</param>
public readonly record struct AptOutcome(bool Succeeded, AptFailure Failure, string Command, string Detail);

/// <summary>
/// The agent's narrow window onto dpkg and apt.
/// </summary>
/// <remarks>
/// <para>
/// Narrow in the same sense as <see cref="ISystemControl"/>: it queries one package, installs one
/// package, and purges one package. There is no general "run apt with these arguments" entry
/// point, because §2.2's "static logic, dynamic values" forbids the catalog from ever becoming a
/// channel through which a server-supplied string reaches a root command line.
/// </para>
/// <para>
/// <b>Every apt invocation goes through <c>env DEBIAN_FRONTEND=noninteractive</c>.</b> The agent
/// runs as a systemd service with no controlling terminal, and a maintainer script that reaches
/// for debconf in that situation produces at best a stream of "unable to initialize frontend"
/// noise in the text an operator is meant to read, and at worst a child process waiting for an
/// answer that can never come. A reconciliation pass that never returns is worse than one that
/// fails, for exactly the reason <see cref="HostProcessRunner"/> already drains both pipes:
/// nothing on the screen ever changes to say so. Wrapping with <c>env</c> rather than widening
/// <see cref="IProcessRunner"/> keeps that interface's argument-vector-only contract intact — the
/// environment assignment is one more element of the same compiled-in vector, with no shell and
/// no word splitting anywhere.
/// </para>
/// <para>
/// <b><c>apt-get</c>, not <c>apt</c>.</b> Guide 5 step 2 uses <c>apt</c> because a human is
/// reading the output; <c>apt</c> prints "WARNING: apt does not have a stable CLI interface" when
/// it is not on a terminal, and means it. Guides 4 and 10 already use <c>apt-get</c>.
/// </para>
/// </remarks>
public sealed class AptPackages
{
    /// <summary>The dpkg database query tool.</summary>
    public const string DpkgQuery = "dpkg-query";

    /// <summary>The wrapper that carries the environment assignment.</summary>
    public const string Env = "env";

    /// <summary>The scripting-stable apt front end.</summary>
    public const string AptGet = "apt-get";

    /// <summary>What stops a maintainer script from asking a question nobody can answer.</summary>
    public const string NoninteractiveFrontend = "DEBIAN_FRONTEND=noninteractive";

    /// <summary>
    /// The dpkg-query format string, spelled as the catalog spells it.
    /// </summary>
    /// <remarks>
    /// <c>${db:Status-Status}</c> is the third field of dpkg's status triple, which is the one
    /// that distinguishes <c>installed</c> from <c>config-files</c>. The catalog gives two
    /// spellings — with and without <c>${Version}</c> — and this is the longer one, because the
    /// version costs nothing to read and a delta that names it is worth more.
    /// </remarks>
    public const string QueryFormat = "-f=${db:Status-Status} ${Version}\\n";

    /// <summary>
    /// The same query widened to name the package, for reading the whole database at once.
    /// </summary>
    /// <remarks>
    /// <c>${binary:Package}</c> rather than <c>${Package}</c>, and the difference only shows on a
    /// multi-arch system: the binary form appends <c>:arch</c> for a foreign-architecture package,
    /// which is what keeps the name unique when the same package is installed twice. The frames
    /// this project builds are single-architecture and the two forms agree there, so the choice
    /// costs nothing today and prevents two entries silently collapsing into one later.
    /// </remarks>
    public const string ListFormat = "-f=${db:Status-Status} ${binary:Package} ${Version}\\n";

    private static readonly string[] LockSignatures =
    [
        "Could not get lock",
        "Unable to acquire the dpkg frontend lock",
        "Unable to lock the administration directory",
        "dpkg frontend lock",
        "dpkg was interrupted",
    ];

    private static readonly string[] UnreachableSignatures =
    [
        "Temporary failure resolving",
        "Could not resolve host",
        "Could not connect to",
        "Unable to connect to",
        "Connection failed",
        "Connection timed out",
        "Network is unreachable",
        "No route to host",
        "Failed to fetch",
        "Unable to fetch some archives",
        "Some index files failed to download",
        "Certificate verification failed",
    ];

    private static readonly string[] MissingSignatures =
    [
        "Unable to locate package",
        "has no installation candidate",
        "Couldn't find any package by",
    ];

    private static readonly SortedDictionary<string, string> ReadOnlyEmpty = new(StringComparer.Ordinal);

    private readonly IProcessRunner _processes;

    /// <summary>Creates the window over <paramref name="processes"/>.</summary>
    public AptPackages(IProcessRunner processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        _processes = processes;
    }

    /// <summary>
    /// Asks dpkg what it knows about <paramref name="package"/>.
    /// </summary>
    /// <remarks>
    /// <b>Local, and therefore always answerable.</b> This is the whole Observe of every package
    /// resource, and it reads <c>/var/lib/dpkg/status</c> — no network, no apt cache, no
    /// dependence on anything having been run in this session. That is what makes it valid on a
    /// freshly booted frame, and it is why an unreachable archive is an Act failure rather than an
    /// observation that could not be made.
    /// </remarks>
    public async Task<PackageStatus> QueryAsync(string package, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);

        var result = await _processes
            .RunAsync(DpkgQuery, ["-W", QueryFormat, package], cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // dpkg-query exits 1 with "no packages found matching <x>" for a name this system has
            // never seen, which is every one of these on a stock image. That is the database
            // answering, not the database being unreadable, and conflating the two would make a
            // bare frame look broken rather than bare.
            return result.Combined.Contains("no packages found", StringComparison.OrdinalIgnoreCase)
                ? new PackageStatus(PackageState.NotInstalled, "not-installed", string.Empty)
                : new PackageStatus(PackageState.Unreadable, Summarise(result.Combined), string.Empty);
        }

        return Parse(result.StandardOutput);
    }

    /// <summary>
    /// Reads dpkg's whole database and returns every package it reports as <c>installed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One process for the whole system rather than ~930 of them. <c>dpkg-query -W</c> with no
    /// package argument reads <c>/var/lib/dpkg/status</c> once and prints a line per entry, so
    /// this costs the same as a single-package query and is the only shape in which reading the
    /// full set several times a day is reasonable at all.
    /// </para>
    /// <para>
    /// <b>Only <c>installed</c> survives the filter.</b> The database also holds <c>rc</c>
    /// entries — removed, configuration kept — and dpkg prints a version for them, so a caller
    /// that took every line would report software that is not on the disk. The same distinction
    /// <see cref="PackageStatus"/> exists to preserve, applied to the whole system.
    /// </para>
    /// <para>
    /// An empty result is returned for a failed query rather than a throw. The caller reports;
    /// it does not converge anything, and there is nothing to escalate — a frame whose dpkg
    /// cannot be read has a much larger problem that the package resources will find on their
    /// own.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> ListInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await _processes
            .RunAsync(DpkgQuery, ["-W", ListFormat], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded ? ParseList(result.StandardOutput) : ReadOnlyEmpty;
    }

    /// <summary>Reads the multi-package form of <c>dpkg-query</c> output.</summary>
    /// <remarks>
    /// Ordinal-ordered on the way out, because the canonical rendering the content hash is taken
    /// over has to be independent of the order dpkg happened to print. A line this parser cannot
    /// read is skipped rather than failing the batch: one damaged entry must not cost the other
    /// nine hundred.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ParseList(string? output)
    {
        var packages = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in (output ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var firstSpace = line.IndexOf(' ', StringComparison.Ordinal);
            if (firstSpace < 0 || !line.AsSpan(0, firstSpace).SequenceEqual("installed"))
            {
                continue;
            }

            var rest = line[(firstSpace + 1)..];
            var secondSpace = rest.IndexOf(' ', StringComparison.Ordinal);
            var name = (secondSpace < 0 ? rest : rest[..secondSpace]).Trim();
            var version = secondSpace < 0 ? string.Empty : rest[(secondSpace + 1)..].Trim();

            if (name.Length > 0)
            {
                packages[name] = version;
            }
        }

        return packages;
    }

    /// <summary>Reads one line of <c>dpkg-query</c> output in the catalog's format.</summary>
    public static PackageStatus Parse(string? output)
    {
        var line = FirstLine(output);
        if (line.Length == 0)
        {
            return new PackageStatus(PackageState.Unreadable, "no answer from dpkg-query", string.Empty);
        }

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var status = space < 0 ? line : line[..space];
        var version = space < 0 ? string.Empty : line[(space + 1)..].Trim();

        var state = status switch
        {
            "installed" => PackageState.Installed,
            "config-files" => PackageState.ConfigFilesOnly,
            "not-installed" => PackageState.NotInstalled,

            // Everything dpkg has a word for that is neither of the above means an install that
            // stopped part way. Treated as not-installed on purpose — the conservative direction,
            // because the alternative is claiming a half-configured package works.
            "half-installed" or "unpacked" or "half-configured" or "triggers-awaited" or "triggers-pending"
                => PackageState.Partial,

            _ => PackageState.Unreadable,
        };

        return new PackageStatus(state, status, version);
    }

    /// <summary>
    /// Refreshes the package list and installs <paramref name="package"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The refresh is part of the Act, not a resource.</b> The catalog has no resource for the
    /// apt index and every package entry lists its <c>dependsOn</c> as "—", so there is nothing to
    /// depend on and inventing one would be adding to the catalog rather than migrating it. The
    /// index is not a device setting anyway: nothing about it is independently verifiable in the
    /// §2.2 sense, because "fresh" is a property of a moment rather than of the frame.
    /// </para>
    /// <para>
    /// <b>It is also what makes the failure classification honest.</b> A frame whose lists are
    /// empty answers <c>E: Unable to locate package labwc</c> — a sentence that says the package
    /// does not exist, when what happened is that the archive was never reached. Running the
    /// refresh first means the two are distinguishable: a "missing" install failure preceded by an
    /// unreachable refresh is re-attributed to the archive, and only a missing install after a
    /// <i>successful</i> refresh is allowed to mean the package genuinely is not there.
    /// </para>
    /// <para>
    /// A failed refresh on its own never fails the resource. apt can still install from a cached
    /// index, and the install's own outcome is the thing being judged.
    /// </para>
    /// </remarks>
    public async Task<AptOutcome> InstallAsync(string package, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);

        var refresh = await RunAsync(["update"], cancellationToken).ConfigureAwait(false);
        var install = await RunAsync(["install", "-y", package], cancellationToken).ConfigureAwait(false);
        var command = refresh.Command + " && " + install.Command;

        if (install.Succeeded)
        {
            return new AptOutcome(true, AptFailure.None, command, install.Detail);
        }

        if (install.Failure is AptFailure.NotInArchive && refresh.Failure is AptFailure.ArchiveUnreachable)
        {
            return new AptOutcome(
                false,
                AptFailure.ArchiveUnreachable,
                command,
                $"{install.Detail} — and the package list could not be refreshed first: {refresh.Detail}");
        }

        return new AptOutcome(
            false,
            install.Failure,
            command,
            refresh.Succeeded && refresh.Failure is AptFailure.None
                ? install.Detail
                : $"{install.Detail} (refreshing the package list also had trouble: {refresh.Detail})");
    }

    /// <summary>
    /// Removes <paramref name="package"/> and its configuration.
    /// </summary>
    /// <remarks>
    /// <c>purge</c> rather than <c>remove</c>, and the reason is parity rather than tidiness: a
    /// removed-but-not-purged package keeps an <c>rc</c> line in <c>dpkg -l</c>, and the v1
    /// reference inventory has no line for it at all. Purging is the only outcome that leaves the
    /// two mechanically equal.
    /// </remarks>
    public async Task<AptOutcome> PurgeAsync(string package, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        return await RunAsync(["purge", "-y", package], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Which of apt's failure modes this output describes, or <see cref="AptFailure.None"/> when
    /// it describes none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The text, not the exit code.</b> <c>apt-get update</c> reports a source it could not
    /// reach as a <c>W:</c> line and still exits zero, having fallen back to the index it already
    /// had — so a classification taken from the exit code alone would call that run clean and then
    /// have no way to explain the <c>Unable to locate package</c> that follows it. Reading the
    /// output means a zero exit and an unreachable archive can both be true at once, which on this
    /// command they routinely are.
    /// </para>
    /// <para>
    /// Order matters. The lock is checked first because it can be the only thing apt says; the
    /// archive is checked before the package, because an empty index makes a genuinely present
    /// package report as missing and never the other way round.
    /// </para>
    /// </remarks>
    public static AptFailure Classify(string? output)
    {
        var text = output ?? string.Empty;

        if (Matches(text, LockSignatures))
        {
            return AptFailure.Locked;
        }

        if (Matches(text, UnreachableSignatures))
        {
            return AptFailure.ArchiveUnreachable;
        }

        return Matches(text, MissingSignatures) ? AptFailure.NotInArchive : AptFailure.None;
    }

    /// <summary>The failure in one technical phrase, for the action text.</summary>
    public static string Explain(AptFailure failure) => failure switch
    {
        AptFailure.ArchiveUnreachable => "the package archive could not be reached",
        AptFailure.NotInArchive => "the archive answered and does not offer this package",
        AptFailure.Locked => "another program is holding the package system's lock",
        AptFailure.Other => "apt-get refused",
        _ => "it worked",
    };

    /// <summary>The failure in one sentence for the person in front of the frame (§2.7).</summary>
    public static string PlainLanguage(AptFailure failure) => failure switch
    {
        AptFailure.ArchiveUnreachable =>
            "This frame could not reach the place software is downloaded from, so nothing could be installed. It will try again shortly.",
        AptFailure.NotInArchive =>
            "The software this frame asked for does not exist where it was looking, which is a fault in the frame's own instructions rather than in your network.",
        AptFailure.Locked =>
            "Another update was already running on this frame, so this one had to stand aside. It will try again shortly.",
        AptFailure.Other =>
            "Installing it did not work, and the exact reason is recorded above.",
        _ => string.Empty,
    };

    /// <summary>
    /// apt's output cut down to the part worth reading.
    /// </summary>
    /// <remarks>
    /// An <c>apt-get install</c> can print hundreds of lines, and every one of them would land in
    /// the reconcile journal, on the frame's screen and in the Fleet Manager's device row. The
    /// <c>E:</c> lines are apt's own errors and are what an operator needs; <c>W:</c> lines are
    /// the next best thing and are the <i>only</i> thing a failed <c>apt-get update</c> produces,
    /// since it exits zero after falling back to the index it had. Everything else is progress,
    /// and when there is nothing else the last line that said anything is the best available.
    /// </remarks>
    public static string Summarise(string? output)
    {
        const int Limit = 240;

        var text = (output ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();
        var last = string.Empty;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            last = line;
            if (line.StartsWith("E:", StringComparison.Ordinal))
            {
                errors.Add(line);
            }
            else if (line.StartsWith("W:", StringComparison.Ordinal))
            {
                warnings.Add(line);
            }
        }

        var chosen = errors.Count > 0 ? errors : warnings.Count > 0 ? warnings : [last];
        var summary = string.Join(" · ", chosen);
        return summary.Length <= Limit ? summary : summary[..Limit] + "…";
    }

    private async Task<AptOutcome> RunAsync(
        IReadOnlyList<string> aptArguments,
        CancellationToken cancellationToken)
    {
        string[] arguments = [NoninteractiveFrontend, AptGet, .. aptArguments];
        var command = Env + " " + string.Join(' ', arguments);

        var result = await _processes.RunAsync(Env, arguments, cancellationToken).ConfigureAwait(false);
        var detail = Summarise(result.Combined);
        var diagnosis = Classify(result.Combined);

        // Succeeded and Failure are two different questions here. A zero exit with an unreachable
        // archive is `apt-get update` falling back to the index it already had, and the diagnosis
        // has to survive that so the install after it can be attributed correctly. A non-zero exit
        // with nothing recognisable in the text is still a failure, and gets the catch-all.
        return result.Succeeded
            ? new AptOutcome(true, diagnosis, command, detail)
            : new AptOutcome(
                false,
                diagnosis is AptFailure.None ? AptFailure.Other : diagnosis,
                command,
                detail);
    }

    private static bool Matches(string text, string[] signatures)
    {
        foreach (var signature in signatures)
        {
            if (text.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FirstLine(string? output)
    {
        foreach (var raw in (output ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }
}
