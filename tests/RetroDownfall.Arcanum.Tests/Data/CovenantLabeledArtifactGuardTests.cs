using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Issue #117 — the check a legacy raw delete makes before it removes a row that might be labelled.
/// </summary>
/// <remarks>
/// The six routes already dispatch through the purge boundary, so in normal operation this guard never
/// fires. It exists for the caller that does not: a repository method is reachable from anywhere in the
/// process, and "every caller remembers to ask the purger first" is a convention rather than a property.
///
/// <para>The two arms answer different questions on purpose. A single delete can name the artifact it is
/// about; a set-based <c>DELETE FROM</c> examines no identity at all, so the only honest question there
/// is whether the kind has any protected member left (§10.20.2).</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class CovenantLabeledArtifactGuardTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public CovenantLabeledArtifactGuardTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]

    public async Task An_unlabeled_artifact_passes_the_guard_untouched()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactGuard guard = CreateGuard();

        Result unlabeled = await guard.EnsureUnlabeledAsync(
            SensitiveArtifactKind.Saga,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(unlabeled.IsSuccess);

        Result none = await guard.EnsureNoneLabeledAsync(
            SensitiveArtifactKind.Saga,
            CancellationToken.None);

        Assert.True(none.IsSuccess);

    }

    [SkippableTheory]

    [InlineData(SensitiveArtifactKind.Saga)]

    [InlineData(SensitiveArtifactKind.Lexicon)]

    [InlineData(SensitiveArtifactKind.AssistantEntry)]

    public async Task A_labeled_artifact_is_refused_outside_the_purge_boundary(SensitiveArtifactKind kind)
    {

        RequireSqlCipher();

        Guid artifactId = Guid.NewGuid();

        await SeedLabelAsync(kind, artifactId, CancellationToken.None);

        ICovenantLabeledArtifactGuard guard = CreateGuard();

        Result refused = await guard.EnsureUnlabeledAsync(
            kind,
            artifactId,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        // The refusal names the boundary rather than the artifact: the message reaches operator surfaces
        // and has no business carrying an identity from the label table.
        Assert.Contains("purge boundary", refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(artifactId.ToString("D"), refused.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// One labelled member is enough to refuse a whole-kind delete.
    /// </summary>
    /// <remarks>
    /// This is the arm that closes the bulk hole. `DELETE FROM saga_memories` would remove every row
    /// including labelled ones, and no per-artifact check can see rows it never enumerated.
    /// </remarks>
    [SkippableFact]

    public async Task One_labeled_member_refuses_the_whole_kind_bulk_delete()
    {

        RequireSqlCipher();

        ICovenantLabeledArtifactGuard guard = CreateGuard();

        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Saga, CancellationToken.None))
                .IsSuccess);

        await SeedLabelAsync(SensitiveArtifactKind.Saga, Guid.NewGuid(), CancellationToken.None);

        Result refused = await guard.EnsureNoneLabeledAsync(
            SensitiveArtifactKind.Saga,
            CancellationToken.None);

        Assert.True(refused.IsFailure);

        // A different kind is unaffected: the bulk arm is per kind, not per installation, so labelling a
        // Saga fact must not block a Lexicon reset that has nothing protected in it.
        Assert.True(
            (await guard.EnsureNoneLabeledAsync(SensitiveArtifactKind.Lexicon, CancellationToken.None))
                .IsSuccess);

    }

    private ICovenantLabeledArtifactGuard CreateGuard()
    {

        ICovenantConnectionSource connections = new CovenantConnectionSource(
            _db!,
            new RecordingScopedOrdinaryConnectionFactory());

        return new CovenantLabeledArtifactGuard(
            new ArtifactSensitivityLedger(connections),
            connections);

    }

    private async Task SeedLabelAsync(
        SensitiveArtifactKind kind,
        Guid artifactId,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(cancellationToken);

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId,
                ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, $kind, $artifact, 1, 1, $generations, NULL, NULL, NULL, NULL,
                    1, zeroblob(32), zeroblob(32), NULL, NULL, NULL, zeroblob(32), $now);
            """;

        _ = command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        _ = command.Parameters.AddWithValue("$artifact", artifactId.ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$generations", Enumerable.Repeat((byte)7, 16).ToArray());

        _ = command.Parameters.AddWithValue("$now", "2026-01-01T00:00:00.0000000Z");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

}
