using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Conclave;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableTheory]
    [InlineData("public", HttpStatusCode.OK)]
    [InlineData("protected", HttpStatusCode.Forbidden)]
    [InlineData("erased", HttpStatusCode.Gone)]
    public async Task Native_question_read_uses_current_checkpoint_disclosure_and_never_metadata_body(string mode, HttpStatusCode expected)
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

        var identity = await CreateNativeQuestionAsync(factory, client, mode == "protected");

        if (mode == "erased")
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            DefaultHttpContext queuedContext = new() { RequestServices = scope.ServiceProvider };

            queuedContext.Response.Body = new MemoryStream();

            IResult queued = await GuildEndpoints.ReadQuestInputAsync(identity.Quest,
                scope.ServiceProvider.GetRequiredService<GuildNativeHost>(), scope.ServiceProvider.GetRequiredService<GuildQuestStore>(), queuedContext);

            await EraseNativeQuestionAsync(factory, identity.Quest);

            await queued.ExecuteAsync(queuedContext);

            Assert.Equal(StatusCodes.Status410Gone, queuedContext.Response.StatusCode);

            queuedContext.Response.Body.Position = 0;

            using StreamReader reader = new(queuedContext.Response.Body);

            Assert.DoesNotContain("Which option should I choose?", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        using HttpResponseMessage metadata = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}");

        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);

        Assert.DoesNotContain("Which option should I choose?", await metadata.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using HttpResponseMessage response = await client.GetAsync($"/api/guild/quests/{identity.Quest:D}/input");

        Assert.Equal(expected, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        if (mode == "public")
        {
            JsonElement input = body.RootElement.GetProperty("data");

            Assert.Equal("Which option should I choose?", input.GetProperty("question").GetString());

            Assert.Equal(identity.Quest, input.GetProperty("questId").GetGuid());

            Assert.True(input.GetProperty("revision").GetInt64() > 0);

            Assert.Equal(identity.Question, input.GetProperty("awaitingInput").GetProperty("questionId").GetGuid());

            Assert.Equal(identity.Continuation, input.GetProperty("awaitingInput").GetProperty("continuationId").GetGuid());
        }
        else
        {
            Assert.DoesNotContain("Which option should I choose?", body.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static async Task<(Guid Quest, Guid Question, Guid Continuation)> CreateNativeQuestionAsync(
        ArcanumWebApplicationFactory factory, HttpClient client, bool protect, string? park = null)
    {
        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Question", "Wait for operator input",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

        SignetAuthority authority = scope.ServiceProvider.GetRequiredService<SignetAuthority>();

        GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

        var owner = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(owner.IsSuccess, owner.Error.Message);

        var found = await store.GetAsync(owner.Value, questId, default);

        Assert.True(found.IsSuccess, found.Error.Message);

        var recovery = await policy.CreateRecoveryAsync(owner.Value, questId, found.Value.Quest.OperationId,
            found.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var rebound = await authority.RebindAsync(recovery.Value, default);

        Assert.True(rebound.IsSuccess, rebound.Error.Message);

        var run = await policy.CreateRunCallerAsync(authority, rebound.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var lease = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
            .TryAcquireLeaseAsync(found.Value.Quest.OperationId, "native-input", now, now.AddMinutes(5));

        Assert.True(lease.Acquired);

        GuildQuestDriver driver = new(rebound.Value, "native-input", lease.Operation.Revision, run.Value);

        var local = await store.ReadLocalPortAsync(driver, questId, default);

        Assert.True(local.IsSuccess, local.Error.Message);

        var prepared = await store.PrepareDispatchAsync(driver, questId, local.Value.Binding.Input.Digest,
            found.Value.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        ArtifactSensitivityLabel? label = protect ? new(Guid.NewGuid(), SensitiveArtifactKind.AssistantEntry, Guid.NewGuid(),
            found.Value.Quest.SessionId, found.Value.Quest.CampaignId, null, 1, new(SHA256.HashData("source"u8)),
            ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()]), null, null, null, now) : null;

        GuildLocalWaitSnapshot snapshot = new(1, local.Value.Binding.Input.Digest, 0,
            [new() { Index = 1, Description = "Select option", Status = "pending" }],
            new() { CurrentStep = 0, ParentApprenticeId = local.Value.Binding.ParentApprenticeId,
                EscalationReason = "Which option should I choose?", LaunchRequested = true });

        Guid question = Guid.NewGuid(), continuation = Guid.NewGuid();

        var waiting = await store.WaitForInputAsync(driver, question, continuation, new(questId, snapshot, label), default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        if (park is not null)
        {
            GuildLocalInferenceExecution execution = new(store, driver, prepared.Value);

            var refreshed = await store.RefreshLocalExecutionAsync(execution, DateTimeOffset.UtcNow, false, default);

            Assert.True(refreshed.IsSuccess, refreshed.Error.Message);

            if (park == "safe")
            {
                var parked = await store.ParkLocalExecutionAsync(execution.Driver, default);

                Assert.True(parked.IsSuccess, parked.Error.Message);

                Assert.True(parked.Value.Quest.ReconciliationRequired);
            }
            else
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
                    .TryTransitionAsync(found.Value.Quest.OperationId, execution.Driver.OperationRevision,
                        execution.Driver.LeaseOwner, LongRunningOperationState.ReconciliationRequired,
                        DateTimeOffset.UtcNow, ErrorCodes.Guild.LocalExecutionInterrupted));
            }
        }

        return (questId, question, continuation);
    }

    private static async Task EraseNativeQuestionAsync(ArcanumWebApplicationFactory factory, Guid quest)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var transaction = connection.Connection.BeginTransaction();

        await using var command = connection.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO guild_content_erasure_receipts
                (ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            SELECT c.ContentId,c.SessionId,c.CampaignId,c.ContentKindCode,c.SourceRecordId,c.Revision,c.Digest,c.MimeType,length(c.Body),'2026-11-08T00:00:00.0000000Z'
            FROM guild_content c JOIN guild_local_wait_checkpoints w ON w.ContentId=c.ContentId WHERE w.QuestId=$quest
            """;

        command.Parameters.AddWithValue("$quest", quest.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "DELETE FROM guild_content WHERE ContentId IN(SELECT ContentId FROM guild_local_wait_checkpoints WHERE QuestId=$quest)";

        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        await transaction.CommitAsync();
    }
}
