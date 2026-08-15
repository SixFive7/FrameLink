using System.Text.Json.Serialization;

namespace FrameLink.Control;

/// <summary>
/// Source-generated serialisation for every type the Fleet Manager puts on a wire that is
/// not part of the frozen protocol contract.
/// </summary>
/// <remarks>
/// <para>
/// Native AOT has no reflection-based serialiser, and <c>Directory.Build.props</c> turns the
/// trim and AOT analysers up to error severity so that a reflection <c>JsonSerializer</c>
/// call fails the build (IL2026/IL3050) rather than a container. Everything crossing the
/// wire therefore appears in this list or in <c>ProtocolJson</c>.
/// </para>
/// <para>
/// This is a second context alongside <c>ProtocolJson</c> rather than an extension of it,
/// because that project is frozen and is not modified. Both are registered on the HTTP JSON
/// options, and the resolver chain picks whichever knows the type.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentPing))]
[JsonSerializable(typeof(AgentPong))]
[JsonSerializable(typeof(SettingsPush))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(DeviceView))]
[JsonSerializable(typeof(DeviceListResponse))]
[JsonSerializable(typeof(AdoptRequest))]
[JsonSerializable(typeof(SettingValueRequest))]
[JsonSerializable(typeof(FleetSettingsResponse))]
[JsonSerializable(typeof(DeviceSettingsResponse))]
[JsonSerializable(typeof(ApiError))]
public sealed partial class ControlJson : JsonSerializerContext;
