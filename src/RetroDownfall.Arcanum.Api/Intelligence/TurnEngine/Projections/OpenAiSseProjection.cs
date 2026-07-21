using System.Threading.Channels;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;

/// <summary>
/// Projects semantic <see cref="TurnEvent"/>s into OpenAI SSE chat chunks.
/// Omits toolResult/toolError/ward/status frames (ADR 0004 OpenAI projection filter).
/// Does not serialize HTTP — writes typed chunks to a transport channel.
/// </summary>
internal sealed class OpenAiSseProjection
{

    private readonly ChannelWriter<OpenAiChatChunk> _writer;

    private readonly string _completionId;

    private readonly string _model;

    private readonly long _created;

    private const int ChoiceIndex = 0;

    /// <summary>
    /// Monotonic tool-call delta index across the whole completion (not reset per tool round),
    /// matching <c>OpenAiV1Endpoints.HandleStreamingAsync</c>.
    /// </summary>
    private int _nextToolCallDeltaIndex;

    public OpenAiSseProjection(
        ChannelWriter<OpenAiChatChunk> writer,
        string completionId,
        string model,
        long? createdUnixSeconds = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _completionId = string.IsNullOrWhiteSpace(completionId)
            ? "chatcmpl-" + Guid.NewGuid().ToString("N")
            : completionId;
        _model = model ?? string.Empty;
        _created = createdUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public async ValueTask ApplyAsync(TurnEvent evt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (OpenAiChatChunk chunk in Map(evt))
        {
            await _writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        if (evt.IsTerminal)
        {
            _ = _writer.TryComplete();
        }
    }

    internal IEnumerable<OpenAiChatChunk> Map(TurnEvent evt) =>
        evt switch
        {
            TextDelta delta =>
            [
                CreateChunk(
                    new OpenAiChatStreamChoice(
                        Index: ChoiceIndex,
                        Delta: new OpenAiDelta(Content: delta.Text),
                        FinishReason: null)),
            ],

            ToolCallProposed proposed => MapToolCallProposed(proposed),

            RunCompleted completed =>
            [
                CreateChunk(
                    new OpenAiChatStreamChoice(
                        Index: ChoiceIndex,
                        Delta: new OpenAiDelta(),
                        FinishReason: completed.FinishReason ?? "stop"),
                    completed.Usage),
            ],

            RunFailed or RunAbandoned =>
            [
                CreateChunk(
                    new OpenAiChatStreamChoice(
                        Index: ChoiceIndex,
                        Delta: new OpenAiDelta(),
                        FinishReason: "stop")),
            ],

            // OpenAI projection filter: omit status/ward/toolResult/toolError/etc.
            _ => [],
        };

    private IEnumerable<OpenAiChatChunk> MapToolCallProposed(ToolCallProposed proposed)
    {
        int deltaIndex = _nextToolCallDeltaIndex++;

        yield return CreateChunk(
            new OpenAiChatStreamChoice(
                Index: ChoiceIndex,
                Delta: new OpenAiDelta(
                    ToolCalls:
                    [
                        new OpenAiStreamToolCall(
                            Index: deltaIndex,
                            Id: proposed.CallId,
                            Type: "function",
                            Function: new OpenAiFunctionCall(proposed.ToolName, proposed.ArgumentsJson)),
                    ]),
                FinishReason: null));
    }

    private OpenAiChatChunk CreateChunk(OpenAiChatStreamChoice choice, ChatCompletionUsage? usage = null) =>
        new(
            _completionId,
            "chat.completion.chunk",
            _created,
            _model,
            [choice],
            usage);

}
