using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableTheory]
    [InlineData("public", HttpStatusCode.OK)]
    [InlineData("protected", HttpStatusCode.Forbidden)]
    [InlineData("erased", HttpStatusCode.Gone)]
    [InlineData("uncertain", HttpStatusCode.NotFound)]
    [InlineData("unsettled", HttpStatusCode.NotFound)]
    [InlineData("mismatched", HttpStatusCode.NotFound)]
    public async Task Native_parked_question_remains_discoverable_only_at_the_proven_input_frontier(string mode, HttpStatusCode expected)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        var identity = await CreateNativeQuestionAsync(factory, client, mode == "protected",
            mode == "uncertain" ? "uncertain" : "safe");

        if (mode == "erased") await EraseNativeQuestionAsync(factory, identity.Quest);

        if (mode is "unsettled" or "mismatched")
            await ContradictParkedQuestionEvidenceAsync(factory, identity.Quest, mode);

        // The issuing scope has ended; the HTTP read must recover the exact durable question.
        using HttpResponseMessage metadata = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}");

        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);

        string metadataText = await metadata.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Which option should I choose?", metadataText, StringComparison.Ordinal);

        using JsonDocument current = JsonDocument.Parse(metadataText);

        JsonElement data = current.RootElement.GetProperty("data");

        Assert.True(data.GetProperty("reconciliationRequired").GetBoolean());

        JsonElement input = data.GetProperty("awaitingInput");

        if (mode is "uncertain" or "unsettled" or "mismatched")
        {
            Assert.Equal(JsonValueKind.Null, input.ValueKind);
        }
        else
        {
            Assert.Equal(JsonValueKind.Object, input.ValueKind);

            Assert.Equal(identity.Question, input.GetProperty("questionId").GetGuid());

            Assert.Equal(identity.Continuation, input.GetProperty("continuationId").GetGuid());
        }

        using HttpResponseMessage question = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}/input");

        Assert.Equal(expected, question.StatusCode);

        string questionText = await question.Content.ReadAsStringAsync();

        if (mode != "public")
        {
            Assert.DoesNotContain("Which option should I choose?", questionText, StringComparison.Ordinal);

            if (mode == "uncertain")
            {
                GuildContinueRequest retained = new(Guid.NewGuid(), data.GetProperty("revision").GetInt64(),
                    identity.Continuation, "text/plain", "Do not restart uncertain work");

                using HttpResponseMessage refused = await client.PostAsync($"/api/guild/quests/{identity.Quest:D}/continue",
                    Json(JsonSerializer.Serialize(retained, GuildJsonContext.Default.GuildContinueRequest)));

                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }

            return;
        }

        using JsonDocument body = JsonDocument.Parse(questionText);

        Assert.Equal("Which option should I choose?", body.RootElement.GetProperty("data").GetProperty("question").GetString());

        GuildContinueRequest answer = new(Guid.NewGuid(), data.GetProperty("revision").GetInt64(),
            input.GetProperty("continuationId").GetGuid(), "text/plain", "Choose the first option");

        using HttpResponseMessage continued = await client.PostAsync($"/api/guild/quests/{identity.Quest:D}/continue",
            Json(JsonSerializer.Serialize(answer, GuildJsonContext.Default.GuildContinueRequest)));

        Assert.Equal(HttpStatusCode.Accepted, continued.StatusCode);

        using HttpResponseMessage consumed = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}/input");

        Assert.Equal(HttpStatusCode.NotFound, consumed.StatusCode);
    }

    private static async Task ContradictParkedQuestionEvidenceAsync(ArcanumWebApplicationFactory factory, Guid quest, string mode)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var transaction = connection.Connection.BeginTransaction();

        await using var command = connection.Connection.CreateCommand();

        command.Transaction = transaction;

        // Retained evidence can disagree even though individual rows satisfy their SQL checks.
        // Begin with a genuine parked wait, then prove the public read rejects the contradiction.
        command.CommandText = mode == "unsettled"
            ? "UPDATE guild_dispatches SET EvidenceCode=2,Revision=Revision+1 WHERE QuestId=$quest AND EvidenceCode=1"
            : "UPDATE guild_quests SET QuestionId=$question,Revision=Revision+1 WHERE Id=$quest";

        command.Parameters.AddWithValue("$quest", quest.ToString("D").ToUpperInvariant());

        if (mode == "mismatched") command.Parameters.AddWithValue("$question", Guid.NewGuid().ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        await transaction.CommitAsync();
    }
}
