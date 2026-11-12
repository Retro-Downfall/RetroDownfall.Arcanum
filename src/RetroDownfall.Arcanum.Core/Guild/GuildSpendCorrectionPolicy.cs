using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Validates cumulative measurements; it does not establish authority or replay identity.</summary>
internal static class GuildSpendCorrectionPolicy
{
    public static Result ValidateOperatorRequest(GuildSpendCorrectionRequest? request)
    {
        if (request is null || request.IdempotencyKey == Guid.Empty || request.ExpectedRevision <= 0 ||
            request.Origin != GuildSpendOrigin.OperatorAttested || request.EvidenceDigest is null ||
            request.EvidenceDigest.Length != 64 || !request.EvidenceDigest.All(Uri.IsHexDigit))
        {
            return Invalid();
        }

        var validation = Validate(new GuildSpendFacts(0, 0, 0, 0, 0, false, false, false), request.Amounts);

        return validation.IsSuccess ? Result.Success() : Result.Failure(validation.Error);
    }

    public static Result<GuildSpendFacts> Validate(GuildSpendFacts? current, GuildSpendAmountsRequest? proposed)
    {
        if (current is null || !ValidNumbers(current.InputTokens, current.OutputTokens, current.CachedTokens, current.ReasoningTokens, current.Usd) ||
            proposed is null || proposed.InputTokens is not long input || proposed.OutputTokens is not long output ||
            proposed.CachedTokens is not long cached || proposed.ReasoningTokens is not long reasoning || proposed.Usd is not decimal usd ||
            !ValidNumbers(input, output, cached, reasoning, usd))
        {
            return Invalid();
        }

        if (input < current.InputTokens || output < current.OutputTokens || cached < current.CachedTokens ||
            reasoning < current.ReasoningTokens || usd < current.Usd)
        {
            return new Error(ErrorCodes.Guild.RevisionConflict, "Cumulative spend cannot decrease.");
        }

        return new GuildSpendFacts(input, output, cached, reasoning, usd, current.UnknownUsd, current.UnknownTokens, current.Complete);
    }

    private static bool ValidNumbers(long input, long output, long cached, long reasoning, decimal usd)
    {
        if (input < 0 || output < 0 || cached < 0 || reasoning < 0 || usd < 0 || cached > input || reasoning > output)
        {
            return false;
        }

        try
        {
            _ = checked(input + output);

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static Error Invalid() => new(ErrorCodes.Guild.InvalidRequest, "Complete, valid cumulative spend facts and correction details are required.");
}
