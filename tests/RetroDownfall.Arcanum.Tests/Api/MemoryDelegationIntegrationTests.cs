using Microsoft.Extensions.AI;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Packaging;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class MemoryDelegationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_wrapper_discloses_selected_child_and_parent_and_atomically_persists_protected_session_entries(bool campaignBound)
    {
        await using var provider = await PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.StartAsync();
        const string credential = "ARCANUM_DELEGATION_HTTP_TEST_KEY";
        string? previous = global::System.Environment.GetEnvironmentVariable(credential);
        global::System.Environment.SetEnvironmentVariable(credential, PublishedArcanumSessionSmokeTests.ProviderKey);
        using var observationStop = new CancellationTokenSource();
        Task<Guid>? eventEvidence = null;

        try
        {
            await using var factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true, configure: settings =>
            {
                settings.DefaultModel = PublishedArcanumSessionSmokeTests.ExpectedModel;
                settings.Providers = [new ProviderSettings
                {
                    Name = "published-smoke", Type = AiProviderKind.OpenAICompatible, Endpoint = provider.Endpoint.AbsoluteUri,
                    CredentialEnvironmentVariable = credential,
                    Models = [new ModelEntry(PublishedArcanumSessionSmokeTests.ExpectedModel, SupportsTools: true)], ContextWindowLimit = 8192
                }];
                settings.Features = new FeatureSettings
                {
                    Apprentices = false, Lexicon = false, ArchiveSearch = false, Metrics = false, Scrying = false,
                    Attachments = false, Reasoning = false, WorkspaceChecks = false, Annals = false, Covenant = true
                };
                PublishedArcanumSessionSmokeTests.ConfigureEvaluationPricing(settings);
            });
            var originals = factory.ServiceOverrides;
            factory.ServiceOverrides = services =>
            {
                originals!(services);
                // The generic route fixture substitutes inference by default. Restore the stock host Wizard;
                // source reader, runner, coordinator, accounting and persistence remain composed normally.
                services.RemoveAll<IArcanumIntelligenceProvider>();
                services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
            };

            using var client = factory.CreateAuthenticatedClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Guid? campaignId = null;
            if (campaignBound)
            {
                string campaignRoot = Path.Combine(factory.TempHome, "delegation-campaign");
                Directory.CreateDirectory(campaignRoot);
                using var register = await client.PostAsync("api/campaigns", JsonContent.Create(
                    new RegisterCampaignRequest("delegation", campaignRoot, WorkspaceType.Campaign, null),
                    ArcanumJsonContext.Default.RegisterCampaignRequest), deadline.Token);
                Assert.Equal(HttpStatusCode.Created, register.StatusCode);
                var registered = await JsonSerializer.DeserializeAsync(await register.Content.ReadAsStreamAsync(deadline.Token),
                    ArcanumJsonContext.Default.ApiResponseCampaignDto, deadline.Token);
                Assert.True(registered!.IsSuccess);
                campaignId = registered.Data!.Id;
            }
            var evidence = await PublishedArcanumSessionSmokeTests.ExerciseMemoryDelegationAsync(client, provider, 0, deadline.Token,
                sessionId =>
                {
                    var hub = factory.Services.GetRequiredService<SessionEventHub>();
                    var subscription = hub.SubscribeAsync(sessionId, observationStop.Token).GetAsyncEnumerator(observationStop.Token);
                    Task<bool> first = subscription.MoveNextAsync().AsTask();
                    Assert.Equal(1, hub.GetSubscriberCount(sessionId));
                    eventEvidence = ObserveFirstToolEventAsync(factory, subscription, first, sessionId, observationStop.Token);
                    return Task.CompletedTask;
                }, campaignId);
            Assert.Equal(evidence.CallEntryId, await eventEvidence!.WaitAsync(deadline.Token));

            using var scope = factory.Services.CreateScope();
            var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(deadline.Token);
            Guid generation = scope.ServiceProvider.GetRequiredService<ICovenantRuntimeGenerationProvider>().Current.Availability.DatasetGeneration!.Value;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT RunId FROM LongRunningOperations WHERE Kind = $kind";
            command.Parameters.AddWithValue("$kind", "subagent");
            string childId = Assert.IsType<string>(await command.ExecuteScalarAsync(deadline.Token));
            command.Parameters.Clear();
            command.CommandText = "SELECT SubjectId, SensitivityCode, ExactGenerationIds FROM external_disclosure_receipts WHERE SubjectKind = $kind AND EffectCategoryCode = $category ORDER BY DisclosedAtUtc, SubjectOrdinal";
            command.Parameters.AddWithValue("$kind", (int)CovenantDisclosureSubjectKind.Turn);
            command.Parameters.AddWithValue("$category", (int)CovenantDisclosureEffectCategory.ProviderDispatch);
            List<string> subjects = [];
            await using (var rows = await command.ExecuteReaderAsync(deadline.Token))
            {
                while (await rows.ReadAsync(deadline.Token))
                {
                    subjects.Add(rows.GetString(0));
                    Assert.Equal((int)ContentSensitivity.CovenantDerived, rows.GetInt32(1));
                    AssertGeneration((byte[])rows[2], generation);
                }
            }
            Assert.Equal(3, subjects.Count);
            Assert.Equal(subjects[0], subjects[2]);
            Assert.NotEqual(subjects[0], subjects[1]);
            Assert.Equal(Guid.Parse(childId), Guid.Parse(subjects[1]));

            foreach (Guid entryId in new[] { evidence.CallEntryId, evidence.ResultEntryId, evidence.AssistantEntryId })
            {
                command.Parameters.Clear();
                command.CommandText = "SELECT SensitivityCode, ExactGenerationIds, ArtifactContentDigest, ArtifactRevision FROM artifact_sensitivity WHERE ArtifactKindCode = $kind AND lower(replace(ArtifactId, '-', '')) = $entry AND lower(replace(SessionId, '-', '')) = $session";
                command.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.AssistantEntry);
                command.Parameters.AddWithValue("$entry", entryId.ToString("N"));
                command.Parameters.AddWithValue("$session", evidence.SessionId.ToString("N"));
                await using var label = await command.ExecuteReaderAsync(deadline.Token);
                Assert.True(await label.ReadAsync(deadline.Token));
                Assert.Equal((int)ContentSensitivity.CovenantDerived, label.GetInt32(0));
                AssertGeneration((byte[])label[1], generation);
                Assert.Equal(32, ((byte[])label[2]).Length);
                Assert.True(label.GetInt64(3) > 0);
                Assert.False(await label.ReadAsync(deadline.Token));
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT MaximumSensitivityCode, TaintedArtifactCount FROM session_sensitivity_state WHERE lower(replace(SessionId, '-', '')) = $session";
            command.Parameters.AddWithValue("$session", evidence.SessionId.ToString("N"));
            await using (var state = await command.ExecuteReaderAsync(deadline.Token))
            {
                Assert.True(await state.ReadAsync(deadline.Token));
                Assert.Equal((int)ContentSensitivity.CovenantDerived, state.GetInt32(0));
                Assert.True(state.GetInt32(1) >= 3);
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT count(*), sum(InputTokens), sum(OutputTokens) FROM BillableOperations";
            await using var usage = await command.ExecuteReaderAsync(deadline.Token);
            Assert.True(await usage.ReadAsync(deadline.Token));
            Assert.Equal(3, usage.GetInt32(0));
            Assert.Equal(60, usage.GetInt32(1));
            Assert.Equal(12, usage.GetInt32(2));
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(credential, previous);
            await observationStop.CancelAsync();
            if (eventEvidence is not null)
            {
                try { await eventEvidence; }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_protected_persistence_requires_claim_without_enabling_maintenance_or_widening_other_surfaces(bool campaignBound)
    {
        ArcanumSettings settings = new();
        settings.Features.LongRest = false;
        settings.Features.CampaignRollups = false;
        var request = new PingRequest("parent", SessionId: Guid.NewGuid());
        var invocation = campaignBound ? InvocationContexts.AttendedSession() : InvocationContexts.AttendedGlobalOnlySession();
        Assert.False(CampaignRollupTurnPreparer.IsLiveEligible(settings, request, invocation));
        Assert.True(CampaignRollupTurnPreparer.IsLiveEligible(settings, request, invocation, requireProtectedToolPersistence: true));
        Assert.False(settings.Features.LongRest);
        Assert.False(settings.ResolveIntelligence().EnableCampaignRollups);
        foreach (var excluded in new[] { InvocationContexts.StatelessOperator(), InvocationContexts.NoContextSession(), ArcanumInvocationContext.None })
            Assert.False(CampaignRollupTurnPreparer.IsLiveEligible(settings, request, excluded, requireProtectedToolPersistence: true));
        Assert.False(CampaignRollupTurnPreparer.IsLiveEligible(settings,
            request with { StatelessMessages = [new("user", "explicit")] }, invocation, requireProtectedToolPersistence: true));
    }

    [Fact]
    public void Sdk_function_call_continuation_binds_wire_fields_and_keeps_unknown_opaque_metadata_refused()
    {
        const string arguments = "{\"prompt\":\"child\",\"max_tokens\":256}";
        var call = new FunctionCallContent("sdk-call", "delegate_task", new Dictionary<string, object?> { ["prompt"] = "child", ["max_tokens"] = 256 })
        {
            RawRepresentation = OpenAI.Chat.ChatToolCall.CreateFunctionToolCall("sdk-call", "delegate_task", BinaryData.FromString(arguments)),
            AdditionalProperties = new() { ["known"] = "retained" },
            Annotations = [],
            Exception = new InvalidOperationException("synthetic mapping status"),
            InformationalOnly = true
        };
        List<ChatMessage> messages = [];
        ToolExecutionPipeline.AppendToolExchangeToMessages(messages, call, "sdk-call", "summary");
        Assert.NotEmpty(ModelCallPayloadFingerprint.Compute(messages, new()));
        var retained = Assert.IsType<FunctionCallContent>(Assert.Single(messages[0].Contents));
        Assert.Equal("sdk-call", retained.CallId);
        Assert.Equal("delegate_task", retained.Name);
        Assert.Equal("child", retained.Arguments!["prompt"]);
        Assert.Equal(256, retained.Arguments["max_tokens"]);
        Assert.Same(call.Arguments, retained.Arguments);
        Assert.Same(call.AdditionalProperties, retained.AdditionalProperties);
        Assert.Same(call.Annotations, retained.Annotations);
        Assert.Same(call.Exception, retained.Exception);
        Assert.True(retained.InformationalOnly);
        Assert.IsType<OpenAI.Chat.ChatToolCall>(call.RawRepresentation);
        Assert.Equal("sdk-call", Assert.IsType<FunctionResultContent>(Assert.Single(messages[1].Contents)).CallId);
        List<ChatMessage> opaque = [];
        ToolExecutionPipeline.AppendToolExchangeToMessages(opaque, new("opaque", "delegate_task", new Dictionary<string, object?>()) { RawRepresentation = new object() }, "opaque", "summary");
        Assert.Empty(ModelCallPayloadFingerprint.Compute(opaque, new()));
        var unknownRaw = new object();
        List<ChatMessage> missingId = [];
        ToolExecutionPipeline.AppendToolExchangeToMessages(missingId,
            new("", "ordinary_tool", new Dictionary<string, object?>()) { RawRepresentation = unknownRaw }, "minted-id", "summary");
        var missing = Assert.IsType<FunctionCallContent>(Assert.Single(missingId[0].Contents));
        Assert.Equal("minted-id", missing.CallId);
        Assert.Same(unknownRaw, missing.RawRepresentation);
        Assert.Empty(ModelCallPayloadFingerprint.Compute(missingId, new()));
        call.AdditionalProperties = new() { ["opaque"] = new object() };
        List<ChatMessage> opaqueProperty = [];
        ToolExecutionPipeline.AppendToolExchangeToMessages(opaqueProperty, call, "sdk-call", "summary");
        Assert.Empty(ModelCallPayloadFingerprint.Compute(opaqueProperty, new()));
        List<ChatMessage> ordinary = [];
        ToolExecutionPipeline.AppendToolExchangeToMessages(ordinary, new("ordinary", "ordinary_tool", new Dictionary<string, object?>()), "ordinary", "clean");
        Assert.NotEmpty(ModelCallPayloadFingerprint.Compute(ordinary, new()));
    }

    private static async Task<Guid> ObserveFirstToolEventAsync(ArcanumWebApplicationFactory factory,
        IAsyncEnumerator<RetroDownfall.Arcanum.Core.Storage.Entities.Entry> subscription, Task<bool> first, Guid sessionId, CancellationToken ct)
    {
        await using var owned = subscription;
        bool available = await first;
        while (available && !subscription.Current.Content.StartsWith("[ToolCall: delegate_task(", StringComparison.Ordinal)) available = await subscription.MoveNextAsync();
        Assert.True(available);
        using var scope = factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT entry.Content, label.SensitivityCode, label.ExactGenerationIds
            FROM Entries entry
            LEFT JOIN artifact_sensitivity label ON label.ArtifactKindCode = $kind
              AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(entry.Id, '-', ''))
            WHERE lower(replace(entry.SessionId, '-', '')) = $session
              AND (entry.ToolName = $tool OR entry.Content LIKE $result)
            ORDER BY entry.Sequence;
            """;
        command.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.AssistantEntry);
        command.Parameters.AddWithValue("$session", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$tool", "delegate_task");
        command.Parameters.AddWithValue("$result", "[ToolResult:%");
        await using var rows = await command.ExecuteReaderAsync(ct);
        int count = 0;
        while (await rows.ReadAsync(ct))
        {
            Assert.StartsWith(count++ == 0 ? "[ToolCall:" : "[ToolResult:", rows.GetString(0));
            Assert.False(rows.IsDBNull(1));
            Assert.Equal((int)ContentSensitivity.CovenantDerived, rows.GetInt32(1));
            Assert.True(((byte[])rows[2]).Length >= 16);
        }
        Assert.Equal(2, count);
        return subscription.Current.Id;
    }

    private static void AssertGeneration(byte[] bytes, Guid generation)
    {
        Assert.Equal(0, bytes.Length % 16);
        Assert.Contains(Enumerable.Range(0, bytes.Length / 16), index => new Guid(bytes.AsSpan(index * 16, 16), bigEndian: true) == generation);
    }
}
