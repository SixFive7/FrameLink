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
/// This is a second context alongside <c>ProtocolJson</c> rather than an extension of it, and
/// the split is the same one as in <c>ControlContracts.cs</c>: what the <i>agent</i> reads is
/// contract and lives in <c>FrameLink.Protocol</c>; what the operator's <i>browser</i> reads
/// ships in the same container as this server and lives here. Both are registered on the HTTP
/// JSON options, and the resolver chain picks whichever knows the type.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(DeviceView))]
[JsonSerializable(typeof(DeviceListResponse))]
[JsonSerializable(typeof(SettingValueRequest))]
[JsonSerializable(typeof(FleetSettingsResponse))]
[JsonSerializable(typeof(DeviceSettingsResponse))]
[JsonSerializable(typeof(DeviceReconcileResponse))]
[JsonSerializable(typeof(DeviceEventsResponse))]
[JsonSerializable(typeof(FleetPackagesResponse))]
[JsonSerializable(typeof(DevicePackagesResponse))]
[JsonSerializable(typeof(ImageRequest))]
[JsonSerializable(typeof(ImageStatusResponse))]
[JsonSerializable(typeof(LiveKitStatusResponse))]
[JsonSerializable(typeof(CallTokenResponse))]
[JsonSerializable(typeof(LiveKitRotateResponse))]
[JsonSerializable(typeof(ApiError))]
public sealed partial class ControlJson : JsonSerializerContext;
