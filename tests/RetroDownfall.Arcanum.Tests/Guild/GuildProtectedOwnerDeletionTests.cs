using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildProtectedOwnerDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_shared_purger_erases_protected_content_after_its_owner_is_deleted(bool sessionOwned)
    {
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync("Data Source=:memory:", CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);

        Guid campaignId = Guid.NewGuid();

        Guid contentId = Guid.NewGuid();

        Guid? sessionId = sessionOwned ? Guid.NewGuid() : null;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt)
            VALUES($campaign,'Guild test','guild test','/guild-owner-test',0,'{}','2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z');
            """;

        _ = command.Parameters.AddWithValue("$campaign", campaignId.ToString("D").ToUpperInvariant());

        _ = await command.ExecuteNonQueryAsync();

        if (sessionId is { } session)
        {
            command.CommandText = """
                INSERT INTO Sessions(Id,CampaignId,Title,CreatedAt,UpdatedAt)
                VALUES($session,$campaign,'Guild owner test','2026-11-05T00:00:00.0000000Z','2026-11-05T00:00:00.0000000Z');
                """;

            _ = command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();
        }

        byte[] body = Encoding.UTF8.GetBytes("protected owner goal");

        DerivedArtifactWrite sensitivity = new(SensitiveArtifactKind.GuildContent, contentId, sessionId, campaignId,
            null, 1, new CovenantDigest(SHA256.HashData(body)), ContentSensitivity.CovenantDerived,
            GenerationProvenance.Create([CovenantOperationGateFixture.DatasetGeneration]));

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result<GuildDataRef> written = await GuildStore.WriteContentWithinAsync(connection, transaction,
                new GuildContentWrite(contentId, Guid.NewGuid(), GuildContentKind.QuestGoal, 1, sessionId, campaignId,
                    "text/plain", body, sensitivity), DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.True(written.IsSuccess, written.Error.Message);

            await transaction.CommitAsync();
        }

        command.CommandText = sessionOwned ? "DELETE FROM Sessions WHERE Id=$session;" : "DELETE FROM Campaigns WHERE Id=$campaign;";

        using (CovenantSqliteAuthorizationScope? deletion = sessionOwned
            ? CovenantSqliteConnectionInitializer.Instance.Authorize(connection, CovenantSqliteAuthorizationKind.SessionRetention)
            : null)
        {
            _ = await command.ExecuteNonQueryAsync();
        }

        command.CommandText = sessionOwned
            ? "SELECT COUNT(*) FROM owner_deletion_events WHERE OwnerKindCode=2 AND OwnerId=$session"
            : "SELECT COUNT(*) FROM owner_deletion_events WHERE OwnerKindCode=1 AND OwnerId=$campaign";

        Assert.Equal(1L, await command.ExecuteScalarAsync());

        FixedCovenantConnectionSource source = new(connection);

        FakeCovenantAvailability availability = new();

        FakeCovenantAuthorityProvider authority = new();

        CovenantSensitivePurgeAuthorityScope scope = new();

        Assert.True(scope.Publish(CovenantErasureAuthorityFixture.OperatorContext(authority)).IsSuccess);

        CovenantSensitiveRetentionPurgeCoordinator purger = new(new ArtifactSensitivityLedger(source), source,
            new CovenantManagedFileErasureRequestReader(), CovenantOperationGateFixture.CreateGate(availability, authority),
            CovenantErasureAuthorityFixture.Issuer(authority), availability,
            new CovenantProtectedArtifactErasureKernel(source, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System),
            new NoManagedFiles(), scope, NullLogger<CovenantSensitiveRetentionPurgeCoordinator>.Instance);

        Result<CovenantSensitivePurgeOutcome> result = await purger.PurgeAsync(
            [new CovenantSensitivePurgeTarget(SensitiveArtifactKind.GuildContent, contentId)]);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.WasPurged(contentId));

        command.CommandText = "SELECT COUNT(*) FROM guild_content";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode=17";

        Assert.Equal(0L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT COUNT(*) FROM guild_content_erasure_receipts WHERE CampaignId=$campaign";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private sealed class NoManagedFiles : ICovenantManagedFileErasureKernel
    {
        public ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(CovenantManagedFileErasureRequest request,
            CovenantArtifactErasureAuthority authority, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Database Guild content cannot route to file erasure.");
    }
}
