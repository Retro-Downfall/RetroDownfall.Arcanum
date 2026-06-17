using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Runtime state of a managed <c>llama-server</c> instance.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LlamaServerState>))]
public enum LlamaServerState
{

    Stopped,

    Starting,

    Running,

    Error,

    Stopping,

}
