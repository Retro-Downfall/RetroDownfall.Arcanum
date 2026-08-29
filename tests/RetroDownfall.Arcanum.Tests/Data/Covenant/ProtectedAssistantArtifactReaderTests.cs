using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The protected read path: one snapshot, one lease, and no partial answers.
/// </summary>
/// <remarks>
/// The assertions that matter here are the refusals. A protected read that returned a row with its
/// content blanked would still disclose that a protected artifact exists at that identity, and a
/// read that outlived its lease would keep emitting bytes after the authority that permitted them
/// was gone.
/// </remarks>
public sealed class ProtectedAssistantArtifactReaderTests
{

    private static readonly Guid Session = Guid.Parse("60718293-A4B5-40C1-92D3-6E7F8091A2B3");

    private static readonly Guid Generation = Guid.Parse("718293A4-B5C6-41D2-83E4-7F8091A2B3C4");

    [Fact]
    public async Task An_untainted_entry_reads_back_with_no_label()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("an ordinary answer");

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Session,
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(ContentSensitivity.None, read.Value.Sensitivity);

        Assert.Null(read.Value.Label);

        Assert.Equal("an ordinary answer", read.Value.Content);

    }

    [Fact]
    public async Task A_tainted_entry_reads_back_with_the_label_that_describes_it()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("the protected answer");

        _ = await fixture.LabelAsync(entryId, "the protected answer");

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Session,
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(ContentSensitivity.CovenantDerived, read.Value.Sensitivity);

        Assert.NotNull(read.Value.Label);

        Assert.Contains(Generation, read.Value.Label!.Provenance.ExactGenerationIds);

    }

    /// <summary>
    /// An artifact belonging to an imported Session reads back over the settled spelling.
    /// </summary>
    /// <remarks>
    /// <b>The spelling this case was built around no longer exists, and the seed says so.</b> The
    /// protected transfer store used to write <c>"Entries"."Id"</c> lowercase — it was the only
    /// production writer that did — and this reader bound the object-relational writer's uppercase form
    /// explicitly, so a protected read of an imported artifact reported "no assistant artifact with
    /// that identity" for a row that was plainly there. That writer now renders the canonical form, the
    /// version-5 sweep settled the stored data on it, and a guard trigger refuses anything else.
    ///
    /// <para>The precondition is pinned rather than assumed, and it is now the opposite one: what is
    /// worth pinning is that the row really does hold the canonical spelling the reader compares
    /// against, because a reader binding some other form would find nothing and say nothing. Asserted
    /// on the content rather than on success alone: a read that resolved the row but returned someone
    /// else's bytes would satisfy a bare success check.</para>
    /// </remarks>
    [Fact]
    public async Task An_artifact_written_by_the_transfer_store_reads_back_over_the_settled_spelling()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("the imported answer");

        string stored = await fixture.StoredEntryIdAsync(entryId);

        Assert.Equal(entryId.ToString("D").ToUpperInvariant(), stored);

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Session,
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : string.Empty);

        Assert.Equal("the imported answer", read.Value.Content);

    }

    [Fact]
    public async Task A_label_that_describes_different_bytes_fails_the_read_closed()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("the answer as stored");

        // Evidence for a different revision of the same artifact identity: exactly what an artifact
        // replaced without its label would leave behind.
        _ = await fixture.LabelAsync(entryId, "some earlier answer");

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Session,
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, read.Error.Code);

    }

    [Fact]
    public async Task An_entry_belonging_to_another_session_is_not_found_rather_than_refused()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("the answer");

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Guid.NewGuid(),
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsFailure);

        // Not-found rather than forbidden: a distinct code here would confirm that the identity
        // exists somewhere in the installation.
        Assert.Equal(ErrorCodes.Covenant.NotFound, read.Error.Code);

    }

    [Fact]
    public async Task A_lease_that_goes_stale_before_the_value_is_returned_stops_the_read()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("the protected answer");

        _ = await fixture.LabelAsync(entryId, "the protected answer");

        fixture.Lease.StaleAfter = 1;

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Session,
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, read.Error.Code);

    }

    [Fact]
    public async Task A_lease_that_is_already_stale_stops_the_read_before_any_query()
    {

        await using ReaderFixture fixture = await ReaderFixture.CreateAsync();

        Guid entryId = await fixture.SeedEntryAsync("the protected answer");

        fixture.Lease.StaleAfter = 0;

        Result<ProtectedAssistantArtifact> read = await fixture.Reader.ReadAsync(
            Session,
            entryId,
            fixture.Lease,
            CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, read.Error.Code);

    }

    private sealed class ReaderFixture : IAsyncDisposable
    {

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly ArtifactSensitivityLedger _ledger;

        private ReaderFixture(CovenantSchemaScratchDatabase database)
        {

            _database = database;

            FixedCovenantConnectionSource connections = new(database.Connection);

            _ledger = new ArtifactSensitivityLedger(connections);

            Reader = new ProtectedAssistantArtifactReader(connections);

            Lease = new CountingLease();

        }

        internal ProtectedAssistantArtifactReader Reader { get; }

        internal CountingLease Lease { get; }

        /// <summary>
        /// How this fixture spells the identities it stores.
        /// </summary>
        /// <remarks>
        /// One spelling, and that is the change rather than a simplification. This used to offer two —
        /// the object-relational writer's uppercase form and the protected transfer store's lowercase
        /// one — because both were real and a fixture offering only one would have agreed with whichever
        /// spelling the reader happened to bind. The transfer store now renders the canonical form, the
        /// version-5 sweep settled the stored data on it, and a guard trigger refuses anything else, so
        /// a lowercase seed here would describe a database production can no longer produce.
        /// </remarks>
        internal static string Stored(Guid value) => value.ToString("D").ToUpperInvariant();

        internal static async Task<ReaderFixture> CreateAsync()
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
                .CreateAsync(CancellationToken.None);

            try
            {

                await database.InstallCoreObjectsAsync(
                    ["Campaigns", "Sessions", "Entries", "artifact_sensitivity", "session_sensitivity_state"],
                    CancellationToken.None);

                await using SqliteCommand seed = database.Connection.CreateCommand();

                seed.CommandText = """
                    INSERT INTO "Sessions" ("Id", "Title", "CreatedAt", "UpdatedAt")
                    VALUES ($sessionId, 'protected', $now, $now);
                    """;

                _ = seed.Parameters.AddWithValue("$sessionId", Stored(Session));

                _ = seed.Parameters.AddWithValue("$now", "2026-08-16T00:00:00.0000000+00:00");

                _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

                return new ReaderFixture(database);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal async Task<Guid> SeedEntryAsync(string content)
        {

            Guid entryId = Guid.NewGuid();

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                INSERT INTO "Entries" (
                    "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ($id, $sessionId, 'assistant', $content, 'test-model', $now, 1);
                """;

            _ = command.Parameters.AddWithValue("$id", Stored(entryId));

            _ = command.Parameters.AddWithValue("$sessionId", Stored(Session));

            _ = command.Parameters.AddWithValue("$content", content);

            _ = command.Parameters.AddWithValue("$now", "2026-08-16T00:00:00.0000000+00:00");

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

            return entryId;

        }

        /// <summary>The exact text <c>"Entries"."Id"</c> holds, read back out of the row.</summary>
        internal async Task<string> StoredEntryIdAsync(Guid entryId)
        {

            await using SqliteCommand command = _database.Connection.CreateCommand();

            command.CommandText = """
                SELECT "Id" FROM "Entries" WHERE "Content" IS NOT NULL AND "Sequence" = 1;
                """;

            object? stored = await command.ExecuteScalarAsync(CancellationToken.None);

            Assert.NotNull(stored);

            Assert.Equal(entryId, Guid.Parse((string)stored!));

            return (string)stored!;

        }

        internal Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(Guid entryId, string describedContent) =>
            _ledger.LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.AssistantEntry,
                    entryId,
                    Session,
                    null,
                    null,
                    artifactRevision: 1,
                    DerivedArtifactContentDigest.ForText(describedContent),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Generation])),
                CancellationToken.None);

        public ValueTask DisposeAsync() => _database.DisposeAsync();

    }

    /// <summary>
    /// A snapshot-read lease that can be made to go stale after a chosen number of revalidations.
    /// </summary>
    /// <remarks>
    /// Modelling staleness by count rather than by wall clock is what makes the "went stale between
    /// the reads and the return" case deterministic; a timer-based fake would make the same test
    /// flaky on a slow machine.
    /// </remarks>
    internal sealed class CountingLease : ICovenantSnapshotReadLease
    {

        private int _revalidations;

        internal int? StaleAfter { get; set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.InstallationRead,
            CovenantLeaseCoverage.Installation,
            null,
            null,
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 0,
            null,
            null,
            null,
            null,
            null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {

            int observed = _revalidations++;

            return ValueTask.FromResult(StaleAfter is { } threshold && observed >= threshold
                ? Result.Failure(new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "The lease generation moved."))
                : Result.Success());

        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

}
