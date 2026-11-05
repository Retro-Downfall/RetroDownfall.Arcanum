using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Intersection removes authority. Scope containment is exclusively host-produced evidence.</summary>
internal static class GuildSignetGrantComposition
{
    internal static GuildGrant Intersect(GuildGrant left, GuildGrant right, GuildGrantScopeEvidence evidence)
    {
        ImmutableArray<GuildWorkspaceGrant>.Builder workspaces = ImmutableArray.CreateBuilder<GuildWorkspaceGrant>();

        foreach (GuildWorkspaceGrant a in left.Workspaces)
        {
            foreach (GuildWorkspaceGrant b in right.Workspaces)
            {
                GuildWorkspaceAccess access = a.Access == GuildWorkspaceAccess.Read && b.Access == GuildWorkspaceAccess.Read
                    || a.Access != b.Access ? GuildWorkspaceAccess.Read : GuildWorkspaceAccess.ReadWrite;

                if (a.WorkspaceId == b.WorkspaceId && a.RelativeRoot == b.RelativeRoot || evidence.WorkspaceContainment.Contains((a, b)))
                {
                    workspaces.Add(b with { Access = access });
                }
                else if (evidence.WorkspaceContainment.Contains((b, a)))
                {
                    workspaces.Add(a with { Access = access });
                }
            }
        }

        ImmutableArray<GuildMemoryGrant>.Builder memory = ImmutableArray.CreateBuilder<GuildMemoryGrant>();

        foreach (GuildMemoryGrant a in left.Memory)
        {
            foreach (GuildMemoryGrant b in right.Memory)
            {
                if (a.Store != b.Store || a.Operation != b.Operation)
                {
                    continue;
                }

                if (Contains(a.Scope, b.Scope, evidence))
                {
                    memory.Add(b);
                }
                else if (Contains(b.Scope, a.Scope, evidence))
                {
                    memory.Add(a);
                }
            }
        }

        GuildUsdBudget usd = left.Budget.Usd.Mode == GuildBudgetMode.Unlimited ? right.Budget.Usd
            : right.Budget.Usd.Mode == GuildBudgetMode.Unlimited || left.Budget.Usd.Value <= right.Budget.Usd.Value
                ? left.Budget.Usd : right.Budget.Usd;

        GuildTokenBudget tokens = left.Budget.Tokens.Mode == GuildBudgetMode.Unlimited ? right.Budget.Tokens
            : right.Budget.Tokens.Mode == GuildBudgetMode.Unlimited || left.Budget.Tokens.Value <= right.Budget.Tokens.Value
                ? left.Budget.Tokens : right.Budget.Tokens;

        DateTimeOffset? expiry = left.ExpiresAtUtc is null ? right.ExpiresAtUtc
            : right.ExpiresAtUtc is null || left.ExpiresAtUtc <= right.ExpiresAtUtc ? left.ExpiresAtUtc : right.ExpiresAtUtc;

        return new(workspaces.Distinct().ToImmutableArray(), left.Tools.Intersect(right.Tools).ToImmutableArray(),
            memory.Distinct().ToImmutableArray(), new(usd, tokens), left.Delegation.Intersect(right.Delegation).ToImmutableArray(), expiry);
    }

    private static bool Contains(GuildMemoryScope parent, GuildMemoryScope child, GuildGrantScopeEvidence evidence) =>
        parent == child || parent.Kind == GuildMemoryScopeKind.Campaign && child.Kind == GuildMemoryScopeKind.Session
        && evidence.SessionOwnership.Contains((parent.Id!.Value, child.Id!.Value));
}
