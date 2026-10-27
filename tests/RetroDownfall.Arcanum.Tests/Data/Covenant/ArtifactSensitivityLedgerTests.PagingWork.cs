using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class ArtifactSensitivityLedgerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Label_continuation_page_work_does_not_rescan_preceding_labels(bool terminalPage)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();
        await SeedOrderedLabelsAsync(fixture, 2048);
        (long earlySteps, Guid[] early) = await MeasureLabelPageAsync(fixture, PagingLabelId(1), 16);
        int after = terminalPage ? 2048 : 2032;
        (long lateSteps, Guid[] late) = await MeasureLabelPageAsync(fixture, PagingLabelId(after), 16);
        Assert.Equal(Enumerable.Range(2, 16).Select(PagingLabelId), early);
        Assert.Equal(terminalPage ? [] : Enumerable.Range(2033, 16).Select(PagingLabelId).ToArray(), late);
        Console.WriteLine($"Native sensitivity page VM instructions: early={earlySteps}; after={after}; late={lateSteps}.");
        Assert.True(earlySteps > 0);
        Assert.True(lateSteps <= earlySteps + 500,
            $"Continuation scanned preceding labels: early16={earlySteps}, after{after}={lateSteps} VM instructions.");
    }

    [Fact]
    public async Task Label_keyset_walk_returns_every_ordered_identity_once_in_the_supplied_snapshot()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();
        await SeedOrderedLabelsAsync(fixture, 129);
        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction();
        List<Guid> seen = [];
        Guid? after = null;
        do
        {
            Result<IReadOnlyList<ArtifactSensitivityLabel>> page = await ArtifactSensitivityLedger.ReadPageWithinAsync(
                fixture.Connection, transaction, after, 16, CancellationToken.None);
            Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);
            Assert.InRange(page.Value.Count, 0, 16);
            if (page.Value.Count == 0)
                break;
            seen.AddRange(page.Value.Select(static label => label.LabelId));
            after = page.Value[^1].LabelId;
        }

        while (seen.Count <= 129);
        Assert.Equal(Enumerable.Range(1, 129).Select(PagingLabelId), seen);
        Assert.Equal(129, seen.Distinct().Count());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("!PRIVATE-LABEL-IDENTITY")]
    public async Task Label_first_page_refuses_malformed_leading_identity_instead_of_skipping_it(string malformed)
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();
        await SeedOrderedLabelsAsync(fixture, 2);
        await using SqliteCommand corrupt = fixture.Connection.CreateCommand();
        corrupt.CommandText = "UPDATE artifact_sensitivity SET LabelId = $malformed WHERE LabelId = $first;";
        corrupt.Parameters.AddWithValue("$malformed", malformed);
        corrupt.Parameters.AddWithValue("$first", PagingLabelId(1).ToString("D").ToUpperInvariant());
        Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        Result<IReadOnlyList<ArtifactSensitivityLabel>> page = await ArtifactSensitivityLedger.ReadPageWithinAsync(
            fixture.Connection, null, null, 16, CancellationToken.None);
        Assert.True(page.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, page.Error.Code);
        Assert.DoesNotContain("PRIVATE", page.Error.Message, StringComparison.Ordinal);
        Assert.Null(page.Error.CovenantInventoryFailure?.LabelId);
        Assert.Equal(CovenantInventoryFailureReason.MalformedLabel, page.Error.CovenantInventoryFailure?.Reason);
    }

    [Fact]
    public async Task Label_page_preserves_cancellation_and_invalid_cursor_refusal()
    {
        await using LedgerFixture fixture = await LedgerFixture.CreateAsync();
        await SeedOrderedLabelsAsync(fixture, 2);
        Result<IReadOnlyList<ArtifactSensitivityLabel>> invalid = await ArtifactSensitivityLedger.ReadPageWithinAsync(
            fixture.Connection, null, Guid.Empty, 16, CancellationToken.None);
        Assert.True(invalid.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.InvalidScope, invalid.Error.Code);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArtifactSensitivityLedger.ReadPageWithinAsync(
            fixture.Connection, null, null, 16, canceled.Token));
        Assert.True((await ArtifactSensitivityLedger.ReadPageWithinAsync(
            fixture.Connection, null, null, 16, CancellationToken.None)).IsSuccess);
    }

    private static Guid PagingLabelId(int index) => Guid.Parse($"10000000-0000-0000-0000-{index:X12}");

    private static async Task SeedOrderedLabelsAsync(LedgerFixture fixture, int count)
    {
        Result<LabeledArtifactWriteReceipt> written = await fixture.Ledger.LabelAsync(
            Tainted(SensitiveArtifactKind.Lexicon, Guid.NewGuid(), GenerationOne), CancellationToken.None);
        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : null);
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        // Copy real provenance fields, then recompute the identity-bound label digests below.
        command.CommandText = """
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < $count)
            INSERT INTO artifact_sensitivity(LabelId,ArtifactKindCode,ArtifactId,SensitivityCode,
                ProvenanceModeCode,ExactGenerationIds,GenerationBloom,SessionId,CampaignId,TurnId,
                ArtifactRevision,ArtifactContentDigest,SensitivityDigest,ProducingPlanDigest,
                ProducingAdmissionDigest,ProducingMaintenanceReceiptDigest,ArtifactLabelDigest,CreatedAtUtc)
            SELECT printf('10000000-0000-0000-0000-%012X',n),ArtifactKindCode,
                printf('20000000-0000-0000-0000-%012X',n),SensitivityCode,
                ProvenanceModeCode,ExactGenerationIds,GenerationBloom,SessionId,CampaignId,TurnId,
                ArtifactRevision,ArtifactContentDigest,SensitivityDigest,ProducingPlanDigest,
                ProducingAdmissionDigest,ProducingMaintenanceReceiptDigest,ArtifactLabelDigest,CreatedAtUtc
            FROM numbers CROSS JOIN artifact_sensitivity template WHERE template.LabelId = $template;
            DELETE FROM artifact_sensitivity WHERE LabelId = $template;
            """;
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$template", written.Value.LabelId!.Value.ToString("D").ToUpperInvariant());
        Assert.Equal(count + 1, await command.ExecuteNonQueryAsync());

        command.Parameters.Clear();

        command.CommandText = "SELECT CreatedAtUtc FROM artifact_sensitivity ORDER BY LabelId LIMIT 1;";

        DateTimeOffset createdAt = DateTimeOffset.Parse((string)(await command.ExecuteScalarAsync())!,
            System.Globalization.CultureInfo.InvariantCulture);

        await using SqliteTransaction transaction = fixture.Connection.BeginTransaction();

        command.Transaction = transaction;

        command.CommandText = "UPDATE artifact_sensitivity SET ArtifactLabelDigest = $digest WHERE LabelId = $label;";

        SqliteParameter digestParameter = command.Parameters.AddWithValue("$digest", new byte[32]);

        SqliteParameter labelParameter = command.Parameters.AddWithValue("$label", "");

        Dictionary<Guid, CovenantDigest> expectedDigests = [];

        for (int index = 1; index <= count; index++)
        {
            Guid labelId = PagingLabelId(index);

            Guid artifactId = Guid.Parse($"20000000-0000-0000-0000-{index:X12}");

            ArtifactSensitivityLabel label = Tainted(SensitiveArtifactKind.Lexicon, artifactId, GenerationOne)
                .ToLabel(labelId, createdAt);

            expectedDigests.Add(labelId, label.LabelDigest);

            digestParameter.Value = label.LabelDigest.Bytes;

            labelParameter.Value = labelId.ToString("D").ToUpperInvariant();

            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        command.Parameters.Clear();

        command.CommandText = "SELECT LabelId, ArtifactLabelDigest FROM artifact_sensitivity;";

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            int verified = 0;

            while (await reader.ReadAsync())
            {
                Assert.Equal(expectedDigests[Guid.Parse(reader.GetString(0))].Bytes, (byte[])reader.GetValue(1));

                verified++;
            }

            Assert.Equal(count, verified);
        }

        await transaction.CommitAsync();
    }

    private static async Task<(long Steps, Guid[] Ids)> MeasureLabelPageAsync(LedgerFixture fixture, Guid after, int limit)
    {
        long steps = 0;
        raw.sqlite3_progress_handler(fixture.Connection.Handle, 1, (object _) => ++steps > 1_000_000 ? 1 : 0, null);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Result<IReadOnlyList<ArtifactSensitivityLabel>> page = await ArtifactSensitivityLedger.ReadPageWithinAsync(
                fixture.Connection, null, after, limit, timeout.Token);
            Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);
            return (steps, page.Value.Select(static label => label.LabelId).ToArray());
        }
        finally
        {
            raw.sqlite3_progress_handler(fixture.Connection.Handle, 0, null, null);
        }
    }
}
