using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class ArtifactSensitivityLedgerTests
{
    [Theory]
    [InlineData(32, false, 0)]
    [InlineData(1024, false, 0)]
    [InlineData(8192, false, 0)]
    [InlineData(32, true, 0)]
    [InlineData(1024, true, 0)]
    [InlineData(8192, true, 0)]
    [InlineData(1024, true, 32)]
    [InlineData(8192, true, 32)]
    public async Task Actual_writer_keeps_exact_original_fold_and_bounded_work_across_diversity(int count, bool diverse, int duplicatePrefix)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, count);

        if (diverse)
        {
            await SeedDiverseProvenanceAsync(fixture, count, duplicatePrefix);
        }

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        transaction.Save("referenceInput");

        var referenceInput = await ArtifactSensitivityLedger.WriteWithinAsync(fixture.Connection, transaction,
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

        Assert.True(referenceInput.IsSuccess);

        await using SqliteCommand index = fixture.Connection.CreateCommand();

        index.Transaction = transaction;

        index.CommandText = "DROP INDEX " + SessionProvenanceIndex;

        await index.ExecuteNonQueryAsync();

        string originalLayout = await ProvenanceLayoutFingerprintAsync(fixture.Connection, transaction);

        CovenantDigest expected;

        int referenceWork;

        using (ProvenanceNativeTrace reference = new(fixture.Connection))
        {
            expected = await OriginalProjectionDigestAsync(fixture.Connection, transaction);

            reference.AssertNoFault();

            referenceWork = Assert.Single(reference.Statements, static item => item.IsFold).VmSteps;
        }

        index.CommandText = SessionProvenanceIndexSql;

        await index.ExecuteNonQueryAsync();

        string coveringLayout = await ProvenanceLayoutFingerprintAsync(fixture.Connection, transaction);

        Assert.NotEqual(originalLayout, coveringLayout);

        int coveringOnlyWork;

        using (ProvenanceNativeTrace covering = new(fixture.Connection))
        {
            Assert.Equal(expected, await OriginalProjectionDigestAsync(fixture.Connection, transaction));

            covering.AssertNoFault();

            coveringOnlyWork = Assert.Single(covering.Statements, static item => item.IsFold).VmSteps;
        }

        transaction.Rollback("referenceInput");

        string actualLayout = await ProvenanceLayoutFingerprintAsync(fixture.Connection, transaction);

        using ProvenanceNativeTrace actual = new(fixture.Connection);

        var written = await ArtifactSensitivityLedger.WriteWithinAsync(fixture.Connection, transaction,
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

        Assert.True(written.IsSuccess);

        actual.AssertNoFault();

        ProvenanceStatement[] reads = actual.Statements.Where(static item => item.Sql.Contains("FROM artifact_sensitivity", StringComparison.Ordinal)).ToArray();

        int allReadWork = reads.Sum(static item => item.VmSteps);

        ProvenanceStatement[] foldReads = reads.Where(static item => item.IsFold || item.Sql.Contains("AS ModeStorage", StringComparison.Ordinal)).ToArray();

        int work = foldReads.Sum(static item => item.VmSteps);

        Assert.Equal(expected, (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(fixture.Connection, transaction,
            Session, CancellationToken.None)).Value.GenerationProvenanceDigest);

        Assert.InRange(reads.Length, 1, 19);

        Assert.InRange(foldReads.Length, 1, 18);

        Assert.True(work < referenceWork * 2, $"Adaptive work amplified beyond the predeclared bound: {referenceWork}->{work}.");

        if (!diverse && count >= 1024)
        {
            Assert.True(allReadWork < 1000);
        }

        Assert.Equal(diverse, reads.Any(static item => item.IsFold));

        Console.WriteLine($"Native physical index layouts: original={originalLayout}; coveringOnly={coveringLayout}; actual={actualLayout}; sourceCoreFingerprint={RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaCatalog.CoreSchemaFingerprint}; originalQuery=SELECT DISTINCT ProvenanceModeCode, ExactGenerationIds, GenerationBloom FROM artifact_sensitivity WHERE SessionId=$session; sameWriterSnapshot=true.");

        Console.WriteLine($"Actual writer diversity: labels={count + 1}; diverse={diverse}; duplicatePrefix={duplicatePrefix}; originalUnindexedFoldVM={referenceWork}; coveringOnlyOriginalFoldVM={coveringOnlyWork}; adaptiveProjectionVM={work}; allActualArtifactReadVM={allReadWork}; allActualArtifactReadCalls={reads.Length}; boundedFoldSQLCalls={foldReads.Length}; unchangedOuterPointReadCalls={reads.Length - foldReads.Length}. Same exact digest/row count in each fold arm; all-read total includes point read; fallback and small-corpus overhead are explicit.");

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("valid-mixed-bloom")]
    [InlineData("extra-ignored-bloom")]
    [InlineData("null-exact")]
    [InlineData("text-exact")]
    [InlineData("real-mode")]
    [InlineData("unknown-mode")]
    [InlineData("null-bloom")]
    [InlineData("text-bloom")]
    [InlineData("short-exact")]
    public async Task Actual_writer_preserves_original_nullable_and_malformed_tuple_outcomes(string shape)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, 4);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        if (shape == "valid-mixed-bloom")
        {
            foreach (byte mask in new byte[] { 0x55, 0xAA })
            {
                var result = await fixture.Ledger.LabelAsync(new(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(),
                    Session, Campaign, null, 1, Digest(3), ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateBloom(Enumerable.Repeat(mask, 32).ToArray())), CancellationToken.None);

                Assert.True(result.IsSuccess);
            }
        }
        else
        {
            command.CommandText = "PRAGMA ignore_check_constraints = ON;";

            await command.ExecuteNonQueryAsync();

            string change = shape switch
            {
                "extra-ignored-bloom" => "GenerationBloom=zeroblob(32)",
                "null-exact" => "ExactGenerationIds=NULL",
                "text-exact" => "ExactGenerationIds='PRIVATE-PROVENANCE-CANARY'",
                "real-mode" => "ProvenanceModeCode=1.5",
                "unknown-mode" => "ProvenanceModeCode=3, ExactGenerationIds=NULL, GenerationBloom=$bloom",
                "null-bloom" => "ProvenanceModeCode=2, ExactGenerationIds=NULL, GenerationBloom=NULL",
                "text-bloom" => "ProvenanceModeCode=2, ExactGenerationIds=NULL, GenerationBloom='PRIVATE-PROVENANCE-CANARY'",
                "short-exact" => "ExactGenerationIds=x'010203'",
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };

            command.CommandText = "UPDATE artifact_sensitivity SET " + change + " WHERE LabelId=$label;";

            command.Parameters.AddWithValue("$label", PagingLabelId(1).ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$bloom", Enumerable.Repeat((byte)0x55, 32).ToArray());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        CovenantDigest? expected = null;

        Exception? referenceFault = await Record.ExceptionAsync(async () => expected = await OriginalProjectionDigestAsync(fixture.Connection, transaction));

        CovenantDigest? observed = null;

        using ProvenanceNativeTrace trace = new(fixture.Connection);

        Exception? actualFault = await Record.ExceptionAsync(async () =>
        {
            var result = await ArtifactSensitivityLedger.WriteWithinAsync(fixture.Connection, transaction,
                Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

            Assert.True(result.IsSuccess);

            observed = (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(fixture.Connection, transaction,
                Session, CancellationToken.None)).Value.GenerationProvenanceDigest;
        });

        trace.AssertNoFault();

        Assert.Equal(referenceFault?.GetType(), actualFault?.GetType());

        Assert.Equal(expected, observed);

        if (shape != "valid-mixed-bloom")
        {
            Assert.Contains(trace.Statements, static item => item.IsFold);
        }

        await transaction.RollbackAsync();

        Assert.Equal(shape == "valid-mixed-bloom" ? 6L : 4L, await fixture.CountLabelsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_writer_seeks_every_mixed_mode_and_null_prefix_tuple_after_the_initial_window(bool initialWindowEndsInBloom)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, 64);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "UPDATE artifact_sensitivity SET ProvenanceModeCode=2, ExactGenerationIds=NULL, GenerationBloom=$bloom WHERE LabelId=$label;";

        int firstBloom = initialWindowEndsInBloom ? 1 : 62;

        foreach (int ordinal in Enumerable.Range(firstBloom, 65 - firstBloom))
        {
            byte mask = ordinal == 64 ? (byte)0x44 : ordinal == 63 ? (byte)0x22 : (byte)0x11;

            command.Parameters.Clear();

            command.Parameters.AddWithValue("$bloom", Enumerable.Repeat(mask, 32).ToArray());

            command.Parameters.AddWithValue("$label", PagingLabelId(ordinal).ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        ProvenanceStatement[] observed;

        using (ProvenanceNativeTrace trace = new(fixture.Connection))
        {
            var result = await ArtifactSensitivityLedger.WriteWithinAsync(fixture.Connection, transaction,
                Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

            Assert.True(result.IsSuccess);

            trace.AssertNoFault();

            observed = trace.Statements.ToArray();
        }

        CovenantDigest expected = await OriginalProjectionDigestAsync(fixture.Connection, transaction);

        Assert.Equal(expected, (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(fixture.Connection, transaction,
            Session, CancellationToken.None)).Value.GenerationProvenanceDigest);

        Assert.DoesNotContain(observed, static item => item.IsFold);

        Assert.InRange(observed.Count(static item => item.Sql.Contains("AS ModeStorage", StringComparison.Ordinal)), 2, 18);

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_writer_does_not_materialize_oversized_ignored_legacy_seek_keys(bool ignoredExact)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, 4);

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "PRAGMA ignore_check_constraints = ON;";

        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE artifact_sensitivity SET " + (ignoredExact
            ? "ProvenanceModeCode=2, ExactGenerationIds=zeroblob(1048576), GenerationBloom=$bloom"
            : "GenerationBloom=zeroblob(1048576)") + " WHERE LabelId=$label;";

        command.Parameters.AddWithValue("$label", PagingLabelId(1).ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$bloom", Enumerable.Repeat((byte)0x55, 32).ToArray());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        CovenantDigest expected = await OriginalProjectionDigestAsync(fixture.Connection, transaction);

        int thread = System.Environment.CurrentManagedThreadId;

        long before = GC.GetAllocatedBytesForCurrentThread();

        var result = await ArtifactSensitivityLedger.WriteWithinAsync(fixture.Connection, transaction,
            Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), CancellationToken.None);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(thread, System.Environment.CurrentManagedThreadId);

        Assert.True(result.IsSuccess);

        Assert.InRange(allocated, 0, 524288);

        Assert.Equal(expected, (await ArtifactSensitivityLedger.ReadProjectionWithinAsync(fixture.Connection, transaction,
            Session, CancellationToken.None)).Value.GenerationProvenanceDigest);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Cancellation_after_actual_initial_window_stops_seeks_and_rolls_back_the_label()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();

        await SeedOrderedLabelsAsync(fixture, 32);

        using CancellationTokenSource caller = new();

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false);

        using ProvenanceNativeTrace trace = new(fixture.Connection, sql =>
        {
            if (sql.Contains("AS ModeStorage", StringComparison.Ordinal))
            {
                caller.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArtifactSensitivityLedger.WriteWithinAsync(
            fixture.Connection, transaction, Tainted(SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(), GenerationTwo), caller.Token));

        Assert.True(caller.IsCancellationRequested);

        trace.AssertNoFault();

        Assert.Single(trace.Statements, static item => item.Sql.Contains("AS ModeStorage", StringComparison.Ordinal));

        await transaction.RollbackAsync();

        Assert.Equal(32L, await fixture.CountLabelsAsync());
    }

    private static async Task<string> ProvenanceLayoutFingerprintAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT type,name,coalesce(sql,'') FROM sqlite_schema WHERE tbl_name='artifact_sensitivity' ORDER BY type,name;";

        List<string> definitions = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0) + "\n" + reader.GetString(1) + "\n" + reader.GetString(2));
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", definitions))));
    }

    private static async Task<CovenantDigest> OriginalProjectionDigestAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT DISTINCT ProvenanceModeCode, ExactGenerationIds, GenerationBloom FROM artifact_sensitivity WHERE SessionId=$session;";

        command.Parameters.AddWithValue("$session", Session.ToString().ToUpperInvariant());

        GenerationProvenance merged = GenerationProvenance.CreateExact([GenerationTwo]);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            merged = merged.Merge((GenerationProvenanceMode)reader.GetInt64(0) == GenerationProvenanceMode.Exact
                ? GenerationProvenance.CreateExact(ReferenceGenerations((byte[])reader.GetValue(1)))
                : GenerationProvenance.CreateBloom((byte[])reader.GetValue(2)));
        }

        return CovenantDigests.Sensitivity(merged.ToDigestInput(ContentSensitivity.CovenantDerived));
    }

    private static IEnumerable<Guid> ReferenceGenerations(byte[] packed)
    {
        for (int offset = 0; offset + 16 <= packed.Length; offset += 16)
        {
            yield return new Guid(packed.AsSpan(offset, 16), bigEndian: true);
        }
    }

    private static async Task SeedDiverseProvenanceAsync(LedgerFixture fixture, int count, int duplicatePrefix = 0)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "SELECT CreatedAtUtc FROM artifact_sensitivity ORDER BY LabelId LIMIT 1;";

        DateTimeOffset created = DateTimeOffset.Parse((string)(await command.ExecuteScalarAsync())!,
            System.Globalization.CultureInfo.InvariantCulture);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction();

        command.Transaction = transaction;

        command.CommandText = "UPDATE artifact_sensitivity SET ExactGenerationIds=$generations, SensitivityDigest=$sensitivity, ArtifactLabelDigest=$digest WHERE LabelId=$label;";

        SqliteParameter generations = command.Parameters.AddWithValue("$generations", new byte[16]);

        SqliteParameter sensitivity = command.Parameters.AddWithValue("$sensitivity", new byte[32]);

        SqliteParameter digest = command.Parameters.AddWithValue("$digest", new byte[32]);

        SqliteParameter labelId = command.Parameters.AddWithValue("$label", "");

        for (int index = 1; index <= count; index++)
        {
            Guid generation = index <= duplicatePrefix ? GenerationOne : Guid.Parse($"30000000-0000-0000-0000-{index:X12}");

            Guid artifact = Guid.Parse($"20000000-0000-0000-0000-{index:X12}");

            ArtifactSensitivityLabel label = Tainted(SensitiveArtifactKind.Lexicon, artifact, generation)
                .ToLabel(PagingLabelId(index), created);

            generations.Value = label.Provenance.ToCanonicalExactBytes();

            sensitivity.Value = label.SensitivityDigest.Bytes;

            digest.Value = label.LabelDigest.Bytes;

            labelId.Value = label.LabelId.ToString("D").ToUpperInvariant();

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await transaction.CommitAsync();
    }
}
