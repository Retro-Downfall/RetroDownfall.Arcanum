using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

internal static class OpenAiChatCompletionMapper
{

    internal static PingRequest ToPingRequest(OpenAiChatRequest request)
    {
        List<OpenAiChatMessage> msgs = request.Messages!;

        List<CoreChatMessage> stateless = new(msgs.Count);

        foreach (OpenAiChatMessage m in msgs)
        {
            stateless.Add(new CoreChatMessage(m.Role, m.Content ?? string.Empty));
        }

        return new PingRequest(
            Prompt: string.Empty,
            Model: request.Model,
            WorkingDirectory: string.Empty,
            ContextSnapshot: null,
            ConversationId: null,
            DisableMcpTools: false,
            CliTerminalFormatting: false,
            UnattendedMode: true,
            AttachedFiles: null,
            ChronosyncDelta: null,
            StatelessMessages: stateless);
    }

}
