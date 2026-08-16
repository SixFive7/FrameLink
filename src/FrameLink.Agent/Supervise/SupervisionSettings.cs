using System.Globalization;
using FrameLink.Agent.Resources;

namespace FrameLink.Agent.Supervise;

/// <summary>
/// §2.10's constants, as fleet settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings rather than resources, and §2.10 says why: "they have no independent on-device
/// drift surface — nothing on disk holds them, the agent holds them in memory."</b> In v1 they
/// were baked into <c>~/chromium-watchdog.sh</c> and two systemd timers, which is precisely what
/// made retuning one of them a hand edit on a frame. Here a threshold can be moved across the
/// fleet, or on one struggling frame, without a release.
/// </para>
/// <para>
/// <b>Every default is evidence, not preference</b>, and the two memory numbers are the ones worth
/// restating because both look wrong at first glance. <c>1843200</c> kB (1.8 GB) is deliberately
/// <i>high</i>: after hours of slideshow a healthy Chromium tree legitimately reaches ~1.7 GB of
/// iframe image cache, released the instant the iframe unloads, and a full six-way call runs a
/// lean ~1.3 GB — so any lower ceiling restarts healthy frames, while the measured pathologies
/// cross it quickly anyway (a dead slideshow iframe leaked 50 MB/min, an expired token's
/// connect-reject-retry loop 15 MB/min). <c>358400</c> kB (350 MB) is the sharper instrument:
/// system-wide multi-second stalls began once free memory fell into the low hundreds of megabytes
/// whatever was consuming it, and the browser is always this machine's largest tenant.
/// </para>
/// </remarks>
public sealed class SupervisionSettings
{
    /// <summary>Fleet setting: Chromium tree RSS ceiling, in kB.</summary>
    public const string BrowserTreeRssCeilingKbKey = "supervision.browserTreeRssCeilingKb";

    /// <summary>Fleet setting: system available-memory floor, in kB.</summary>
    public const string MemAvailableFloorKbKey = "supervision.memAvailableFloorKb";

    /// <summary>Fleet setting: sampling interval for both memory limits.</summary>
    public const string MemoryCheckIntervalKey = "supervision.memoryCheckInterval";

    /// <summary>Fleet setting: scheduled browser restart; empty disables it.</summary>
    public const string DailyRestartTimeKey = "supervision.dailyRestartTime";

    /// <summary>Fleet setting: local-channel silence that triggers a restart.</summary>
    public const string KioskSilenceTimeoutKey = "supervision.kioskSilenceTimeout";

    /// <summary>Fleet setting: how often that silence is evaluated.</summary>
    public const string KioskCheckIntervalKey = "supervision.kioskCheckInterval";

    /// <summary>Fleet setting: minimum spacing between liveness restarts.</summary>
    public const string KioskRestartCooldownKey = "supervision.kioskRestartCooldown";

    /// <summary>Fleet setting: per-call camera recycle; off at PipeWire ≥ 1.6.</summary>
    public const string CameraRestartOnCallEndKey = "supervision.cameraRestartOnCallEnd";

    /// <summary>Fleet setting: minimum spacing between page refreshes.</summary>
    public const string PageRefreshCooldownKey = "supervision.pageRefreshCooldown";

    /// <summary>Fleet setting: when an unrecovered supervision action becomes drift.</summary>
    public const string RecoveryDeadlineKey = "supervision.recoveryDeadline";

    /// <summary>Fleet setting: actions of one behaviour that raise a fault.</summary>
    public const string FaultRateThresholdKey = "supervision.faultRateThreshold";

    /// <summary>Fleet setting: the window that count is taken over.</summary>
    public const string FaultRateWindowKey = "supervision.faultRateWindow";

    private readonly FleetValues _values;

    /// <summary>Creates a view over <paramref name="values"/>.</summary>
    public SupervisionSettings(FleetValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    /// <summary>A view over nothing, so every default applies.</summary>
    public static SupervisionSettings Defaults { get; } = new(FleetValues.None);

    /// <summary>1.8 GB — the healthy tree's ceiling, not its typical size.</summary>
    public long BrowserTreeRssCeilingKb => Number(BrowserTreeRssCeilingKbKey, 1_843_200);

    /// <summary>350 MB — where system-wide stalls began, whatever was consuming the memory.</summary>
    public long MemAvailableFloorKb => Number(MemAvailableFloorKbKey, 358_400);

    /// <summary>Five minutes, as v1's <c>chromium-watchdog.timer</c> ran it.</summary>
    public TimeSpan MemoryCheckInterval => Duration(MemoryCheckIntervalKey, TimeSpan.FromMinutes(5));

    /// <summary>03:00 local, or null when the operator has emptied the setting.</summary>
    public TimeOnly? DailyRestartTime => Time(DailyRestartTimeKey, new TimeOnly(3, 0));

    /// <summary>90 s — validated live: a SIGKILLed renderer healed in exactly this.</summary>
    public TimeSpan KioskSilenceTimeout => Duration(KioskSilenceTimeoutKey, TimeSpan.FromSeconds(90));

    /// <summary>15 s, so a 90 s silence is noticed within one interval of becoming true.</summary>
    public TimeSpan KioskCheckInterval => Duration(KioskCheckIntervalKey, TimeSpan.FromSeconds(15));

    /// <summary>Five minutes between liveness restarts, so a page that will not load cannot spin.</summary>
    public TimeSpan KioskRestartCooldown => Duration(KioskRestartCooldownKey, TimeSpan.FromMinutes(5));

    /// <summary>Whether the camera node is recycled after every call.</summary>
    public bool CameraRestartOnCallEnd => Flag(CameraRestartOnCallEndKey, fallback: true);

    /// <summary>
    /// Five minutes between page refreshes, matching the liveness restart's floor.
    /// </summary>
    /// <remarks>
    /// A refresh that does not take — a page that ignores the command, or one that cannot fetch the
    /// document it is being sent for — must not become a frame reloading itself every fifteen
    /// seconds. The same floor as <see cref="KioskRestartCooldown"/> because it answers the same
    /// question about the same page, and because it is the spacing at which
    /// <see cref="FaultRateThreshold"/> turns a repeat into a reported fault within the hour.
    /// </remarks>
    public TimeSpan PageRefreshCooldown => Duration(PageRefreshCooldownKey, TimeSpan.FromMinutes(5));

    /// <summary>Two minutes, after which an unrecovered action becomes ordinary drift (§2.10).</summary>
    public TimeSpan RecoveryDeadline => Duration(RecoveryDeadlineKey, TimeSpan.FromMinutes(2));

    /// <summary>More than three actions of one behaviour in the window raises a fault.</summary>
    public int FaultRateThreshold => (int)Number(FaultRateThresholdKey, 3);

    /// <summary>One hour.</summary>
    public TimeSpan FaultRateWindow => Duration(FaultRateWindowKey, TimeSpan.FromHours(1));

    /// <summary>
    /// Parses <c>90s</c>, <c>5m</c>, <c>1h</c> or a bare number of seconds.
    /// </summary>
    /// <remarks>
    /// The suffixed forms are what §2.10's own table writes, so an operator typing what the
    /// specification says is the case that has to work. Anything unparseable falls through to the
    /// default rather than to zero — a mistyped interval must never become "sample continuously"
    /// or "consider the page dead immediately".
    /// </remarks>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var unit = text[^1];
        var digits = char.IsAsciiDigit(unit) ? text : text[..^1];

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount < 0)
        {
            return null;
        }

        return unit switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            _ when char.IsAsciiDigit(unit) => TimeSpan.FromSeconds(amount),
            _ => null,
        };
    }

    /// <summary>Parses <c>HH:mm</c>; an empty value means the schedule is switched off.</summary>
    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;

    private long Number(string key, long fallback) =>
        long.TryParse(_values.Find(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private TimeSpan Duration(string key, TimeSpan fallback) => ParseDuration(_values.Find(key)) ?? fallback;

    private TimeOnly? Time(string key, TimeOnly fallback)
    {
        var configured = _values.Find(key);

        // An *explicitly empty* setting disables the schedule; an absent one takes the default.
        // FleetValues.Find collapses both to null, so "the operator switched it off" is expressed
        // as a value that parses to nothing rather than as an absence — "off" is spelled `off`.
        return configured is null ? fallback
            : string.Equals(configured, "off", StringComparison.OrdinalIgnoreCase) ? null
            : ParseTime(configured) ?? fallback;
    }

    private bool Flag(string key, bool fallback) =>
        bool.TryParse(_values.Find(key), out var parsed) ? parsed : fallback;
}
