using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Operator_content_requires_exact_revision_and_omits_body_from_metadata()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = OperatorContentFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Content", "Exact original goal",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage accepted = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        string metadata = await accepted.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Exact original goal", metadata, StringComparison.Ordinal);

        using JsonDocument quest = JsonDocument.Parse(metadata);

        JsonElement reference = quest.RootElement.GetProperty("data").GetProperty("goalRef");

        Guid id = reference.GetProperty("contentId").GetGuid();

        long revision = reference.GetProperty("revision").GetInt64();

        using HttpResponseMessage body = await client.GetAsync($"/api/guild/content/{id:D}?revision={revision}");

        Assert.True(body.StatusCode == HttpStatusCode.OK, await body.Content.ReadAsStringAsync());

        using JsonDocument content = JsonDocument.Parse(await body.Content.ReadAsStringAsync());

        Assert.Equal("Exact original goal", content.RootElement.GetProperty("data").GetProperty("body").GetString());

        using HttpResponseMessage absentRevision = await client.GetAsync($"/api/guild/content/{id:D}");

        Assert.Equal(HttpStatusCode.BadRequest, absentRevision.StatusCode);

        using HttpResponseMessage wrongRevision = await client.GetAsync($"/api/guild/content/{id:D}?revision={revision + 1}");

        Assert.Equal(HttpStatusCode.NotFound, wrongRevision.StatusCode);

        using HttpResponseMessage unknown = await client.GetAsync($"/api/guild/content/{Guid.NewGuid():D}?revision=1");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        Guid privateInput;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            DefaultHttpContext bufferedContext = new() { RequestServices = scope.ServiceProvider };

            bufferedContext.Response.Body = new MemoryStream();

            IResult buffered = await GuildEndpoints.ReadOperatorContentAsync(id, revision,
                scope.ServiceProvider.GetRequiredService<GuildNativeHost>(), scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>(),
                scope.ServiceProvider.GetRequiredService<GuildStore>(), scope.ServiceProvider.GetRequiredService<GuildQuestStore>(), bufferedContext);

            var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var held = opened.Value;

            await using var transaction = held.Connection.BeginTransaction(deferred: false);

            await using var query = held.Connection.CreateCommand();

            query.Transaction = transaction;

            query.CommandText = "SELECT ContentId FROM guild_content WHERE ContentKindCode=9";

            privateInput = Guid.Parse((string)(await query.ExecuteScalarAsync())!);

            JsonElement data = quest.RootElement.GetProperty("data");

            var labelled = await ArtifactSensitivityLedger.WriteWithinAsync(held.Connection, transaction,
                new(SensitiveArtifactKind.GuildContent, id, data.GetProperty("sessionId").GetGuid(), null, null,
                    checked((ulong)revision), new CovenantDigest(Convert.FromHexString(reference.GetProperty("digest").GetString()!)),
                    ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()])), default);

            Assert.True(labelled.IsSuccess, labelled.Error.Message);

            await transaction.CommitAsync();

            await buffered.ExecuteAsync(bufferedContext);

            Assert.Equal(StatusCodes.Status403Forbidden, bufferedContext.Response.StatusCode);

            bufferedContext.Response.Body.Position = 0;

            using StreamReader bufferedReader = new(bufferedContext.Response.Body);

            Assert.DoesNotContain("Exact original goal", await bufferedReader.ReadToEndAsync(), StringComparison.Ordinal);

        }

        using HttpResponseMessage privateResponse = await client.GetAsync($"/api/guild/content/{privateInput:D}?revision=1");

        Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);

        using HttpResponseMessage protectedResponse = await client.GetAsync($"/api/guild/content/{id:D}?revision={revision}");

        Assert.Equal(HttpStatusCode.Forbidden, protectedResponse.StatusCode);

        Assert.DoesNotContain("Exact original goal", await protectedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_message_replays_exact_receipt_before_revision_check(bool preserveWhitespace)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = OperatorContentFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Message", "Receive native message",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage accepted = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using JsonDocument quest = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());

        Guid id = quest.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        GuildSendOperatorMessageRequest message = new(Guid.NewGuid(), quest.RootElement.GetProperty("data").GetProperty("revision").GetInt64(),
            GuildMessageKind.Message, "text/plain", preserveWhitespace ? "  Operator information\r\n\n" : "Operator information");

        string body = JsonSerializer.Serialize(message, GuildJsonContext.Default.GuildSendOperatorMessageRequest);

        using HttpResponseMessage first = await client.PostAsync($"/api/guild/quests/{id:D}/messages", Json(body));

        Assert.True(first.StatusCode == HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());

        using JsonDocument receipt = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        Guid messageId = receipt.RootElement.GetProperty("data").GetProperty("messageId").GetGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using var held = opened.Value;

            await using var query = held.Connection.CreateCommand();

            query.CommandText = "SELECT ContentId FROM guild_envelopes WHERE Id=$id";

            query.Parameters.AddWithValue("$id", messageId.ToString("D").ToUpperInvariant());

            Guid contentId = Guid.Parse((string)(await query.ExecuteScalarAsync())!);

            using HttpResponseMessage exact = await client.GetAsync($"/api/guild/content/{contentId:D}?revision=1");

            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);

            using JsonDocument exactBody = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());

            Assert.Equal(message.Body, exactBody.RootElement.GetProperty("data").GetProperty("body").GetString());
        }

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/quests/{id:D}/messages", Json(body));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(receipt.RootElement.GetProperty("data").GetProperty("messageId").GetGuid(), replayed.RootElement.GetProperty("data").GetProperty("messageId").GetGuid());

        Assert.True(replayed.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        using HttpResponseMessage collision = await client.PostAsync($"/api/guild/quests/{id:D}/messages",
            Json(JsonSerializer.Serialize(message with { Body = "Changed" }, GuildJsonContext.Default.GuildSendOperatorMessageRequest)));

        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);

        using HttpResponseMessage changedRevision = await client.PostAsync($"/api/guild/quests/{id:D}/messages",
            Json(JsonSerializer.Serialize(message with { ExpectedRevision = message.ExpectedRevision + 1 },
                GuildJsonContext.Default.GuildSendOperatorMessageRequest)));

        Assert.Equal(HttpStatusCode.Conflict, changedRevision.StatusCode);
    }

    [SkippableFact]
    public async Task Operator_continuation_message_replays_after_the_question_is_consumed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = OperatorContentFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        var identity = await CreateNativeQuestionAsync(factory, client, false);

        using HttpResponseMessage current = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}");

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);

        using JsonDocument quest = JsonDocument.Parse(await current.Content.ReadAsStringAsync());

        GuildSendOperatorMessageRequest message = new(Guid.NewGuid(), quest.RootElement.GetProperty("data").GetProperty("revision").GetInt64(),
            GuildMessageKind.Continuation, "text/plain", "  Preserve this answer\r\n");

        string body = JsonSerializer.Serialize(message, GuildJsonContext.Default.GuildSendOperatorMessageRequest);

        using HttpResponseMessage accepted = await client.PostAsync($"/api/guild/quests/{identity.Quest:D}/messages", Json(body));

        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());

        using JsonDocument receipt = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());

        Assert.Equal(identity.Continuation, receipt.RootElement.GetProperty("data").GetProperty("messageId").GetGuid());

        using HttpResponseMessage consumed = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}/input");

        Assert.Equal(HttpStatusCode.NotFound, consumed.StatusCode);

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/quests/{identity.Quest:D}/messages", Json(body));

        Assert.True(replay.StatusCode == HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(receipt.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("id").GetGuid(),
            repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("id").GetGuid());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        using HttpResponseMessage conflict = await client.PostAsync($"/api/guild/quests/{identity.Quest:D}/messages",
            Json(JsonSerializer.Serialize(message with { Body = "Different answer" }, GuildJsonContext.Default.GuildSendOperatorMessageRequest)));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [SkippableFact]
    public async Task Operator_messages_refuse_new_admission_when_guild_is_disabled()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = OperatorContentFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Message", "Receive native message",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage accepted = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using JsonDocument quest = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());

        Guid id = quest.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        GuildSendOperatorMessageRequest message = new(Guid.NewGuid(), quest.RootElement.GetProperty("data").GetProperty("revision").GetInt64(),
            GuildMessageKind.Message, "text/plain", "Accepted before disable");

        string payload = JsonSerializer.Serialize(message, GuildJsonContext.Default.GuildSendOperatorMessageRequest);

        using HttpResponseMessage first = await client.PostAsync($"/api/guild/quests/{id:D}/messages", Json(payload));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        factory.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue.Features.Guild = false;

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/quests/{id:D}/messages", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using HttpResponseMessage response = await client.PostAsync($"/api/guild/quests/{id:D}/messages",
            Json(JsonSerializer.Serialize(message with { IdempotencyKey = Guid.NewGuid() }, GuildJsonContext.Default.GuildSendOperatorMessageRequest)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static ArcanumWebApplicationFactory OperatorContentFactory() => new()
    {
        SettingsOverride = settings =>
        {
            settings.Features.Guild = true;

            settings.Features.Apprentices = true;

            return settings;
        },
        ServiceOverrides = HoldExecution,
    };
}
