using System.Data.Common;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class TapestryRetrievalDispatchTests
{
    [Theory]
    [InlineData(false, "edit", false)]
    [InlineData(true, "edit", false)]
    [InlineData(false, "edit-revert", false)]
    [InlineData(true, "edit-revert", false)]
    [InlineData(false, "delete", false)]
    [InlineData(true, "delete", false)]
    [InlineData(false, "publication", false)]
    [InlineData(true, "publication", false)]
    [InlineData(false, "embedding", false)]
    [InlineData(true, "embedding", false)]
    [InlineData(false, "epoch", false)]
    [InlineData(true, "epoch", false)]
    [InlineData(false, "canonical", false)]
    [InlineData(true, "canonical", false)]
    [InlineData(false, "unchanged", true)]
    [InlineData(true, "unchanged", true)]
    [InlineData(false, "append", true)]
    [InlineData(true, "append", true)]
    public async Task DirectDispatch_RechecksActualSourceAndIdentityBeforeAnySend(bool streaming, string change, bool expected)
    {
        await using Harness h = await Harness.CreateAsync();

        await h.ChangeAsync(change);

        bool succeeded = await h.SendAsync(streaming, h.Observation);

        Assert.Equal(expected, succeeded);

        Assert.Equal(expected ? 1 : 0, h.Http.Sends);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task NativeRetry_CacheDisabledRechecksBetweenPhysicalAttempts(bool streaming, bool observed)
    {
        await using Harness h = await Harness.CreateAsync();

        h.Http.AfterFirst = () => h.ChangeAsync("edit");

        bool succeeded = await h.SendAsync(streaming, observed ? h.Observation : null, sdkRetries: true);

        Assert.Equal(!observed, succeeded);

        Assert.Equal(observed ? 1 : 2, h.Http.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownObservationImplementationCannotAuthorizeAProviderSend(bool streaming)
    {
        await using Harness h = await Harness.CreateAsync();

        Assert.False(await h.SendAsync(streaming, new UnknownObservation()));

        Assert.Equal(0, h.Http.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownObservationWithoutRequiredValidatorCannotSend(bool streaming)
    {
        await using Harness h = await Harness.CreateAsync();

        Assert.False(await h.SendAsync(streaming, h.Observation, validationInstalled: false));

        Assert.Equal(0, h.Http.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoObservationMasksOuterPhysicalContextAndRestoresItAfterCall(bool streaming)
    {
        await using Harness h = await Harness.CreateAsync();

        int outerChecks = 0;

        using var outer = TapestryRetrievalDispatchPolicy.Enter((_, _, _) =>
        {
            outerChecks++;

            return Task.FromResult(TapestryInvocationValidator.Refused());
        });

        int initialResolutions = h.CanonicalCalls;

        Assert.True(await h.SendAsync(streaming, null));

        Assert.Equal(0, outerChecks);

        Assert.Equal(initialResolutions, h.CanonicalCalls);

        Assert.NotNull(await Record.ExceptionAsync(h.DirectNativeAsync));

        Assert.Equal(1, outerChecks);

        Assert.Equal(1, h.Http.Sends);
    }

    [Fact]
    public async Task StreamingConsumerContinuationDoesNotInheritTheProviderAttemptScope()
    {
        await using Harness h = await Harness.CreateAsync();

        int consumerChecks = 0;

        using var outer = TapestryRetrievalDispatchPolicy.Enter((_, _, _) =>
        {
            consumerChecks++;

            return Task.FromResult(TapestryInvocationValidator.Refused());
        });

        int textUpdates = 0;

        Assert.True(await h.SendAsync(true, h.Observation, consumer: async update =>
        {
            if (update is not ModelCallTextDelta) return;

            textUpdates++;

            Assert.NotNull(await Record.ExceptionAsync(h.DirectNativeAsync));
        }));

        Assert.True(textUpdates > 0);

        Assert.Equal(textUpdates, consumerChecks);

        Assert.Equal(1, h.Http.Sends);
    }

    [Theory]
    [InlineData(false, "provider", true)]
    [InlineData(true, "provider", true)]
    [InlineData(false, "model", true)]
    [InlineData(true, "model", true)]
    [InlineData(false, "provider", false)]
    [InlineData(true, "provider", false)]
    [InlineData(false, "model", false)]
    [InlineData(true, "model", false)]
    public async Task NativeAttemptMatchesActualInferenceIdentityWithoutTreatingItAsQueryProvenance(bool streaming, string changed, bool observed)
    {
        await using Harness h = await Harness.CreateAsync();

        Assert.Equal(!observed, await h.SendAsync(streaming, observed ? h.Observation : null,
            contextProvider: changed == "provider" ? "different-provider" : null,
            contextModel: changed == "model" ? "different-inference-model" : null));

        Assert.Equal(observed ? 0 : 1, h.Http.Sends);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task NativeRetry_CacheConfiguredOrdinaryAttemptStillUsesRetrievalFence(bool streaming, bool observed)
    {
        await using Harness h = await Harness.CreateAsync();

        h.Http.AfterFirst = () => h.ChangeAsync("edit");

        Assert.Equal(!observed, await h.SendAsync(streaming, observed ? h.Observation : null, cacheConfigured: true, sdkRetries: true));

        Assert.Equal(observed ? 1 : 2, h.Http.Sends);
    }

    private sealed class UnknownObservation : ITapestryRetrievalObservation;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly GrimoireFixture _fixture = new();

        private readonly Task10Authority _authority = new();

        private ServiceProvider _services = null!;

        private readonly CountingResolver _resolver = new();

        internal int CanonicalCalls => _resolver.Calls;

        internal TaskCompletionSource FinalCanonicalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FinalCanonicalRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void HoldFinalCanonicalResolution() => _resolver.AfterResolve = async (call, token) =>
        {
            // Initial authorization, direct pre/post, native pre/post: five resolves.
            if (call != 5) return;

            FinalCanonicalEntered.TrySetResult();

            await FinalCanonicalRelease.Task.WaitAsync(token);
        };

        private ArcanumDbContext _db = null!;

        private TapestryStore _store = null!;

        internal HeldGenerationStore? Held { get; private set; }

        private TapestryGenerationRecipe _recipe = null!;

        internal MutableTestOptionsMonitor<ArcanumSettings> Settings { get; private set; } = null!;

        internal TapestryRetrievalObservation Observation { get; private set; } = null!;

        internal RetryHandler Http { get; } = new();

        internal TapestryInvocationValidator Validator { get; private set; } = null!;

        private static TapestryScope Scope => new(TapestryScopeKind.Workspace, "/task10");

        internal static async Task<Harness> CreateAsync(bool holdGeneration = false)
        {
            Harness h = new();

            h._db = h._fixture.CreateContext(h._fixture.CopyDatabase());

            await h._db.Database.OpenConnectionAsync();

            await h.SqlAsync("INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('task10-source','/task10','a.cs',0,'private-old-source',0,18,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')");

            h._store = new(h._db, new WeaveIndexAvailability());

            string identity = new EmbeddingProvenance("provider", "embed").Identity;

            h._recipe = new(RaptorAlgorithmManifest.Version, TapestryHash.OfSettings(5, 8, 24, 256, 512, 3, identity),
                null, TapestryHash.SummaryRecipeVersion, 3, identity);

            string generation = await h.PublishAsync();

            ArcanumSettings settings = new()
            {
                DefaultModel = "model",
                Features = new() { Tapestry = true, Covenant = false },
                Integrations = new() { Embeddings = new() { Provider = "provider", Model = "embed" } },
                Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://provider.invalid/v1", Models = ["model", "embed"] }],
            };

            h.Settings = new(settings);

            ServiceCollection services = new();

            services.AddSingleton<ICanonicalCampaignContextResolver>(h._resolver);

            services.AddSingleton<IWorkspaceIndexingService>(new ScopeWorkspace());

            h.Held = holdGeneration ? new(h._store) : null;

            services.AddSingleton<ITapestryStore>(h.Held is { } held ? held : h._store);

            h._services = services.BuildServiceProvider();

            h.Validator = new(h._services.GetRequiredService<IServiceScopeFactory>(), h.Settings, h._authority);

            var selection = await h.Validator.AuthorizeAsync(new PingRequest("query", "model", "/task10"), InvocationContexts.StatelessOperator(), default);

            Assert.NotNull(selection);

            h.Observation = new(selection, new(identity, 3), [new(Scope, generation)]);

            return h;
        }

        internal async Task<bool> SendAsync(bool streaming, ITapestryRetrievalObservation? observation, bool validationInstalled = true, Func<ModelCallUpdate, Task>? consumer = null,
            string? contextProvider = null, string? contextModel = null, bool cacheConfigured = false, bool sdkRetries = false)
        {
            Settings.CurrentValue.Features.DynamicContextInjectionV2 = cacheConfigured;

            using PromptCacheConfigurationEpoch? epoch = cacheConfigured ? new(Settings, Guid.NewGuid()) : null;

            // No binding means an ordinary attempt, including when the cache infrastructure is configured.
            var factory = new ChatClientFactory(new HttpFactory(Http), Settings, new Keys(), promptCacheConfigurationEpoch: epoch);

            using ChatClientLease captured = await factory.ResolveClientAsync("model", default);

            using ChatClientLease? retry = sdkRetries ? ProviderSdkTestTransport.RetryChat(captured, Http) : null;

            ChatClientLease lease = retry ?? captured;

            var estimator = new ModelTokenEstimator(new InferenceTokenizerResolver(NullLogger<InferenceTokenizerResolver>.Instance));

            var executor = new ModelCallExecutor(estimator, Settings, (ILogger?)null, null, null,
                validationInstalled ? Validator : null);

            ModelCallContext context = new(lease.Provider with { Name = contextProvider ?? lease.Provider.Name },
                contextModel ?? lease.ResolvedModel, 0, 0)
            { TapestryObservation = observation };

            List<ChatMessage> messages = [new(ChatRole.System, "private-old-source"), new(ChatRole.User, "query")];

            ChatOptions options = new() { ModelId = "model" };

            if (!streaming)
            {
                var result = await executor.ExecuteBufferedAsync(lease.ChatClient, messages, options,
                    UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context);

                return result.IsSuccess;
            }

            bool failed = false;

            await foreach (ModelCallUpdate update in executor.ExecuteStreamingAsync(lease.ChatClient, messages, options,
                UnrestrictedTurnBudget.Instance, ModelCallPurpose.MainInference, default, context))
            {
                failed |= update is ModelCallFailureUpdate;

                if (consumer is not null) await consumer(update);
            }

            return !failed;
        }

        internal async Task DirectNativeAsync()
        {
            var factory = new ChatClientFactory(new HttpFactory(Http), Settings, new Keys());

            using ChatClientLease lease = await factory.ResolveClientAsync("model", default);

            _ = await lease.ChatClient.GetResponseAsync("unrelated direct call");
        }

        internal async Task ChangeAsync(string change)
        {
            switch (change)
            {
                case "edit": await SqlAsync("UPDATE workspace_file_chunks SET Content='private-edited-source' WHERE ChunkId='task10-source'"); break;
                case "edit-revert":
                    await ChangeAsync("edit");
                    await SqlAsync("UPDATE workspace_file_chunks SET Content='private-old-source' WHERE ChunkId='task10-source'");
                    break;
                case "delete": await SqlAsync("DELETE FROM workspace_file_chunks WHERE ChunkId='task10-source'"); break;
                case "append": await SqlAsync("INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt) VALUES('task10-new','/task10','b.cs',0,'new',0,3,'2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')"); break;
                case "publication": _ = await PublishAsync(); break;
                case "embedding":
                    var replacement = Settings.CurrentValue with { Integrations = new() { Embeddings = new() { Provider = "provider", Model = "different-embedding" } } };
                    Settings.CurrentValue = replacement;
                    break;
                case "epoch": _authority.Current = _authority.Current! with { AuthorityEpoch = 12 }; break;
                case "canonical": _resolver.Changed = true; break;
                case "physical-root": _resolver.Paths.Changed = true; break;
                case "configured-dimension": Settings.CurrentValue.Integrations.Embeddings.Dimensions++; break;
                case "configured-summary-model":
                    Settings.CurrentValue.Providers[0].Models = ["model", "embed", "summary-next"];
                    Settings.CurrentValue.Integrations.Embeddings.Tapestry.SummaryModel = "summary-next";
                    break;
            }
        }

        private async Task<string> PublishAsync()
        {
            var snapshot = await _store.ReadBuildSnapshotAsync(Scope, default);

            string generation = await _store.BeginGraphGenerationAsync(snapshot, _recipe, DateTimeOffset.UtcNow, default);

            await _store.AppendGraphCheckpointAsync(new(generation, [new(new("task10-node-" + generation, generation,
                Scope.Kind, Scope.Id, 0, TapestryNodeKind.Leaf, null, TapestryLeafSourceKind.WorkspaceFileChunk,
                "task10-source", "a.cs", null, TapestryHash.OfContent("private-old-source"), null, 1, 0,
                TapestryPartitionReason.None, 3, DateTimeOffset.UtcNow), [1f, 0f, 0f])], []), default);

            Assert.True((await _store.PublishGenerationAsync(new(generation, snapshot, _recipe,
                TapestryTerminalReason.LeafOnly, DateTimeOffset.UtcNow), default)).IsSuccess);

            return generation;
        }

        private async Task SqlAsync(string sql)
        {
            await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();

            await _db.DisposeAsync();

            _fixture.Dispose();

            Http.Dispose();
        }
    }

    private sealed class CountingResolver : ICanonicalCampaignContextResolver
    {
        internal ChangingPhysicalPath Paths { get; } = new();

        private readonly ICanonicalCampaignContextResolver _inner;

        internal CountingResolver() => _inner = ProductionCampaignResolver.Over(new(), new(ChangingPhysicalPath.Campaign), Paths);

        internal int Calls { get; private set; }

        internal bool Changed { get; set; }

        internal Func<int, CancellationToken, Task>? AfterResolve { get; set; }

        public async ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken token)
        {
            int call = ++Calls;

            Result<CanonicalCampaignContext> result = Changed
                ? Result<CanonicalCampaignContext>.Failure(new Error(ErrorCodes.Covenant.CampaignBindingConflict, "Canonical source changed."))
                : await _inner.ResolveAsync(request, token);

            if (AfterResolve is { } after) await after(call, token);

            return result;
        }
    }

    private sealed class Task10Authority : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; set; } = new(1,
            "2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A", 11, 1, 1, CovenantHostToolsState.Clean, null);
    }

    private sealed class ScopeWorkspace : IWorkspaceIndexingService
    {
        public string ResolveIndexedWorkspacePath(string workspacePath) => "/task10";

        public void RegisterWorkspace(string workspacePath) => throw new NotSupportedException();

        public void UnregisterWorkspace(string workspacePath) => throw new NotSupportedException();

        public Result<WorkspaceIndexQueueDisposition> QueueIndexNow(string workspacePath) => throw new NotSupportedException();
    }

    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Keys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        internal int Sends { get; private set; }

        internal Func<Task>? AfterFirst { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;

            string body = await request.Content!.ReadAsStringAsync(token);

            if (Sends == 1 && AfterFirst is not null)
            {
                await AfterFirst();

                HttpResponseMessage retry = new(HttpStatusCode.ServiceUnavailable) { Content = ProviderSdkTestTransport.Content("{}", "application/json") };

                retry.Headers.Add("retry-after-ms", "1");

                return retry;
            }

            const string completion = """{"id":"chat-test","object":"chat.completion","created":1,"model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

            string content = body.Contains("\"stream\":true", StringComparison.Ordinal)
                ? "data: {\"id\":\"chat-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n"
                : completion;

            return new(HttpStatusCode.OK) { Content = ProviderSdkTestTransport.Content(content, content == completion ? "application/json" : "text/event-stream") };
        }
    }
}
