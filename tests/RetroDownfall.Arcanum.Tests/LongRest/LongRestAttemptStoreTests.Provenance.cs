using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestAttemptStoreTests
{
    [Fact]
    public async Task Original_physical_work_is_captured_by_Prepare_and_survives_disposition_until_next_exact_Prepare()
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        await LongRestProvenanceTestEvidence.RequireSchemaAsync(h);

        LongRestWorkIdentity original = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(original,
            LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        string originalJson = await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original);

        LongRestDispatchAttempt sent = LongRestSweepHarness.Require(await store.MarkSentAsync(prepared, CancellationToken.None));

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        Assert.True((await store.PrepareAttemptAsync(original, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None)).IsFailure);

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        LongRestDispatchCompletion completion = new(LongRestDispatchAttemptState.Invalid, new(4, 1, 5, 0, 0), 0.25m, "LongRest.InvalidOutput");

        LongRestDispatchAttempt invalid = LongRestSweepHarness.Require(await store.CompleteAttemptAsync(sent, completion, CancellationToken.None));

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        Assert.Equal(invalid, LongRestSweepHarness.Require(await store.CompleteAttemptAsync(sent, completion, CancellationToken.None)));

        Assert.True((await store.CompleteAttemptAsync(sent, completion with { CostUsd = 0.5m }, CancellationToken.None)).IsFailure);

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        LongRestDispatchAttempt interrupted = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(original,
            LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        interrupted = LongRestSweepHarness.Require(await store.MarkSentAsync(interrupted, CancellationToken.None));

        Assert.Equal(2UL, interrupted.PhysicalOrdinal);

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        LongRestRecipeIdentity recipe = JsonSerializer.Deserialize(Assert.IsType<string>(await h.ScalarAsync(
            "SELECT RecipeJson FROM long_rest_discovery_generations;")), LongRestJsonContext.Default.LongRestRecipeIdentity)!;

        LongRestSweepFence bootB = original.Fence with { Configuration = original.Fence.Configuration with { BootId = Guid.NewGuid(), Epoch = 9 } };

        LongRestSweepHarness.Require(await h.Store.StartOrResumeAsync(original.OperationId, original.Scope, bootB, recipe, CancellationToken.None));

        Assert.Equal(6L, await h.ScalarAsync("SELECT StateCode FROM long_rest_provider_effects;"));

        Assert.Equal(DBNull.Value, await h.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));

        Assert.Equal(interrupted.AttemptId.ToString("D").ToUpperInvariant(), await h.ScalarAsync("SELECT AttemptId FROM long_rest_provider_effects;"));

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        Assert.Equal(2L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT InvalidCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.True((await store.CompleteAttemptAsync(interrupted,
            new(LongRestDispatchAttemptState.Accepted, new(5, 1, 6, 0, 0), 0.5m, null), CancellationToken.None)).IsFailure);

        Assert.Equal(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, original));

        LongRestWorkIdentity current = await WorkAsync(h);

        Assert.Equal(bootB, current.Fence);

        LongRestDispatchAttempt fresh = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(current,
            LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        Assert.Equal(3UL, fresh.PhysicalOrdinal);

        Assert.NotEqual(interrupted.AttemptId, fresh.AttemptId);

        Assert.NotEqual(originalJson, await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, current));

        Assert.Equal(2L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT InvalidCount FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT FailureCount FROM long_rest_provider_effects;"));

        Assert.Equal(4L, await h.ScalarAsync("SELECT InputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT OutputTokens FROM long_rest_provider_effects;"));

        Assert.Equal(5L, await h.ScalarAsync("SELECT TotalTokens FROM long_rest_provider_effects;"));

        Assert.Equal("0.25", await h.ScalarAsync("SELECT CostUsd FROM long_rest_provider_effects;"));

        Assert.Equal(DBNull.Value, await h.ScalarAsync("SELECT CompletionJson FROM long_rest_provider_effects;"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("hash-mismatch")]
    public async Task Invalid_original_physical_work_refuses_before_send_or_attempt_replacement(string corruption)
    {
        await using LongRestSweepHarness h = await FixtureAsync();

        await LongRestProvenanceTestEvidence.RequireSchemaAsync(h);

        LongRestWorkIdentity work = await WorkAsync(h);

        LongRestAttemptStore store = new(h.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt prepared = LongRestSweepHarness.Require(await store.PrepareAttemptAsync(work,
            LongRestProviderPayloadKind.Summary, Payload, Destination, CancellationToken.None));

        await LongRestProvenanceTestEvidence.RequireOriginalAsync(h, work);

        string bad = corruption == "malformed" ? "{{" : JsonSerializer.Serialize(LongRestStoredWork.From(
            work with { ItemId = work.ItemId + ":changed" }), LongRestProvenanceTestJsonContext.Default.LongRestStoredWork);

        object? original = await h.ScalarAsync(LongRestProvenanceTestEvidence.PhysicalSnapshotSql);

        await h.ExecuteAsync("CREATE TEMP TABLE fixture_original_effect AS SELECT * FROM long_rest_provider_effects;");

        // Corrupt a clone of a real Prepared row through insertion, leaving any immutable
        // update guard intact. Structural schema rejection is itself an acceptable refusal.
        await h.ExecuteAsync("DELETE FROM long_rest_provider_effects;");

        try
        {
            Exception? insert = await Record.ExceptionAsync(() => h.ExecuteAsync("""
                INSERT INTO long_rest_provider_effects (WorkItemId, WorkDigest, OriginalWorkJson, AttemptId, PayloadKindCode, PhysicalOrdinal,
                    Revision, StateCode, PayloadDigest, DestinationDigest, DisclosureReceiptDigest, SentCount, PaidCount, InvalidCount,
                    FailureCount, InputTokens, OutputTokens, TotalTokens, CachedInputTokens, ReasoningTokens, CostUsd, CompletionJson, FailureCode, UpdatedAtUtc)
                SELECT WorkItemId, WorkDigest, $bad, AttemptId, PayloadKindCode, PhysicalOrdinal, Revision, StateCode, PayloadDigest,
                    DestinationDigest, DisclosureReceiptDigest, SentCount, PaidCount, InvalidCount, FailureCount, InputTokens, OutputTokens,
                    TotalTokens, CachedInputTokens, ReasoningTokens, CostUsd, CompletionJson, FailureCode, UpdatedAtUtc FROM fixture_original_effect;
                """, ("$bad", bad)));

            if (insert is not null)
            {
                Assert.Equal(19, Assert.IsType<SqliteException>(insert).SqliteErrorCode);

                Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));
            }
            else
            {
                object? corrupt = await h.ScalarAsync(LongRestProvenanceTestEvidence.PhysicalSnapshotSql);

                Assert.NotEqual(original, corrupt);

                Assert.True((await store.ValidateAttemptAsync(prepared, CancellationToken.None)).IsFailure);

                Assert.True((await store.MarkSentAsync(prepared, CancellationToken.None)).IsFailure);

                Assert.True((await store.PrepareAttemptAsync(work, LongRestProviderPayloadKind.Embedding, Payload, Destination, CancellationToken.None)).IsFailure);

                Assert.Equal(corrupt, await h.ScalarAsync(LongRestProvenanceTestEvidence.PhysicalSnapshotSql));

                Assert.Equal(0L, await h.ScalarAsync("SELECT SentCount FROM long_rest_provider_effects;"));

                Assert.Equal(0L, await h.ScalarAsync("SELECT PaidCount FROM long_rest_provider_effects;"));
            }
        }
        finally
        {
            await h.ExecuteAsync("DELETE FROM long_rest_provider_effects;");

            await h.ExecuteAsync("INSERT INTO long_rest_provider_effects SELECT * FROM fixture_original_effect;");

            await h.ExecuteAsync("DROP TABLE fixture_original_effect;");
        }

        Assert.Equal(original, await h.ScalarAsync(LongRestProvenanceTestEvidence.PhysicalSnapshotSql));

        Assert.True((await store.ValidateAttemptAsync(prepared, CancellationToken.None)).IsSuccess);

        Assert.Equal(0, h.SummarySends);

        Assert.Equal(0, h.EmbeddingSends);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LongRestStoredWork))]
internal sealed partial class LongRestProvenanceTestJsonContext : JsonSerializerContext;

internal static class LongRestProvenanceTestEvidence
{
    internal static async Task RequireSchemaAsync(LongRestSweepHarness h)
    {
        Assert.True((long)(await h.ScalarAsync("SELECT count(*) FROM pragma_table_info('long_rest_provider_effects') WHERE name = 'OriginalWorkJson' AND upper(type) = 'TEXT' AND \"notnull\" = 1;"))! == 1,
            "Long Rest physical attempts require the bounded NOT NULL TEXT OriginalWorkJson schema contract before provenance behavior can execute.");
    }

    internal static async Task<string> RequireOriginalAsync(LongRestSweepHarness h, LongRestWorkIdentity expected)
    {
        await using SqliteCommand command = h.Connection.CreateCommand();

        command.CommandText = "SELECT effect.OriginalWorkJson, effect.WorkDigest FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId WHERE unit.KindCode = 2;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        string json = reader.GetString(0);

        LongRestWorkIdentity original = (JsonSerializer.Deserialize(json, LongRestProvenanceTestJsonContext.Default.LongRestStoredWork)
            ?? throw new InvalidDataException("Physical provenance did not contain a stored Work.")).ToDomain();

        Assert.Equal(expected, original);

        Assert.Equal(LongRestSweepHarness.Require(LongRestDiscoveryHash.Work(original)).Bytes, (byte[])reader[1]);

        Assert.False(await reader.ReadAsync());

        return json;
    }

    internal const string PhysicalSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(WorkItemId, hex(WorkDigest), OriginalWorkJson, AttemptId, PayloadKindCode, PhysicalOrdinal, Revision, StateCode,
                hex(PayloadDigest), hex(DestinationDigest), hex(DisclosureReceiptDigest), SentCount, PaidCount, InvalidCount, FailureCount,
                InputTokens, OutputTokens, TotalTokens, CachedInputTokens, ReasoningTokens, CostUsd, CompletionJson, FailureCode, UpdatedAtUtc) AS Row
            FROM long_rest_provider_effects ORDER BY WorkItemId);
        """;
}
