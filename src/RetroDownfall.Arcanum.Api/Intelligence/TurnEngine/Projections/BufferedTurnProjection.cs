using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;

/// <summary>
/// Projects the semantic turn stream into a single buffered <see cref="PromptTurnResult"/>.
/// <see cref="RunCompleted"/> is authoritative for final text/usage/tool calls.
/// </summary>
internal sealed class BufferedTurnProjection
{

    private RunCompleted? _completed;

    private RunFailed? _failed;

    private RunAbandoned? _abandoned;

    public void Apply(TurnEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt)
        {
            case RunCompleted completed:
                _completed = completed;

                break;

            case RunFailed failed:
                _failed = failed;

                break;

            case RunAbandoned abandoned:
                _abandoned = abandoned;

                break;
        }
    }

    public Result<PromptTurnResult> ToResult()
    {
        if (_completed is not null)
        {
            PromptTurnResult turn = new(
                _completed.FinalText,
                _completed.Usage,
                _completed.ToolCalls?.ToList(),
                _completed.FinishReason)
            {
                Warnings = _completed.Warnings,
            };

            return Result<PromptTurnResult>.Success(turn);
        }

        if (_failed is not null)
        {
            return Result<PromptTurnResult>.Failure(_failed.Error);
        }

        if (_abandoned is not null)
        {
            Error error = _abandoned.Error
                ?? new Error(ErrorCodes.Hub.Error, "Turn abandoned.");

            return Result<PromptTurnResult>.Failure(error);
        }

        return Result<PromptTurnResult>.Failure(
            new Error(ErrorCodes.Hub.Error, "Turn ended without a terminal event."));
    }

}
