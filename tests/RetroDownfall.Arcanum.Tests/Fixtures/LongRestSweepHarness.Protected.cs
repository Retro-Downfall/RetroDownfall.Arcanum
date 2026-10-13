using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal sealed partial class LongRestSweepHarness
{
    internal LongRestMaintenanceCapability? Capability { get; private set; }

    internal async Task<CovenantDigest> ProtectAsync(Guid memory, GenerationProvenance provenance)
    {
        await using SqliteTransaction transaction = Connection.BeginTransaction();

        await using SqliteCommand command = Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT Content, SessionId, CampaignId FROM saga_memories WHERE Id = $memory;";

        command.Parameters.AddWithValue("$memory", memory.ToString("D"));

        string content;

        Guid? session;

        Guid? campaign;

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());

            content = reader.GetString(0);

            session = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));

            campaign = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
        }

        Require(await ArtifactSensitivityLedger.WriteWithinAsync(Connection, transaction, new DerivedArtifactWrite(SensitiveArtifactKind.Saga,
            memory, session, campaign, null, 37, DerivedArtifactContentDigest.ForText(content), ContentSensitivity.CovenantDerived, provenance), CancellationToken.None));

        ArtifactSensitivityLabel label = Require(await ArtifactSensitivityLedger.ReadLabelWithinAsync(Connection, transaction, SensitiveArtifactKind.Saga, memory, CancellationToken.None))!;

        await transaction.CommitAsync();

        return label.SensitivityDigest;
    }

    internal static async Task<LongRestMaintenanceCapability> CreateCapabilityAsync(LongRestDiscoveryGeneration generation, CovenantTurnLease authority)
    {
        LongRestConfigurationStamp configuration = generation.Fence.Configuration;

        CanonicalCampaignContext canonical = generation.Fence.CanonicalScope;

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn,
            canonical, InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools,
            CovenantReadAuthorityEpoch.CreateForTests(Guid.NewGuid(), authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.AuthorityEpoch)).Value;

        SessionTurnClaim claim = new(Guid.NewGuid(), Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid(), SessionTurnSurface.Intelligence,
            configuration.ValueDigest, configuration.ValueDigest, SessionTurnClaimState.PendingMaintenance, null, 18, 0, 0,
            Guid.NewGuid(), null, null, configuration.BootId, 0, 0, null, DateTimeOffset.UtcNow);

        SessionTurnClaimLease lease = new(claim, SessionTurnClaimDisposition.Created, Guid.NewGuid(), Guid.NewGuid(), configuration.BootId, DateTimeOffset.UtcNow.AddMinutes(5));

        LongRestMaintenanceCapability capability = Require(await LongRestMaintenanceCapability.CreateAsync(generation.OperationId, invocation, lease,
            canonical, authority, configuration, CancellationToken.None));

        Assert.True((await capability.ConsumeAsync(generation.OperationId, generation.Scope, configuration, CancellationToken.None)).IsSuccess);

        return capability;
    }

    internal async Task<Guid> SessionAsync(Guid? campaign = null, int binding = 1)
    {
        Guid session = Guid.NewGuid();

        const string at = "2026-10-10T00:00:00.0000000Z";

        if (campaign is not null)
        {
            await ExecuteAsync("""
                INSERT OR IGNORE INTO Campaigns (Id, Name, NameLower, Path, Type, Settings, CreatedAt, UpdatedAt)
                VALUES ($id, $id, $id, $path, 0, '{}', $at, $at);
                """, ("$id", campaign.Value.ToString("D").ToUpperInvariant()), ("$path", "/campaigns/" + campaign.Value.ToString("N")), ("$at", at));
        }

        await ExecuteAsync("INSERT INTO Sessions (Id, CampaignId, Status, CreatedAt, UpdatedAt) VALUES ($id, $campaign, 'active', $at, $at);",
            ("$id", session.ToString("D").ToUpperInvariant()), ("$campaign", campaign?.ToString("D").ToUpperInvariant()), ("$at", at));

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync("INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc) VALUES ($id, $kind, $campaign, $at);",
            ("$id", session.ToString("D").ToUpperInvariant()), ("$kind", campaign is null ? binding : 2), ("$campaign", campaign?.ToString("D").ToUpperInvariant()), ("$at", at));

        return session;
    }
}
