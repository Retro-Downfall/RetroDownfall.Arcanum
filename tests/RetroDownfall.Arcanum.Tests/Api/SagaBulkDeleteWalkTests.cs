using System.Globalization;
using System.Net;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// How <c>DELETE /api/saga</c> walks a Saga larger than one page, entered through the mapped route.
/// </summary>
/// <remarks>
/// <para>Memories are written through the Saga store's insert and labelled through the host's own
/// sensitivity ledger, and the kernel that purges them is the real one. The one seam is a decorator in
/// front of the purger that records every page it is handed, because the route answers only
/// <c>204</c> and what the walk examined is not otherwise visible from outside.</para>
///
/// <para>Three hundred memories is more than two pages of 128. A hundred of them are newest and
/// distinct, a hundred and forty share one instant, and the rest are older and distinct, so a page
/// boundary falls inside the tie group and the order has to be a total one to resume there. One memory
/// carries an identity that is not a Guid and ends a page, so the walk has to step over a row it cannot
/// hand to the purger. Every second memory in walk order is labelled, which is what makes the purger
/// remove rows out from under the walk: a cursor that counted positions would slide past the rows that
/// moved up into the gaps (§10.20.2).</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class SagaBulkDeleteWalkTests
{
    private const int PageSize = 128;

    /// <summary>Position, in walk order, of the one memory whose identity is not a Guid.</summary>
    private const int UnparseablePosition = 255;

    /// <summary>
    /// Every memory is examined once, in order, whatever the purger removes between pages, and the
    /// route reports the deletion it performed.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_every_Saga_memory_examines_each_one_once_in_order_across_pages_and_a_tie_group()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        PurgeRecorder recorder = new();

        await using ArcanumWebApplicationFactory factory = Host(recorder, new MovedLabelArm());

        using HttpClient client = factory.CreateAuthenticatedClient();

        SeededMemory[] order = await SeedAsync(factory);

        using HttpResponseMessage response = await client.DeleteAsync("/api/saga?confirm=true");

        string body = await response.Content.ReadAsStringAsync();

        Guid[] expected = [.. order.Where(static memory => Guid.TryParse(memory.Id, out _)).Select(static memory => Guid.Parse(memory.Id))];

        // Once each, whatever order they came in: no memory skipped, none examined twice.
        Assert.Equal(expected.Order(), recorder.Examined.Order());

        Assert.Equal(expected, recorder.Examined);

        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{(int)response.StatusCode}: {body}");

        Assert.All(recorder.Pages, static page => Assert.InRange(page.Length, 1, PageSize));

        Assert.Equal(150, recorder.Count(CovenantSensitivePurgeDisposition.Purged));

        Assert.Equal(149, recorder.Count(CovenantSensitivePurgeDisposition.Unlabeled));

        Assert.Empty(await ReadMemoryIdsAsync(factory));

        Assert.Equal(0, await CountLabelsAsync(factory));
    }

    /// <summary>
    /// A blocked memory late in the second page stops the walk there: what came before it is gone, what
    /// follows it is untouched, no later page is read, and the route refuses rather than answering 204.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_every_Saga_memory_stops_at_the_first_blocked_memory_and_reads_no_later_page()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Among the fifteen distinct instants that follow the tie group, so its place in walk order does
        // not depend on how a tie is broken.
        const int blockedPosition = 246;

        PurgeRecorder recorder = new();

        MovedLabelArm arm = new();

        await using ArcanumWebApplicationFactory factory = Host(recorder, arm);

        using HttpClient client = factory.CreateAuthenticatedClient();

        SeededMemory[] order = await SeedAsync(factory);

        arm.Arm(Guid.Parse(order[blockedPosition].Id));

        using HttpResponseMessage response = await client.DeleteAsync("/api/saga?confirm=true");

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {body}");

        Assert.Contains(ErrorCodes.Covenant.StaleSnapshot, body, StringComparison.Ordinal);

        Assert.True(arm.Fired);

        // Two pages and no third. The second page's last row is the one without a Guid identity, which is
        // never handed to the purger.
        Assert.Equal(2, recorder.Pages.Count);

        Assert.Equal(PageSize, recorder.Pages[0].Length);

        Assert.Equal(PageSize - 1, recorder.Pages[1].Length);

        // Labelled memories sit at the even positions. Those before the blocked one are purged; the
        // blocked one and the labelled ones after it on its page are blocked, never dispatched; the
        // labelled ones on the page after it are not examined at all.
        int[] labelled = [.. Enumerable.Range(0, order.Length).Where(static position => position % 2 == 0)];

        int purgedCount = labelled.Count(position => position < blockedPosition);

        Assert.Equal(purgedCount, recorder.Count(CovenantSensitivePurgeDisposition.Purged));

        Assert.Equal(
            labelled.Count(position => position >= blockedPosition && position < 2 * PageSize),
            recorder.Count(CovenantSensitivePurgeDisposition.Blocked));

        HashSet<string> purged = [.. labelled.Where(position => position < blockedPosition).Select(position => order[position].Id)];

        Assert.Equal(
            order.Select(static memory => memory.Id).Where(id => !purged.Contains(id)).Order(StringComparer.Ordinal),
            (await ReadMemoryIdsAsync(factory)).Order(StringComparer.Ordinal));

        Assert.Equal(labelled.Length - purgedCount, await CountLabelsAsync(factory));
    }

    /// <summary>
    /// A host with the Covenant and Saga on, an in-memory credential store, the recording purger in front
    /// of the real one, and, when armed, a ledger that moves one label.
    /// </summary>
    /// <remarks>
    /// Turning Saga on also turns on the embedding substrate it runs on, and configuration validation
    /// refuses that substrate without a provider and model, so the host names the same test provider the
    /// erasure route suites do. Nothing here calls it.
    /// </remarks>
    private static ArcanumWebApplicationFactory Host(PurgeRecorder recorder, MovedLabelArm arm)
    {
        InMemoryOsCredentialStore credentials = new();

        return new ArcanumWebApplicationFactory
        {
            SettingsOverride = static settings => settings with
            {
                Features = settings.Features with
                {
                    Covenant = true,
                    Saga = true,
                    Embeddings = true,
                },
                Integrations = settings.Integrations with
                {
                    Embeddings = settings.Integrations.Embeddings with
                    {
                        Provider = "test",
                        Model = "test-embed",
                        Dimensions = 64,
                    },
                },
            },
            ServiceOverrides = services =>
            {
                services.RemoveAll<IOsCredentialStore>();

                services.AddSingleton<IOsCredentialStore>(credentials);

                services.AddSingleton(recorder);

                services.AddSingleton(arm);

                services.AddScoped<IArtifactSensitivityLedger>(static sp => new MovedLabelLedger(
                    new ArtifactSensitivityLedger(sp.GetRequiredService<ICovenantConnectionSource>()),
                    sp.GetRequiredService<MovedLabelArm>()));

                Func<IServiceProvider, object> real = services
                    .Last(static descriptor => descriptor.ServiceType == typeof(ICovenantSensitiveArtifactPurger))
                    .ImplementationFactory
                    ?? throw new InvalidOperationException("The purger is registered by a factory.");

                services.AddScoped<ICovenantSensitiveArtifactPurger>(sp => new RecordingPurger(
                    (ICovenantSensitiveArtifactPurger)real(sp),
                    sp.GetRequiredService<PurgeRecorder>()));
            },
        };
    }

    /// <summary>
    /// Writes the corpus through the store's own insert, labels every second memory in walk order
    /// through the host's ledger, and returns the memories in the order the walk visits them.
    /// </summary>
    private static async Task<SeededMemory[]> SeedAsync(ArcanumWebApplicationFactory factory)
    {
        DateTimeOffset origin = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        DateTimeOffset tie = origin.AddHours(5);

        List<SeededMemory> seeded = [];

        for (int index = 0; index < 100; index++)
        {
            seeded.Add(Memory(Guid.NewGuid().ToString("D"), index, origin.AddHours(10).AddSeconds(index)));
        }

        for (int index = 100; index < 240; index++)
        {
            seeded.Add(Memory(Guid.NewGuid().ToString("D"), index, tie));
        }

        for (int index = 240; index < 255; index++)
        {
            seeded.Add(Memory(Guid.NewGuid().ToString("D"), index, origin.AddMinutes(index)));
        }

        seeded.Add(Memory("legacy-memory-without-a-guid", 255, origin.AddMinutes(239)));

        for (int index = 256; index < 300; index++)
        {
            seeded.Add(Memory(Guid.NewGuid().ToString("D"), index, origin.AddMinutes(index - 300)));
        }

        SeededMemory[] order =
        [
            .. seeded
                .OrderByDescending(static memory => memory.CreatedAt)
                .ThenByDescending(static memory => memory.Id, StringComparer.Ordinal),
        ];

        Assert.Equal("legacy-memory-without-a-guid", order[UnparseablePosition].Id);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ISagaMemoryStore store = scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>();

        foreach (SeededMemory memory in seeded)
        {
            SagaMemoryWriteOutcome outcome = await store.InsertAsync(
                memory.Id,
                memory.Content,
                memory.CreatedAt,
                sessionId: null,
                tags: null,
                source: "extraction",
                new float[64],
                CancellationToken.None);

            Assert.Equal(SagaMemoryWriteOutcome.Written, outcome);
        }

        for (int position = 0; position < order.Length; position += 2)
        {
            await LabelAsync(factory, order[position]);
        }

        return order;
    }

    private static SeededMemory Memory(string id, int index, DateTimeOffset createdAt) =>
        new(id, $"Ward-stone {index} stands in the mill yard.", createdAt);

    /// <summary>Labels one memory through the host's sensitivity ledger, the one production writer.</summary>
    private static async Task LabelAsync(ArcanumWebApplicationFactory factory, SeededMemory memory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ICovenantAvailability availability = scope.ServiceProvider.GetRequiredService<ICovenantAvailability>();

        Result<LabeledArtifactWriteReceipt> receipt = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Saga,
                    Guid.Parse(memory.Id),
                    null,
                    null,
                    null,
                    1,
                    DerivedArtifactContentDigest.ForText(memory.Content),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([availability.Current.DatasetGeneration!.Value])),
                CancellationToken.None);

        Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : null);
    }

    /// <summary>Every memory identity still stored, on a fresh read-only connection. Assertion-only.</summary>
    private static async Task<string[]> ReadMemoryIdsAsync(ArcanumWebApplicationFactory factory)
    {
        await using IGrimoireOrdinaryConnectionLease lease = await OpenReadOnlyAsync(factory);

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.CommandText = """SELECT "Id" FROM "saga_memories";""";

        List<string> ids = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync(CancellationToken.None))
        {
            ids.Add(reader.GetString(0));
        }

        return [.. ids];
    }

    /// <summary>How many sensitivity labels remain, on a fresh read-only connection. Assertion-only.</summary>
    private static async Task<long> CountLabelsAsync(ArcanumWebApplicationFactory factory)
    {
        await using IGrimoireOrdinaryConnectionLease lease = await OpenReadOnlyAsync(factory);

        await using SqliteCommand command = lease.Connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM artifact_sensitivity;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    private static async Task<IGrimoireOrdinaryConnectionLease> OpenReadOnlyAsync(ArcanumWebApplicationFactory factory)
    {
        Result<IGrimoireOrdinaryConnectionLease> opened = await factory.Services
            .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

        return opened.Value;
    }

    private sealed record SeededMemory(string Id, string Content, DateTimeOffset CreatedAt);

    /// <summary>
    /// Every page the purger was handed, in order, and what it answered for each.
    /// </summary>
    private sealed class PurgeRecorder
    {
        private readonly Lock _gate = new();

        private readonly List<Guid[]> _pages = [];

        private readonly List<CovenantSensitivePurgeResult> _results = [];

        internal IReadOnlyList<Guid[]> Pages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pages];
                }
            }
        }

        internal Guid[] Examined
        {
            get
            {
                lock (_gate)
                {
                    return [.. _pages.SelectMany(static page => page)];
                }
            }
        }

        internal int Count(CovenantSensitivePurgeDisposition disposition)
        {
            lock (_gate)
            {
                return _results.Count(result => result.Disposition == disposition);
            }
        }

        internal void Record(Guid[] page, CovenantSensitivePurgeOutcome? outcome)
        {
            lock (_gate)
            {
                _pages.Add(page);

                if (outcome is not null)
                {
                    _results.AddRange(outcome.Results);
                }
            }
        }
    }

    /// <summary>The real purger, with each page it is handed and each answer it gives recorded.</summary>
    private sealed class RecordingPurger(ICovenantSensitiveArtifactPurger inner, PurgeRecorder recorder)
        : ICovenantSensitiveArtifactPurger
    {
        public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
            IReadOnlyList<CovenantSensitivePurgeTarget> targets,
            CancellationToken cancellationToken = default)
        {
            Result<CovenantSensitivePurgeOutcome> outcome = await inner
                .PurgeAsync(targets, cancellationToken)
                .ConfigureAwait(false);

            recorder.Record(
                [.. targets.Select(static target => target.ArtifactId)],
                outcome.IsSuccess ? outcome.Value : null);

            return outcome;
        }
    }

    /// <summary>
    /// Replaces exactly the next label read for one artifact, once, with a label that is not the one in
    /// the database, which is how a label that moves between the purge's two reads looks to it.
    /// </summary>
    private sealed class MovedLabelArm
    {
        private readonly Lock _gate = new();

        private Guid? _artifactId;

        private bool _fired;

        internal bool Fired
        {
            get
            {
                lock (_gate)
                {
                    return _fired;
                }
            }
        }

        internal void Arm(Guid artifactId)
        {
            lock (_gate)
            {
                _artifactId = artifactId;

                _fired = false;
            }
        }

        internal bool TryTake(Guid artifactId)
        {
            lock (_gate)
            {
                if (_artifactId != artifactId)
                {
                    return false;
                }

                _artifactId = null;

                _fired = true;

                return true;
            }
        }
    }

    /// <summary>The real ledger, except that an armed read is answered with a label that has moved.</summary>
    private sealed class MovedLabelLedger(IArtifactSensitivityLedger inner, MovedLabelArm arm) : IArtifactSensitivityLedger
    {
        public Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
            DerivedArtifactWrite write,
            CancellationToken cancellationToken) =>
            inner.LabelAsync(write, cancellationToken);

        public Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
            SensitiveArtifactKind artifactKind,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            arm.TryTake(artifactId)
                ? Task.FromResult(Result<ArtifactSensitivityLabel?>.Success(
                    CovenantErasureAuthorityFixture.Label(artifactId, Guid.NewGuid(), artifactKind)))
                : inner.TryReadLabelAsync(artifactKind, artifactId, cancellationToken);

        public Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            inner.ReadSessionProjectionAsync(sessionId, cancellationToken);
    }
}
