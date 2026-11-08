using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildBudgetHint(GuildUsdBudget? Usd = null, GuildTokenBudget? Tokens = null);

/// <summary>Pure resolution of optional proposals against explicit remaining authority and operator policy.</summary>
internal static class GuildBudgetAllocationPolicy
{
    internal static Result<GuildBudgetGrant> ResolveHint(GuildBudgetHint? hint, GuildBudgetGrant remaining, GuildBudgetGrant policy)
    {
        if (!Valid(remaining) || !Valid(policy)) return Invalid;

        GuildBudgetGrant requested = new(hint?.Usd ?? remaining.Usd, hint?.Tokens ?? remaining.Tokens);

        if (!Valid(requested)) return Invalid;

        return Minimum(Minimum(requested, remaining), policy);
    }

    internal static Result<GuildBudgetGrant> ResolveStrict(GuildBudgetGrant requested, GuildBudgetGrant remaining, GuildBudgetGrant policy)
    {
        if (!Valid(requested) || !Valid(remaining) || !Valid(policy)) return Invalid;

        return Minimum(Minimum(requested, remaining), policy) == requested ? requested
            : new Error(ErrorCodes.Guild.GrantExceeded, "The explicit allocation exceeds remaining authority or operator policy.");
    }

    private static bool Valid(GuildBudgetGrant? grant) => grant is not null
        && GuildGrantPolicy.Validate(new([], [], [], grant, [], null)).IsSuccess;

    private static GuildBudgetGrant Minimum(GuildBudgetGrant left, GuildBudgetGrant right) => new(
        left.Usd.Mode == GuildBudgetMode.Unlimited ? right.Usd
            : right.Usd.Mode == GuildBudgetMode.Unlimited || left.Usd.Value <= right.Usd.Value ? left.Usd : right.Usd,
        left.Tokens.Mode == GuildBudgetMode.Unlimited ? right.Tokens
            : right.Tokens.Mode == GuildBudgetMode.Unlimited || left.Tokens.Value <= right.Tokens.Value ? left.Tokens : right.Tokens);

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "Budget dimensions must be explicit, nonnegative and representable.");
}
