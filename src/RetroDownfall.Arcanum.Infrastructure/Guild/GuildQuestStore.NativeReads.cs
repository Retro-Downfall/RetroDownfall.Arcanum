using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildLocalResultObservation(GuildQuestDto Quest, GuildResultReceipt? Receipt, string? Body);

internal sealed partial class GuildQuestStore
{
    /// <summary>Reads committed result evidence under the exact current caller and root authority.</summary>
    internal async Task<Result<GuildLocalResultObservation>> ReadLocalResultAsync(GuildCallerContext caller,
        Guid questId, bool materialize, CancellationToken ct)
    {
        if (caller is null || questId == Guid.Empty) return OwnerUnavailable;

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildLocalResultObservation>(located.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;
                }

                Result<GuildResultReceipt?> result = await ReadResultWithinAsync(connection, transaction, found.Value, token).ConfigureAwait(false);

                if (result.IsFailure) return result.Error;

                string? body = null;

                if (materialize && result.Value?.Content is { } content)
                {
                    if (content.ContentErased)
                        return new Error(ErrorCodes.Guild.ContentErased, "The committed Quest result body was erased.");

                    Result<string> disclosed = await ReadProviderContentWithinAsync(connection, transaction, content, token).ConfigureAwait(false);

                    if (disclosed.IsFailure) return disclosed.Error;

                    body = disclosed.Value;
                }

                return new GuildLocalResultObservation(found.Value.Quest, result.Value, body);
            }, ct).ConfigureAwait(false);
    }
}
