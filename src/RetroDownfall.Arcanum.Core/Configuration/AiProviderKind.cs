using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<AiProviderKind>))]

public enum AiProviderKind
{

    OpenAICompatible,

    /// <summary>
    /// The operator's installed Claude Code CLI (<c>claude</c>), invoked headlessly as a Familiar.
    /// A transport, not a second model of intelligence: subscription identity, auth storage, and
    /// rate limits stay entirely with the CLI installation.
    /// </summary>
    ClaudeCodeCli,

    /// <summary>
    /// The operator's installed OpenAI Codex CLI (<c>codex</c>), invoked headlessly as a Familiar.
    /// </summary>
    CodexCli,

}
