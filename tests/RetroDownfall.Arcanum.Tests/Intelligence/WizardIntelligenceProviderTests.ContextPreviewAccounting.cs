using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    /// <summary>
    /// The context preview publishes no turn accounting, so the embeddings the semantic Spell router
    /// makes each take <see cref="WeaveService"/>'s own-run branch: a run row on the <c>embedding</c>
    /// surface and one <c>Embedding</c> ledger row per provider batch. Only the model-backed auxiliary
    /// calls have nothing to record into, and no model call is made here at all.
    /// </summary>
    [Fact]
    public async Task ContextPreview_SemanticRoutingEmbeddings_EachOpenTheirOwnLedgeredEmbeddingRun()
    {
        await CreateSpellWithDeclaredToolsAsync("preview-routed", []);

        PreviewLedgerWriter writer = new();

        ServiceCollection services = new();

        services.AddSingleton<ITurnRunWriter>(writer);

        ArcanumSettings settings = DefaultSettings() with
        {
            Features = DefaultSettings().Features with
            {
                Embeddings = true,
                SemanticSpellRouting = true,
            },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "local",
                    Model = "nomic-embed-text",
                },
            },
        };

        WeaveService weave = new(
            new PreviewEmbeddingGeneratorFactory(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WeaveService>.Instance);

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, weaveService: weave);

        Result<ContextPreviewResult> preview = await wizard.PreviewContextAsync(
            new ContextPreviewRequest(
                Prompt: "inspect semantic routing",
                Model: ModelName,
                WorkingDirectory: _workspace.Root),
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(preview.IsSuccess);

        Assert.Equal("preview-routed", preview.Value.SelectedSpell);

        // Pure embedding mode resolves the Spell by vector similarity: no model call of any kind.
        Assert.Equal(0, chat.BufferedCallCount);

        Assert.Contains(preview.Value.AuxiliaryCalls, static call => call.Purpose == "spell-catalog-embedding");

        Assert.Contains(preview.Value.AuxiliaryCalls, static call => call.Purpose == "spell-routing-embedding");

        // The catalog embedding and the prompt embedding are separate WeaveService calls, so two runs.
        Assert.Equal(2, writer.Runs.Count);

        Assert.All(writer.Runs, static run => Assert.Equal("embedding", run.Surface));

        Assert.Equal(2, writer.Operations.Count);

        Assert.All(writer.Operations, static operation => Assert.Equal(BillableOperationType.Embedding, operation.OperationType));

        Assert.Equal(writer.Runs.Count, writer.Completions.Count);
    }

    private sealed class PreviewLedgerWriter : ITurnRunWriter
    {
        public List<InferenceRunStart> Runs { get; } = [];

        public List<InferenceRunStatus> Completions { get; } = [];

        public List<BillableOperationRecord> Operations { get; } = [];

        public Task<Guid> StartRunAsync(
            InferenceRunStart start,
            CancellationToken cancellationToken = default)
        {
            lock (Runs)
            {
                Runs.Add(start);
            }

            return Task.FromResult(Guid.NewGuid());
        }

        public Task CompleteRunAsync(
            Guid runId,
            InferenceRunStatus status,
            CancellationToken cancellationToken = default)
        {
            lock (Runs)
            {
                Completions.Add(status);
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryAbandonRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Guid> RecordBillableOperationAsync(
            BillableOperationRecord operation,
            CancellationToken cancellationToken = default)
        {
            lock (Runs)
            {
                Operations.Add(operation);
            }

            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class PreviewEmbeddingGeneratorFactory : IEmbeddingGeneratorFactory
    {
        public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingGeneratorLease(new PreviewEmbeddingGenerator(), ownsGenerator: true));
    }

    private sealed class PreviewEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            GeneratedEmbeddings<Embedding<float>> result = [];

            foreach (string _ in values)
            {
                result.Add(new Embedding<float>(new float[] { 1f, 0f, 0f }));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
