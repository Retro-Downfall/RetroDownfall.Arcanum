using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed class GuildLocalExecutionAdmission(IOptionsMonitor<ArcanumSettings> settings) : IGuildLocalExecutionAdmission
{
    internal static bool IsAvailable(ArcanumSettings current) => TryResolve(current, out _, out _);

    public Task<Result<GuildLocalExecutionInput>> ResolveToolWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
        SqliteConnection connection, SqliteTransaction transaction, GuildChildToolAdmission admission, CancellationToken cancellationToken)
    {
        if (!settings.CurrentValue.Features.Guild || !settings.CurrentValue.Features.Apprentices
            || !admission.Registry.Owns(admission) || !ReferenceEquals(admission.Registry.Parent.Driver.Caller, caller)
            || quest.Quest.ParentQuestId != admission.Registry.Parent.Driver.Execution.QuestId)
            return Task.FromResult(Result<GuildLocalExecutionInput>.Failure(new(ErrorCodes.Guild.AuthorityUnavailable,
                "The child execution does not belong to this live tool admission.")));

        GuildLocalExecutionInput inherited = admission.Registry.AcceptedInput;

        return Task.FromResult(Result<GuildLocalExecutionInput>.Success(new(1,
            admission.Kind == GuildLocalChildToolKind.DelegateTask ? GuildLocalExecutionMode.Sterile : GuildLocalExecutionMode.Apprentice,
            quest.Quest.GoalRef, inherited.Provider, inherited.Model, [], inherited.MaxOutputTokens, inherited.DelegationChain)));
    }

    public async Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        ArcanumSettings current = settings.CurrentValue;

        if (!TryResolve(current, out ProviderSettings? provider, out string model))
            return new Error(ErrorCodes.Guild.Unavailable,
                "A configured local HTTP inference provider and enabled Apprentice execution are required.");

        GuildLocalExecutionInput input = new(1, GuildLocalExecutionMode.Apprentice,
            quest.Quest.GoalRef, provider!.Name, model, [], null);

        if (caller.Execution is { } parent)
        {
            var inherited = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction,
                parent.QuestId, null, cancellationToken).ConfigureAwait(false);

            if (inherited.IsFailure) return inherited.Error;

            input = input with { DelegationChain = inherited.Value.DelegationChain };
        }

        return input;
    }

    private static bool TryResolve(ArcanumSettings current, out ProviderSettings? provider, out string model)
    {
        provider = null;

        model = string.Empty;

        return current.Features.Guild && current.Features.Apprentices
            && ProviderResolver.TryResolveProviderForModel(current, null, out provider, out model)
            && provider is { Type: AiProviderKind.OpenAICompatible }
            && !string.IsNullOrWhiteSpace(provider.Name) && !string.IsNullOrWhiteSpace(model)
            && Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint)
            && endpoint.Scheme is "https" or "http";
    }
}
