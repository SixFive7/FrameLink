using FrameLink.Agent.Reconcile;

namespace FrameLink.Agent.Resources;

/// <summary>
/// One entry of the catalog's package block: an apt name and the words that describe it to
/// somebody standing in front of the frame.
/// </summary>
/// <remarks>
/// The logic is uniform across every package, which is the point — §2.2 says "one apt package"
/// is a canonical resource and one package is one resource — so what varies is the name and the
/// sentences. §2.7 requires the repair screen to say what was detected and why it matters in
/// plain language, and only the catalog knows what a given package is <i>for</i>, so those
/// sentences live here beside the name rather than being synthesised from it.
/// </remarks>
public sealed record AptPackageSpec
{
    /// <summary>The apt package name.</summary>
    public required string Package { get; init; }

    /// <summary>What was detected, for a reader with no computer experience (§2.7 item 1).</summary>
    public required string Detected { get; init; }

    /// <summary>Why it matters, in one short sentence (§2.7 item 2).</summary>
    public required string WhyItMatters { get; init; }

    /// <summary>Plain-language gloss on the change being made (§2.7 item 3).</summary>
    public required string Gloss { get; init; }

    /// <summary>Whether the desired state is the package's <i>absence</i>.</summary>
    public bool MustBeAbsent { get; init; }

    /// <summary>The catalog id this spec produces.</summary>
    public string ResourceName => MustBeAbsent
        ? PackageResource.Prefix + Package + PackageResource.AbsentSuffix
        : PackageResource.Prefix + Package;
}

/// <summary>
/// <c>pkg.&lt;name&gt;</c> and <c>pkg.&lt;name&gt;.absent</c> — one apt package, present or gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observe is <c>dpkg-query</c>, and it distinguishes three states a boolean cannot.</b> The
/// catalog spells the command out, and the reason for that spelling is the middle state:
/// <c>dpkg -l</c> prints an <c>rc</c> line for a package that was removed but not purged, so
/// anything that decides "installed" by finding the name in a list is wrong about it. The status
/// field <c>${db:Status-Status}</c> is the field that answers the question, and only the literal
/// value <c>installed</c> counts.
/// </para>
/// <para>
/// <b>Observe never touches the network, and that is load-bearing.</b> It reads dpkg's database,
/// so it is valid on a freshly booted frame, on a frame whose internet is down, and on a frame
/// that has not run apt in months — and, crucially, it is not a side effect of having just run
/// the install. The Act is where the network is needed, so an unreachable archive is a failed
/// <i>apply</i>, not an unmakeable observation. See <see cref="AptFailure"/> for why that is not
/// <see cref="ObservationOutcome.Unevaluable"/>.
/// </para>
/// <para>
/// <b>Versions float (§7.1).</b> The observed value carries the installed version because it is
/// free and a delta that names it is worth more, but nothing compares it. The catalog says the
/// same thing in its own words: it "pins the presence, not the version".
/// </para>
/// <para>
/// <b>The transitive set is apt's problem, not the catalog's.</b> Guide 5's five packages pull in
/// roughly 215 dependencies; none of them is enumerated here and none should be. A resource that
/// asserted the closure would report drift every time Debian re-cut a dependency.
/// </para>
/// </remarks>
public sealed class PackageResource : IResource
{
    /// <summary>The catalog's id prefix for every package resource.</summary>
    public const string Prefix = "pkg.";

    /// <summary>The suffix that marks a package whose desired state is absence.</summary>
    public const string AbsentSuffix = ".absent";

    private readonly AptPackages _apt;
    private readonly AptPackageSpec _spec;

    /// <summary>Creates the resource for <paramref name="spec"/>.</summary>
    public PackageResource(AptPackages apt, AptPackageSpec spec)
    {
        ArgumentNullException.ThrowIfNull(apt);
        ArgumentNullException.ThrowIfNull(spec);

        _apt = apt;
        _spec = spec;
    }

    /// <inheritdoc/>
    public string Name => _spec.ResourceName;

    /// <inheritdoc/>
    public string Detected => _spec.Detected;

    /// <inheritdoc/>
    public string WhyItMatters => _spec.WhyItMatters;

    /// <summary>The apt package this resource owns.</summary>
    public string Package => _spec.Package;

    /// <summary>Whether the desired state is absence rather than presence.</summary>
    public bool MustBeAbsent => _spec.MustBeAbsent;

    /// <inheritdoc/>
    public async ValueTask<ResourceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var status = await _apt.QueryAsync(_spec.Package, cancellationToken).ConfigureAwait(false);

        // Absence is satisfied by `rc` as well as by `not-installed`, because what this resource
        // is about is the package's files being gone — and `rc` means exactly that, with only
        // configuration left behind. The raw state still travels in the observed value, so a
        // frame sitting in `rc` is visible in telemetry rather than quietly equated with a clean
        // one. The Act purges, so the agent's own path never produces that state anyway.
        var inSync = _spec.MustBeAbsent ? !status.IsPresent : status.IsInstalled;

        return new ResourceObservation(
            inSync,
            $"{_spec.Package} {(_spec.MustBeAbsent ? "not installed" : "installed")}",
            $"{_spec.Package} {status.Describe()}");
    }

    /// <inheritdoc/>
    public async ValueTask<ResourceAction> ActAsync(CancellationToken cancellationToken)
    {
        var outcome = _spec.MustBeAbsent
            ? await _apt.PurgeAsync(_spec.Package, cancellationToken).ConfigureAwait(false)
            : await _apt.InstallAsync(_spec.Package, cancellationToken).ConfigureAwait(false);

        if (outcome.Succeeded)
        {
            return new ResourceAction(outcome.Command, _spec.Gloss);
        }

        // A refused Act still reports what it tried and why it failed, in both registers. §2.5
        // needs the delta to reach a person, and "labwc is not installed" is a true and useless
        // sentence when the cause is that the archive was unreachable.
        return new ResourceAction(
            $"{outcome.Command} — {AptPackages.Explain(outcome.Failure)}: {outcome.Detail}",
            $"{_spec.Gloss} {AptPackages.PlainLanguage(outcome.Failure)}");
    }
}

/// <summary>
/// The catalog's package block, in the order the catalog's own dependency table gives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fifteen resources, positions 6–22 of the proposed ordering, minus two.</b> That table lists
/// seventeen. <c>pkg.git</c> is dropped because open question 3's adopted reading obtains
/// <c>xvf_host</c> as a pinned, checksum-verified upstream artifact rather than a clone — the
/// catalog says outright that "if it does not, this resource disappears" — and guide 10's other
/// use of git went with the embedded app. <c>tool.xvf-host.installed</c> is in that phase but is
/// not an apt package at all: it is the Immich Kiosk shape, a pinned fetch with a checksum set and
/// a fixed working directory, and it belongs with the audio block it serves.
/// </para>
/// <para>
/// <b>Nothing here declares a <c>dependsOn</c>, because the catalog gives every one of them
/// "—".</b> They need no fleet value, no session, no unit and no other resource; apt resolves the
/// transitive set itself. In particular they do <i>not</i> depend on adoption: §3.3 withholds
/// <i>configuration</i> from a pending device, and a package set the catalog fixes is not
/// configuration. A pending frame installing its kiosk stack is also what §2.7 asks for, since
/// the browser stage cannot render the repair screen until the stack is there.
/// </para>
/// <para>
/// <b>There is no resource for the apt index, and one has not been added.</b> Several of these
/// need a refreshed package list to install, and the catalog names nothing to depend on for it —
/// so the refresh lives inside <see cref="AptPackages.InstallAsync"/> as part of applying, which
/// is where an operation that is not independently verifiable belongs.
/// </para>
/// <para>
/// <b>Fifteen resources is fifteen reboots</b> — §2.4 has no exceptions and decision 26 is
/// explicit that deciding which of these "really" needs one is the reasoning that produced v1's
/// governor bug. At the 22.3 s measured on this hardware that is about five and a half minutes,
/// before download time.
/// </para>
/// </remarks>
public static class PackageCatalog
{
    /// <summary>The block, in catalog order.</summary>
    public static IReadOnlyList<AptPackageSpec> Specs { get; } =
    [
        // Guide 5 step 2 — the kiosk stack.
        new AptPackageSpec
        {
            Package = "labwc",
            Detected = "The program that arranges what appears on this frame's screen is missing.",
            WhyItMatters = "Nothing at all can be shown on the screen until it is there.",
            Gloss = "Installing the program that puts things on this frame's screen.",
        },
        new AptPackageSpec
        {
            Package = "chromium",
            Detected = "The web browser this frame shows everything through is missing.",
            WhyItMatters = "The photos and the video call are both web pages, so without it there is nothing to show.",
            Gloss = "Installing the browser this frame shows the photos and the video call in.",
        },
        new AptPackageSpec
        {
            Package = "wireplumber",
            Detected = "The part of the sound system that decides where sound goes is missing.",
            WhyItMatters = "Without it this frame has no working speaker and no working microphone.",
            Gloss = "Installing the part of the sound system that routes sound to the speaker and the microphone.",
        },
        new AptPackageSpec
        {
            Package = "pipewire-alsa",
            Detected = "The link between the sound system and this frame's speaker and microphone is missing.",
            WhyItMatters = "Without it the sound system cannot reach the hardware, so a call is silent.",
            Gloss = "Installing the link that lets the sound system reach this frame's speaker and microphone.",
        },
        new AptPackageSpec
        {
            Package = "wlr-randr",
            Detected = "The tool that turns the picture the right way round is missing.",
            WhyItMatters = "Without it the screen stays sideways.",
            Gloss = "Installing the tool that turns this frame's picture the right way round.",
        },

        // Guide 6 step 1 — the camera chain.
        new AptPackageSpec
        {
            Package = "xdg-desktop-portal",
            Detected = "The part that lets the browser ask for the camera is missing.",
            WhyItMatters = "Without it the browser can never find this frame's camera.",
            Gloss = "Installing the part the browser asks for the camera through.",
        },
        new AptPackageSpec
        {
            Package = "xdg-desktop-portal-gtk",
            Detected = "The half of the camera permission system that answers the request is missing.",
            WhyItMatters = "Without it the browser's request for the camera is never answered and the picture stays black.",
            Gloss = "Installing the half of the camera permission system that answers the browser.",
        },
        new AptPackageSpec
        {
            Package = "gstreamer1.0-tools",
            Detected = "The program that runs this frame's camera is missing.",
            WhyItMatters = "Without it the camera never starts.",
            Gloss = "Installing the program that runs this frame's camera.",
        },
        new AptPackageSpec
        {
            Package = "gstreamer1.0-plugins-base",
            Detected = "The basic video building blocks the camera needs are missing.",
            WhyItMatters = "Without them the camera cannot produce a picture the rest of the frame understands.",
            Gloss = "Installing the basic video building blocks the camera is assembled from.",
        },
        new AptPackageSpec
        {
            Package = "gstreamer1.0-libcamera",
            Detected = "The part that reads pictures out of this frame's camera is missing.",
            WhyItMatters = "Without it nothing can get a picture from the camera at all.",
            Gloss = "Installing the part that reads pictures out of this frame's camera.",
        },
        new AptPackageSpec
        {
            Package = "gstreamer1.0-pipewire",
            Detected = "The part that hands the camera's picture to the rest of the frame is missing.",
            WhyItMatters = "Without it the camera runs and nothing can see what it produces.",
            Gloss = "Installing the part that hands the camera's picture to the rest of this frame.",
        },
        new AptPackageSpec
        {
            // Guide 6 step 1's "just as important is what is not installed". Absence is a real,
            // independently verifiable state with a real fix, which is what makes it a resource
            // rather than a note: measured on this hardware, the node this plugin creates is
            // capped near 30 fps, advertises no framerates, and Chromium cannot acquire it above
            // 720p. It is listed here rather than first because a package installed later can drag
            // it back in, and declaration order puts it after everything in this block that might.
            Package = "libspa-0.2-libcamera",
            MustBeAbsent = true,
            Detected = "An old camera part is installed that takes the camera over and holds it back.",
            WhyItMatters = "While it is there the picture this frame sends in a call is stuck at a low quality.",
            Gloss = "Removing the old camera part so that this frame's own camera is the only one.",
        },

        // Guide 4 step 3 — the firmware flasher for the microphone and speaker unit.
        new AptPackageSpec
        {
            Package = "dfu-util",
            Detected = "The tool that updates the microphone-and-speaker unit is missing.",
            WhyItMatters = "Without it that unit cannot be brought to the version this frame is built around.",
            Gloss = "Installing the tool that updates the microphone-and-speaker unit's own software.",
        },

        // Guide 8 step 7, promoted to required by §3.6's diagnostics allowlist.
        new AptPackageSpec
        {
            Package = "grim",
            Detected = "The tool that takes a picture of this frame's screen is missing.",
            WhyItMatters = "Without it nobody can see from elsewhere what this frame is showing.",
            Gloss = "Installing the tool that takes a picture of this frame's screen for you to look at remotely.",
        },

        // Guide 12 step 6. Absent from the v1 reference — see open question 9.
        new AptPackageSpec
        {
            Package = "unattended-upgrades",
            Detected = "The service that installs security fixes on its own is missing.",
            WhyItMatters = "Without it this frame stops receiving security fixes.",
            Gloss = "Installing the service that fetches security fixes for this frame by itself.",
        },
    ];

    /// <summary>Builds the block's resources, in catalog order.</summary>
    public static IReadOnlyList<IResource> Build(AptPackages apt)
    {
        ArgumentNullException.ThrowIfNull(apt);

        var resources = new List<IResource>(Specs.Count);
        foreach (var spec in Specs)
        {
            resources.Add(new PackageResource(apt, spec));
        }

        return resources;
    }
}
