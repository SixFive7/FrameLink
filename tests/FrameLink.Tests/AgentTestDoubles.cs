using System.Collections.Concurrent;
using System.Text;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Hosting;
using FrameLink.Agent.Link;
using FrameLink.Agent.State;
using FrameLink.Agent.Supervise;
using FrameLink.Agent.Update;
using FrameLink.Protocol;

namespace FrameLink.Tests;

/// <summary>
/// Test doubles shared by the agent suite.
/// </summary>
/// <remarks>
/// The agent's Linux-only surfaces — the console device, the virtual terminals behind it,
/// <c>/var/lib</c>, systemd, the socket — are each behind one interface, so this file is what lets
/// the whole of M1 be asserted on Windows without a single OS check leaking into the code under
/// test.
/// </remarks>
internal sealed class RecordingLog : IAgentLog
{
    public List<string> Lines { get; } = [];

    public string Transcript => string.Join('\n', Lines);

    public void Write(AgentLogLevel level, string message) => Lines.Add($"{level}: {message}");
}

/// <summary>Records every mode application so "root-only" is an assertable outcome.</summary>
internal sealed class RecordingPermissions : IFilePermissions
{
    public List<(string Path, UnixFileMode Mode)> Applied { get; } = [];

    public UnixFileMode? ModeOf(string path)
    {
        for (var index = Applied.Count - 1; index >= 0; index--)
        {
            if (string.Equals(Applied[index].Path, path, StringComparison.Ordinal))
            {
                return Applied[index].Mode;
            }
        }

        return null;
    }

    public void Restrict(string path, UnixFileMode mode) => Applied.Add((path, mode));
}

/// <summary>
/// A clock whose waits are instant and recorded.
/// </summary>
/// <remarks>
/// Every wait in the agent goes through <see cref="IAgentClock"/>, so a loop that would take
/// hours of wall-clock runs here in milliseconds while still being asserted on the schedule it
/// actually chose.
/// </remarks>
internal sealed class ManualClock : IAgentClock
{
    private readonly ConcurrentQueue<TaskCompletionSource> _held = new();

    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    /// <summary>When set, delays do not complete until <see cref="ReleaseOne"/> is called.</summary>
    public bool Hold { get; set; }

    /// <summary>Invoked after each delay is recorded, so a test can stop a forever-loop.</summary>
    public Action<ManualClock>? OnDelay { get; set; }

    public int HeldCount => _held.Count;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        UtcNow += delay;
        OnDelay?.Invoke(this);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        if (!Hold)
        {
            return Task.CompletedTask;
        }

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _held.Enqueue(pending);
        return pending.Task.WaitAsync(cancellationToken);
    }

    public bool ReleaseOne()
    {
        if (!_held.TryDequeue(out var pending))
        {
            return false;
        }

        pending.TrySetResult();
        return true;
    }
}

/// <summary>An in-memory boot partition and <c>/proc</c>.</summary>
internal sealed class MemoryTextFiles : ITextFileReader
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public List<string> Reads { get; } = [];

    public string? ReadAllTextOrNull(string path)
    {
        Reads.Add(path);
        return Files.TryGetValue(path, out var content) ? content : null;
    }
}

/// <summary>A real state directory under a temporary path, removed on dispose.</summary>
internal sealed class TemporaryStore : IDisposable
{
    public TemporaryStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "fl-agent-tests", Guid.NewGuid().ToString("N"));
        Permissions = new RecordingPermissions();
        Store = new FileStateStore(Root, Permissions);
    }

    public string Root { get; }

    public RecordingPermissions Permissions { get; }

    public FileStateStore Store { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

/// <summary>What a scripted server does when the agent connects.</summary>
public enum ServerBehaviour
{
    /// <summary>Connecting throws.</summary>
    RefuseConnection,

    /// <summary>Accept, then close before answering the hello.</summary>
    CloseImmediately,

    /// <summary>Accept, then send bytes that are not FrameLink traffic at all.</summary>
    SendGarbage,

    /// <summary>Accept, then answer with a message of the wrong kind.</summary>
    SendWrongKind,

    /// <summary>Accept, then throw mid-handshake.</summary>
    ThrowMidHandshake,

    /// <summary>Complete the handshake, then close the connection.</summary>
    CompleteHandshake,
}

/// <summary>One scripted connection.</summary>
internal sealed class TrackingTransport : IControlTransport
{
    private readonly TrackingTransportFactory _owner;
    private readonly Queue<byte[]> _inbound;
    private readonly ServerBehaviour _behaviour;
    private int _disposed;

    public TrackingTransport(TrackingTransportFactory owner, ServerBehaviour behaviour, IEnumerable<byte[]> inbound)
    {
        _owner = owner;
        _behaviour = behaviour;
        _inbound = new Queue<byte[]>(inbound);
    }

    public List<byte[]> Sent { get; } = [];

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ValueTask SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Sent.Add(utf8.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_behaviour == ServerBehaviour.ThrowMidHandshake)
        {
            throw new IOException("The connection was reset by the peer.");
        }

        // Written out rather than as a conditional expression on purpose. `count == 0 ? null :
        // Dequeue()` compiles, but the null goes through byte[] -> ReadOnlyMemory<byte>, which
        // turns it into a *present* empty buffer instead of the absent value that means "closed".
        if (_inbound.Count == 0)
        {
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(_inbound.Dequeue());
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.NoteDisposed();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A factory that counts what it hands out and what comes back.
/// </summary>
/// <remarks>
/// Deliberately keeps only <see cref="WeakReference"/>s to the transports it created. If it held
/// strong ones it would itself be the thing keeping them alive, and the weak-reference leak test
/// would prove nothing.
/// </remarks>
internal sealed class TrackingTransportFactory : IControlTransportFactory
{
    private int _created;
    private int _disposed;
    private int _live;
    private int _maximumLive;

    public ServerBehaviour Behaviour { get; set; } = ServerBehaviour.RefuseConnection;

    public Func<HandshakeResult>? Verdict { get; set; }

    public List<WeakReference> Tracked { get; } = [];

    public int Created => Volatile.Read(ref _created);

    public int Disposed => Volatile.Read(ref _disposed);

    public int Live => Volatile.Read(ref _live);

    public int MaximumConcurrentLive => Volatile.Read(ref _maximumLive);

    public ValueTask<IControlTransport> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var transport = new TrackingTransport(this, Behaviour, ScriptFor(Behaviour));

        Interlocked.Increment(ref _created);
        var live = Interlocked.Increment(ref _live);
        if (live > _maximumLive)
        {
            Volatile.Write(ref _maximumLive, live);
        }

        Tracked.Add(new WeakReference(transport));

        if (Behaviour == ServerBehaviour.RefuseConnection)
        {
            // The obligation stated on IControlTransportFactory: a factory that throws must have
            // released everything it allocated first.
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new IOException("Connection refused.");
        }

        return ValueTask.FromResult<IControlTransport>(transport);
    }

    public void NoteDisposed()
    {
        Interlocked.Increment(ref _disposed);
        Interlocked.Decrement(ref _live);
    }

    private byte[][] ScriptFor(ServerBehaviour behaviour) => behaviour switch
    {
        ServerBehaviour.CloseImmediately => [],
        ServerBehaviour.SendGarbage => [Encoding.UTF8.GetBytes("<html>captive portal</html>")],
        ServerBehaviour.SendWrongKind =>
        [
            WireMessage.Encode("shell", new HandshakeChallenge { Nonce = "x" }, ProtocolJson.Default.HandshakeChallenge),
        ],
        ServerBehaviour.CompleteHandshake => AgentServerScript.Handshake(Verdict?.Invoke() ?? AgentServerScript.Pending()),
        _ => [],
    };
}

/// <summary>Builds the byte sequences a Fleet Manager would send.</summary>
internal static class AgentServerScript
{
    public static byte[][] Handshake(HandshakeResult verdict) =>
    [
        WireMessage.Encode(
            WireMessage.KindChallenge,
            new HandshakeChallenge { Nonce = DeviceIdentity.NewNonce() },
            ProtocolJson.Default.HandshakeChallenge),
        WireMessage.Encode(WireMessage.KindResult, verdict, ProtocolJson.Default.HandshakeResult),
    ];

    public static HandshakeResult Pending() => new()
    {
        Status = HandshakeStatus.Pending,
        ProtocolVersion = ProtocolConstants.Version,
    };

    public static HandshakeResult Ok(string? deviceName = null, string? servedVersion = null) => new()
    {
        Status = HandshakeStatus.Ok,
        ProtocolVersion = ProtocolConstants.Version,
        DeviceName = deviceName,
        ServedAgentVersion = servedVersion,
    };

    /// <summary>§3.3's backpressure: "not this minute", said to a frame that is otherwise fine.</summary>
    public static HandshakeResult RateLimited() => new()
    {
        Status = HandshakeStatus.RateLimited,
        ProtocolVersion = ProtocolConstants.Version,
        Message = "This frame has reconnected too often in the last minute.",
    };
}

/// <summary>A terminal that keeps every frame it was asked to paint.</summary>
internal sealed class MemoryTerminal : ITerminal
{
    public MemoryTerminal(int columns = 80, int rows = 24, bool colour = false)
    {
        Columns = columns;
        Rows = rows;
        SupportsColour = colour;
    }

    public int Columns { get; }

    public int Rows { get; }

    public bool SizeIsKnown { get; init; } = true;

    public bool SupportsColour { get; }

    public List<string> Frames { get; } = [];

    public bool IsDisposed { get; private set; }

    public void Write(string text) => Frames.Add(text);

    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// Virtual terminals with no kernel behind them.
/// </summary>
/// <remarks>
/// The counterpart to <c>TtyTerminal.Over</c>, and it exists for the same reason: the boundary
/// between the two stages has to be assertable without a console, because the machine that runs the
/// suite does not have one and the machine that has one cannot run the suite. The two failure modes
/// are separate on purpose — <see cref="Accepts"/> is the kernel refusing the request, which is a
/// machine with no consoles at all, and <see cref="Completes"/> is the kernel taking the request
/// and the switch never happening, which is a compositor that will not let go.
/// </remarks>
internal sealed class RecordingVirtualTerminals : IVirtualTerminals
{
    public RecordingVirtualTerminals(int foreground = 1) => Active = foreground;

    /// <summary>Which terminal reads as in front.</summary>
    public int? Active { get; set; }

    /// <summary>Whether <c>VT_ACTIVATE</c> is accepted at all.</summary>
    public bool Accepts { get; set; } = true;

    /// <summary>Whether an accepted request actually changes the foreground terminal.</summary>
    public bool Completes { get; set; } = true;

    /// <summary>Every terminal that was asked for, in order.</summary>
    public List<int> Activated { get; } = [];

    /// <summary>Called before the request is answered, so a test can time it against other work.</summary>
    public Action<int>? OnActivate { get; set; }

    public int? Foreground() => Active;

    public bool Activate(int terminal)
    {
        Activated.Add(terminal);
        OnActivate?.Invoke(terminal);

        if (!Accepts)
        {
            return false;
        }

        if (Completes)
        {
            Active = terminal;
        }

        return true;
    }
}

/// <summary>Answers one exact command vector, and records every one it was asked.</summary>
internal sealed class ScriptedProcessRunner : IProcessRunner
{
    /// <summary>Starts with no compositor, which is what a frame looks like before it has one.</summary>
    public ScriptedProcessRunner() => CompositorRunning = false;

    public List<string> Commands { get; } = [];

    public Dictionary<string, ProcessResult> Answers { get; } = new(StringComparer.Ordinal);

    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty);

    /// <summary>Whether <c>pgrep -x labwc</c> finds a compositor.</summary>
    public bool CompositorRunning
    {
        get => Answers.TryGetValue(PgrepLabwc, out var answer) && answer.Succeeded;
        set => Answers[PgrepLabwc] = new ProcessResult(value ? 0 : 1, string.Empty, string.Empty);
    }

    private const string PgrepLabwc = "pgrep -x labwc";

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = executable + " " + string.Join(' ', arguments);
        Commands.Add(line);

        return Task.FromResult(Answers.TryGetValue(line, out var scripted) ? scripted : Default);
    }
}

/// <summary>A release feed with no server behind it.</summary>
internal sealed class StubReleaseSource : IReleaseSource
{
    public AgentRelease? Release { get; set; }

    public byte[]? Payload { get; set; }

    public bool DownloadFails { get; set; }

    public int ReleaseCalls { get; private set; }

    public int DownloadCalls { get; private set; }

    public Task<AgentRelease?> GetReleaseAsync(
        Uri endpoint,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        ReleaseCalls++;
        return Task.FromResult(Release);
    }

    public Task<Stream?> DownloadAsync(Uri endpoint, AgentRelease release, CancellationToken cancellationToken)
    {
        DownloadCalls++;
        return Task.FromResult<Stream?>(
            DownloadFails || Payload is null ? null : new MemoryStream(Payload, writable: false));
    }
}

/// <summary>Records restart requests instead of making them.</summary>
internal sealed class RecordingRestart : IRestartSignal
{
    public List<string> Requests { get; } = [];

    public void Request(string reason) => Requests.Add(reason);
}

/// <summary>An endpoint source with a scripted answer and a call counter.</summary>
internal sealed class StubEndpointSource : IEndpointSource
{
    public StubEndpointSource(string name, params string[] endpoints)
    {
        Name = name;
        Endpoints = [.. endpoints.Select(e => new Uri(e))];
    }

    public string Name { get; }

    public IReadOnlyList<Uri> Endpoints { get; set; }

    public int Calls { get; private set; }

    public Task<IReadOnlyList<Uri>> DiscoverAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Endpoints);
    }
}

/// <summary>A multicast transport that replays canned packets.</summary>
internal sealed class StubMulticastQuery : IMulticastQuery
{
    public List<byte[]> Responses { get; } = [];

    public byte[]? LastQuery { get; private set; }

    public Task<IReadOnlyList<byte[]>> AskAsync(byte[] query, TimeSpan window, CancellationToken cancellationToken)
    {
        LastQuery = query;
        return Task.FromResult<IReadOnlyList<byte[]>>(Responses);
    }
}

/// <summary>Records systemctl invocations.</summary>
internal sealed class RecordingSystemControl : ISystemControl
{
    private readonly Dictionary<string, SystemControlResult> _answers = new(StringComparer.Ordinal);

    public List<string> Commands { get; } = [];

    public bool Succeed { get; set; } = true;

    /// <summary>Scripts what <c>systemctl</c> says to one exact argument vector.</summary>
    /// <remarks>
    /// Needed once resources started reading systemd's <i>answers</i> rather than only its exit
    /// codes — <c>systemctl show -p ExecStart</c> is how the autologin drop-in's effective value is
    /// told apart from the value in the file, and those two disagreeing is the fault the resource
    /// exists to catch.
    /// </remarks>
    public void Answer(string command, string output, bool succeeded = true) =>
        _answers[command] = new SystemControlResult(succeeded, output);

    public Task<SystemControlResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = string.Join(' ', arguments);
        Commands.Add(line);

        return Task.FromResult(_answers.TryGetValue(line, out var scripted)
            ? scripted
            : new SystemControlResult(Succeed, string.Empty));
    }
}

/// <summary>
/// The login user's session, without a login user.
/// </summary>
/// <remarks>
/// The one seam that cannot be pointed at a temporary directory: <c>systemctl --user</c> answers
/// about a systemd manager that exists only inside an autologin session on a Pi. Everything the
/// kiosk block does through it — enable, is-enabled, is-active, restart, <c>wlr-randr</c> — is a
/// command and an answer, so scripting the answers exercises the resources' real parsing.
/// </remarks>
internal sealed class FakeUserSession : IUserSession
{
    public string UserName { get; set; } = "framelink";

    public string HomeDirectory => "/home/" + UserName;

    public List<string> Commands { get; } = [];

    public List<string> Owned { get; } = [];

    public Dictionary<string, ProcessResult> Answers { get; } = new(StringComparer.Ordinal);

    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty);

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var line = executable + " " + string.Join(' ', arguments);
        Commands.Add(line);

        return Task.FromResult(Answers.TryGetValue(line, out var scripted) ? scripted : Default);
    }

    public Task GiveToUserAsync(string path, CancellationToken cancellationToken)
    {
        Owned.Add(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// What <see cref="UserSessionGate"/> sees. Ready by default, so every test written before the
    /// gate existed still describes a frame with a live session and keeps asserting the real
    /// parsing; a test about the boot race sets it false and says so.
    /// </summary>
    public SessionReadiness Readiness { get; set; } = SessionReadiness.Up;

    public int ReadinessProbes { get; private set; }

    public Task<SessionReadiness> ReadinessAsync(CancellationToken cancellationToken)
    {
        ReadinessProbes++;
        return Task.FromResult(Readiness);
    }
}

/// <summary>A memory reading a test sets outright.</summary>
internal sealed class StubMemoryProbe : IMemoryProbe
{
    public MemorySample Sample { get; set; } = new(200_000, 4, 900_000);

    public int Samples { get; private set; }

    public ValueTask<MemorySample> SampleAsync(CancellationToken cancellationToken)
    {
        Samples++;
        return ValueTask.FromResult(Sample);
    }
}

/// <summary>Convenience factories for agent status values.</summary>
internal static class AgentStatusFactory
{
    public static AgentStatus Starting(string deviceId = "TEST-DEVI-CEID-0001") => new()
    {
        Condition = DeviceStateLadder.Starting,
        DeviceId = deviceId,
    };

    /// <summary>A frame the Fleet Manager has cleared, with nothing drifted (§2.6's InSync rung).</summary>
    public static AgentStatus Green(string deviceId = "TEST-DEVI-CEID-0001") => new()
    {
        Condition = DeviceStateLadder.FromHandshake(AgentServerScript.Ok()),
        LastAuthoritative = DeviceStateLadder.FromHandshake(AgentServerScript.Ok()),
        DeviceId = deviceId,
    };
}

/// <summary>
/// <c>wpctl status</c> as this project's own hardware prints it.
/// </summary>
/// <remarks>
/// Shared because two blocks read the same output for different reasons — the camera block asks
/// what <c>Video</c> / <c>Sources</c> holds, and <see cref="MediaGraphGate"/> asks whether
/// <c>Audio</c> has been built at all — and a fixture copied into both is two fixtures that can
/// drift apart.
/// </remarks>
internal static class WpctlCaptures
{
    /// <summary>
    /// <c>wpctl status</c> captured off Frame #1 on 2026-08-23, converged, with the camera node up.
    /// </summary>
    /// <remarks>
    /// A whole capture rather than the abridged ones above, because the gate reads two subsections
    /// of it and a fixture that omits either would agree with the parser by construction. The
    /// default markers are as <c>wpctl</c> printed them.
    /// </remarks>
    internal const string Settled =
        "PipeWire 'pipewire-0' [1.4.2, framelink@framelink-mule, cookie:3177108782]\n"
        + " └─ Clients:\n"
        + "        33. WirePlumber                         [1.4.2, framelink@framelink-mule, pid:1069]\n"
        + "        34. pipewire                            [1.4.2, framelink@framelink-mule, pid:1070]\n"
        + "        52. gst-launch-1.0                      [1.4.2, framelink@framelink-mule, pid:1067]\n"
        + "        58. xdg-desktop-portal                  [1.4.2, framelink@framelink-mule, pid:1401]\n"
        + "\n"
        + "Audio\n"
        + " ├─ Devices:\n"
        + " │      43. reSpeaker XVF3800 4-Mic Array       [alsa]\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  *   48. reSpeaker XVF3800 4-Mic Array Analog Stereo [vol: 1.00]\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  *   45. reSpeaker XVF3800 4-Mic Array Analog Stereo [vol: 1.00]\n"
        + " │  \n"
        + " ├─ Filters:\n"
        + " │  \n"
        + " └─ Streams:\n"
        + "\n"
        + "Video\n"
        + " ├─ Devices:\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  *   56. FrameLinkCam                       \n"
        + " │  \n"
        + " ├─ Filters:\n"
        + " │  \n"
        + " └─ Streams:\n"
        + "\n"
        + "Settings\n"
        + " └─ Default Configured Devices:\n";

    /// <summary>
    /// The same frame in the seconds before WirePlumber has built anything.
    /// </summary>
    /// <remarks>
    /// <b>Derived from <see cref="Settled"/>, not captured</b> — reproducing the state would
    /// mean rebooting a converged frame, which nothing here is allowed to do. What it is derived
    /// from <i>is</i> measured: the frame's user journal for the cascade of 2026-08-19 has
    /// <c>wireplumber.service</c> started at 03:03:27.642 and its device monitors still loading at
    /// 03:03:31.049, so a graph with no <c>Audio</c> device and no default sink is the state a pass
    /// landing in that window asks about. The entries WirePlumber has not created yet are the only
    /// difference; the skeleton, the tree characters and the section order are the capture's.
    /// </remarks>
    internal const string Unsettled =
        "PipeWire 'pipewire-0' [1.4.2, framelink@framelink-mule, cookie:3177108782]\n"
        + " └─ Clients:\n"
        + "        33. WirePlumber                         [1.4.2, framelink@framelink-mule, pid:1069]\n"
        + "        34. pipewire                            [1.4.2, framelink@framelink-mule, pid:1070]\n"
        + "\n"
        + "Audio\n"
        + " ├─ Devices:\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  \n"
        + " ├─ Filters:\n"
        + " │  \n"
        + " └─ Streams:\n"
        + "\n"
        + "Video\n"
        + " ├─ Devices:\n"
        + " │  \n"
        + " ├─ Sinks:\n"
        + " │  \n"
        + " ├─ Sources:\n"
        + " │  \n"
        + " ├─ Filters:\n"
        + " │  \n"
        + " └─ Streams:\n"
        + "\n"
        + "Settings\n"
        + " └─ Default Configured Devices:\n";
}
