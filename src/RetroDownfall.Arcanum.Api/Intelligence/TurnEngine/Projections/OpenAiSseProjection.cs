using System.Threading.Channels;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;

/// <summary>
/// Projects semantic <see cref="TurnEvent"/>s into OpenAI SSE chat chunks.
/// Omits toolResult/toolError/ward/status frames (ADR 0004 OpenAI projection filter).
/// Does not serialize HTTP — writes typed chunks to a transport channel. This is a semantic
/// helper/characterization path for reasoning-field and typed-error rules. The authoritative
/// production <c>/v1</c> route instead reshapes native <c>IntelligenceEvent</c> frames in
/// <c>OpenAiV1Endpoints</c>; terminal usage chunks and tool-argument fragmentation belong to that
/// endpoint mapper and are outside this helper's contract.
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

    private bool _reasoningEmitted;

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

    internal IEnumerable<OpenAiChatChunk> Map(TurnEvent evt)
    {
        if (evt is ReasoningDelta reasoning)
        {
            _reasoningEmitted = true;
            return [CreateReasoningChunk(reasoning.Reasoning)];
        }

        if (evt is RunCompleted completed)
        {
            return MapCompleted(completed);
        }

        return evt switch
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

            RunFailed failed => [CreateErrorChunk(failed.Error)],

            RunAbandoned abandoned => [CreateErrorChunk(abandoned.Error)],

            // OpenAI projection filter: omit status/ward/toolResult/toolError/etc.
            _ => [],
        };
    }

    private IEnumerable<OpenAiChatChunk> MapCompleted(RunCompleted completed)
    {
        if (!_reasoningEmitted)
        {
            foreach (Core.Intelligence.ReasoningContentSegment reasoning in completed.Reasoning)
            {
                yield return CreateReasoningChunk(reasoning);
            }

            _reasoningEmitted = completed.Reasoning.Count > 0;
        }

        yield return CreateChunk(
            new OpenAiChatStreamChoice(
                Index: ChoiceIndex,
                Delta: new OpenAiDelta(),
                FinishReason: completed.FinishReason ?? "stop"),
            completed.Usage);
    }

    private OpenAiChatChunk CreateReasoningChunk(Core.Intelligence.ReasoningContentSegment reasoning) =>
        CreateChunk(
            new OpenAiChatStreamChoice(
                Index: ChoiceIndex,
                Delta: reasoning.Output == Core.Intelligence.ReasoningOutputMode.Summary
                    ? new OpenAiDelta(ReasoningSummary: reasoning.Text)
                    : new OpenAiDelta(ReasoningContent: reasoning.Text),
                FinishReason: null));

    private OpenAiChatChunk CreateErrorChunk(Core.Primitives.Error? error) =>
        CreateChunk(
            new OpenAiChatStreamChoice(
                Index: ChoiceIndex,
                Delta: new OpenAiDelta(),
                FinishReason: "error"),
            error: OpenAiStreamErrorMapper.Map(error));

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

    private OpenAiChatChunk CreateChunk(
        OpenAiChatStreamChoice choice,
        ChatCompletionUsage? usage = null,
        OpenAiErrorDetail? error = null) =>
        new(
            _completionId,
            "chat.completion.chunk",
            _created,
            _model,
            [choice],
            usage,
            Error: error);

}
