using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The cost guard of discovery quality. Paid adjudication is the expensive call, and a sweep over memories that have nothing to do with
// one another must not make any: not through the words they all share, not through their vectors, and not through their summaries.
public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(40)]
    public async Task A_sweep_over_unrelated_memories_makes_no_adjudication_call(int sourceCount)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, sourceCount: sourceCount,
            sourceText: index => $"observation about topic{index} with detail{index} and the common words");

        // Mutually orthogonal raw vectors, and a distinct summary vector for every summary the provider writes.
        string[] memories = [.. Enumerable.Range(0, sourceCount).Select(static _ => "")];

        await using (Microsoft.Data.Sqlite.SqliteCommand command = f.H.Connection.CreateCommand())
        {
            command.CommandText = "SELECT Id FROM saga_memories ORDER BY Id;";

            await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

            for (int index = 0; await reader.ReadAsync(); index++) memories[index] = reader.GetString(0);
        }

        for (int index = 0; index < sourceCount; index++)
        {
            float[] vector = new float[sourceCount];

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

        // The tree was built, and paid for: every fold's summary and embedding. Not one adjudication request was sent.
        Assert.Contains(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Summary);

        Assert.DoesNotContain(f.Payloads, static payload => payload.Kind == LongRestProviderPayloadKind.Adjudication);

        Assert.Equal(f.Payloads.Count, f.Handler.SendCount);

        Assert.Equal(0L, await ScalarLongAsync(f, "SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await UnsettledEffectsAsync(f));
    }
}
