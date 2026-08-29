using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The invariants the Annals delegates to SQLite rather than to a writer.
/// </summary>
/// <remarks>
/// Every case here writes directly to the tables, and that is the point: the assertion is that the
/// schema refuses these things whatever produced them, and a case that could only be reached through
/// <c>AnnalsClaimWriter</c> would prove only that one writer behaves. A future writer taking another
/// code path has to meet the same refusals.
///
/// <para>Nothing here sets a pragma of its own. <see cref="EvolutionScratchDatabase.OpenAsync"/> routes
/// through the same <c>CovenantSqliteConnectionInitializer</c> that every production connection uses,
/// which both sets and verifies <c>PRAGMA foreign_keys=ON</c>. A test that established its own
/// enforcement would be proving the schema under conditions production might not have.</para>
/// </remarks>
public sealed class AnnalsSchemaInvariantTests
{

    static AnnalsSchemaInvariantTests() => SqliteNativeRuntime.Instance.Initialize();

    /// <summary>
    /// Cycle safety is structural: an edge may only point strictly backwards in allocation order, so a
    /// cycle needs an edge this table cannot hold. A self-edge is excluded by the same check.
    /// </summary>
    [Fact]
    public async Task An_edge_that_does_not_point_strictly_backwards_is_refused()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string first, long firstSequence) = await scratch.SeedVersionAsync("claim-a");

        (string second, long secondSequence) = await scratch.SeedVersionAsync("claim-b");

        await scratch.SeedEdgeAsync(second, secondSequence, first, firstSequence);

        SqliteException forwards = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(first, firstSequence, second, secondSequence));

        Assert.Contains("CHECK constraint failed", forwards.Message, StringComparison.Ordinal);

        SqliteException self = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(first, firstSequence, first, firstSequence));

        Assert.Contains("CHECK constraint failed", self.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// An edge whose recorded sequence disagrees with the version it names would let the ordering check
    /// be told a lie, so both endpoints carry a composite reference rather than a bare version id.
    /// </summary>
    [Fact]
    public async Task An_edge_that_misstates_a_versions_sequence_is_refused()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string first, long firstSequence) = await scratch.SeedVersionAsync("claim-a");

        (string second, long secondSequence) = await scratch.SeedVersionAsync("claim-b");

        SqliteException lie = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(second, secondSequence, first, firstSequence - 1));

        Assert.Contains("FOREIGN KEY constraint failed", lie.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_seventeenth_edge_on_one_version_is_refused()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        List<(string VersionId, long Sequence)> targets = [];

        for (int index = 0; index < 17; index++)
        {

            targets.Add(await scratch.SeedVersionAsync($"claim-{index}"));

        }

        (string dependent, long dependentSequence) = await scratch.SeedVersionAsync("claim-dependent");

        for (int index = 0; index < 16; index++)
        {

            await scratch.SeedEdgeAsync(
                dependent,
                dependentSequence,
                targets[index].VersionId,
                targets[index].Sequence,
                ordinal: index + 1);

        }

        SqliteException overflow = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedEdgeAsync(
                dependent,
                dependentSequence,
                targets[16].VersionId,
                targets[16].Sequence,
                ordinal: 17));

        Assert.Contains("CHECK constraint failed", overflow.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Claims_versions_and_edges_all_refuse_an_update()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string version, long sequence) = await scratch.SeedVersionAsync("claim-a");

        (string other, long otherSequence) = await scratch.SeedVersionAsync("claim-b");

        await scratch.SeedEdgeAsync(other, otherSequence, version, sequence);

        Assert.Contains(
            "append-only",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.ExecuteAsync("UPDATE annal_claims SET SubjectId = 'moved';"))).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "append-only",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.ExecuteAsync(
                    "UPDATE annal_versions SET RecordedAtUtc = '2030-01-01T00:00:00.0000000+00:00';"))).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "append-only",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.ExecuteAsync("UPDATE annal_dependencies SET RelationCode = 2;"))).Message,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A head is meant to move. What it must never do is move backwards, which would make a superseded
    /// version current again.
    /// </summary>
    [Fact]
    public async Task A_head_may_advance_but_never_retreat()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        (string first, _) = await scratch.SeedVersionAsync("claim-a");

        (string second, _) = await scratch.SeedVersionAsync("claim-a", revision: 2, predecessorVersionId: first);

        await scratch.SeedHeadAsync("claim-a", first, revision: 1);

        await scratch.AdvanceHeadAsync("claim-a", second, revision: 2);

        Assert.Contains(
            "may only advance",
            (await Assert.ThrowsAsync<SqliteException>(
                () => scratch.AdvanceHeadAsync("claim-a", first, revision: 1))).Message,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A head that could adopt a version belonging to another claim would silently relabel a memory's
    /// whole history, which is what the composite reference exists to refuse.
    /// </summary>
    [Fact]
    public async Task A_head_cannot_adopt_a_version_belonging_to_another_claim()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        _ = await scratch.SeedVersionAsync("claim-a");

        (string foreignVersion, _) = await scratch.SeedVersionAsync("claim-b");

        SqliteException adopted = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedHeadAsync("claim-a", foreignVersion, revision: 1));

        Assert.Contains("FOREIGN KEY constraint failed", adopted.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// A retirement binds to no content, and an assertion must bind to some. Both halves matter: without
    /// the second, a claim could be asserted about nothing at all.
    /// </summary>
    [Fact]
    public async Task A_retirement_carries_no_content_hash_and_an_assertion_must()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedVersionAsync("claim-a", operationCode: 3, withContentHash: true));

        _ = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedVersionAsync("claim-b", operationCode: 1, withContentHash: false));

    }

    /// <summary>
    /// The two unresolved scope kinds must be storable, because a claim over a memory whose ownership
    /// was never resolved has to be able to say so rather than rounding up to installation-global.
    /// </summary>
    [Fact]
    public async Task An_unresolved_scope_is_storable_and_a_campaign_scope_must_name_its_campaign()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        _ = await scratch.SeedVersionAsync("claim-unclassified", scopeKindCode: 0);

        _ = await scratch.SeedVersionAsync("claim-unresolved", scopeKindCode: 3);

        SqliteException missing = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedVersionAsync("claim-campaign", scopeKindCode: 2));

        Assert.Contains("CHECK constraint failed", missing.Message, StringComparison.Ordinal);

        SqliteException borrowed = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedVersionAsync("claim-global", scopeKindCode: 1, campaignId: "A0000000-0000-4000-8000-000000000001"));

        Assert.Contains("CHECK constraint failed", borrowed.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// One durable row may be described by exactly one claim. Without this, two claims could quietly own
    /// the same memory and an erasure that removed one would leave the other pointing at deleted content.
    /// </summary>
    [Fact]
    public async Task Two_claims_cannot_own_one_durable_row()
    {

        await using AnnalsScratch scratch = await AnnalsScratch.StartAsync();

        await scratch.SeedClaimAsync("claim-a", "shared-subject");

        SqliteException duplicate = await Assert.ThrowsAsync<SqliteException>(
            () => scratch.SeedClaimAsync("claim-b", "shared-subject"));

        Assert.Contains("UNIQUE constraint failed", duplicate.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// One open scratch installation at head, with direct access to the four Annals tables.
    /// </summary>
    private sealed class AnnalsScratch : IAsyncDisposable
    {

        private const string Timestamp = "2026-01-01T00:00:00.0000000+00:00";

        private readonly EvolutionScratchDatabase _file;

        private readonly SqliteConnection _connection;

        private AnnalsScratch(EvolutionScratchDatabase file, SqliteConnection connection)
        {

            _file = file;

            _connection = connection;

        }

        internal static async Task<AnnalsScratch> StartAsync()
        {

            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
                connection,
                GrimoireSchemaVersionChains.Default,
                1536,
                CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            return new AnnalsScratch(file, connection);

        }

        /// <summary>
        /// Inserts one claim and lets every constraint on it surface, which <see cref="SeedVersionAsync"/>
        /// deliberately cannot: that one uses <c>INSERT OR IGNORE</c> so repeated calls for one claim add
        /// versions rather than failing, and the suppression would hide a duplicate-subject refusal.
        /// </summary>
        internal Task SeedClaimAsync(string claimId, string subjectId) =>
            ExecuteAsync(
                """
                INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
                VALUES ($claimId, 1, $subjectId, $now);
                """,
                ("$claimId", claimId),
                ("$subjectId", subjectId),
                ("$now", Timestamp));

        /// <summary>
        /// Inserts a claim for this id when it has none, then one version of it, and returns the version
        /// id with the sequence SQLite allocated.
        /// </summary>
        internal async Task<(string VersionId, long Sequence)> SeedVersionAsync(
            string claimId,
            int revision = 1,
            string? predecessorVersionId = null,
            int operationCode = 1,
            bool withContentHash = true,
            int scopeKindCode = 1,
            string? campaignId = null,
            string? subjectId = null)
        {

            await ExecuteAsync(
                """
                INSERT OR IGNORE INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
                VALUES ($claimId, 1, $subjectId, $now);
                """,
                ("$claimId", claimId),
                ("$subjectId", subjectId ?? $"subject-{claimId}"),
                ("$now", Timestamp));

            string versionId = Guid.NewGuid().ToString();

            await ExecuteAsync(
                """
                INSERT INTO annal_versions (
                    VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, CampaignId,
                    SensitivityCode, ContentHash, ValidFromUtc, ValidToUtc, RecordedAtUtc,
                    PredecessorVersionId, SourceSessionId)
                VALUES (
                    $versionId, $claimId, $revision, $operationCode, 4, $scopeKindCode, $campaignId,
                    0, $contentHash, $now, NULL, $now, $predecessor, NULL);
                """,
                ("$versionId", versionId),
                ("$claimId", claimId),
                ("$revision", revision),
                ("$operationCode", operationCode),
                ("$scopeKindCode", scopeKindCode),
                ("$campaignId", campaignId),
                ("$contentHash", withContentHash ? new byte[32] : null),
                ("$now", Timestamp),
                ("$predecessor", predecessorVersionId));

            return (versionId, await ScalarAsync(
                "SELECT Sequence FROM annal_versions WHERE VersionId = $versionId;",
                ("$versionId", versionId)));

        }

        internal Task SeedEdgeAsync(
            string dependentVersionId,
            long dependentSequence,
            string dependencyVersionId,
            long dependencySequence,
            int ordinal = 1) =>
            ExecuteAsync(
                """
                INSERT INTO annal_dependencies (
                    DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence,
                    RelationCode, Ordinal, CreatedAtUtc)
                VALUES ($dependent, $dependentSequence, $dependency, $dependencySequence, 1, $ordinal, $now);
                """,
                ("$dependent", dependentVersionId),
                ("$dependentSequence", dependentSequence),
                ("$dependency", dependencyVersionId),
                ("$dependencySequence", dependencySequence),
                ("$ordinal", ordinal),
                ("$now", Timestamp));

        internal Task SeedHeadAsync(string claimId, string versionId, int revision) =>
            ExecuteAsync(
                """
                INSERT INTO annal_heads (
                    ClaimId, SubjectStoreCode, CurrentVersionId, CurrentRevision, CurrentOperationCode, UpdatedAtUtc)
                VALUES ($claimId, 1, $versionId, $revision, 1, $now);
                """,
                ("$claimId", claimId),
                ("$versionId", versionId),
                ("$revision", revision),
                ("$now", Timestamp));

        internal Task AdvanceHeadAsync(string claimId, string versionId, int revision) =>
            ExecuteAsync(
                """
                UPDATE annal_heads
                SET CurrentVersionId = $versionId, CurrentRevision = $revision, UpdatedAtUtc = $now
                WHERE ClaimId = $claimId;
                """,
                ("$claimId", claimId),
                ("$versionId", versionId),
                ("$revision", revision),
                ("$now", Timestamp));

        internal async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object? value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            }

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

        public async ValueTask DisposeAsync()
        {

            await _connection.DisposeAsync();

            _file.Dispose();

        }

        private async Task<long> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
        {

            await using SqliteCommand command = _connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object? value) in parameters)
            {

                _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

            }

            return Convert.ToInt64(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture);

        }

    }

}
