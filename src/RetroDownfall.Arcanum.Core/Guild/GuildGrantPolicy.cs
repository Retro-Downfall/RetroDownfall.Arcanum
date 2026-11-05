using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>
/// Host-produced containment facts for pure comparisons. No filesystem discovery or ownership lookup
/// occurs here; the issuing host must resolve these facts and revalidate at the actual effect boundary.
/// </summary>
[JsonConverter(typeof(GuildHostHandleJsonConverter<GuildGrantScopeEvidence>))]
public sealed class GuildGrantScopeEvidence
{
    internal GuildGrantScopeEvidence(ImmutableArray<(GuildWorkspaceGrant Parent, GuildWorkspaceGrant Child)> workspaceContainment,
        ImmutableArray<(Guid CampaignId, Guid SessionId)> sessionOwnership)
    {
        if (workspaceContainment.IsDefault || sessionOwnership.IsDefault)
        {
            throw new ArgumentException("Scope evidence must be explicitly initialized.");
        }

        WorkspaceContainment = workspaceContainment;

        SessionOwnership = sessionOwnership;
    }

    internal ImmutableArray<(GuildWorkspaceGrant Parent, GuildWorkspaceGrant Child)> WorkspaceContainment { get; }

    internal ImmutableArray<(Guid CampaignId, Guid SessionId)> SessionOwnership { get; }
}

/// <summary>Pure grant-data validation and non-escalation; never authentication or live effect admission.</summary>
public static class GuildGrantPolicy
{
    /// <summary>Validates the closed data algebra, not resource ownership, availability or live authority.</summary>
    public static Result Validate(GuildGrant grant)
    {
        if (grant is null || grant.Workspaces.IsDefault || grant.Tools.IsDefault || grant.Memory.IsDefault
            || grant.Delegation.IsDefault || grant.Budget is null)
        {
            return Invalid("Every resolved Guild grant axis must be explicitly supplied.");
        }

        if (grant.Workspaces.Any(item => item is null || item.WorkspaceId == Guid.Empty || !Enum.IsDefined(item.Access)
            || !IsRelativeRoot(item.RelativeRoot)))
        {
            return Invalid("A workspace grant requires a canonical relative root and explicit access.");
        }

        if (grant.Tools.Any(item => item is null || !Enum.IsDefined(item.SourceKind)
            || !IsExactName(item.SourceRegistrationId) || !IsExactName(item.Name)
            || (item.SourceKind is GuildToolSourceKind.BuiltIn or GuildToolSourceKind.InternalMcp
                && item.SourceRegistrationId != "arcanum")))
        {
            return Invalid("A tool grant requires an exact registered capability identity.");
        }

        if (grant.Memory.Any(item => !IsMemoryGrant(item)))
        {
            return Invalid("A memory grant contains an unsupported store, operation or scope.");
        }

        if (!IsBudget(grant.Budget))
        {
            return Invalid("Both budget dimensions must be explicitly unlimited or finite nonnegative values.");
        }

        if (grant.Delegation.Any(item => !Enum.IsDefined(item)))
        {
            return Invalid("A delegation grant contains an unknown right.");
        }

        if (grant.ExpiresAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            return Invalid("A grant expiry must be an absolute UTC instant.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Compares requested axes against an already resolved effective parent. Scope facts must have
    /// been verified by the host; this does not inspect paths, authenticate a caller, allocate budget,
    /// check a clock or turn the returned data into an executable capability.
    /// </summary>
    public static Result<GuildGrant> Attenuate(GuildGrant effectiveParent, GuildRequestedGrant request, GuildGrantScopeEvidence evidence)
    {
        Result validParent = Validate(effectiveParent);

        if (validParent.IsFailure)
        {
            return Result<GuildGrant>.Failure(validParent.Error);
        }

        if (request is null || evidence is null
            || request.Workspaces.IsSpecified && request.Workspaces.Value is null
            || request.Tools.IsSpecified && request.Tools.Value is null
            || request.Memory.IsSpecified && request.Memory.Value is null
            || request.Budget.IsSpecified && request.Budget.Value is null
            || request.Delegation.IsSpecified && request.Delegation.Value is null)
        {
            return Result<GuildGrant>.Failure(Invalid("A supplied child grant axis cannot be null, except expiry.").Error);
        }

        GuildGrant child = new(
            request.Workspaces.IsSpecified ? request.Workspaces.Value!.Value : effectiveParent.Workspaces,
            request.Tools.IsSpecified ? request.Tools.Value!.Value : effectiveParent.Tools,
            request.Memory.IsSpecified ? request.Memory.Value!.Value : effectiveParent.Memory,
            request.Budget.IsSpecified ? request.Budget.Value! : effectiveParent.Budget,
            request.Delegation.IsSpecified ? request.Delegation.Value!.Value : effectiveParent.Delegation,
            request.ExpiresAtUtc.IsSpecified ? request.ExpiresAtUtc.Value : effectiveParent.ExpiresAtUtc);

        Result validChild = Validate(child);

        if (validChild.IsFailure)
        {
            return Result<GuildGrant>.Failure(validChild.Error);
        }

        bool workspaceSubset = !request.Workspaces.IsSpecified || child.Workspaces.All(item =>
            effectiveParent.Workspaces.Any(parent =>
                (parent.Access == GuildWorkspaceAccess.ReadWrite || item.Access == GuildWorkspaceAccess.Read)
                && evidence.WorkspaceContainment.Contains((parent, item))));

        bool toolSubset = child.Tools.All(effectiveParent.Tools.Contains);

        bool memorySubset = child.Memory.All(item => effectiveParent.Memory.Any(parent =>
            parent.Store == item.Store && parent.Operation == item.Operation && ScopeContains(parent.Scope, item.Scope, evidence)));

        bool budgetSubset = UsdWithin(child.Budget.Usd, effectiveParent.Budget.Usd)
            && TokensWithin(child.Budget.Tokens, effectiveParent.Budget.Tokens);

        bool delegationSubset = child.Delegation.All(effectiveParent.Delegation.Contains);

        bool expirySubset = effectiveParent.ExpiresAtUtc is null
            || child.ExpiresAtUtc is { } expiry && expiry <= effectiveParent.ExpiresAtUtc.Value;

        if (!workspaceSubset || !toolSubset || !memorySubset || !budgetSubset || !delegationSubset || !expirySubset)
        {
            return Result<GuildGrant>.Failure(new Error(ErrorCodes.Guild.GrantWidening,
                "The requested grant is not a verified subset of its effective issuer grant."));
        }

        return Result<GuildGrant>.Success(child);
    }

    private static bool IsRelativeRoot(string root)
    {
        if (root is null || root.Contains('\\') || root.Contains(':') || root.Contains('*') || root.Contains('?') || root.Any(char.IsControl))
        {
            return false;
        }

        return root is "" or "." || root.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool IsExactName(string value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && !value.Contains('*') && !value.Contains('?') && !value.Any(char.IsControl);

    private static bool IsMemoryGrant(GuildMemoryGrant item)
    {
        if (item is null || !Enum.IsDefined(item.Store) || !Enum.IsDefined(item.Operation) || item.Scope is null
            || !Enum.IsDefined(item.Scope.Kind))
        {
            return false;
        }

        if (item.Scope.Kind == GuildMemoryScopeKind.Global ? item.Scope.Id is not null : item.Scope.Id is null || item.Scope.Id == Guid.Empty)
        {
            return false;
        }

        if (item.Scope.Kind == GuildMemoryScopeKind.Session && item.Store is GuildMemoryStore.Saga or GuildMemoryStore.Lexicon)
        {
            return false;
        }

        return item.Store switch
        {
            GuildMemoryStore.Transcript or GuildMemoryStore.Attachment or GuildMemoryStore.Tapestry => item.Operation == GuildMemoryOperation.Read,
            GuildMemoryStore.Saga => item.Operation is GuildMemoryOperation.Read or GuildMemoryOperation.Append,
            GuildMemoryStore.Lexicon => true,
            _ => false,
        };
    }

    private static bool IsBudget(GuildBudgetGrant budget) => budget.Usd is not null && budget.Tokens is not null
        && (budget.Usd.Mode switch
        {
            GuildBudgetMode.Unlimited => budget.Usd.Value is null,
            GuildBudgetMode.Finite => budget.Usd.Value is >= 0,
            _ => false,
        })
        && (budget.Tokens.Mode switch
        {
            GuildBudgetMode.Unlimited => budget.Tokens.Value is null,
            GuildBudgetMode.Finite => budget.Tokens.Value is >= 0,
            _ => false,
        });

    private static bool ScopeContains(GuildMemoryScope parent, GuildMemoryScope child, GuildGrantScopeEvidence evidence) =>
        parent == child || parent.Kind == GuildMemoryScopeKind.Campaign && child.Kind == GuildMemoryScopeKind.Session
        && evidence.SessionOwnership.Contains((parent.Id!.Value, child.Id!.Value));

    private static bool UsdWithin(GuildUsdBudget child, GuildUsdBudget parent) => parent.Mode == GuildBudgetMode.Unlimited
        || child.Mode == GuildBudgetMode.Finite && child.Value <= parent.Value;

    private static bool TokensWithin(GuildTokenBudget child, GuildTokenBudget parent) => parent.Mode == GuildBudgetMode.Unlimited
        || child.Mode == GuildBudgetMode.Finite && child.Value <= parent.Value;

    private static Result Invalid(string message) => Result.Failure(new Error(ErrorCodes.Guild.InvalidRequest, message));
}
