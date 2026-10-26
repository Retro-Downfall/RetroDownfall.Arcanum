using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Weave;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Security;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed partial class TapestryWeaverTests
{
    [SkippableFact]
    public async Task Raptor_ProviderSourceValidation_AllowsPureAppendButRefusesEditRevert()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryBuildSnapshot snapshot = await _store!.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        var registrations = new ServiceCollection();

        registrations.AddScoped(_ => _fixture.CreateContext(_dbPath));

        await using ServiceProvider services = registrations.BuildServiceProvider();

        var settings = ProviderValidationSettings();

        var validator = new TapestryProviderValidator(services.GetRequiredService<IServiceScopeFactory>(), new TestOptionsMonitor<ArcanumSettings>(settings));

        using IDisposable observation = TapestryBuildContext.Enter(new(snapshot, "summary", "summary-model", "local-embeddings", "embed-a"));

        Assert.True((await validator.ValidateAsync("local-embeddings", "embed-a", true, CancellationToken.None)).IsSuccess);

        await SeedChunksAsync(("append", "append.cs", "new source"));

        Assert.True((await validator.ValidateAsync("summary", "summary-model", false, CancellationToken.None)).IsSuccess);

        await SeedChunksAsync(("c00", "file0.cs", "changed"));

        await SeedChunksAsync(("c00", "file0.cs", "content body number 0"));

        Assert.True((await validator.ValidateAsync("local-embeddings", "embed-a", true, CancellationToken.None)).IsFailure);

        Assert.True((await validator.ValidateAsync("summary", "summary-model", false, CancellationToken.None)).IsFailure);
    }

    [SkippableTheory]
    [InlineData(true, "actual-model")]
    [InlineData(false, "actual-model")]
    [InlineData(true, "actual-provider")]
    [InlineData(false, "actual-provider")]
    [InlineData(true, "configuration")]
    [InlineData(false, "configuration")]
    [InlineData(true, "disabled")]
    [InlineData(false, "disabled")]
    public async Task Raptor_ProviderSourceValidation_RequiresActualIdentityAndCurrentConfiguration(bool embedding, string change)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryBuildSnapshot snapshot = await _store!.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        var registrations = new ServiceCollection();

        registrations.AddScoped(_ => _fixture.CreateContext(_dbPath));

        await using ServiceProvider services = registrations.BuildServiceProvider();

        var settings = ProviderValidationSettings();

        var validator = new TapestryProviderValidator(services.GetRequiredService<IServiceScopeFactory>(), new TestOptionsMonitor<ArcanumSettings>(settings));

        using IDisposable observation = TapestryBuildContext.Enter(new(snapshot, "summary", "summary-model", "local-embeddings", "embed-a"));

        if (change == "configuration")
        {
            if (embedding) settings.Integrations.Embeddings.Model = "replacement";
            else settings.DefaultModel = "replacement";
        }

        if (change == "disabled") settings.Features.Tapestry = false;

        var result = await validator.ValidateAsync(change == "actual-provider" ? "other" : embedding ? "local-embeddings" : "summary",
            change == "actual-model" ? "other" : embedding ? "embed-a" : "summary-model", embedding, CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [SkippableFact]
    public async Task Raptor_ProviderSourceValidation_UsesAdmittedConnectionDuringEffectDrain()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        TapestryBuildSnapshot snapshot = await _store!.ReadBuildSnapshotAsync(Scope, CancellationToken.None);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        CovenantConnectionDrain drain = new();

        var registrations = new ServiceCollection();

        registrations.AddScoped(_ =>
        {
            var builder = new DbContextOptionsBuilder<ArcanumDbContext>()
                .UseSqlite(_db!.Database.GetConnectionString())
                .UseModel(ArcanumDbContextModel.Instance);

            ArcanumDbContextOptionsConfigurator.ConfigureServingEnrolment(builder,
                new GrimoireOrdinaryConnectionLifecycle(gate, drain), drain,
                new CovenantSqliteConnectionInitializer(SqliteNativeRuntime.Instance));

            return new ArcanumDbContext(builder.Options, new TestApiKeySecretStore(string.Empty), new RetroDownfall.Arcanum.Infrastructure.Security.GrimoireDbPassphraseSource());
        });

        await using ServiceProvider services = registrations.BuildServiceProvider();

        var validator = new TapestryProviderValidator(services.GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(ProviderValidationSettings()));

        using IDisposable observation = TapestryBuildContext.Enter(new(snapshot, "summary", "summary-model", "local-embeddings", "embed-a"));

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.TapestryWeaving, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect));

        await using IGrimoireExternalEffectGroup group = effect!;

        await using IGrimoireClosingOwner closing = gate.BeginOrResumeExclusive(new CovenantExclusiveRecoveryOwner(
            Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]))).Value;

        var waiting = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Assert.True((await validator.ValidateAsync("summary", "summary-model", false, CancellationToken.None)).IsSuccess);

        Assert.False(waiting.IsCompleted);

        await group.DisposeAsync();

        await work.DisposeAsync();

        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        await Assert.ThrowsAsync<GrimoireMaintenanceUnavailableException>(() =>
            validator.ValidateAsync("summary", "summary-model", false, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Raptor_ActualEmbeddingLeaseOptionsRace_RefusesBeforeAnyHttpSend()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedTenChunksAsync(this);

        var snapshot = await _store!.ReadBuildSnapshotAsync(Scope, default);

        var monitor = new MutableOptionsMonitor<ArcanumSettings>(ProviderValidationSettings());

        ServiceCollection registrations = [];

        registrations.AddScoped(_ => _fixture.CreateContext(_dbPath));

        await using var services = registrations.BuildServiceProvider();

        var scopes = services.GetRequiredService<IServiceScopeFactory>();

        var validator = new TapestryProviderValidator(scopes, monitor);

        using var handler = new NoSendHandler();

        var factory = new EmbeddingGeneratorFactory(new ValidationHttpFactory(handler), monitor, new ChangeOptionsKeys(() =>
        {
            var changed = ProviderValidationSettings();

            changed.Integrations.Embeddings.Model = "replacement-model";

            monitor.CurrentValue = changed;
        }), validator);

        var weave = new WeaveService(factory, monitor, scopes, NullLogger<WeaveService>.Instance, validator);

        using IDisposable observation = TapestryBuildContext.Enter(new(snapshot, "summary", "summary-model", "local-embeddings", "embed-a"));

        var result = await weave.EmbedAsync("exact untruncated source", default);

        Assert.True(result.IsFailure);

        Assert.Equal("replacement-model", monitor.CurrentValue.Integrations.Embeddings.Model);

        Assert.Equal(0, handler.Sends);
    }

    [SkippableFact]
    public async Task Raptor_StoredSummaryVectorAndHash_UseExactlyTheFullProviderInput()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedSummaryCorpusAsync();

        var settings = ProviderValidationSettings();

        string fullSummary = new('x', settings.ResolveEmbeddings().ChunkSizeChars + 1000);

        _summarizer!.AlwaysFits = true;

        _summarizer.SummaryTextOverride = fullSummary;

        var monitor = new TestOptionsMonitor<ArcanumSettings>(settings);

        ServiceCollection registrations = [];

        registrations.AddScoped(_ => _fixture.CreateContext(_dbPath));

        await using var services = registrations.BuildServiceProvider();

        var scopes = services.GetRequiredService<IServiceScopeFactory>();

        var validator = new TapestryProviderValidator(scopes, monitor);

        var factory = new FullInputFactory();

        var weave = new WeaveService(factory, monitor, scopes, NullLogger<WeaveService>.Instance, validator);

        var weaver = new TapestryWeaver(_store!, weave, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        var result = await weaver.WeaveAsync(Scope, Settings(maxChildren: 24), default);

        Assert.Equal(TapestryWeaveStatus.Woven, result.Status);

        Assert.Equal(2, factory.Inputs.Count(text => text == fullSummary));

        var summaries = await _store!.GetLayerNodesAsync(result.GenerationId!, 1, default);

        var vectors = await _store.GetNodeEmbeddingsAsync(summaries.Select(node => node.NodeId).ToArray(), default);

        Assert.Equal(2, summaries.Count);

        foreach (var summary in summaries)
        {
            Assert.Equal(fullSummary, summary.Content);

            Assert.Equal(TapestryHash.OfContent(fullSummary), summary.ContentHash);

            Assert.Equal(FullInputFactory.Vector(fullSummary), vectors[summary.NodeId]);
        }
    }

    [SkippableFact]
    public async Task Raptor_CanonicalProviderAlias_AgreesWithActualFactoryBuildAndRetrieval()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedSummaryCorpusAsync();

        var settings = ProviderValidationSettings();

        settings.Integrations.Embeddings.Provider = "LoCaL-EmBeDdInGs";

        settings.Integrations.Embeddings.Dimensions = TestDimensions;

        var monitor = new TestOptionsMonitor<ArcanumSettings>(settings);

        ServiceCollection registrations = [];

        registrations.AddScoped(_ => _fixture.CreateContext(_dbPath));

        await using var services = registrations.BuildServiceProvider();

        var scopes = services.GetRequiredService<IServiceScopeFactory>();

        var validator = new TapestryProviderValidator(scopes, monitor);

        using var handler = new NoSendHandler();

        var actualFactory = new EmbeddingGeneratorFactory(new ValidationHttpFactory(handler), monitor, new ChangeOptionsKeys(() => { }), validator);

        using var lease = await actualFactory.ResolveGeneratorAsync(default);

        Assert.Equal(new EmbeddingProvenance("local-embeddings", "embed-a"), lease.Provenance);

        EmbeddingSettings runtime = settings.ResolveEmbeddings();

        Assert.Equal(lease.Provenance!.Identity, TapestryHash.OfEmbeddingModel(runtime.Provider, runtime.Model));

        var factory = new FullInputFactory();

        var weave = new WeaveService(factory, monitor, scopes, NullLogger<WeaveService>.Instance, validator);

        _summarizer!.AlwaysFits = true;

        var weaver = new TapestryWeaver(_store!, weave, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        var built = await weaver.WeaveAsync(Scope, runtime, default);

        Assert.Equal(TapestryWeaveStatus.Woven, built.Status);

        var access = await _store!.GetRetrievableGenerationAsync(Scope, new(lease.Provenance.Identity, TestDimensions), default);

        Assert.True(access.IsSuccess);

        Assert.Equal(built.GenerationId, access.Value.Generation.GenerationId);

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, new(TapestryHash.OfEmbeddingModel(runtime.Provider, "Embed-A"), TestDimensions), default)).IsFailure);

        Assert.Equal("LoCaL-EmBeDdInGs", settings.Integrations.Embeddings.Provider);

        Assert.Equal(0, handler.Sends);
    }

    [SkippableFact]
    public async Task Raptor_CanonicalProviderAlias_RefusesAndRebuildsPriorDifferentlySpelledFingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedSummaryCorpusAsync();

        _summarizer!.AlwaysFits = true;

        var settings = ProviderValidationSettings();

        settings.Integrations.Embeddings.Provider = "LOCAL-EMBEDDINGS";

        settings.Integrations.Embeddings.Dimensions = TestDimensions;

        // Represents a persisted pre-canonicalization identity. It is never relabeled as current proof.
        EmbeddingSettings historical = settings.ResolveEmbeddings() with { Provider = "LOCAL-EMBEDDINGS" };

        _weave!.Provenance = new("LOCAL-EMBEDDINGS", "embed-a");

        var weaver = new TapestryWeaver(_store!, _weave, _summarizer, _backoff, TimeProvider.System, _logger, new FixedWidthClusterer(12));

        var old = await weaver.WeaveAsync(Scope, historical, default);

        Assert.Equal(TapestryWeaveStatus.Woven, old.Status);

        var prior = (await _store!.GetCurrentGenerationAsync(Scope, default))!;

        string canonical = TapestryHash.OfEmbeddingModel("local-embeddings", "embed-a");

        Assert.NotEqual(canonical, prior.EmbeddingIdentity);

        Assert.True((await _store.GetRetrievableGenerationAsync(Scope, new(canonical, TestDimensions), default)).IsFailure);

        _weave.Provenance = new("local-embeddings", "embed-a");

        var rebuilt = await weaver.WeaveAsync(Scope, settings.ResolveEmbeddings(), default);

        Assert.Equal(TapestryWeaveStatus.Woven, rebuilt.Status);

        Assert.NotEqual(old.GenerationId, rebuilt.GenerationId);

        Assert.Equal(0, rebuilt.SummariesReused);

        Assert.Equal(2, rebuilt.SummaryCallsMade);

        var access = await _store.GetRetrievableGenerationAsync(Scope, new(canonical, TestDimensions), default);

        Assert.True(access.IsSuccess);

        Assert.Equal(rebuilt.GenerationId, access.Value.Generation.GenerationId);

        Assert.NotEqual(prior.SettingsFingerprint, access.Value.Generation.SettingsFingerprint);

        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM tapestry_generations WHERE GenerationId='{old.GenerationId}' AND EmbeddingIdentity='{prior.EmbeddingIdentity}' AND Status='Superseded'"));
    }

    private sealed class FullInputFactory : IEmbeddingGeneratorFactory, IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Inputs { get; } = [];

        public Task<EmbeddingGeneratorLease> ResolveGeneratorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingGeneratorLease(this, false, new EmbeddingProvenance("local-embeddings", "embed-a")));

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            string[] texts = values.ToArray();

            Inputs.AddRange(texts);

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(texts.Select(text => new Embedding<float>(Vector(text)))));
        }

        internal static float[] Vector(string text)
        {
            float[] vector = new float[TestDimensions];

            vector[0] = text.Length;

            vector[1] = 1;

            return vector;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class MutableOptionsMonitor<T>(T current) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = current;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => new NoopSubscription();

        private sealed class NoopSubscription : IDisposable { public void Dispose() { } }
    }

    private sealed class ChangeOptionsKeys(Action change) : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            change();

            return Task.FromResult<string?>(null);
        }
    }

    private sealed class ValidationHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoSendHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static ArcanumSettings ProviderValidationSettings() => new()
    {
        DefaultModel = "summary-model",
        Features = new() { Tapestry = true },
        Integrations = new() { Embeddings = new() { Provider = "local-embeddings", Model = "embed-a" } },
        Providers =
        [
            new() { Name = "summary", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://summary.test/v1", Models = ["summary-model"] },
            new() { Name = "local-embeddings", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://embedding.test/v1", Models = ["embed-a"] },
        ],
    };
}
