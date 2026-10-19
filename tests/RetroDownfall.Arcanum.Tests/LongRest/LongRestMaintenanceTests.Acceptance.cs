using System.Globalization;

using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    // Removing any suppression, rewriting/deleting a source, or paying again on completed request replay must break this acceptance proof.
    [Fact]
    public async Task Ten_paraphrases_preserve_every_exact_source_when_consolidated_and_replayed()
    {
        // The fixture's protected sources stay in their own partition. This request covers only the ten clean observations below.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false);

        string[] paraphrases =
        [
            "The user prefers black coffee without sugar.",
            "The user drinks coffee black, no sugar.",
            "Black coffee with no sugar is what the user prefers.",
            "The user takes their coffee black and unsweetened.",
            "The user's coffee preference is black and sugar free.",
            "When drinking coffee, the user prefers it black without sugar.",
            "For the user, coffee should be black with no added sugar.",
            "The user likes their coffee unsweetened and black.",
            "Black and unsweetened is how the user prefers their coffee.",
            "The user's preferred coffee is served black without sugar.",
        ];

        string[] memories = new string[10];

        LongRestTarget[] targets = new LongRestTarget[10];

        Guid[] sessions = new Guid[10];

        DateTimeOffset recorded = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        // Reverse insertion makes the ordinal survivor independent of SQLite allocation and insertion order.
        for (int index = 9; index >= 0; index--)
        {
            string suffix = (index + 1).ToString("D12", CultureInfo.InvariantCulture);

            memories[index] = "10000000-0000-0000-0000-" + suffix;

            sessions[index] = await f.H.SessionAsync();

            AttachmentMemoryProvenance provenance = new(sessions[index], Guid.Parse("20000000-0000-0000-0000-" + suffix),
                "coffee-preference", 1, (index + 1).ToString("X64", CultureInfo.InvariantCulture), recorded,
                "SessionAttachmentRag", AttachmentSourceAvailability.Unavailable);

            Assert.Equal(SagaMemoryWriteOutcome.Written, await f.H.Saga.Store.InsertAsync(memories[index], paraphrases[index], recorded,
                sessions[index], null, "extraction", f.H.Related(index + 3), provenance, CancellationToken.None));

            targets[index] = await f.H.TargetAsync(memories[index]);
        }

        Assert.Equal(10, sessions.Distinct().Count());

        Assert.Equal(10, targets.Select(static target => target.ExpectedVersionId).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(10, targets.Select(static target => target.ExpectedContentHash).Distinct(StringComparer.Ordinal).Count());

        string[] sourceRows = await ReadTenSourceRowsAsync(f);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.AllPairs(f.Handler.LastBody!) : "{\"summary\":\"The user prefers black coffee without sugar.\"}";

            return Task.CompletedTask;
        };

        var requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(requested.Id, null, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(requested.Id))!.State);

        Assert.Equal(1L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 1;"));

        string receiptId = (string)(await f.H.ScalarAsync("SELECT ReceiptId FROM long_rest_receipts WHERE OutcomeCode = 1;"))!;

        LongRestReceipt receipt = LongRestSweepHarness.Require(await new SagaLongRestService(f.H.Saga.Context, TimeProvider.System)
            .GetReceiptAsync(receiptId, CancellationToken.None));

        Assert.Equal(LongRestTransformationKind.EquivalentObservations, receipt.Kind);

        Assert.Equal(memories[0], receipt.SurvivorMemoryId);

        Assert.Equal(targets[0].ExpectedVersionId, receipt.SurvivorVersionId);

        Assert.Equal(memories, receipt.Targets.Select(static target => target.MemoryId));

        Assert.Equal(targets.Select(static target => target.ExpectedVersionId), receipt.Targets.Select(static target => target.VersionId));

        Assert.Equal(targets.Select(static target => target.ExpectedContentHash), receipt.Targets.Select(static target => target.ContentHash));

        Assert.Equal(targets.Skip(1).Select(static target => target.ExpectedVersionId).Order(StringComparer.Ordinal),
            receipt.SupersededVersionIds.Order(StringComparer.Ordinal));

        Assert.Equal(10L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(9L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(9L, await ScalarLongAsync(f, """
            SELECT count(*) FROM long_rest_suppressions s
            JOIN long_rest_receipts r ON r.ReceiptId = s.ReceiptId AND r.SurvivorVersionId = s.SurvivorVersionId
            JOIN long_rest_receipt_inputs i ON i.ReceiptId = s.ReceiptId AND i.VersionId = s.SourceVersionId
            WHERE r.OutcomeCode = 1;
            """));

        IReadOnlyDictionary<string, SagaMemoryDto> recalled = await f.H.Saga.Store.GetByIdsAsync(memories, CancellationToken.None);

        Assert.Equal(memories[0], Assert.Single(recalled).Key);

        SagaMemoryDto[] inspected = await f.H.Saga.Store.ListAsync(null, null, MemoryScope.Installation, 20, 0, CancellationToken.None);

        foreach (int index in Enumerable.Range(0, 10))
        {
            SagaMemoryDto memory = Assert.Single(inspected, memory => memory.Id == memories[index]);

            Assert.Equal(paraphrases[index], memory.Content);

            Assert.Equal(sessions[index], memory.SessionId);

            Assert.NotNull(memory.AttachmentProvenance);

            Assert.Equal(sessions[index], memory.AttachmentProvenance.SessionId);

            Assert.Equal(index == 0 ? null : memories[0], memory.ConsolidatedIntoMemoryId);

            Assert.Equal(index == 0 ? null : targets[0].ExpectedVersionId, memory.ConsolidatedIntoVersionId);
        }

        Assert.Equal(sourceRows, await ReadTenSourceRowsAsync(f));

        long changes = await ScalarLongAsync(f, "SELECT total_changes();");

        int sends = f.Handler.SendCount;

        Assert.True(sends > 0);

        Assert.Equal(requested.Id, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).Id);

        Assert.Equal(changes, await ScalarLongAsync(f, "SELECT total_changes();"));

        Assert.Equal(sends, f.Handler.SendCount);
    }

    private static async Task<string[]> ReadTenSourceRowsAsync(MaintenanceFixture f)
    {
        // Snapshot the real canonical bytes, including all source metadata, embedding bytes, versions, heads and attachment provenance.
        string[] queries =
        [
            "SELECT * FROM saga_memories WHERE Id LIKE '10000000-%' ORDER BY Id;",
            "SELECT * FROM saga_memory_embeddings WHERE MemoryId LIKE '10000000-%' ORDER BY MemoryId;",
            "SELECT * FROM saga_memory_attachment_provenance WHERE MemoryId LIKE '10000000-%' ORDER BY MemoryId;",
            "SELECT c.*, v.*, h.* FROM annal_claims c JOIN annal_versions v ON v.ClaimId = c.ClaimId JOIN annal_heads h ON h.ClaimId = c.ClaimId WHERE c.SubjectStoreCode = 1 AND c.SubjectId LIKE '10000000-%' ORDER BY c.SubjectId, v.Revision;",
        ];

        List<string> rows = [];

        foreach (string query in queries)
        {
            await using var command = f.H.Connection.CreateCommand();

            command.CommandText = query;

            await using var reader = await command.ExecuteReaderAsync();

            int count = 0;

            while (await reader.ReadAsync())
            {
                count++;

                for (int column = 0; column < reader.FieldCount; column++)
                {
                    object value = reader.GetValue(column);

                    rows.Add(value switch
                    {
                        DBNull => "null",
                        byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                        _ => "value:" + Convert.ToString(value, CultureInfo.InvariantCulture),
                    });
                }
            }

            Assert.Equal(10, count);
        }

        return rows.ToArray();
    }
}
