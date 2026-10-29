using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, "known", 1)]
    [InlineData(true, "known", 1)]
    [InlineData(false, "unknown", 2)]
    [InlineData(true, "unknown", 2)]
    [InlineData(false, "skip-routing", 1)]
    [InlineData(true, "skip-routing", 1)]
    [InlineData(false, "no-retrieval", 1)]
    [InlineData(true, "no-retrieval", 1)]
    public async Task Automatic_routing_and_retrieval_share_only_a_proven_identical_query(bool streamed, string mode, int expectedQueries)
    {
        await CreateSpellWithDescriptionAsync("routing-alpha", "RoutingAlpha", "Alpha routing description", "alpha routing body");

        await CreateSpellWithDescriptionAsync("routing-beta", "RoutingBeta", "Beta routing description", "beta routing body");

        string dbPath = Path.Combine(_workspace.Root, $"routing-query-{Guid.NewGuid():N}.db");

        await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(dbPath);

        await SeedWorkspaceFileChunkAsync(db, _workspace.Root, "src/Routing.cs", "routing-chunk", "public class RoutingContent {}");

        FakeRagWeaveService weave = new()
        {
            Available = true,
            QueryVector = [1f, 0f, 0f],
            QueryProvenance = mode == "unknown" ? null : new EmbeddingProvenance("test", ModelName),
        };

        weave.BatchVectorsByText["Alpha routing description"] = [1f, 0f, 0f];

        weave.BatchVectorsByText["Beta routing description"] = [0f, 1f, 0f];

        RoutingQueryDivination divination = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                SemanticSpellRouting = true,
                CodebaseRetrieval = mode != "no-retrieval",
            },
            Integrations = new()
            {
                Embeddings = new() { Provider = "test", Model = ModelName, Dimensions = 3 },
            },
        };

        ScriptingChatClient chat = new();

        if (streamed)
        {
            chat.EnqueueStreamTokens("routing answer");
        }
        else
        {
            chat.EnqueueText("routing answer");
        }

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave, divinationService: divination, db: db);

        PingRequest request = BaseRequest() with
        {
            Prompt = "route and retrieve this exact query",
            SkipSpellRouting = mode == "skip-routing",
            DisableMcpTools = true,
            WorkingDirectory = _workspace.Root,
        };

        if (streamed)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result && frame.Message == "routing answer");

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("routing answer", result.Value.Text);
        }

        string actualPrompt = ExtractSystemPromptText(chat.LastBufferedMessages);

        if (mode != "skip-routing")
        {
            Assert.Contains("### Active Operational Spell (RoutingAlpha)", actualPrompt, StringComparison.Ordinal);

            Assert.Contains("alpha routing body", actualPrompt, StringComparison.Ordinal);

            Assert.DoesNotContain("beta routing body", actualPrompt, StringComparison.Ordinal);
        }

        if (mode != "no-retrieval")
        {
            Assert.Contains("public class RoutingContent {}", actualPrompt, StringComparison.Ordinal);

            Assert.Equal(weave.QueryVector, Assert.Single(divination.Queries).Vector.ToArray());
        }
        else
        {
            Assert.Empty(divination.Queries);
        }

        Assert.Equal(1, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.Equal(mode == "skip-routing" ? 0 : 1, weave.EmbedBatchCallCount);

        Assert.Equal(expectedQueries, weave.EmbedCallCount);
    }

    [Theory]
    [InlineData("known", 1)]
    [InlineData("unknown", 2)]
    [InlineData("no-retrieval", 0)]
    public async Task Supported_preview_shares_routing_query_without_inventing_a_second_invocation(string mode, int expectedQueries)
    {
        await CreateSpellWithDescriptionAsync("preview-alpha", "PreviewAlpha", "Preview alpha description", "preview alpha body");

        string dbPath = Path.Combine(_workspace.Root, $"preview-routing-query-{Guid.NewGuid():N}.db");

        await using ArcanumDbContext db = CreateWorkspaceChunksDbContext(dbPath);

        await SeedWorkspaceFileChunkAsync(db, _workspace.Root, "src/Routing.cs", "routing-chunk", "public class RoutingContent {}");

        FakeRagWeaveService weave = new()
        {
            Available = true,
            QueryProvenance = mode == "unknown" ? null : new EmbeddingProvenance("test", ModelName),
        };

        RoutingQueryDivination divination = new();

        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, SemanticSpellRouting = true, CodebaseRetrieval = true },
            Integrations = new()
            {
                Embeddings = new() { Provider = "test", Model = ModelName, Dimensions = 3 },
            },
        };

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, weaveService: weave, divinationService: divination, db: db);

        Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(
            new ContextPreviewRequest(Prompt: "preview this exact routing probe", Model: ModelName,
                WorkingDirectory: _workspace.Root, NoRetrieval: mode == "no-retrieval"),
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

        Assert.Equal(mode == "no-retrieval" ? 0 : 1, weave.EmbedBatchCallCount);

        if (mode == "no-retrieval")
        {
            Assert.Empty(divination.Queries);

            Assert.Empty(result.Value.AuxiliaryCalls);
        }
        else
        {
            Assert.Equal("PreviewAlpha", result.Value.SelectedSpell);

            Assert.True(Assert.Single(result.Value.Sources, static row => row.Source == ContextTokenSource.WorkspaceRag).TokenCount > 0);

            Assert.Equal(weave.QueryVector, Assert.Single(divination.Queries).Vector.ToArray());
        }

        Assert.Equal(expectedQueries, weave.EmbedCallCount);

        Assert.Equal(expectedQueries, result.Value.AuxiliaryCalls.Count(static call => call.Invoked
            && call.Purpose is "spell-routing-embedding" or "retrieval-embedding"));
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("probe", false)]
    [InlineData("turn-provider", false)]
    [InlineData("turn-model", false)]
    [InlineData("turn-dimensions", false)]
    [InlineData("reload-provider", false)]
    [InlineData("reload-model", false)]
    [InlineData("reload-dimensions", false)]
    [InlineData("reload-disabled", false)]
    [InlineData("unknown", false)]
    [InlineData("wrong-provenance", false)]
    [InlineData("changed-provenance", false)]
    [InlineData("nonfinite", false)]
    [InlineData("empty", false)]
    public async Task Routing_query_handoff_requires_exact_probe_and_both_current_bindings(string mode, bool reusable)
    {
        ArcanumSettings configuration = DefaultSettings() with
        {
            Features = DefaultSettings().Features with { Embeddings = true, SemanticSpellRouting = true },
            Integrations = new() { Embeddings = new() { Provider = "test", Model = ModelName, Dimensions = 3 } },
        };

        FakeRagWeaveService weave = new()
        {
            QueryProvenance = mode == "unknown" ? null : new("test", mode == "wrong-provenance" ? "other" : ModelName),
            QueryVector = mode == "nonfinite" ? [float.NaN, 0f, 0f] : mode == "empty" ? [] : [1f, 0f, 0f],
        };

        weave.BatchVectorsByText["description"] = [1f, 0f, 0f];

        RoutingHandoffMonitor monitor = new(configuration);

        SemanticSpellRouter router = new(
            new RetroDownfall.Arcanum.Infrastructure.Weave.SpellWeaveCache(weave, monitor,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RetroDownfall.Arcanum.Infrastructure.Weave.SpellWeaveCache>.Instance),
            weave, monitor, Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticSpellRouter>.Instance);

        SemanticSpellRouter.RoutingResolution resolution = await router.ResolveWithQueryAsync(
            [new RetroDownfall.Arcanum.Infrastructure.Workspaces.SpellMetadata("spell", "description", "/spell/SPELL.md")],
            "exact probe", CancellationToken.None);

        EmbeddingSettings turn = configuration.ResolveEmbeddings();

        if (mode == "turn-provider") turn.Provider = "other";

        if (mode == "turn-model") turn.Model = "other";

        if (mode == "turn-dimensions") turn.Dimensions = 4;

        if (mode.StartsWith("reload-", StringComparison.Ordinal))
        {
            monitor.CurrentValue = configuration with
            {
                Features = configuration.Features with
                {
                    Embeddings = mode != "reload-disabled",
                    SemanticSpellRouting = mode != "reload-disabled",
                },
                Integrations = new()
                {
                    Embeddings = new()
                    {
                        Provider = mode == "reload-provider" ? "other" : "test",
                        Model = mode == "reload-model" ? "other" : ModelName,
                        Dimensions = mode == "reload-dimensions" ? 4 : 3,
                    },
                },
            };
        }

        if (mode == "changed-provenance")
        {
            Assert.NotNull(resolution.QueryEmbedding);

            resolution.QueryEmbedding.Embedding.AdditionalProperties![EmbeddingProvenance.MetadataKey] = new EmbeddingProvenance("other", ModelName);
        }

        Assert.Equal(reusable, router.TryReuseQueryEmbedding(resolution.QueryEmbedding,
            mode == "probe" ? "different probe" : "exact probe", turn, out Embedding<float>? reused));

        if (reusable)
        {
            Assert.Same(resolution.QueryEmbedding!.Embedding, reused);

            Assert.Equal(weave.QueryVector, reused!.Vector.ToArray());
        }
        else
        {
            Assert.Null(reused);
        }

        Assert.Equal(1, weave.EmbedCallCount);
    }

    private sealed class RoutingHandoffMonitor(ArcanumSettings initial) : Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; set; } = initial;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class RoutingQueryDivination : IDivinationService
    {
        internal List<Embedding<float>> Queries { get; } = [];

        private static Result<DivinationResult[]> Hits() => Result<DivinationResult[]>.Success(
            [new DivinationResult("routing-chunk", 0.95f, EmptyDivinationMetadata)]);

        public Task<Result<DivinationResult[]>> SearchAsync(string tableName, string primaryKeyColumn, string embeddingColumn,
            Embedding<float> queryEmbedding, int maxResults, float similarityThreshold, CancellationToken cancellationToken)
        {
            Queries.Add(queryEmbedding);

            return Task.FromResult(Hits());
        }

        public Task<Result<DivinationResult[]>> SearchScopedAsync(string tableName, string primaryKeyColumn, string embeddingColumn,
            string scopeTableName, string scopeJoinColumn, string scopeFilterColumn, string scopeFilterValue,
            Embedding<float> queryEmbedding, int maxResults, float similarityThreshold, CancellationToken cancellationToken) =>
            SearchAsync(tableName, primaryKeyColumn, embeddingColumn, queryEmbedding, maxResults, similarityThreshold, cancellationToken);

        public Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(string tableName, string primaryKeyColumn, string embeddingColumn,
            DivinationCampaignScope scope, Embedding<float> queryEmbedding, int maxResults, float similarityThreshold, CancellationToken cancellationToken) =>
            SearchAsync(tableName, primaryKeyColumn, embeddingColumn, queryEmbedding, maxResults, similarityThreshold, cancellationToken);
    }
}
