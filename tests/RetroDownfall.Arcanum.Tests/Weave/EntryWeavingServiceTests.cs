using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EntryWeavingServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public EntryWeavingServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task RunTickAsync_EmbedsUnembeddedEntries_AndSkipsAlreadyEmbeddedOnNextTick()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        Guid entryOneId = await CreateEntryAsync(sessionId, "first entry content");

        Guid entryTwoId = await CreateEntryAsync(sessionId, "second entry content");

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(2, await CountEntryEmbeddingsAsync());

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // Second tick: the LEFT JOIN excludes both already-embedded rows, so no new embed call happens.
        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.Equal(2, await CountEntryEmbeddingsAsync());

        _ = entryOneId;

        _ = entryTwoId;

    }

    [SkippableFact]
    public async Task RunTickAsync_SkipsEmptyContentEntries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "   ");

        await CreateEntryAsync(sessionId, "real content");

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(1, await CountEntryEmbeddingsAsync());

        Assert.Single(weave.LastBatch!);

        Assert.Equal("real content", weave.LastBatch![0]);

    }

    [SkippableFact]
    public async Task RunTickAsync_TruncatesContentToChunkSizeChars()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        int chunkSize = ArcanumSettingClamps.EmbeddingsChunkSizeChars(
            ArcanumRuntimeDefaults.Embeddings.ChunkSizeChars);

        string longContent = new('x', chunkSize + 200);

        await CreateEntryAsync(sessionId, longContent);

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Single(weave.LastBatch!);

        Assert.Equal(chunkSize, weave.LastBatch![0].Length);

        Assert.Equal(new string('x', chunkSize), weave.LastBatch![0]);

    }

    [SkippableFact]
    public async Task RunTickAsync_EmbeddingFailure_LogsAndContinues_WritesNoRows()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content that will fail to embed");

        FakeWeaveService weave = new() { FailNextBatch = true };

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        // Never throws — the tick logs a warning and returns without writing any rows.
        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task RunTickAsync_ShortProviderResponse_WritesNoRowsAndDoesNotThrow()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "first entry");

        await CreateEntryAsync(sessionId, "second entry");

        // IWeaveService never promises one vector per input. Indexing positionally against the request
        // count would throw IndexOutOfRangeException, which the hosted loop's catch-all retries once a
        // second forever — re-issuing a billable embedding call every second.
        FakeWeaveService weave = new() { DropVectors = 1 };

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task RunTickAsync_RespectsBatchSizeAcrossMultipleTicks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(
            ArcanumRuntimeDefaults.Embeddings.BatchSize);
        for (int i = 0; i < (batchSize * 2) + 1; i++)
        {

            await CreateEntryAsync(sessionId, $"entry number {i}");

        }

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(batchSize, await CountEntryEmbeddingsAsync());

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(batchSize * 2, await CountEntryEmbeddingsAsync());

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal((batchSize * 2) + 1, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task ExecuteAsync_IdlesWhenDisabled_NeverCallsEmbedBatch()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "should not be embedded while disabled");

        FakeWeaveService weave = new();

        ArcanumSettings disabledSettings = new()
        {
            Features = new FeatureSettings
            {
                Embeddings = false,
                SessionSearch = false,
            },
        };

        EntryWeavingService service = new(
            new TestOptionsMonitor<ArcanumSettings>(disabledSettings),
            weave,
            new WeaveIndexAvailability(),
            BuildScopeFactory(),
            OpenGate(),
            NullLogger<EntryWeavingService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task ExecuteAsync_TickThrowsRepeatedly_BacksOffInsteadOfTightLooping()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "entry that will never successfully embed");

        FakeWeaveService weave = new() { ThrowOnEmbed = true };

        EntryWeavingService service = CreateService(weave, out _);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        // Without a backoff after a failed tick, this would spin as fast as the CPU allows within
        // 300ms — dozens or hundreds of calls. With the 1s backoff, at most one or two ticks can run.
        Assert.True(
            weave.EmbedBatchCallCount <= 2,
            $"Expected at most 2 tick attempts within 300ms given a 1s backoff after failure; got {weave.EmbedBatchCallCount}.");

    }

    [SkippableFact]
    public async Task RunTickAsync_AdmittedTick_ReportsWoven()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content to imprint");

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        Assert.Equal(
            EntryWeavingTickOutcome.Woven,
            await service.RunTickAsync(embeddings, CancellationToken.None));

    }

    [SkippableFact]
    public async Task RunTickAsync_DeniedItsWorkLease_MakesNoScopeProviderCallOrWrite()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content that must not be imprinted");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory();

        EntryWeavingService service = CreateService(
            weave,
            out EmbeddingSettings embeddings,
            gate,
            scopes);

        await using IGrimoireClosingOwner closing = BeginClosing(gate, 41);

        Assert.Equal(
            EntryWeavingTickOutcome.DeferredForMaintenance,
            await service.RunTickAsync(embeddings, CancellationToken.None));

        Assert.Equal(0, scopes.ScopesCreated);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task RunTickAsync_RevocationWinsTheEffectRace_MakesNoProviderCallOrWrite()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content the frontier must refuse");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory();

        IGrimoireClosingOwner? closing = null;

        // Closing the gate here lands between the lease and the effect group, which is the exact
        // window the frontier arbitrates. No sleep can place it as precisely.
        scopes.OnScopeCreated = () => closing = BeginClosing(gate, 42);

        EntryWeavingService service = CreateService(
            weave,
            out EmbeddingSettings embeddings,
            gate,
            scopes);

        Assert.Equal(
            EntryWeavingTickOutcome.DeferredForMaintenance,
            await service.RunTickAsync(embeddings, CancellationToken.None));

        Assert.Equal(1, scopes.ScopesCreated);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task RunTickAsync_HoldsItsWorkLeaseUntilAfterTheScopeHasDisposed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content imprinted while a closure waits");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory();

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drain = null;

        bool drainWasStillWaitingOnTheLease = false;

        // The probe runs after the tick's scope is already disposed. A drain started there is a
        // deterministic read of whether the work lease outlived it: the gate returns a completed
        // task synchronously when no request or work lifetime remains, and an incomplete one while
        // this tick's lease is still registered. Nothing here depends on a continuation having had
        // time to run, which is what made an earlier IsCompleted-after-the-fact probe meaningless.
        scopes.OnScopeDisposed = () =>
        {

            closing = BeginClosing(gate, 43);

            Task<Result> started = gate
                .DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask();

            drainWasStillWaitingOnTheLease = !started.IsCompleted;

            drain = started;

            return ValueTask.CompletedTask;

        };

        EntryWeavingService service = CreateService(
            weave,
            out EmbeddingSettings embeddings,
            gate,
            scopes);

        Assert.Equal(
            EntryWeavingTickOutcome.Woven,
            await service.RunTickAsync(embeddings, CancellationToken.None));

        Assert.True(
            drainWasStillWaitingOnTheLease,
            "The work lease was already released when the tick's scope finished disposing, so a closure could conclude its drain while the scoped context was still going back.");

        Result drained = await drain!;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(1, await CountEntryEmbeddingsAsync());

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task RunTickAsync_EffectStartWinsTheRace_IsNotCutAndTheClosureWaitsThroughIt()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content the frontier already admitted");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory();

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drain = null;

        // Closing the gate from inside the provider call is the losing half of the frontier race:
        // the effect group is already open, so maintenance must wait it out rather than revoke it.
        weave.OnEmbed = () =>
        {

            closing = BeginClosing(gate, 45);

            drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            return Task.CompletedTask;

        };

        EntryWeavingService service = CreateService(
            weave,
            out EmbeddingSettings embeddings,
            gate,
            scopes);

        Assert.Equal(
            EntryWeavingTickOutcome.Woven,
            await service.RunTickAsync(embeddings, CancellationToken.None));

        Result drained = await drain!;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // The write that the admitted provider call earned still landed. A revocation delivered into
        // the group would have lost it, which is the billing failure the frontier exists to stop.
        Assert.Equal(1, await CountEntryEmbeddingsAsync());

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task ExecuteAsync_RepeatedlyDeferred_DoesNotLogAnErrorOrEnterTheFaultBackoff()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content deferred for the whole window");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory();

        TestCapturingLogger<EntryWeavingService> logger = new();

        EntryWeavingService service = CreateService(
            weave,
            out _,
            gate,
            scopes,
            logger);

        await using IGrimoireClosingOwner closing = BeginClosing(gate, 44);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Equal(0, scopes.ScopesCreated);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        // The deferral takes the configured cadence, not the one-second fault backoff. Within 300ms
        // a worker on the fault path would have logged and retried; one on the cadence path has not
        // come back at all.
        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    private EntryWeavingService CreateService(
        FakeWeaveService weave,
        out EmbeddingSettings embeddings,
        IGrimoireConnectionAdmissionGate? gate = null,
        ObservingScopeFactory? scopeFactory = null,
        ILogger<EntryWeavingService>? logger = null)
    {

        embeddings = ArcanumRuntimeDefaults.Embeddings;

        return new EntryWeavingService(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Features = new FeatureSettings
                {
                    Embeddings = true,
                    SessionSearch = true,
                },
                Integrations = new IntegrationSettings
                {
                    Embeddings = new EmbeddingIntegrationSettings
                    {
                        Provider = "test",
                        Model = "test-embed",
                    },
                },
            }),
            weave,
            new WeaveIndexAvailability(),
            scopeFactory ?? BuildScopeFactory(),
            gate ?? OpenGate(),
            logger ?? NullLogger<EntryWeavingService>.Instance);

    }

    private ObservingScopeFactory BuildScopeFactory()
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        return new ObservingScopeFactory(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    }

    /// <summary>A gate whose ordinary admission is open, which is every pre-existing case here.</summary>
    private static GrimoireConnectionAdmissionGate OpenGate() => new(TimeProvider.System);

    private static CovenantExclusiveRecoveryOwner Owner(byte seed) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{seed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(seed, 32).ToArray()));

    private static IGrimoireClosingOwner BeginClosing(
        GrimoireConnectionAdmissionGate gate,
        byte seed)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner(seed));

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    /// <summary>
    /// A scope factory that counts the scopes a tick creates and can act at their boundaries.
    /// </summary>
    /// <remarks>
    /// Counting is what proves the "no scope" half of a maintenance deferral: a tick refused its
    /// work lease must not reach the container at all, and an assertion on the provider call count
    /// alone would pass for a tick that built a scope, opened a connection and then found nothing
    /// pending.
    ///
    /// <para><see cref="OnScopeCreated"/> fires between the lease and the effect group, which is the
    /// only window a test can close the gate in to make the frontier race deterministic without a
    /// sleep. <see cref="OnScopeDisposed"/> fires once the inner scope is already gone, so a probe
    /// there reads the one thing worth reading: whether the work lease outlived it.</para>
    /// </remarks>
    private sealed class ObservingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {

        private int _scopesCreated;

        internal int ScopesCreated => Volatile.Read(ref _scopesCreated);

        internal Action? OnScopeCreated { get; set; }

        internal Func<ValueTask>? OnScopeDisposed { get; set; }

        public IServiceScope CreateScope()
        {

            _ = Interlocked.Increment(ref _scopesCreated);

            IServiceScope scope = inner.CreateScope();

            OnScopeCreated?.Invoke();

            return new ObservingScope(scope, OnScopeDisposed);

        }

    }

    private sealed class ObservingScope(
        IServiceScope inner,
        Func<ValueTask>? onDisposing) : IServiceScope, IAsyncDisposable
    {

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose() => inner.Dispose();

        public async ValueTask DisposeAsync()
        {

            inner.Dispose();

            if (onDisposing is not null)
            {

                await onDisposing().ConfigureAwait(false);

            }

        }

    }

    private async Task<Guid> CreateSessionAsync()
    {

        Session session = new()
        {
            Id = Guid.NewGuid(),
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db!.Sessions.Add(session);

        await _db.SaveChangesAsync();

        return session.Id;

    }

    /// <summary>
    /// Direct seeding bypasses the repository allocation of <see cref="Entry.Sequence"/>, and the
    /// unique <c>(SessionId, Sequence)</c> index rejects duplicates, so stamp append order here.
    /// </summary>
    private long _seededSequence;

    private async Task<Guid> CreateEntryAsync(Guid sessionId, string content)
    {

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
            Sequence = ++_seededSequence,
        };

        _db!.Entries.Add(entry);

        await _db.SaveChangesAsync();

        return entry.Id;

    }

    private async Task<int> CountEntryEmbeddingsAsync()
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT COUNT(*) FROM "entry_embeddings";""";

        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool FailNextBatch { get; set; }

        public bool ThrowOnEmbed { get; set; }

        /// <summary>Vectors to omit from an otherwise successful response, simulating a short provider reply.</summary>
        public int DropVectors { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public List<string>? LastBatch { get; private set; }

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("EntryWeavingService only calls EmbedBatchAsync.");

        /// <summary>Runs inside the tick's effect group, before the provider result is produced.</summary>
        public Func<Task>? OnEmbed { get; set; }

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            LastBatch = [.. texts];

            if (OnEmbed is not null)
            {

                await OnEmbed().ConfigureAwait(false);

            }

            if (ThrowOnEmbed)
            {

                throw new InvalidOperationException("Simulated unexpected tick failure.");

            }

            if (FailNextBatch)
            {

                return Result<Embedding<float>[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure."));

            }

            Embedding<float>[] generated = new Embedding<float>[Math.Max(0, texts.Count - DropVectors)];

            for (int i = 0; i < generated.Length; i++)
            {

                generated[i] = new Embedding<float>(new float[] { 1f, 0f, 0f });

            }

            return Result<Embedding<float>[]>.Success(generated);

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by EntryWeavingService.");

    }

}
