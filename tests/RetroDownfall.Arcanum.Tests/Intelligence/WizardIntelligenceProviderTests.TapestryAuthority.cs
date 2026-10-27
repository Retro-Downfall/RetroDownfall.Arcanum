using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Tests.Covenant;
using System.Net;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData("buffered", "none")]
    [InlineData("streamed", "none")]
    [InlineData("preview", "none")]
    [InlineData("buffered", "no-context")]
    [InlineData("streamed", "no-context")]
    [InlineData("preview", "no-context")]
    [InlineData("buffered", "no-epoch")]
    [InlineData("streamed", "no-epoch")]
    [InlineData("preview", "no-epoch")]
    [InlineData("buffered", "isolated")]
    [InlineData("streamed", "isolated")]
    [InlineData("preview", "isolated")]
    [InlineData("buffered", "unresolved")]
    [InlineData("streamed", "unresolved")]
    [InlineData("preview", "unresolved")]
    [InlineData("buffered", "stale-epoch")]
    [InlineData("streamed", "stale-epoch")]
    [InlineData("preview", "stale-epoch")]
    public async Task TapestryAuthority_IneligibleInvocationDoesNotPayForTapestryOnlyQuery(string entry, string mode)
    {
        var configuration = DefaultSettings();

        configuration.Features.Tapestry = true;

        configuration.Features.Covenant = false;

        FakeRagWeaveService weave = new();

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var wizard = CreateWizard(chat, configuration, weaveService: weave, tapestryAuthority: new Task10Authority(),
            campaignResolver: ProductionCampaignResolver.Over(new(), new()));

        var invocation = mode == "none" ? ArcanumInvocationContext.None
            : mode == "no-context" ? InvocationContexts.NoContextSession()
            : mode == "no-epoch" ? ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
                CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended, CovenantContextPolicy.Default,
                ToolPolicy.NoTools, null).Value
            : mode is "unresolved" or "stale-epoch" ? ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
                mode == "unresolved" ? null : CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended,
                CovenantContextPolicy.Default, ToolPolicy.NoTools, CovenantReadAuthorityEpoch.CreateForTests(
                    Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A"), 1, mode == "stale-epoch" ? 12 : 11)).Value
            : InvocationContexts.StatelessOperator();

        using IDisposable? child = mode == "isolated" ? SubagentExecutionAmbient.EnterChild(new(10000, null)) : null;

        var request = BaseRequest() with
        {
            Prompt = "query",
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        if (entry == "preview")
        {
            var result = await wizard.PreviewContextAsync(new(Prompt: request.Prompt, Model: ModelName,
                WorkingDirectory: request.WorkingDirectory, DisableAllTools: true), invocation, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }
        else if (entry == "streamed")
        {
            await foreach (var frame in wizard.StreamPromptAsync(request, invocation, default)) { }

            Assert.Equal(1, chat.StreamingCallCount);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, invocation, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        Assert.Equal(0, weave.EmbedCallCount);
    }

    [Theory]
    [InlineData("buffered")]
    [InlineData("streamed")]
    [InlineData("preview")]
    public async Task TapestryAuthority_FeatureOffRetainsNoEmbeddingEffect(string entry)
    {
        FakeRagWeaveService weave = new();

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var wizard = CreateWizard(chat, weaveService: weave);

        var request = BaseRequest() with
        {
            Prompt = "query",
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        if (entry == "preview")
        {
            var result = await wizard.PreviewContextAsync(new(Prompt: request.Prompt, Model: ModelName,
                WorkingDirectory: request.WorkingDirectory, DisableAllTools: true), InvocationContexts.Inspection(), default);

            Assert.True(result.IsSuccess);
        }
        else if (entry == "streamed")
        {
            await foreach (var frame in wizard.StreamPromptAsync(request, InvocationContexts.StatelessOperator(), default)) { }
        }
        else
        {
            Assert.True((await wizard.ExecutePromptAsync(request, InvocationContexts.StatelessOperator(), default)).IsSuccess);
        }

        Assert.Equal(0, weave.EmbedCallCount);
    }

    [Theory]
    [InlineData("buffered", false)]
    [InlineData("streamed", false)]
    [InlineData("preview", false)]
    [InlineData("buffered", true)]
    [InlineData("streamed", true)]
    [InlineData("preview", true)]
    public Task TapestryAuthority_CleanDefaultUsesActualDagRetrieverWithOrWithoutCovenant(string entry, bool covenant) =>
        ExerciseDagInvocationAsync(entry, covenant);

    [Theory]
    [InlineData("buffered")]
    [InlineData("streamed")]
    [InlineData("preview")]
    public Task TapestryAuthority_ExplicitMaterializedBytesTakePrecedence(string entry) =>
        ExerciseDagInvocationAsync(entry, false, explicitRaw: true);

    [Theory]
    [InlineData("buffered")]
    [InlineData("streamed")]
    [InlineData("preview")]
    public Task TapestryAuthority_SourceChangeAfterRenderRefusesFinalEffect(string entry) =>
        ExerciseDagInvocationAsync(entry, false, invalidateAfterRender: true);

    [Theory]
    [InlineData("buffered")]
    [InlineData("streamed")]
    public Task TapestryAuthority_UnknownPhysicalRetryRetainsReservation(string entry) =>
        ExerciseDagInvocationAsync(entry, false, nativeRetry: true);

    [Theory]
    [InlineData("buffered")]
    [InlineData("streamed")]
    public Task TapestryAuthority_ActualDefaultSdkDoesNotRetryTheFirstFailedPhysicalAttempt(string entry) =>
        ExerciseDagInvocationAsync(entry, false, nativeRetry: true, actualDefaultSdk: true);

    [Theory]
    [InlineData("buffered", "tool", false)]
    [InlineData("streamed", "tool", false)]
    [InlineData("buffered", "structured", false)]
    [InlineData("streamed", "structured", false)]
    [InlineData("buffered", "tool", true)]
    [InlineData("streamed", "tool", true)]
    [InlineData("buffered", "structured", true)]
    [InlineData("streamed", "structured", true)]
    [InlineData("buffered", "compatibility", false)]
    [InlineData("streamed", "compatibility", false)]
    [InlineData("buffered", "compatibility", true)]
    [InlineData("streamed", "compatibility", true)]
    [InlineData("buffered", "fallback", false)]
    [InlineData("streamed", "fallback", false)]
    [InlineData("buffered", "fallback", true)]
    [InlineData("streamed", "fallback", true)]
    public Task TapestryAuthority_ReentryRevalidatesActuallyInjectedGeneration(string entry, string reentry, bool change) =>
        ExerciseDagInvocationAsync(entry, false, reentry: reentry, changeAfterFirstCall: change);

    private async Task ExerciseDagInvocationAsync(string entry, bool covenant, bool explicitRaw = false,
        bool invalidateAfterRender = false, bool nativeRetry = false, string? reentry = null, bool changeAfterFirstCall = false,
        bool actualDefaultSdk = false)
    {
        using GrimoireFixture fixture = new();

        await using var db = fixture.CreateContext(fixture.CopyDatabase());

        await db.Database.OpenConnectionAsync();

        await using (DbCommand command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('task10-source',@workspace,'a.cs',0,'task10-dag-body',0,15,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

            var workspace = command.CreateParameter();

            workspace.ParameterName = "@workspace";

            workspace.Value = _workspace.Root;

            command.Parameters.Add(workspace);

            _ = await command.ExecuteNonQueryAsync();
        }

        TapestryStore store = new(db, new WeaveIndexAvailability());

        TapestryScope scope = new(TapestryScopeKind.Workspace, _workspace.Root);

        TapestryBuildSnapshot snapshot = await store.ReadBuildSnapshotAsync(scope, default);

        string identity = TapestryHash.OfEmbeddingModel("local", "embedding-model");

        string settingsHash = TapestryHash.OfSettings(5, 8, 24, 256, 512, 3, identity);

        TapestryGenerationRecipe recipe = new(RaptorAlgorithmManifest.Version, settingsHash, null,
            TapestryHash.SummaryRecipeVersion, 3, identity);

        string generation = await store.BeginGraphGenerationAsync(snapshot, recipe, DateTimeOffset.UtcNow, default);

        await store.AppendGraphCheckpointAsync(new(generation, [new(new("task10-node", generation,
            scope.Kind, scope.Id, 0, TapestryNodeKind.Leaf, null, TapestryLeafSourceKind.WorkspaceFileChunk,
            "task10-source", "a.cs", null, TapestryHash.OfContent("task10-dag-body"), null, 1, 0,
            TapestryPartitionReason.None, 3, DateTimeOffset.UtcNow), [1f, 0f, 0f])], []), default);

        Assert.True((await store.PublishGenerationAsync(new(generation, snapshot, recipe,
            TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), default)).IsSuccess);

        var configuration = DefaultSettings();

        configuration.Features.Tapestry = true;

        configuration.Features.Covenant = covenant;

        // Exercise actual direct explicit materialization, not the no-op persisted attachment store.
        configuration.Features.Attachments = false;

        configuration.Integrations.Embeddings.Provider = "local";

        configuration.Integrations.Embeddings.Model = "embedding-model";

        FakeRagWeaveService weave = new() { QueryProvenance = new("local", "embedding-model") };

        ScriptingChatClient chat = new();

        FakeMcpConnectionManager mcp = new();

        if (reentry is not null) chat.UsageTotalTokens = 30;

        if (reentry == "tool")
        {
            mcp.Tools.Add(CreateMcpTool("task10_read"));

            if (entry == "streamed") chat.EnqueueStreamToolCallOnly("task10_read");
            else chat.EnqueueToolCall("task10_read");
        }
        else if (reentry is "compatibility" or "fallback")
        {
            Exception failure = reentry == "fallback" ? new HttpRequestException("scripted provider connectivity failure")
                : new InvalidOperationException("model does not support tools");

            if (entry == "streamed") chat.EnqueueImmediateStreamFailure(failure);
            else chat.EnqueueException(failure);
        }
        else if (reentry == "structured")
        {
            if (entry == "streamed") chat.EnqueueStreamTokens("invalid json");
            else chat.EnqueueText("invalid json");
        }

        chat.EnqueueText(reentry == "structured" ? "{\"name\":\"answer\"}" : "answer");

        chat.EnqueueStreamTokens("answer");

        if (reentry is not null) chat.BeforeCall = () =>
        {
            if (!changeAfterFirstCall || chat.BufferedCallCount + chat.StreamingCallCount != 1) return;

            using DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = "UPDATE workspace_file_chunks SET Content='edited after first logical call' WHERE ChunkId='task10-source'";

            _ = command.ExecuteNonQuery();
        };

        RecordingTurnRunWriter writer = new();

        RecordingBudgetReservationService reservations = new() { ReservedUsdOverride = nativeRetry ? 1m : null };

        Task10MutatingEstimator estimator = new(new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance)), () =>
        {
            using DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = "UPDATE workspace_file_chunks SET Content='edited after materialization' WHERE ChunkId='task10-source'";

            _ = command.ExecuteNonQuery();
        }, invalidateAfterRender);

        using Task10NativeRetryHandler native = new(() =>
        {
            using DbCommand command = db.Database.GetDbConnection().CreateCommand();

            command.CommandText = "UPDATE workspace_file_chunks SET Content='edited after physical attempt' WHERE ChunkId='task10-source'";

            _ = command.ExecuteNonQuery();
        });

        IChatClientFactory? nativeFactory = null;

        if (nativeRetry)
        {
            ChatClientFactory productionFactory = new(new Task10HttpFactory(native),
                new TestOptionsMonitor<ArcanumSettings>(configuration), new Task10NoKeys());

            // Intentional SDK-retry stress reuses actual production binding stamps and dispatch policy.
            // The separate default control exercises the production zero-retry factory unchanged.
            nativeFactory = actualDefaultSdk ? productionFactory : new Task10RetryingSdkFactory(productionFactory, native);
        }

        DelegationCandidateFactory? candidates = null;

        if (reentry == "fallback")
        {
            var first = configuration.Providers[0];

            configuration.Providers = [first, first with { Name = "task10-fallback", ContextWindowLimit = 64000 }];

            candidates = new(first, chat, chat);

            nativeFactory = candidates;
        }

        var wizard = CreateWizard(chat, configuration, mcp: mcp, weaveService: weave, tapestryStore: store, factory: nativeFactory,
            healthTracker: candidates is null ? null : new RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker(NullLogger<RetroDownfall.Arcanum.Infrastructure.Resilience.ProviderHealthTracker>.Instance),
            modelTokenEstimator: estimator, turnRunWriter: writer, budgetReservationService: reservations,
            workspaceIndexingService: new FakeRagWorkspaceIndexingService { IndexedPath = _workspace.Root },
            campaignResolver: ProductionCampaignResolver.Over(new(), new()), tapestryAuthority: new Task10Authority());

        string prompt;

        if (entry == "preview")
        {
            var result = await wizard.PreviewContextAsync(new(Prompt: "query", Model: ModelName,
                WorkingDirectory: _workspace.Root, DisableAllTools: true, ShowContent: true,
                AttachedFiles: explicitRaw ? [new("a.cs", "task10-dag-body")] : null), InvocationContexts.Inspection(), default);

            if (invalidateAfterRender)
            {
                Assert.True(estimator.Mutated);

                Assert.True(result.IsFailure);

                Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

                return;
            }

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            prompt = result.Value.Content!.SystemPrompt;
        }
        else
        {
            var request = BaseRequest() with
            {
                Prompt = "query",
                WorkingDirectory = _workspace.Root,
                SkipSpellRouting = true,
                DisableAllTools = reentry is not ("tool" or "compatibility"),
                AttachedFiles = explicitRaw ? [new("a.cs", "task10-dag-body")] : null
            };

            if (reentry == "structured")
            {
                using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""");

                request = request with { ResponseFormat = "json_schema", ResponseFormatJsonSchema = StrictJsonSchema(schema.RootElement) };
            }

            if (entry == "streamed")
            {
                await foreach (var frame in wizard.StreamPromptAsync(request, InvocationContexts.StatelessOperator(), default)) { }
            }
            else
            {
                var result = await wizard.ExecutePromptAsync(request, InvocationContexts.StatelessOperator(), default);

                Assert.Equal(!invalidateAfterRender && !nativeRetry && (!changeAfterFirstCall || reentry == "fallback"), result.IsSuccess);
            }

            if (reentry is not null)
            {
                Assert.Equal(changeAfterFirstCall && reentry != "fallback" ? 1 : 2, chat.BufferedCallCount + chat.StreamingCallCount);

                var calls = chat.AllStreamingCalls.Concat(chat.AllBufferedCalls).ToArray();

                Assert.Contains(calls[0], message => message.Role == ChatRole.System
                    && message.Text?.Contains("task10-dag-body", StringComparison.Ordinal) == true);

                if (calls.Length == 2)
                    Assert.Equal(reentry != "fallback" || !changeAfterFirstCall,
                        calls[1].Any(message => message.Role == ChatRole.System
                            && message.Text?.Contains("task10-dag-body", StringComparison.Ordinal) == true));

                if (candidates is not null)
                {
                    Assert.Equal([configuration.Providers[0].Name, "task10-fallback"], candidates.Leases);

                    Assert.Equal(InferenceRunStatus.Completed, writer.CompletedRuns[^1].Status);
                }
                else
                    Assert.Equal(changeAfterFirstCall ? InferenceRunStatus.Failed : InferenceRunStatus.Completed,
                        Assert.Single(writer.CompletedRuns).Status);

                // The scripted stream carries no usage receipt; only its buffered structured retry does.
                int knownCalls = reentry is "compatibility" or "fallback"
                    ? entry == "buffered" && (!changeAfterFirstCall || reentry == "fallback") ? 1 : 0
                    : entry == "buffered" ? (changeAfterFirstCall ? 1 : 2)
                    : reentry == "structured" && !changeAfterFirstCall ? 1 : 0;

                Assert.Equal(knownCalls, writer.Operations.Count);

                return;
            }

            if (nativeRetry)
            {
                Assert.Equal(1, native.Sends);

                Assert.True(native.SawTapestry);

                Assert.Empty(writer.Operations);

                Assert.Equal(InferenceRunStatus.Failed, Assert.Single(writer.CompletedRuns).Status);

                if (actualDefaultSdk)
                {
                    Assert.True(reservations.WasReleased);

                    Assert.Equal(0, reservations.UncertainCalls);

                    Assert.Equal(0, reservations.ReconcileCount);

                    return;
                }

                // One physical attempt may have been billed without returning usage. No zero-spend assertion is justified.
                Assert.False(reservations.WasReleased);

                Assert.Equal(1, reservations.UncertainCalls);

                Assert.Equal(0, reservations.ReconcileCount);

                return;
            }

            if (invalidateAfterRender)
            {
                Assert.True(estimator.Mutated);

                Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

                Assert.Empty(writer.Operations);

                Assert.Equal(InferenceRunStatus.Failed, Assert.Single(writer.CompletedRuns).Status);

                Assert.True(reservations.WasReleased);

                return;
            }

            prompt = ExtractSystemPromptText(chat.LastBufferedMessages);
        }

        Assert.Contains("task10-dag-body", prompt);

        Assert.Equal(!explicitRaw, prompt.Contains("Hierarchical Context (The Tapestry)", StringComparison.Ordinal));

        Assert.Equal(1, weave.EmbedCallCount);
    }

    [Theory]
    [InlineData("buffered", "cross-campaign")]
    [InlineData("streamed", "cross-campaign")]
    [InlineData("preview", "cross-campaign")]
    [InlineData("buffered", "wrong-session")]
    [InlineData("streamed", "wrong-session")]
    [InlineData("preview", "wrong-session")]
    [InlineData("buffered", "missing-binding")]
    [InlineData("streamed", "missing-binding")]
    [InlineData("preview", "missing-binding")]
    [InlineData("buffered", "path-binding-conflict")]
    [InlineData("streamed", "path-binding-conflict")]
    [InlineData("preview", "path-binding-conflict")]
    public async Task TapestryAuthority_CanonicalConflictPreventsEmbeddingBeforeScopeLookup(string entry, string conflict)
    {
        Guid sessionId = Guid.NewGuid();

        Guid campaignA = Guid.NewGuid();

        Guid campaignB = Guid.NewGuid();

        StubSessionBindings sessions = new();

        if (conflict != "missing-binding") sessions.Bind(sessionId, conflict == "wrong-session" ? campaignB : campaignA);

        var resolver = new Task10TrackingResolver(ProductionCampaignResolver.Over(sessions, new(campaignA, campaignB),
            conflict == "path-binding-conflict" ? new Task10ConflictingPath(campaignA, campaignB) : null));

        var configuration = DefaultSettings();

        configuration.Features.Tapestry = true;

        configuration.Features.Covenant = false;

        FakeRagWeaveService weave = new();

        ScriptingChatClient chat = new();

        chat.EnqueueText("answer");

        var wizard = CreateWizard(chat, configuration, new FakeGrimoireRepository { Session = new Session { Id = sessionId, Entries = [] } },
            weaveService: weave, campaignResolver: resolver, tapestryAuthority: new Task10Authority());

        var invocation = InvocationContexts.AttendedSession(campaignA);

        var request = BaseRequest() with
        {
            Prompt = "query",
            SessionId = sessionId,
            CampaignId = conflict == "cross-campaign" ? campaignB : null,
            WorkingDirectory = conflict == "path-binding-conflict" ? _workspace.Root : string.Empty,
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        if (entry == "preview")
            _ = await wizard.PreviewContextAsync(new(Prompt: request.Prompt, Model: ModelName, SessionId: sessionId,
                CampaignId: request.CampaignId, WorkingDirectory: request.WorkingDirectory, DisableAllTools: true), invocation, default);
        else if (entry == "streamed")
            await foreach (var frame in wizard.StreamPromptAsync(request, invocation, default)) { }
        else _ = await wizard.ExecutePromptAsync(request, invocation, default);

        Assert.True(resolver.Calls > 0);

        Assert.Equal(0, weave.EmbedCallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TapestryAuthority_NewlyClaimedSessionDoesNotAddOriginalRequestScopes(bool streaming, bool enabled)
    {
        ArcanumSettings configuration = DefaultSettings();

        configuration.Features.Tapestry = enabled;

        configuration.Features.CampaignRollups = false;

        configuration.Features.Covenant = false;

        configuration.Integrations.Embeddings.Provider = "local";

        configuration.Integrations.Embeddings.Model = "embedding-model";

        FakeCovenantAuthorityProvider authority = new();

        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(CampaignRollupTurnSnapshotTests.CampaignId, CovenantCampaignScopeState.Live);

        var operationGate = CovenantOperationGateFixture.CreateGate(authority: authority, campaigns: campaigns);

        CampaignWizardClaims claims = new();

        Guid createdSession = Guid.NewGuid();

        FakeGrimoireRepository grimoire = new()
        {
            FixedSessionId = createdSession,
            Session = new Session { Id = createdSession, CampaignId = CampaignRollupTurnSnapshotTests.CampaignId }
        };

        var monitor = new TestOptionsMonitor<ArcanumSettings>(configuration);

        CampaignRollupTurnPreparer preparer = new(new CampaignRollupTurnSnapshotTests.ProbeStore(), monitor,
            new CampaignRollupTurnSnapshotTests.Resolver(), operationGate,
            UnreachedLongRestPorts.Coordinator, UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery,
            UnreachedLongRestPorts.Configuration, UnreachedLongRestPorts.RootEnricher,
            new CampaignWizardMaintenance(), claims, grimoire, claims, authority);

        CovenantDispatchGate dispatch = new(new StagingPlanContextProvider(CovenantCompositionFixture.Plan(confirmed: 0, proposed: 0)),
            new CampaignMutatingJournal(() => { }), new UntaintedSensitivityLedger(), new StagingAuthority(),
            TimeProvider.System, NullLogger<CovenantDispatchGate>.Instance);

        FakeRagWeaveService weave = new();

        ScriptingChatClient chat = new();

        chat.EnqueueText("claimed");

        chat.EnqueueStreamTokens("claimed");

        var canonical = ProductionCampaignResolver.Over(new StubSessionBindings().Bind(createdSession, CampaignRollupTurnSnapshotTests.CampaignId),
            new(CampaignRollupTurnSnapshotTests.CampaignId));

        var wizard = CreateWizard(chat, configuration, grimoire, campaignRollups: preparer, turnCommitter: claims,
            claimedBeginStore: claims, claims: claims, covenantDispatch: dispatch, weaveService: weave,
            campaignResolver: canonical, tapestryAuthority: authority);

        var epoch = authority.Current!;

        var invocation = ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignRollupTurnSnapshotTests.Campaign, InvocationAttendance.Attended, CovenantContextPolicy.Default,
            ToolPolicy.AllTools, CovenantReadAuthorityEpoch.CreateForTests(Guid.Parse(epoch.InstallationIdentity),
                epoch.RuntimeAuthorityGeneration, epoch.AuthorityEpoch)).Value;

        PingRequest request = BaseRequest() with
        {
            Prompt = "new session",
            SessionId = null,
            CampaignId = CampaignRollupTurnSnapshotTests.CampaignId,
            WorkingDirectory = string.Empty,
            SkipSpellRouting = true,
            DisableAllTools = false,
            ToolPolicy = ToolPolicy.AllTools
        };

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (var frame in wizard.StreamPromptAsync(request, invocation, default)) frames.Add(frame);

            Assert.DoesNotContain(frames, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Equal(1, chat.StreamingCallCount);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, invocation, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal(1, chat.BufferedCallCount);
        }

        Assert.Equal(1, claims.Begins);

        Assert.Equal(createdSession, Assert.Single(claims.Requests).SessionId);

        Assert.Equal(0, weave.EmbedCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TapestryAuthority_GrantedOtherMemoryDoesNotGrantChildTapestry(bool streaming)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();

        await using var root = await h.PrepareAsync();

        await using var child = (await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).Value;

        Assert.Contains("fact", child.Sources.RenderedMemory);

        var configuration = DefaultSettings();

        configuration.Features.Tapestry = true;

        configuration.Features.Covenant = false;

        configuration.Integrations.Embeddings.Provider = "local";

        configuration.Integrations.Embeddings.Model = "embedding-model";

        FakeRagWeaveService weave = new();

        ScriptingChatClient chat = new();

        chat.EnqueueText("child");

        chat.EnqueueStreamTokens("child");

        var resolver = new Task10TrackingResolver(ProductionCampaignResolver.Over(new(), new()));

        var wizard = CreateWizard(chat, configuration, weaveService: weave, tapestryResolver: resolver,
            tapestryAuthority: new Task10Authority(), covenantDispatch: WizardIntelligenceProviderCovenantTests.EmptyDisclosureGate());

        using var isolated = SubagentExecutionAmbient.EnterChild(new(10000, null), child,
            new MemoryDelegationProtection(child.Sensitivity, child.Revocation, child.AdmitEffectAsync), child.Sources);

        PingRequest request = BaseRequest() with
        {
            Prompt = "child",
            WorkingDirectory = _workspace.Root,
            StatelessMessages = [new CoreChatMessage("user", "child")],
            SkipSpellRouting = true,
            DisableAllTools = true
        };

        if (streaming)
        {
            await foreach (var frame in wizard.StreamPromptAsync(request, ArcanumInvocationContext.None, default)) { }

            Assert.Equal(1, chat.StreamingCallCount);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, ArcanumInvocationContext.None, default);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        Assert.Contains("fact", chat.LastBufferedMessages.Single(message => message.Role == ChatRole.System).Text!);

        Assert.True(root.IsConsumed);

        Assert.Equal(0, resolver.Calls);

        Assert.Equal(0, weave.EmbedCallCount);
    }

    private sealed class Task10TrackingResolver(ICanonicalCampaignContextResolver inner) : ICanonicalCampaignContextResolver
    {
        internal int Calls { get; private set; }

        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken token)
        {
            Calls++;

            return inner.ResolveAsync(request, token);
        }
    }

    private sealed class Task10ConflictingPath(Guid campaignA, Guid campaignB) : ICampaignPathIdentityReader
    {
        private static CovenantDigest RootDigest => new(Enumerable.Repeat((byte)0xA1, 32).ToArray());

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? path, CancellationToken token) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(new(campaignB, 1, 1, 1, RootDigest)));

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken token) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(new(campaignA, 1, 1, 1, RootDigest)));
    }

    private sealed class Task10RetryingSdkFactory(ChatClientFactory production, HttpMessageHandler handler) : IChatClientFactory
    {
        public async Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {
            using ChatClientLease captured = await production.ResolveClientAsync(targetModel, cancellationToken);

            return ProviderSdkTestTransport.RetryChat(captured, handler);
        }

        public async Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string resolvedModel, CancellationToken cancellationToken)
        {
            using ChatClientLease captured = await production.ResolveClientAsync(provider, resolvedModel, cancellationToken);

            return ProviderSdkTestTransport.RetryChat(captured, handler);
        }
    }

    private sealed class Task10NoKeys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken token = default) => Task.FromResult<string?>(null);
    }

    private sealed class Task10HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Task10NativeRetryHandler(Action mutate) : HttpMessageHandler
    {
        internal int Sends { get; private set; }

        internal bool SawTapestry { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;

            string body = await request.Content!.ReadAsStringAsync(token);

            SawTapestry |= body.Contains("task10-dag-body", StringComparison.Ordinal);

            if (Sends == 1)
            {
                mutate();

                HttpResponseMessage retry = new(HttpStatusCode.ServiceUnavailable) { Content = ProviderSdkTestTransport.Content("{}", "text/plain") };

                retry.Headers.Add("retry-after-ms", "1");

                return retry;
            }

            const string completion = """{"id":"chat-test","object":"chat.completion","created":1,"model":"wizard-test-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

            string content = body.Contains("\"stream\":true", StringComparison.Ordinal)
                ? "data: {\"id\":\"chat-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"wizard-test-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n"
                : completion;

            return new(HttpStatusCode.OK) { Content = ProviderSdkTestTransport.Content(content, content == completion ? "application/json" : "text/event-stream") };
        }
    }

    private sealed class Task10Authority : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; set; } = new(1,
            "2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A", 11, 1, 1, CovenantHostToolsState.Clean, null);
    }

    private sealed class Task10MutatingEstimator(IModelTokenEstimator inner, Action mutate, bool enabled) : IModelTokenEstimator
    {
        internal bool Mutated { get; private set; }

        public ResolvedModelTokenizationProfile ResolveProfile(ProviderSettings provider, string model) => inner.ResolveProfile(provider, model);

        public ResolvedModelTokenizationProfile ResolveEffectiveProfile(ProviderSettings provider, string model) => inner.ResolveEffectiveProfile(provider, model);

        public TokenEstimate EstimateText(ProviderSettings provider, string model, string? text) => inner.EstimateText(provider, model, text);

        public ContextTokenBreakdown EstimateContext(ModelTokenizationRequest request)
        {
            if (enabled && !Mutated && request.Messages.Any(message => message.Text?.Contains("task10-dag-body", StringComparison.Ordinal) == true))
            {
                Mutated = true;

                mutate();
            }

            return inner.EstimateContext(request);
        }
    }
}
