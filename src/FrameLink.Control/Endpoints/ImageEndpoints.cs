using FrameLink.Control.Imaging;

namespace FrameLink.Control.Endpoints;

/// <summary>
/// Generating a ready-to-flash SD image (§3.9).
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/api</c>, so <see cref="Authentication.OperatorGate"/> guards all three routes with
/// no special case: producing an image an operator will hand to a household is exactly as
/// operator-shaped an action as adopting a device.
/// </para>
/// <para>
/// Three routes and no more. Read the state, ask for a build, take the file. The build is
/// asynchronous because it is minutes long (§3.9), so the POST answers 202 and the console polls
/// the GET — which is also what makes a browser refresh mid-build harmless.
/// </para>
/// </remarks>
public static class ImageEndpoints
{
    /// <summary>Maps the image-generation routes.</summary>
    public static void MapImageEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/image", GetStatus);
        app.MapPost("/api/image", Start);
        app.MapGet("/api/image/artifact", GetArtifact);
    }

    private static IResult GetStatus(ImageBuildService builds) =>
        Results.Json(Describe(builds), ControlJson.Default.ImageStatusResponse);

    /// <summary>Starts a build.</summary>
    /// <remarks>
    /// The body parameter is nullable deliberately, and the reason is recorded on
    /// <c>OperatorEndpoints.AdoptAsync</c>: a non-nullable body makes it <i>required</i> in a
    /// minimal API, and a POST carrying no <c>Content-Type</c> is not routed to a body-bound
    /// endpoint at all — it reaches the SPA fallback, so the caller gets 200 <c>text/html</c> back
    /// from an API call. Nullable means a bodyless press lands here and is answered in words.
    /// </remarks>
    private static IResult Start(ImageRequest? request, ImageBuildService builds)
    {
        if (request is null)
        {
            return Error("bad-request", "A control URL is required.", StatusCodes.Status400BadRequest);
        }

        // Validation happens here rather than inside the build, so a typo is an immediate red
        // field in the console instead of a failure discovered three minutes into a 2.8 GB copy.
        if (!ImageSeed.TryCreate(request.ControlUrl, request.LanUrl, out var seed, out var problem))
        {
            return Error("bad-request", problem, StatusCodes.Status400BadRequest);
        }

        if (!builds.TryStart(seed, out var refusal))
        {
            return Error("already-building", refusal, StatusCodes.Status409Conflict);
        }

        return Results.Json(
            Describe(builds),
            ControlJson.Default.ImageStatusResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>The finished image, as a download.</summary>
    /// <remarks>
    /// Range processing on, for the same reason the agent binary route has it: this is a 2.8 GB
    /// file over whatever link the operator has, and a drop at 90% should cost the last 10%
    /// rather than the whole thing.
    /// </remarks>
    private static IResult GetArtifact(ImageBuildService builds)
    {
        var path = builds.Builder.ArtifactPath;
        if (!File.Exists(path))
        {
            return Error(
                "no-image",
                "No image has been generated yet.",
                StatusCodes.Status404NotFound);
        }

        return Results.File(
            path,
            "application/octet-stream",
            fileDownloadName: ImageBuilder.ArtifactFileName,
            enableRangeProcessing: true);
    }

    private static ImageStatusResponse Describe(ImageBuildService builds)
    {
        var builder = builds.Builder;
        var pin = builder.Pin;
        var status = builds.Status;

        return new ImageStatusResponse
        {
            Base = new BaseImageView
            {
                Release = pin.Release,
                FileName = pin.ImageFileName,
                ArchiveUrl = pin.ArchiveUrl.AbsoluteUri,
                ArchiveSha256 = pin.ArchiveSha256,
                ImageSha256 = pin.ImageSha256,
                ImageSizeBytes = pin.ImageSizeBytes,
                ReviewedUtc = pin.ReviewedUtc,
                Directory = builder.ImageDirectory,
                PreparationCommand = pin.PreparationCommand,
                Problem = pin.InspectWithoutHashing(builder.BaseImagePath),
            },
            State = status.State.ToString(),
            Step = status.Step,
            StartedUtc = status.StartedUtc,
            CompletedUtc = status.CompletedUtc,
            Problem = status.Problem,
            Result = status.Result,
            Artifact = status.Artifact,
            ArtifactAvailable = File.Exists(builder.ArtifactPath),
        };
    }

    private static IResult Error(string code, string? detail, int statusCode) =>
        Results.Json(
            new ApiError { Error = code, Detail = detail },
            ControlJson.Default.ApiError,
            statusCode: statusCode);
}
