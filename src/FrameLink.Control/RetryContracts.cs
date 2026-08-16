namespace FrameLink.Control;

/// <summary>What the console is told about a retry it asked for (§2.5 rung 3).</summary>
/// <remarks>
/// <para>
/// Its own file rather than another record in <c>ControlContracts.cs</c>, so the retry feature is
/// legible in one place. The <c>[JsonSerializable]</c> registration is the one line of it that
/// could <b>not</b> live here: <see cref="ControlJson"/> is a partial class and a second partial
/// declaration carrying only the new attribute compiles, but the System.Text.Json generator emits
/// per declaration and then collides with itself —
/// <c>The hintName 'ControlJson.Boolean.g.cs' of the added source file must be unique within a
/// generator</c> — which takes the whole context down, not just the addition. So the attribute
/// goes in <c>ControlJson.cs</c> beside every other one, and this comment is here to stop the next
/// person spending the same twenty minutes discovering why.
/// </para>
/// </remarks>
public sealed record RetryResponse
{
    /// <summary>The device the retry was addressed to.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The resource named, or null when every resource that gave up was asked.</summary>
    public string? Resource { get; init; }

    /// <summary><c>sent</c>, or <c>offline</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>One sentence for the operator, in the same register as the device row.</summary>
    /// <remarks>
    /// Carries the latency rather than hiding it. A retry resets the budget and the loop picks it
    /// up on its next pass, which on a frame that has stopped reconciling is up to the drift-sweep
    /// interval away — so a console that said only "done" would leave the operator watching an
    /// unchanged screen wondering whether the button worked.
    /// </remarks>
    public required string Detail { get; init; }
}
