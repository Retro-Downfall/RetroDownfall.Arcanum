using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// What a provider is sent is decided at capture. A memory the operator retired, or pinned, or that a consolidation already suppressed, is not a
// leaf, so its text is in no summary request, no adjudication request and no embedding request.
public sealed partial class LongRestMaintenanceTests
{
    private const string IneligibleToken = "zzineligibletoken";

    [Theory]
    [InlineData("retired")]
    [InlineData("pinned")]
    public async Task An_ineligible_memorys_text_never_reaches_a_provider_request(string state)
    {
        // Session-less sources are Global memories: a retired one keeps its eligible owner binding, so the lifecycle predicate alone decides it.
        // A Session-owned one is also excluded by its owner check once retired, which would let a broken predicate pass this test.
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: 6, sessionless: true,
            sourceText: index => index == 2 ? $"observation about {IneligibleToken} with detail{index}" : $"observation about topic{index} with detail{index} and the common words");

        string memory = (string)(await f.H.ScalarAsync($"SELECT Id FROM saga_memories WHERE Content LIKE '%{IneligibleToken}%';"))!;

        string text = (string)(await f.H.ScalarAsync($"SELECT Content FROM saga_memories WHERE Id = '{memory}';"))!;

        _ = state == "retired"
            ? await f.H.Saga.Store.RetireAsync(memory, AnnalContentDigest.ForSagaMemory(text), DateTimeOffset.UtcNow, CancellationToken.None)
            : await f.H.Saga.Store.SetPinAsync(memory, true, DateTimeOffset.UtcNow, CancellationToken.None);

        string[] memories = new string[6];

        await using (Microsoft.Data.Sqlite.SqliteCommand command = f.H.Connection.CreateCommand())
        {
            command.CommandText = "SELECT Id FROM saga_memories ORDER BY Id;";

            await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

            for (int index = 0; await reader.ReadAsync(); index++) memories[index] = reader.GetString(0);
        }

        for (int index = 0; index < memories.Length; index++)
        {
            float[] vector = new float[memories.Length];

            vector[index] = 1;

            await f.H.SetEmbeddingAsync(memories[index], vector);
        }

        int sends = 0;

        f.Handler.VectorFor = body =>
        {
            float[] vector = new float[64];

            vector[int.Parse(Regex.Match(body, @"Summary (\d+)").Groups[1].Value) % 64] = 1;

            return vector;
        };

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Summary " + ++sends + "\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.True(progress.Phase == LongRestSweepPhase.Completed, progress.DeferredReason);

        Assert.Equal(0L, await ScalarLongAsync(f, $"SELECT count(*) FROM long_rest_discovery_leaves WHERE MemoryId = '{memory}';"));

        Assert.Equal(5L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_discovery_leaves;"));

        // The control: the other five memories' text did go out, so the absence below is the capture's doing and not an empty sweep.
        string[] requests = [.. f.Payloads.Select(static payload => payload.Kind == LongRestProviderPayloadKind.Embedding
            ? payload.EmbeddingText! : LongRestProviderCallFreezer.RenderChat(payload).Value.UserPrompt)];

        Assert.Contains(requests, static request => request.Contains("detail0", StringComparison.Ordinal));

        Assert.DoesNotContain(requests, static request => request.Contains(IneligibleToken, StringComparison.Ordinal));

        Assert.Equal(f.Payloads.Count, f.Handler.SendCount);

        Assert.DoesNotContain(f.Handler.LastBody!, IneligibleToken, StringComparison.Ordinal);
    }
}
