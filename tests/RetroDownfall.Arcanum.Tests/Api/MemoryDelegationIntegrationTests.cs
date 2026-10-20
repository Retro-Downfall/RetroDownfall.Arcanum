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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Real_wrapper_discloses_selected_child_and_parent_and_atomically_persists_protected_session_entries(bool campaignBound, bool omitSession)
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
                omitSession ? null : sessionId =>
                {
                    var hub = factory.Services.GetRequiredService<SessionEventHub>();
                    var subscription = hub.SubscribeAsync(sessionId, observationStop.Token).GetAsyncEnumerator(observationStop.Token);
                    Task<bool> first = subscription.MoveNextAsync().AsTask();
                    Assert.Equal(1, hub.GetSubscriberCount(sessionId));
                    eventEvidence = ObserveFirstToolEventAsync(factory, subscription, first, sessionId, observationStop.Token);
                    return Task.CompletedTask;
                }, campaignId, omitSession ? () => FindFinalSessionAsync(factory, PublishedArcanumSessionSmokeTests.DelegationFinal, deadline.Token) : null);
            if (eventEvidence is not null) Assert.Equal(evidence.CallEntryId, await eventEvidence.WaitAsync(deadline.Token));

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
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public async Task Ordinary_protected_tool_turn_prepares_genuine_claim_with_maintenance_disabled(bool campaignBound, bool retainedHistoryOnly, bool fallbackWithoutTools, bool omitSession, bool missingPreparer)
    {
        await using var provider = await PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.StartAsync();
        const string credential = "ARCANUM_ORDINARY_PROTECTED_TOOL_TEST_KEY";
        const string fallbackCredential = "ARCANUM_ORDINARY_PROTECTED_TOOL_FALLBACK_KEY";
        string? previous = global::System.Environment.GetEnvironmentVariable(credential);
        string? previousFallback = global::System.Environment.GetEnvironmentVariable(fallbackCredential);
        global::System.Environment.SetEnvironmentVariable(credential, PublishedArcanumSessionSmokeTests.ProviderKey);
        global::System.Environment.SetEnvironmentVariable(fallbackCredential, PublishedArcanumSessionSmokeTests.ProviderKey);
        ArcanumSettings? liveSettings = null;
        ClaimFallbackTransport? fallbackTransport = null;
        try
        {
            await using var factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true, configure: settings =>
            {
                liveSettings = settings;
                settings.DefaultModel = PublishedArcanumSessionSmokeTests.ExpectedModel;
                settings.Providers = [new ProviderSettings
                {
                    Name = "ordinary-protected", Type = AiProviderKind.OpenAICompatible, Endpoint = provider.Endpoint.AbsoluteUri,
                    CredentialEnvironmentVariable = credential,
                    Models = [new ModelEntry(PublishedArcanumSessionSmokeTests.ExpectedModel, SupportsTools: true)], ContextWindowLimit = 8192
                }];
                if (fallbackWithoutTools)
                {
                    settings.Providers = [settings.Providers[0], settings.Providers[0] with
                    {
                        Name = "ordinary-fallback", CredentialEnvironmentVariable = fallbackCredential, Models = [new ModelEntry(PublishedArcanumSessionSmokeTests.ExpectedModel, SupportsTools: false)]
                    }];
                }
                settings.Features = new FeatureSettings
                {
                    Covenant = true, LongRest = false, CampaignRollups = false, Apprentices = false,
                    Lexicon = false, ArchiveSearch = false, Metrics = false, Scrying = false,
                    Attachments = false, Reasoning = false, WorkspaceChecks = false, Annals = false
                };
                PublishedArcanumSessionSmokeTests.ConfigureEvaluationPricing(settings);
            });
            var originals = factory.ServiceOverrides;
            factory.ServiceOverrides = services =>
            {
                originals!(services);
                services.RemoveAll<IArcanumIntelligenceProvider>();
                services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
                if (missingPreparer) services.RemoveAll<CampaignRollupTurnPreparer>();
                if (fallbackWithoutTools)
                {
                    fallbackTransport = new ClaimFallbackTransport();
                    services.RemoveAll<IChatClientFactory>();
                    services.AddSingleton<IChatClientFactory>(fallbackTransport);
                }
            };
            using var client = factory.CreateAuthenticatedClient();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Guid? campaignId = null;
            if (campaignBound)
            {
                string root = Path.Combine(factory.TempHome, "ordinary-protected-campaign");
                Directory.CreateDirectory(root);
                using var register = await client.PostAsync("api/campaigns", JsonContent.Create(
                    new RegisterCampaignRequest("ordinary-protected", root, WorkspaceType.Campaign, null), ArcanumJsonContext.Default.RegisterCampaignRequest), deadline.Token);
                Assert.Equal(HttpStatusCode.Created, register.StatusCode);
                campaignId = (await JsonSerializer.DeserializeAsync(await register.Content.ReadAsStreamAsync(deadline.Token),
                    ArcanumJsonContext.Default.ApiResponseCampaignDto, deadline.Token))!.Data!.Id;
            }
            Guid sessionId;
            if (retainedHistoryOnly)
            {
                sessionId = (await PublishedArcanumSessionSmokeTests.ExerciseMemoryDelegationAsync(client, provider, 0, deadline.Token, campaignId: campaignId)).SessionId;
                liveSettings!.Features.Covenant = false;
            }
            else
            {
                await new MemoryErasureRouteDriver(client).SetCovenantAsync(CovenantScope.Global, null, "ordinary.protected", "ordinary-covenant-secret", deadline.Token);
                sessionId = Guid.Empty;
                if (!omitSession)
                {
                    using var create = await client.PostAsync("api/sessions", JsonContent.Create(
                        new CreateSessionRequest(campaignId, "ordinary protected"), ArcanumJsonContext.Default.CreateSessionRequest), deadline.Token);
                    Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                    sessionId = (await JsonSerializer.DeserializeAsync(await create.Content.ReadAsStreamAsync(deadline.Token),
                        ArcanumJsonContext.Default.ApiResponseSessionDetailDto, deadline.Token))!.Data!.Id;
                }
            }
            int before = provider.RequestCount;
            var request = new PingRequest(PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.OrdinaryToolPrompt,
                Model: PublishedArcanumSessionSmokeTests.ExpectedModel, SessionId: omitSession ? null : sessionId,
                DisableMcpTools: true, SkipSpellRouting: true, MaxOutputTokens: 256);
            using var response = await client.PostAsync("api/intelligence/ping", JsonContent.Create(request, ArcanumJsonContext.Default.PingRequest), deadline.Token);
            string body = await response.Content.ReadAsStringAsync(deadline.Token);
            if (missingPreparer)
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var refusal = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);
                Assert.False(refusal!.IsSuccess);
                Assert.Equal("Covenant.Unavailable", refusal.Error!.Value.Code);
                Assert.Equal(before, provider.RequestCount);
                return;
            }
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Ordinary protected turn returned {(int)response.StatusCode}: {body}; sends={provider.RequestCount - before}");
            var result = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);
            Assert.True(result!.IsSuccess);
            Assert.Equal(PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.OrdinaryToolFinal, result.Data!.Text);
            if (omitSession) sessionId = await FindFinalSessionAsync(factory, PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.OrdinaryToolFinal, deadline.Token);
            if (fallbackTransport is not null)
            {
                Assert.Equal([true, false], fallbackTransport.ToolSupport);
                Assert.Equal(2, fallbackTransport.Calls);
                Assert.True(fallbackTransport.FinalOptions?.Tools is null or { Count: 0 });
            }
            else
            {
                Assert.Equal(before + 2, provider.RequestCount);
                var sends = provider.Requests.Skip(before).ToArray();
                Assert.Contains("get_local_system_time", sends[0].Payload.GetRawText());
                Assert.Contains("call-published-delegation", sends[1].Payload.GetRawText());
            }
            Assert.False(liveSettings!.Features.LongRest);
            Assert.False(liveSettings.Features.CampaignRollups);
            using var scope = factory.Services.CreateScope();
            var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT label.SensitivityCode, label.ExactGenerationIds
                FROM Entries entry LEFT JOIN artifact_sensitivity label
                  ON label.ArtifactKindCode = $kind AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(entry.Id, '-', ''))
                WHERE lower(replace(entry.SessionId, '-', '')) = $session
                  AND entry.Sequence > (SELECT max(Sequence) FROM Entries WHERE lower(replace(SessionId, '-', '')) = $session AND Content = $prompt)
                ORDER BY entry.Sequence;
                """;
            command.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.AssistantEntry);
            command.Parameters.AddWithValue("$session", sessionId.ToString("N"));
            command.Parameters.AddWithValue("$prompt", request.Prompt);
            await using (var rows = await command.ExecuteReaderAsync(deadline.Token))
            {
                int count = 0;
                while (await rows.ReadAsync(deadline.Token))
                {
                    Assert.False(rows.IsDBNull(0));
                    Assert.Equal((int)ContentSensitivity.CovenantDerived, rows.GetInt32(0));
                    Assert.True(((byte[])rows[1]).Length >= 16);
                    count++;
                }
                Assert.Equal(fallbackWithoutTools ? 1 : 3, count);
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT count(*) FROM session_turn_claims claim JOIN Entries answer ON answer.Id = claim.AssistantEntryId WHERE lower(replace(claim.SessionId, '-', '')) = $session AND answer.Content = $final AND claim.StateCode = 3";
            command.Parameters.AddWithValue("$session", sessionId.ToString("N"));
            command.Parameters.AddWithValue("$final", PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.OrdinaryToolFinal);
            Assert.Equal(1L, await command.ExecuteScalarAsync(deadline.Token));
            command.Parameters.Clear();
            command.CommandText = "SELECT count(*) FROM external_disclosure_receipts WHERE SubjectKind = $subject AND EffectCategoryCode = $effect AND SensitivityCode = $sensitivity";
            command.Parameters.AddWithValue("$subject", (int)CovenantDisclosureSubjectKind.Turn);
            command.Parameters.AddWithValue("$effect", (int)CovenantDisclosureEffectCategory.ProviderDispatch);
            command.Parameters.AddWithValue("$sensitivity", (int)ContentSensitivity.CovenantDerived);
            Assert.Equal((long)before + 2, await command.ExecuteScalarAsync(deadline.Token));
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable(credential, previous);
            global::System.Environment.SetEnvironmentVariable(fallbackCredential, previousFallback);
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
        var creating = request with { SessionId = null };
        var stateless = InvocationContexts.StatelessOperator();
        Assert.False(CampaignRollupTurnPreparer.IsLiveEligible(settings, creating, stateless));
        Assert.True(CampaignRollupTurnPreparer.IsLiveEligible(settings, creating, stateless, requireProtectedToolPersistence: true));
        Assert.False(stateless.CanStageCovenantMutation);
        Assert.False(CampaignRollupTurnPreparer.IsLiveEligible(settings,
            creating with { StatelessMessages = [new("user", "explicit")] }, stateless, requireProtectedToolPersistence: true));
        foreach (var excluded in new[] { InvocationContexts.NoContextSession(), ArcanumInvocationContext.None,
            ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn, null,
                InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.AllTools, stateless.ReadAuthorityEpoch).Value,
            ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn, default(CanonicalCampaignContext),
                InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.AllTools, stateless.ReadAuthorityEpoch).Value })
            Assert.False(CampaignRollupTurnPreparer.IsLiveEligible(settings, creating, excluded, requireProtectedToolPersistence: true));
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

    private static async Task<Guid> FindFinalSessionAsync(ArcanumWebApplicationFactory factory, string final, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SessionId FROM Entries WHERE Content = $final";
        command.Parameters.AddWithValue("$final", final);
        return Guid.Parse(Assert.IsType<string>(await command.ExecuteScalarAsync(ct)));
    }

    private sealed class ClaimFallbackTransport : IChatClientFactory
    {
        public List<bool> ToolSupport { get; } = [];
        public int Calls { get; private set; }
        public ChatOptions? FinalOptions { get; private set; }

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fallback candidate must be resolved explicitly.");

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
        {
            bool tools = provider.Models.Single().SupportsTools != false;
            ToolSupport.Add(tools);
            return Task.FromResult(new ChatClientLease(new Client(this, tools), provider, resolvedModel, null));
        }

        private sealed class Client(ClaimFallbackTransport owner, bool tools) : IChatClient
        {
            public void Dispose() { }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                owner.Calls++;
                if (tools) throw new HttpRequestException("synthetic first-candidate connectivity failure");
                owner.FinalOptions = options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    PublishedArcanumSessionSmokeTests.FakeOpenAiProvider.OrdinaryToolFinal))
                {
                    Usage = new() { InputTokenCount = 20, OutputTokenCount = 4, TotalTokenCount = 24 }
                });
            }
            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("This regression exercises the ordinary buffered fallback.");
        }
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
