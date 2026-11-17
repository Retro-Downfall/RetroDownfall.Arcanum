using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAiChatMessage = OpenAI.Chat.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Preserves Ollama thinking across the pinned OpenAI adapter's same-provider tool continuations.</summary>
internal sealed class OllamaReasoningChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
#pragma warning disable SCME0001 // The pinned SDK's public Patch is the AOT-safe extension-field boundary.
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<MeAiChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(
            PrepareContinuation(messages, cancellationToken), options, cancellationToken).ConfigureAwait(false);

        if (response.RawRepresentation is ChatCompletion raw
            && response.Messages.Count > 0
            && !response.Messages[0].Contents.Any(static content => content is TextReasoningContent)
            && raw.Patch.TryGetValue("$.choices[0].message.reasoning"u8, out string? thinking)
            && !string.IsNullOrEmpty(thinking))
        {
            response.Messages[0].Contents.Add(new TextReasoningContent(thinking));
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MeAiChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
                           PrepareContinuation(messages, cancellationToken), options, cancellationToken).ConfigureAwait(false))
        {
            if (update.RawRepresentation is StreamingChatCompletionUpdate raw
                && !update.Contents.Any(static content => content is TextReasoningContent)
                && raw.Patch.TryGetValue("$.choices[0].delta.reasoning"u8, out string? thinking)
                && !string.IsNullOrEmpty(thinking))
            {
                update.Contents.Add(new TextReasoningContent(thinking));
            }

            yield return update;
        }
    }

    private static IEnumerable<MeAiChatMessage> PrepareContinuation(
        IEnumerable<MeAiChatMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (MeAiChatMessage message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (message.Role != ChatRole.Assistant)
            {
                yield return message;

                continue;
            }

            StringBuilder? thinking = null;

            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent { Text.Length: > 0 } reasoning)
                {
                    (thinking ??= new()).Append(reasoning.Text);
                }
            }

            if (thinking is null)
            {
                yield return message;

                continue;
            }

            // Let the pinned adapter retain its own visible-content and tool-argument mapping.
            // No ChatOptions here: Instructions are applied once by the actual provider call.
            foreach (OpenAiChatMessage converted in new[] { message }.AsOpenAIChatMessages())
            {
                OpenAiChatMessage outgoing = converted;

                if (ReferenceEquals(converted, message.RawRepresentation))
                {
                    // The SDK bypasses conversion for raw messages. Clone through its generated
                    // persistence implementation before patching, preserving unknown fields too.
                    IPersistableModel<OpenAiChatMessage> model = (IPersistableModel<OpenAiChatMessage>)converted;

                    outgoing = model.Create(model.Write(ModelReaderWriterOptions.Json), ModelReaderWriterOptions.Json)
                        ?? throw new InvalidOperationException("The SDK could not copy the assistant continuation message.");
                }

                outgoing.Patch.Set("$.reasoning"u8, thinking.ToString());

                MeAiChatMessage continuation = message.Clone();

                continuation.RawRepresentation = outgoing;

                yield return continuation;
            }
        }
    }
#pragma warning restore SCME0001
}
