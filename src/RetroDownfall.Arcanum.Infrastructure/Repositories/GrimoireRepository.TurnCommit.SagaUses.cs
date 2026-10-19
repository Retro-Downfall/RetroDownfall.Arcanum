using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed partial class GrimoireRepository
{
    private static async Task PublishSagaUsesAsync(SqliteConnection connection, SqliteTransaction transaction, TurnCommitRequest request, CancellationToken ct)
    {
        if (request.SagaUses.IsEmpty || request.Outcome != AssistantFinalizationOutcome.Committed || request.ClaimInterruption is not null
            || await GrimoireCoreSchemaVersion.ReadAsync(connection, ct, transaction).ConfigureAwait(false) < 20) return;

        bool suppression = await LongRestRecallSql.AvailableAsync(connection, ct, transaction).ConfigureAwait(false);

        foreach (SagaUseCoordinate coordinate in request.SagaUses.Distinct())
        {
            if (coordinate is null || coordinate.HashFormat != 1 || !coordinate.ContentDigest.IsValid
                || !Opaque(coordinate.MemoryId) || !Opaque(coordinate.ClaimId) || !Opaque(coordinate.VersionId)) continue;

            await using SqliteCommand source = connection.CreateCommand();

            source.Transaction = transaction;

            source.CommandText = $"""
                SELECT memory.Content FROM saga_memories memory
                JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
                JOIN annal_heads head ON head.ClaimId = claim.ClaimId AND head.SubjectStoreCode = 1
                JOIN annal_versions version ON version.ClaimId = claim.ClaimId AND version.VersionId = head.CurrentVersionId
                WHERE memory.Id = $memory AND claim.ClaimId = $claim AND version.VersionId = $version
                    AND version.Revision = head.CurrentRevision AND version.OperationCode = head.CurrentOperationCode
                    AND version.OperationCode <> 3 AND version.SensitivityCode = 0
                    AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                        AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', '')))
                    AND version.ContentHashFormatCode = $format AND version.ContentHash = $digest
                    AND version.ScopeKindCode = memory.ScopeKindCode AND lower(replace(version.CampaignId, '-', '')) IS lower(replace(memory.CampaignId, '-', ''))
                    AND version.ValidFromUtc <= $now AND version.RecordedAtUtc <= $now
                    AND (version.ValidToUtc IS NULL OR version.ValidToUtc > $now)
                    AND memory.RetiredAtUtc IS NULL AND {LongRestRecallSql.Eligible("memory", suppression)}
                    AND (memory.SessionId IS NULL AND memory.ScopeKindCode = 1
                        OR EXISTS (SELECT 1 FROM Sessions owner JOIN session_campaign_bindings owner_binding ON owner_binding.SessionId = owner.Id
                            WHERE lower(replace(owner.Id, '-', '')) = lower(replace(memory.SessionId, '-', ''))
                                AND owner_binding.BindingKindCode = memory.ScopeKindCode
                                AND lower(replace(owner_binding.CampaignId, '-', '')) IS lower(replace(memory.CampaignId, '-', ''))))
                    AND (version.SourceSessionId IS NULL OR EXISTS (SELECT 1 FROM Sessions author
                        WHERE lower(replace(author.Id, '-', '')) = lower(replace(version.SourceSessionId, '-', ''))))
                    AND (memory.ScopeKindCode = 1 AND memory.CampaignId IS NULL
                        OR memory.ScopeKindCode = 2 AND EXISTS (SELECT 1 FROM session_campaign_bindings binding
                            WHERE binding.SessionId = $session AND binding.BindingKindCode = 2 AND lower(replace(binding.CampaignId, '-', '')) = lower(replace(memory.CampaignId, '-', ''))))
                """;

            source.Parameters.AddWithValue("$memory", coordinate.MemoryId);

            source.Parameters.AddWithValue("$claim", coordinate.ClaimId);

            source.Parameters.AddWithValue("$version", coordinate.VersionId);

            source.Parameters.AddWithValue("$format", coordinate.HashFormat);

            source.Parameters.AddWithValue("$digest", coordinate.ContentDigest.Bytes);

            source.Parameters.AddWithValue("$session", request.SessionId.ToString("D").ToUpperInvariant());

            UtcInstantSql.AddParameter(source, "$now", DateTimeOffset.UtcNow);

            if (await source.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string content
                || new CovenantDigest(AnnalContentDigest.ForSagaMemory(content)) != coordinate.ContentDigest) continue;

            await using SqliteCommand insert = connection.CreateCommand();

            insert.Transaction = transaction;

            insert.CommandText = """
                INSERT OR IGNORE INTO saga_committed_uses(AssistantEntryId,MemoryId,ClaimId,VersionId,HashFormat,ContentDigest)
                VALUES($assistant,$memory,$claim,$version,$format,$digest)
                """;

            insert.Parameters.AddWithValue("$assistant", request.AssistantEntryId.ToString("D").ToUpperInvariant());

            insert.Parameters.AddWithValue("$memory", coordinate.MemoryId);

            insert.Parameters.AddWithValue("$claim", coordinate.ClaimId);

            insert.Parameters.AddWithValue("$version", coordinate.VersionId);

            insert.Parameters.AddWithValue("$format", coordinate.HashFormat);

            insert.Parameters.AddWithValue("$digest", coordinate.ContentDigest.Bytes);

            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool Opaque(string? value) => value is { Length: > 0 and <= 1024 }
        && System.Text.Encoding.UTF8.GetByteCount(value) <= 1024 && !value.Any(char.IsControl);
}
