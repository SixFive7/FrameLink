using System.Net;
using FrameLink.Control.Agent;
using FrameLink.Control.Alerting;
using FrameLink.Control.Endpoints;
using FrameLink.Control.Authentication;
using FrameLink.Control.Imaging;
using FrameLink.Control.LiveKit;
using FrameLink.Control.Storage;
using FrameLink.Control.Updates;
using FrameLink.Protocol;
using Microsoft.AspNetCore.HttpOverrides;

namespace FrameLink.Control;

/// <summary>
/// Composition root for <c>fl-control</c>.
/// </summary>
/// <remarks>
/// Separated from <c>Program</c> so that a test can stand the real server up on an ephemeral
/// port with its own database and clock. §7.2 asks for tests that assert outcomes; the
/// outcomes that matter most here — what a pending device is answered, what it receives
/// afterwards — are properties of the whole pipeline, not of a method.
/// </remarks>
public static class ControlApp
{
    /// <summary>Builds the configured server without starting it.</summary>
    /// <param name="args">Command-line arguments; <c>--urls</c> is honoured.</param>
    /// <param name="options">Paths and budgets. Read from the environment when omitted.</param>
    /// <param name="credential">The operator password. Read from the environment when omitted.</param>
    /// <param name="clock">Time source. The system clock when omitted.</param>
    /// <param name="livekit">The call server's configuration (§3.7). From the environment when omitted.</param>
    /// <param name="download">
    /// Where the pinned LiveKit release is fetched from. Real HTTPS when omitted; a test supplies
    /// its own so that the suite never reaches GitHub and never writes fifty megabytes.
    /// </param>
    /// <param name="alerts">§3.5's alerting thresholds and channel. From the environment when omitted.</param>
    /// <param name="sink">
    /// Where notifications are delivered. Chosen from <paramref name="alerts"/> when omitted; a
    /// test supplies its own so the suite never POSTs anywhere and can assert what was sent.
    /// </param>
    public static WebApplication Build(
        string[] args,
        ControlOptions? options = null,
        OperatorCredential? credential = null,
        TimeProvider? clock = null,
        LiveKitOptions? livekit = null,
        ILiveKitDownload? download = null,
        AlertOptions? alerts = null,
        IAlertSink? sink = null)
    {
        options ??= ControlOptions.FromEnvironment();
        credential ??= OperatorCredential.FromEnvironment();
        clock ??= TimeProvider.System;

        // The slim builder, per §3.1. Everything this server needs is registered explicitly
        // below, and nothing it does not need is dragged in by a convention.
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            // Both source-generated contexts, chained. Native AOT has no reflection-based
            // serialiser, so a type absent from both is a runtime failure — which is why the
            // trim and AOT analysers run at error severity in Directory.Build.props.
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, ControlJson.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(1, ProtocolJson.Default);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(credential);
        builder.Services.AddSingleton(clock);

        builder.Services.AddSingleton(sp => new SqliteDatabase(
            sp.GetRequiredService<ControlOptions>().DatabasePath));

        // The repository seam of §3.1: everything above this line asks for the interface, so
        // a Postgres implementation later is a registration change and nothing else.
        builder.Services.AddSingleton<IDeviceStore, SqliteDeviceStore>();
        builder.Services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        builder.Services.AddSingleton<IFleetTelemetryStore, SqliteFleetTelemetryStore>();
        builder.Services.AddSingleton<IPackageStore, SqlitePackageStore>();

        builder.Services.AddSingleton<OperatorSessions>();
        builder.Services.AddSingleton<FleetEvents>();
        builder.Services.AddSingleton<AgentConnectionRegistry>();
        builder.Services.AddSingleton<RegistrationRateLimiter>();
        builder.Services.AddSingleton<AgentReleaseCatalog>();

        // §3.9. The tool runner and the storage probe are the two places this capability touches
        // the machine rather than a file it owns, so both are interfaces — which is what lets the
        // suite drive a full disk and a refusing debugfs on a workstation that has neither.
        builder.Services.AddSingleton<IImageToolRunner, ProcessImageToolRunner>();
        builder.Services.AddSingleton<IStorageProbe, DriveStorageProbe>();
        builder.Services.AddSingleton<ImageBuilder>();
        builder.Services.AddSingleton<ImageBuildService>();

        builder.Services.AddSingleton<SettingsPublisher>();
        builder.Services.AddSingleton<RetryPublisher>();
        builder.Services.AddSingleton<ShutdownPublisher>();
        builder.Services.AddSingleton<DeviceHandshake>();
        builder.Services.AddSingleton<TelemetryIngest>();
        builder.Services.AddSingleton<AgentSocketHandler>();
        builder.Services.AddHostedService<PendingDeviceReaper>();

        // §3.7. Registered in dependency order rather than alphabetically, because the order is
        // the argument: the store owns the secret, the deployment decides whether that secret or
        // an operator's own is in force, the provisioner turns it into per-device tokens, and
        // only the last of the four is the thing that fetches and supervises a child process.
        // Everything except that last one works identically on a workstation with no LiveKit
        // anywhere, which is what lets adoption be tested without one.
        builder.Services.AddSingleton(livekit ?? LiveKitOptions.FromEnvironment(options.DataDirectory));
        builder.Services.AddSingleton<ILiveKitStore, SqliteLiveKitStore>();
        builder.Services.AddSingleton<LiveKitDeployment>();
        builder.Services.AddSingleton<CallProvisioning>();

        if (download is null)
        {
            builder.Services.AddSingleton<ILiveKitDownload, HttpLiveKitDownload>();
        }
        else
        {
            // A supplied download is the caller's, so it is registered as an instance — the
            // container disposes what it constructs and leaves alone what it is handed, which is
            // the behaviour a test wants for a double it may assert against afterwards.
            builder.Services.AddSingleton(download);
        }

        builder.Services.AddSingleton<LiveKitService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveKitService>());

        // §3.5's alerting. The sink is chosen once, here, rather than by a branch inside the
        // sweep: a Fleet Manager with no webhook is not a broken one, it is one whose delivery
        // channel is the container log, and expressing that as a different implementation keeps
        // FleetWatch with exactly one code path to test.
        builder.Services.AddSingleton(alerts ?? AlertOptions.FromEnvironment());
        builder.Services.AddSingleton<IAlertStore, SqliteAlertStore>();

        if (sink is not null)
        {
            builder.Services.AddSingleton(sink);
        }
        else
        {
            builder.Services.AddSingleton<IAlertSink>(sp =>
                sp.GetRequiredService<AlertOptions>().HasWebhook
                    ? ActivatorUtilities.CreateInstance<WebhookAlertSink>(sp)
                    : LogOnlyAlertSink.Instance);
        }

        builder.Services.AddSingleton<FleetWatch>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetWatch>());

        if (options.TrustedProxies.Count > 0)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var proxy in options.TrustedProxies)
                {
                    if (IPAddress.TryParse(proxy, out var address))
                    {
                        forwarded.KnownProxies.Add(address);
                    }
                }
            });
        }

        var app = builder.Build();

        if (options.TrustedProxies.Count > 0)
        {
            // Only when a proxy is named. Believing X-Forwarded-For unconditionally would let
            // one attacker present a fresh source address per request and walk straight
            // through the per-IP budget that §3.3 makes mandatory.
            app.UseForwardedHeaders();
        }

        app.UseWebSockets(new WebSocketOptions
        {
            // Transport-level keepalive as defence in depth. The mechanism that is actually
            // relied on is the application-level ping/pong in AgentConnection, because that is
            // the one with a deadline and the one the tests can drive.
            KeepAliveInterval = options.PingInterval,
        });

        // §6.2: UseStaticFiles, never MapStaticAssets. Under the slim builder the latter
        // silently serves empty 200s (aspnetcore#58986, still open) — a failure that looks
        // like a working server and costs a day to find.
        var webRoot = app.Environment.WebRootPath;
        if (!string.IsNullOrEmpty(webRoot) && Directory.Exists(webRoot))
        {
            app.UseStaticFiles();
        }

        app.UseOperatorGate();

        app.MapAgentEndpoints();
        app.MapOperatorEndpoints();
        app.MapImageEndpoints();
        app.MapGuiEndpoints();

        return app;
    }
}
