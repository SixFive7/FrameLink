using System.Text.Json.Serialization;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Link;

namespace FrameLink.Agent;

/// <summary>
/// Source-generated serialisation for the agent's <i>own</i> persisted types.
/// </summary>
/// <remarks>
/// Separate from <c>ProtocolJson</c> on purpose. That context is the frozen wire contract
/// shared with the Fleet Manager (§4.2); this one covers state that only ever lives on the
/// frame, where the shape is free to change between versions because nothing else reads it.
/// Mixing them would put agent-local records inside a contract that is not allowed to move.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ControlEndpoints))]
public sealed partial class AgentJson : JsonSerializerContext;

/// <summary>
/// Source-generated serialisation for the post-handshake channel payloads the agent sends.
/// </summary>
/// <remarks>
/// Not folded into <see cref="AgentJson"/> because that context writes indented JSON, which is
/// right for a state file a human may read on the frame and wrong for a message sent every
/// twenty-five seconds for the life of the connection. The naming policy matches the Fleet
/// Manager's own context, since camelCase is part of the wire contract rather than a local
/// preference.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlPong))]
public sealed partial class AgentWireJson : JsonSerializerContext;
