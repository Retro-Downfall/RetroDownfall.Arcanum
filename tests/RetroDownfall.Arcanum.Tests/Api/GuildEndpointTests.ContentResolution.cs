using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Operator_content_prefix_resolves_message_metadata_without_disclosing_protected_or_private_bodies()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = OperatorContentFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Content selection", "Find a visible reference",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage accepted = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using JsonDocument quest = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());

        JsonElement data = quest.RootElement.GetProperty("data");

        Guid questId = data.GetProperty("id").GetGuid();

        GuildSendOperatorMessageRequest message = new(Guid.NewGuid(), data.GetProperty("revision").GetInt64(),
            GuildMessageKind.Message, "text/plain", "Do not disclose this message while selecting its identity");

        using HttpResponseMessage sent = await client.PostAsync($"/api/guild/quests/{questId:D}/messages",
            Json(JsonSerializer.Serialize(message, GuildJsonContext.Default.GuildSendOperatorMessageRequest)));

        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);

        using JsonDocument receipt = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());

        Guid contentId;

        Guid privateInput;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var held = opened.Value;

            await using var query = held.Connection.CreateCommand();

            query.CommandText = "SELECT ContentId FROM guild_envelopes WHERE Id=$id";

            query.Parameters.AddWithValue("$id", receipt.RootElement.GetProperty("data").GetProperty("messageId").GetGuid().ToString("D").ToUpperInvariant());

            contentId = Guid.Parse((string)(await query.ExecuteScalarAsync())!);

            query.CommandText = "SELECT ContentId FROM guild_content WHERE ContentKindCode=9";

            privateInput = Guid.Parse((string)(await query.ExecuteScalarAsync())!);
        }

        string prefix = contentId.ToString("D")[..35];

        using HttpResponseMessage resolved = await client.PostAsync("/api/guild/content/resolve", Json("{\"prefix\":\"" + prefix + "\"}"));

        Assert.True(resolved.StatusCode == HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        string metadata = await resolved.Content.ReadAsStringAsync();

        Assert.DoesNotContain(message.Body, metadata, StringComparison.Ordinal);

        using JsonDocument candidates = JsonDocument.Parse(metadata);

        JsonElement reference = Assert.Single(candidates.RootElement.GetProperty("data").GetProperty("candidates").EnumerateArray());

        Assert.Equal(contentId, reference.GetProperty("contentId").GetGuid());

        Assert.False(reference.TryGetProperty("body", out _));

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var held = opened.Value;

            await using var transaction = held.Connection.BeginTransaction(deferred: false);

            var labelled = await ArtifactSensitivityLedger.WriteWithinAsync(held.Connection, transaction,
                new(SensitiveArtifactKind.GuildContent, contentId, data.GetProperty("sessionId").GetGuid(), null, null,
                    1, new CovenantDigest(Convert.FromHexString(reference.GetProperty("digest").GetString()!)),
                    ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()])), default);

            Assert.True(labelled.IsSuccess, labelled.Error.Message);

            await transaction.CommitAsync();
        }

        using HttpResponseMessage protectedMetadata = await client.PostAsync("/api/guild/content/resolve", Json("{\"prefix\":\"" + prefix + "\"}"));

        Assert.Equal(HttpStatusCode.OK, protectedMetadata.StatusCode);

        Assert.DoesNotContain(message.Body, await protectedMetadata.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage protectedBody = await client.GetAsync($"/api/guild/content/{contentId:D}?revision=1");

        Assert.Equal(HttpStatusCode.Forbidden, protectedBody.StatusCode);

        using HttpResponseMessage hidden = await client.PostAsync("/api/guild/content/resolve", Json("{\"prefix\":\"" + privateInput.ToString("D")[..35] + "\"}"));

        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);

        using JsonDocument hiddenResult = JsonDocument.Parse(await hidden.Content.ReadAsStringAsync());

        Assert.Empty(hiddenResult.RootElement.GetProperty("data").GetProperty("candidates").EnumerateArray());

        using HttpResponseMessage invalid = await client.PostAsync("/api/guild/content/resolve", Json("{\"prefix\":\"not-an-id\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
