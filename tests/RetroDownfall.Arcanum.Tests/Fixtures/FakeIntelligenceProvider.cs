using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider
{

    public string NextText { get; set; } = "pong";

    public Error? NextFailure { get; set; }

    public List<PromptToolCall>? NextToolCalls { get; set; }

    public string? NextFinishReason { get; set; }

    public Exception? NextStreamException { get; set; }

    public Exception? ThrowOnSecondYield { get; set; }

    public string? LastPrompt { get; private set; }

    public bool StreamCancellationObserved { get; private set; }

    public Task<Result<PromptTurnResult>> ExecutePromptAsync(
        PingRequest request,
        CancellationToken cancellationToken = default)
    {

        LastPrompt = request.Prompt;

        if (NextFailure is Error failure)
        {

            return Task.FromResult(Result<PromptTurnResult>.Failure(failure));

        }

        return Task.FromResult(
            Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null, NextToolCalls, NextFinishReason)));

    }

    public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
        PingRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        await Task.CompletedTask.ConfigureAwait(false);

        if (cancellationToken.CanBeCanceled)
        {

            cancellationToken.Register(() => StreamCancellationObserved = true);

        }

        if (NextFailure is Error failure)
        {

            yield return new IntelligenceEvent(IntelligenceEventType.Error, failure.Message);

            yield break;

        }

        if (NextStreamException is Exception ex)
        {

            throw ex;

        }

        yield return new IntelligenceEvent(IntelligenceEventType.Token, string.Empty, NextText);

        if (ThrowOnSecondYield is Exception secondEx)
        {

            throw secondEx;

        }

        yield return new IntelligenceEvent(
            IntelligenceEventType.Result,
            "Complete",
            "0",
            null,
            FinishReason: NextFinishReason ?? "stop");

    }

}
