namespace FrameLink.Control.Imaging;

/// <summary>Where a build has got to.</summary>
public enum ImageBuildState
{
    /// <summary>Nothing has been asked for since this server started.</summary>
    Idle,

    /// <summary>A build is under way.</summary>
    Running,

    /// <summary>The last build produced a checked image.</summary>
    Succeeded,

    /// <summary>The last build stopped, and <see cref="ImageBuildStatus.Problem"/> says why.</summary>
    Failed,
}

/// <summary>The whole of what the operator's console renders for image generation.</summary>
public sealed record ImageBuildStatus
{
    /// <summary>Where the build has got to.</summary>
    public required ImageBuildState State { get; init; }

    /// <summary>The step under way, or the last one attempted.</summary>
    public string? Step { get; init; }

    /// <summary>When the current or last build started.</summary>
    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>When it finished.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Why it stopped, when it did.</summary>
    public string? Problem { get; init; }

    /// <summary>The machine-readable verdict of the last build.</summary>
    public string? Result { get; init; }

    /// <summary>The image now on disk, when there is one.</summary>
    public ImageArtifact? Artifact { get; init; }
}

/// <summary>
/// Runs at most one image build at a time, in the background, and reports where it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>One slot, deliberately, and it is the storage policy as much as the concurrency policy.</b>
/// A build copies the whole 2.8 GB base image before it writes a byte, so two at once is 5.6 GB
/// of working copies on a volume §3.1 sized for a SQLite file. One at a time means peak usage is
/// the base image, one working copy and one previous artifact, and the free-space check in
/// <see cref="ImageBuilder"/> is written against exactly that figure. A second request while one
/// is running is refused with a sentence rather than queued, because an operator pressing the
/// button twice wants to know it is already going.
/// </para>
/// <para>
/// Background rather than synchronous because the work is minutes of copying and checking, and a
/// request held open for minutes is a request some proxy between §3.8's Traefik and the browser
/// will eventually give up on. The console polls <see cref="Status"/>, which is the same shape
/// whether or not anything is running.
/// </para>
/// <para>
/// Nothing here is persisted. A restarted server reports <see cref="ImageBuildState.Idle"/> with
/// whatever artifact is on disk, which is honest: the file is the durable thing, and a build
/// interrupted by a restart left only a working directory that the next build resets.
/// </para>
/// </remarks>
public sealed class ImageBuildService(
    ImageBuilder builder,
    TimeProvider clock,
    ILogger<ImageBuildService> logger) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private ImageBuildStatus _status = new() { State = ImageBuildState.Idle };
    private Task _running = Task.CompletedTask;

    /// <summary>The current status. Safe to read from any thread at any time.</summary>
    public ImageBuildStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The builder, for the routes that describe the pin and serve the artifact.</summary>
    public ImageBuilder Builder => builder;

    /// <summary>Starts a build, or explains why it will not.</summary>
    /// <param name="seed">What the image will carry.</param>
    /// <param name="refusal">Why the request was declined, when this returns false.</param>
    public bool TryStart(ImageSeed seed, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(seed);

        lock (_gate)
        {
            if (_status.State is ImageBuildState.Running)
            {
                refusal = $"An image is already being built ({_status.Step}). "
                    + "Only one runs at a time, because each one needs a full copy of the base image.";
                return false;
            }

            _status = new ImageBuildStatus
            {
                State = ImageBuildState.Running,
                Step = "Starting",
                StartedUtc = clock.GetUtcNow(),

                // The previous artifact stays visible while the new one is built. It is still on
                // disk and still flashable right up to the instant the rename replaces it.
                Artifact = _status.Artifact,
            };

            // The token is read here rather than inside the background task. Dispose cancels and
            // then disposes the source, and reading Token after that throws — a race that would
            // turn an orderly shutdown into an unobserved exception exactly when a build is in
            // flight, which is the only time it could happen.
            _running = Task.Run(() => BuildAsync(seed, _shutdown.Token), CancellationToken.None);
        }

        refusal = null;
        return true;
    }

    /// <summary>Waits for the current build to finish. For tests and for shutdown.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task BuildAsync(ImageSeed seed, CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(step =>
        {
            lock (_gate)
            {
                if (_status.State is ImageBuildState.Running)
                {
                    _status = _status with { Step = step };
                }
            }
        });

        try
        {
            var outcome = await builder
                .BuildAsync(seed, progress, cancellationToken)
                .ConfigureAwait(false);

            Finish(
                outcome.Result is ImageBuildResult.Succeeded ? ImageBuildState.Succeeded : ImageBuildState.Failed,
                outcome.Result.ToString(),
                outcome.Problem,
                outcome.Artifact);
        }
        catch (OperationCanceledException)
        {
            Finish(ImageBuildState.Failed, "Cancelled", "The server shut down while the image was being built.", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A build failing must never take the server down with it. The operator gets the
            // message and can press the button again; nothing was published, because publishing
            // is the last thing BuildAsync does and only after e2fsck passed.
            logger.ImageBuildFaulted(exception);
            Finish(ImageBuildState.Failed, "Faulted", exception.Message, null);
        }
    }

    private void Finish(ImageBuildState state, string result, string? problem, ImageArtifact? artifact)
    {
        lock (_gate)
        {
            _status = new ImageBuildStatus
            {
                State = state,
                Step = _status.Step,
                StartedUtc = _status.StartedUtc,
                CompletedUtc = clock.GetUtcNow(),
                Problem = problem,
                Result = result,

                // A failed build leaves whatever was already there. The previous image is still a
                // good image, and hiding it because a later attempt failed would cost an operator
                // the artifact they were about to flash.
                Artifact = artifact ?? _status.Artifact,
            };
        }
    }
}
