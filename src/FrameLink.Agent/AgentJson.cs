using System.Text.Json.Serialization;
using FrameLink.Agent.Discovery;
using FrameLink.Agent.Local;
using FrameLink.Agent.Reconcile;
using FrameLink.Agent.Resources;
using FrameLink.Agent.State;

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
[JsonSerializable(typeof(ReconcileJournalState))]
[JsonSerializable(typeof(BootTrialState))]
[JsonSerializable(typeof(AgentMemoryState))]
[JsonSerializable(typeof(PageMessage))]
[JsonSerializable(typeof(StageMessage))]
[JsonSerializable(typeof(AppConfigDocument))]
public sealed partial class AgentJson : JsonSerializerContext;
