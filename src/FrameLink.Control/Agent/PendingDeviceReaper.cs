using FrameLink.Control.Storage;

namespace FrameLink.Control.Agent;

/// <summary>
/// Auto-expiry of un-adopted rows, and housekeeping for the rate limiter (§3.3).
/// </summary>
/// <remarks>
/// The third of the four mandatory abuse controls, after per-address rate limiting and the
/// pending cap. It exists so that noise created by an attacker has a lifetime: a row nobody
/// adopts and nothing reconnects to is gone within
/// <see cref="ControlOptions.PendingDeviceTtl"/>. A frame that is genuinely running refreshes
/// its own timestamp on every reconnect, so it is never a candidate.
/// </remarks>
public sealed class PendingDeviceReaper(
    IDeviceStore devices,
    IFleetTelemetryStore telemetry,
    RegistrationRateLimiter limiter,
    ControlOptions options,
    TimeProvider clock,
    ILogger<PendingDeviceReaper> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReaperInterval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Runs one sweep. Public so a test can drive it without waiting an hour.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.GetUtcNow();
            var expired = await devices
                .ExpirePendingAsync(now - options.PendingDeviceTtl, cancellationToken)
                .ConfigureAwait(false);

            limiter.Sweep(now);

            if (expired > 0)
            {
                logger.ExpiredPendingDevices(expired);
            }

            // §3.5: one month of events and reconciliation history, then rolled off. On the same
            // timer as the pending sweep because both are the same job — keeping a single-volume
            // SQLite file from growing without an upper bound on a server nobody watches.
            var rolled = await telemetry
                .ExpireEventsAsync(now - options.TelemetryRetention, cancellationToken)
                .ConfigureAwait(false);

            if (rolled > 0)
            {
                logger.ExpiredDeviceEvents(rolled);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Housekeeping failing must never take the server down with it; the next tick
            // tries again and the cap keeps the table bounded in the meantime.
            logger.SweepFailed(exception);
        }
    }
}
