using System.Data.Common;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

internal sealed partial class TapestryStore
{
    private const string NonblankSource = "s.Content IS NOT NULL AND trim(s.Content, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) <> ''";

    public Task<TapestryBuildSnapshot> ReadBuildSnapshotAsync(TapestryScope scope, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            TapestryBuildSnapshot snapshot = await ReadBuildSnapshotAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return snapshot;
        }, cancellationToken);

    private static async Task<(long Corpus, long Invalidation)> ReadRevisionsAsync(DbConnection connection, DbTransaction transaction, TapestryScope scope, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT CorpusRevision,InvalidationRevision FROM tapestry_scope_revisions WHERE ScopeKind=@kind AND ScopeId=@scope";

        AddParameter(command, "@kind", scope.Kind.ToString());

        AddParameter(command, "@scope", scope.Id);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    private static async Task<TapestryBuildSnapshot> ReadBuildSnapshotAsync(DbConnection connection, DbTransaction transaction, TapestryScope scope, CancellationToken cancellationToken)
    {
        (long corpusRevision, long invalidationRevision) = await ReadRevisionsAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);

        LeafCorpus corpus = LeafCorpus.For(scope.Kind);

        List<(string SourceId, string ContentHash)> identities = [];

        string? after = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            string continuation = after is null ? string.Empty : $"AND s.{corpus.IdColumn} > @after COLLATE BINARY";

            command.CommandText = $"""
                SELECT s.{corpus.IdColumn},s.Content FROM {corpus.Table} s
                WHERE {corpus.ScopePredicate} AND {NonblankSource}
                    {continuation}
                ORDER BY s.{corpus.IdColumn} COLLATE BINARY LIMIT @limit
                """;

            AddParameter(command, "@scopeId", scope.Id);

            AddParameter(command, "@after", (object?)after ?? DBNull.Value);

            AddParameter(command, "@limit", Math.Min(LeafPageSize, TapestryLimits.MaxLeavesPerScope + 1 - identities.Count));

            int read = 0;

            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    after = reader.GetString(0);

                    identities.Add((after, TapestryHash.OfContent(reader.GetString(1))));

                    read++;
                }
            }

            if (identities.Count > TapestryLimits.MaxLeavesPerScope)
            {
                return new(scope, corpusRevision, invalidationRevision, new(identities.Count, string.Empty, true));
            }

            if (read < LeafPageSize)
            {
                return new(scope, corpusRevision, invalidationRevision, new(identities.Count, TapestryHash.OfCorpus(identities), false));
            }
        }
    }

    public Task<string> BeginGraphGenerationAsync(TapestryBuildSnapshot snapshot, TapestryGenerationRecipe recipe, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            ArgumentNullException.ThrowIfNull(recipe);

            if (snapshot.Corpus.ExceedsCeiling || snapshot.Corpus.LeafCount == 0 || !SupportedAlgorithm(recipe.AlgorithmVersion)
                || recipe.EmbeddingDimension < 1 || string.IsNullOrWhiteSpace(recipe.EmbeddingIdentity))
            {
                throw new ArgumentException("A graph generation requires a supported complete source and embedding identity.");
            }

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (await ReadBuildSnapshotAsync(connection, transaction, snapshot.Scope, cancellationToken).ConfigureAwait(false) != snapshot)
            {
                throw new InvalidOperationException("Tapestry source snapshot changed before staging.");
            }

            string id = Guid.NewGuid().ToString("N");

            await using DbCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO tapestry_generations(GenerationId,ScopeKind,ScopeId,Status,AlgorithmVersion,SettingsFingerprint,SummaryModel,SummaryRecipeVersion,EmbeddingDimension,CorpusFingerprint,StartedAt,SourceRevision,ValidatedInvalidationRevision,EmbeddingIdentity)
                VALUES(@id,@kind,@scope,'Building',@algorithm,@settings,@model,@recipe,@dimension,@corpus,@started,@revision,@invalidation,@embedding)
                """;

            AddParameter(command, "@id", id);

            AddParameter(command, "@kind", snapshot.Scope.Kind.ToString());

            AddParameter(command, "@scope", snapshot.Scope.Id);

            AddParameter(command, "@algorithm", recipe.AlgorithmVersion);

            AddParameter(command, "@settings", recipe.SettingsFingerprint);

            AddParameter(command, "@model", (object?)recipe.SummaryModel ?? DBNull.Value);

            AddParameter(command, "@recipe", recipe.SummaryRecipeVersion);

            AddParameter(command, "@dimension", recipe.EmbeddingDimension);

            AddParameter(command, "@corpus", snapshot.Corpus.Fingerprint);

            AddParameter(command, "@started", Iso(startedAt));

            AddParameter(command, "@revision", snapshot.CorpusRevision);

            AddParameter(command, "@invalidation", snapshot.InvalidationRevision);

            AddParameter(command, "@embedding", recipe.EmbeddingIdentity);

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return id;
        }, cancellationToken);

    private static bool SupportedAlgorithm(string algorithm) => algorithm is RaptorAlgorithmManifest.Version or SphericalKMeans.AlgorithmVersion;

    private static async Task<TapestryGeneration?> ReadGraphGenerationAsync(DbConnection connection, DbTransaction transaction, string generationId, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT {GenerationColumns} FROM tapestry_generations WHERE GenerationId=@id";

        AddParameter(command, "@id", generationId);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGeneration(reader) : null;
    }

    private static async Task<TapestryGeneration?> ReadCurrentGenerationAsync(DbConnection connection, DbTransaction transaction, TapestryScope scope, CancellationToken cancellationToken)
    {
        TapestryGeneration? generation;

        await using (DbCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;

            read.CommandText = $"SELECT {GenerationColumns} FROM tapestry_generations g WHERE ScopeKind=@kind AND ScopeId=@scope AND Status='Complete' AND {DocumentFreeGenerationPredicate} LIMIT 1";

            AddParameter(read, "@kind", scope.Kind.ToString());

            AddParameter(read, "@scope", scope.Id);

            await using DbDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            generation = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGeneration(reader) : null;
        }

        if (generation is null || !SupportedAlgorithm(generation.AlgorithmVersion))
        {
            return null;
        }

        (_, long invalidation) = await ReadRevisionsAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);

        if (generation.ValidatedInvalidationRevision is long validated)
        {
            return validated == invalidation ? generation : null;
        }

        // A pending legacy row has no evidence surviving an edit/revert, even if the bytes match again.
        if (invalidation != 0)
        {
            return null;
        }

        TapestryBuildSnapshot snapshot = await ReadBuildSnapshotAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);

        if (snapshot.Corpus.ExceedsCeiling || snapshot.Corpus.Fingerprint != generation.CorpusFingerprint)
        {
            return null;
        }

        await using DbCommand stamp = connection.CreateCommand();

        stamp.Transaction = transaction;

        stamp.CommandText = "UPDATE tapestry_generations SET ValidatedInvalidationRevision=0 WHERE GenerationId=@id AND Status='Complete' AND ValidatedInvalidationRevision IS NULL";

        AddParameter(stamp, "@id", generation.GenerationId);

        _ = await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return generation with { ValidatedInvalidationRevision = 0 };
    }
}
