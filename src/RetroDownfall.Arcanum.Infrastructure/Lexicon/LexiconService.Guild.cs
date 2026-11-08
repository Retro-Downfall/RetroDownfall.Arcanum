using System.Data.Common;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    public Task<Result<Guid?>> FindAllLifecycleIdentityForDeletionAsync(string name, LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Delete,
            ct => FindAllLifecycleIdentityForDeletionCoreAsync(name, scope, ct), cancellationToken);

    public Task<Result<LexiconAgentDeletionTarget?>> FindAgentDeletionTargetAsync(string name, LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Delete,
            ct => FindAgentDeletionTargetCoreAsync(name, scope, ct), cancellationToken);

    public Task<Result<bool>> DeleteByNameAsync(string name, LexiconScope scope, LexiconDeletionOrigin origin,
        CancellationToken cancellationToken = default) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Delete,
            ct => DeleteByNameCoreAsync(name, scope, origin, ct), cancellationToken);

    public Task<Result<bool>> HasEligibleEntriesAsync(LexiconScope scope, CancellationToken cancellationToken = default) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read,
            ct => HasEligibleEntriesCoreAsync(scope, ct), cancellationToken);

    public Task<Result<LexiconEntryDto?>> GetByNameAsync(string name, LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read,
            ct => GetByNameCoreAsync(name, scope, ct), cancellationToken);

    public Task<Result<LexiconEntryDto?>> GetByNameInScopeAsync(string name, LexiconScope scope,
        CancellationToken cancellationToken = default) =>
        GuildMemoryAccess.RunResultAsync(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read,
            ct => GetByNameInScopeCoreAsync(name, scope, ct), cancellationToken);

    private static LexiconEntryDto GuildWriteAcknowledgement(Guid id, LexiconCanonicalValue submitted,
        LexiconScope scope, DateTimeOffset updatedAt) =>
        new(id, submitted.Name, submitted.Type, submitted.Facts.ToArray(), updatedAt, ScopeCampaignId: scope.CampaignId);

    private static async Task DemandCleanGuildMutationAsync(DbConnection connection, string normalized,
        string scopeKey, CancellationToken cancellationToken)
    {
        if (!GuildMemoryAccess.IsGuildInvocation)
        {
            return;
        }

        // Refuse protected or unresolved history before loading existing content, including no-op writes.
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT 1 FROM lexicon_entries
            WHERE NameNormalized = @normalized AND ScopeCampaignId = @scopeKey
                AND NOT (1=1 {CleanRecallPredicate("lexicon_entries")}) LIMIT 1
            """;

        AddParameter(command, "@normalized", normalized);

        AddParameter(command, "@scopeKey", scopeKey);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new GuildMemoryAccessException(new(ErrorCodes.Lexicon.ProtectedMutationRefused,
                "Guild mutation requires a clean Lexicon target."));
        }
    }

    private static Error? GuildOperatorRefusal()
    {
        try
        {
            GuildMemoryAccess.DemandOperator();

            return null;
        }
        catch (GuildMemoryAccessException exception)
        {
            return exception.Error;
        }
    }
}
