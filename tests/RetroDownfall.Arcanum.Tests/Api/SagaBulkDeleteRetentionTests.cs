using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Drives the real generated deletion route with bounded synthetic pages and purge outcomes.
/// The existing SagaBulkDeleteWalkTests separately verify native writes, leases and erasure kernels.
/// </summary>
public sealed class SagaBulkDeleteRetentionTests
{
    [Theory]
    [InlineData("unlabeled")]
    [InlineData("purged")]
    [InlineData("mixed")]
    public async Task Earlier_page_results_are_collectible_before_the_walk_finishes(string mode)
    {
        PagedSagaStore store = new(16);

        TrackingPurger purger = new(mode, failSecondPage: false);

        await using WebApplication app = await HostAsync(store, purger);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.DeleteAsync("/api/saga?confirm=true");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(16, purger.Calls);

        Assert.Equal(1, store.DeleteAllCalls);

        Assert.Equal(256, purger.TrackedCount);

        Assert.Equal(0, purger.EarlyResultsAliveAtLastPage);

        Assert.Equal(mode != "unlabeled", response.Headers.CacheControl?.Private == true);
    }

    [Theory]
    [InlineData("purged", true)]
    [InlineData("unlabeled", false)]
    public async Task Later_page_failure_preserves_protection_if_an_earlier_page_purged(string mode, bool protectedResponse)
    {
        PagedSagaStore store = new(3);

        TrackingPurger purger = new(mode, failSecondPage: true);

        await using WebApplication app = await HostAsync(store, purger);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.DeleteAsync("/api/saga?confirm=true");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        Assert.Equal(2, purger.Calls);

        Assert.Equal(0, store.DeleteAllCalls);

        Assert.Equal(protectedResponse, response.Headers.CacheControl?.Private == true);

        if (protectedResponse)
        {
            Assert.True(response.Headers.CacheControl!.NoStore);

            Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
        }
    }

    private static async Task<WebApplication> HostAsync(PagedSagaStore store, TrackingPurger purger)
    {
        WebApplicationBuilder builder = RouteGraphHost.CreateBuilder();

        builder.Services.AddSingleton<ISagaMemoryStore>(store);

        builder.Services.AddSingleton<ICovenantSensitiveArtifactPurger>(purger);

        WebApplication app = builder.Build();

        _ = app.MapGroup("/api").MapSagaEndpoints();

        await app.StartAsync();

        return app;
    }

    private sealed class TrackingPurger(string mode, bool failSecondPage) : ICovenantSensitiveArtifactPurger
    {
        private readonly List<WeakReference<CovenantSensitivePurgeResult>> _earlyResults = [];

        public int Calls { get; private set; }

        public int TrackedCount => _earlyResults.Count;

        public int EarlyResultsAliveAtLastPage { get; private set; } = -1;

        public ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
            IReadOnlyList<CovenantSensitivePurgeTarget> targets, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;

            if (failSecondPage && Calls == 2)
            {
                return ValueTask.FromResult(Result<CovenantSensitivePurgeOutcome>.Failure(
                    new Error(ErrorCodes.Covenant.Unavailable, "The next label page could not be read.")));
            }

            if (Calls == 16)
            {
                GC.Collect();

                GC.WaitForPendingFinalizers();

                GC.Collect();

                EarlyResultsAliveAtLastPage = _earlyResults.Count(static weak => weak.TryGetTarget(out _));
            }

            return ValueTask.FromResult(CreateOutcome(targets));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Result<CovenantSensitivePurgeOutcome> CreateOutcome(IReadOnlyList<CovenantSensitivePurgeTarget> targets)
        {
            CovenantSensitivePurgeResult[] results = new CovenantSensitivePurgeResult[targets.Count];

            for (int index = 0; index < targets.Count; index++)
            {
                CovenantSensitivePurgeDisposition disposition = mode == "purged" || (mode == "mixed" && index % 2 == 0)
                    ? CovenantSensitivePurgeDisposition.Purged
                    : CovenantSensitivePurgeDisposition.Unlabeled;

                results[index] = new CovenantSensitivePurgeResult(targets[index].ArtifactId, targets[index].Kind,
                    disposition, CovenantErasureBlocker.None);

                if (Calls <= 2)
                {
                    _earlyResults.Add(new WeakReference<CovenantSensitivePurgeResult>(results[index]));
                }
            }

            return Result<CovenantSensitivePurgeOutcome>.Success(
                new CovenantSensitivePurgeOutcome(results, CovenantArtifactErasureProgress.Empty));
        }
    }

    private sealed class PagedSagaStore(int pages) : ISagaMemoryStore
    {
        private int _next;

        private SagaMemoryPosition? _last;

        public int DeleteAllCalls { get; private set; }

        public Task<SagaMemoryPosition[]> ListPositionsAfterAsync(
            SagaMemoryPosition? after, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Assert.Equal(_last, after);

            Assert.InRange(limit, 1, 128);

            int count = Math.Min(limit, pages * 128 - _next);

            SagaMemoryPosition[] positions = new SagaMemoryPosition[count];

            for (int i = 0; i < count; i++)
            {
                int index = ++_next;

                positions[i] = new SagaMemoryPosition(
                    DateTimeOffset.UnixEpoch.AddTicks(-index).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    $"00000000-0000-0000-0000-{index:D12}");
            }

            if (count > 0)
            {
                _last = positions[^1];
            }

            return Task.FromResult(positions);
        }

        public Task<SagaMemoryDto[]> ListAsync(string? query, Guid? sessionId, MemoryScope scope,
            int limit, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<SagaMemoryCurationRow[]> ListCurationRowsAsync(
            string? query,
            Guid? sessionId,
            MemoryScope scope,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            SagaMemoryDto[] memories = await ListAsync(query, sessionId, scope, limit, offset, cancellationToken);

            return
            [
                .. memories.Select(static memory => new SagaMemoryCurationRow(
                    memory,
                    new SagaMemoryLifecycle(memory.RetiredAtUtc, memory.PinnedAtUtc),
                    HasEmbedding: true)),
            ];
        }

        public Task<bool> AnyRetrievableAsync(MemoryScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaMemoryWriteOutcome> InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(pages * 128);

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> RetireAsync(
            string id, byte[] expectedContentDigest, DateTimeOffset retiredAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> ReinstateAsync(
            string id,
            byte[] expectedContentDigest,
            float[] embedding,
            DateTimeOffset reinstatedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> CorrectAsync(
            string id,
            byte[] expectedContentDigest,
            string content,
            float[] embedding,
            DateTimeOffset correctedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SagaCurationOutcome> SetPinAsync(
            string id, bool pinned, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAllAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DeleteAllCalls++;

            return Task.CompletedTask;
        }

        public Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetWatermarkAsync(
            Guid sessionId,
            DateTimeOffset lastExtractedEntryCreatedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
