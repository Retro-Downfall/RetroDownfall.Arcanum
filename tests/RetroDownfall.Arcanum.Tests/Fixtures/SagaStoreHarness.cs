using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

/// <summary>
/// One encrypted, temporary Grimoire at the current Core schema for Saga curation suites that need a
/// live connection but not the xunit collection-fixture wiring <see cref="GrimoireFixture"/> otherwise
/// requires.
/// </summary>
/// <remarks>
/// <see cref="GrimoireFixture"/> already does the real work — building and caching the schema template
/// once per process and handing out cheap copies — so this wraps one rather than repeating it. What it
/// adds is a call site a single test method can use without joining <c>[Collection("Grimoire")]</c> and
/// implementing <see cref="IAsyncLifetime"/> itself.
/// </remarks>
public sealed class SagaStoreHarness : IAsyncDisposable
{

    /// <summary>
    /// Matches <see cref="ArcanumSettingClamps.EmbeddingsDimensions"/>'s 64-dimension floor — the
    /// smallest configured value that is not itself clamped up, so <see cref="Store"/>'s
    /// dimension-validation guard sees exactly this length.
    /// </summary>
    private const int Dimensions = 64;

    private readonly GrimoireFixture _fixture;

    private readonly ArcanumDbContext _db;

    private bool _disposed;

    private SagaStoreHarness(
        GrimoireFixture fixture,
        ArcanumDbContext db,
        SagaMemoryStore store,
        IAnnalsStore annals)
    {

        _fixture = fixture;

        _db = db;

        Store = store;

        Annals = annals;

    }

    /// <summary>The open connection into the temporary Grimoire.</summary>
    public DbConnection Connection => _db.Database.GetDbConnection();

    /// <summary>A live <see cref="SagaMemoryStore"/> over the temporary Grimoire.</summary>
    internal SagaMemoryStore Store { get; }

    /// <summary>A live <see cref="IAnnalsStore"/> reading the same temporary Grimoire.</summary>
    public IAnnalsStore Annals { get; }

    /// <summary>
    /// Builds a fresh temporary Grimoire with <c>Arcanum:Features:Annals</c> off, skipping the calling
    /// test when SQLCipher is unavailable.
    /// </summary>
    public static Task<SagaStoreHarness> CreateAsync() => CreateAsync(annalsEnabled: false);

    /// <summary>
    /// Builds a fresh temporary Grimoire with <c>Arcanum:Features:Annals</c> set to
    /// <paramref name="annalsEnabled"/>, skipping the calling test when SQLCipher is unavailable.
    /// </summary>
    public static Task<SagaStoreHarness> CreateAsync(bool annalsEnabled)
    {

        // Must run before the fixture is constructed: GrimoireFixture's constructor silently no-ops
        // when SQLCipher is unavailable rather than throwing, so CopyDatabase() below would fail with a
        // FileNotFoundException instead of a clean skip if this check came after it.
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireFixture fixture = new();

        ArcanumDbContext db = fixture.CreateContext(fixture.CopyDatabase());

        SagaMemoryStore store = new(
            db,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = annalsEnabled },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings
                        {
                            Dimensions = Dimensions,
                        },
                    },
                }));

        return Task.FromResult(new SagaStoreHarness(fixture, db, store, new AnnalsStore(db)));

    }

    /// <summary>A deterministic <see cref="Dimensions"/>-length vector, distinct per <paramref name="seed"/>.</summary>
    public float[] Embedding(int seed = 0)
    {

        Random random = new(seed);

        float[] vector = new float[Dimensions];

        for (int i = 0; i < vector.Length; i++)
        {

            vector[i] = (float)(random.NextDouble() * 2.0 - 1.0);

        }

        return vector;

    }

    /// <summary>Counts rows in one table matching a caller-supplied predicate.</summary>
    public async Task<int> CountAsync(string table, string predicate)
    {

        ArgumentException.ThrowIfNullOrEmpty(table);

        ArgumentException.ThrowIfNullOrEmpty(predicate);

        await using DbCommand command = Connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate}";

        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return Convert.ToInt32(result, CultureInfo.InvariantCulture);

    }

    /// <summary>The raw <c>Embedding</c> BLOB stored for one memory, for before/after comparison.</summary>
    public async Task<byte[]> EmbeddingBytesAsync(string id)
    {

        ArgumentException.ThrowIfNullOrEmpty(id);

        await using DbCommand command = Connection.CreateCommand();

        command.CommandText = """SELECT "Embedding" FROM "saga_memory_embeddings" WHERE "MemoryId" = @id""";

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@id";

        parameter.Value = id;

        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return (byte[])result!;

    }

    /// <summary>
    /// Labels one Saga memory sensitive through <see cref="IArtifactSensitivityLedger"/> — the one
    /// production writer of <c>artifact_sensitivity</c> — rather than by inserting the row directly.
    /// A test that seeded the row it then asserted on would prove only that the row exists, not that
    /// curation leaves a real label alone.
    /// </summary>
    public async Task LabelSensitiveAsync(Guid id)
    {

        SagaMemoryCurationRow row = (await Store.ReadCurationRowAsync(id.ToString(), CancellationToken.None)
            .ConfigureAwait(false))!;

        ArtifactSensitivityLedger ledger = new(new CovenantConnectionSource(_db, new CovenantConnectionDrain()));

        DerivedArtifactWrite write = new(
            SensitiveArtifactKind.Saga,
            id,
            sessionId: null,
            campaignId: null,
            turnId: null,
            artifactRevision: 1,
            DerivedArtifactContentDigest.ForText(row.Memory.Content),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]));

        Result<LabeledArtifactWriteReceipt> receipt = await ledger
            .LabelAsync(write, CancellationToken.None).ConfigureAwait(false);

        if (receipt.IsFailure)
        {

            throw new InvalidOperationException(receipt.Error.Message);

        }

    }

    /// <summary>
    /// Creates a new Campaign and a Session canonically bound to it, so a caller can drive a Saga write
    /// the production scope classifier resolves into that Campaign — never by declaring a scope the
    /// caller chose itself.
    /// </summary>
    public async Task<Guid> SessionBoundToNewCampaignAsync()
    {

        Guid campaignId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        string now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await ExecuteAsync(
            """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $name, $path, 0, '{}', $now, $now);
            """,
            ("$id", Canonical(campaignId)),
            ("$name", campaignId.ToString("N")),
            ("$path", $"/campaigns/{campaignId:N}"),
            ("$now", now)).ConfigureAwait(false);

        await ExecuteAsync(
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, $campaignId, 'active', $now, $now);
            """,
            ("$id", Canonical(sessionId)),
            ("$campaignId", Canonical(campaignId)),
            ("$now", now)).ConfigureAwait(false);

        // The same false-by-default scope authority production borrows: nothing may state a Session's
        // binding without it, this harness included.
        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance
            .Authorize((SqliteConnection)Connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        await ExecuteAsync(
            """
            INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
            VALUES ($id, 2, $campaignId, $now);
            """,
            // Canonical, because the foreign key leaves no choice: session_campaign_bindings.SessionId
            // is declared REFERENCES "Sessions"("Id") and foreign keys are both set and verified on
            // every connection, so this column holds whatever the parent holds - and the parent is
            // written by the object-relational writer, which renders it uppercase.
            //
            // This seed once carried the opposite claim, that only the schema initializer could write
            // this column and that the repository writer and its readers all rendered the minority
            // form. That was true when it was written and is not any more: both were converted in the
            // same change that added the guard below, so GrimoireRepository.InsertBindingAsync now
            // writes exactly what this line does.
            ("$id", Canonical(sessionId)),
            // Canonical, like the SessionId above, though for a different reason.
            // session_campaign_bindings.CampaignId is still unconstrained by any foreign key - it is the
            // historical authority identity, so a Campaign deletion can clear its own row without
            // rewriting it - but its two production writers no longer disagree about how to spell one:
            // GrimoireRepository.InsertBindingAsync, the writer this harness stands in for, now renders
            // exactly this. The Saga store once copied that spelling into saga_memories.CampaignId - it
            // now canonicalizes the identity first, so a memory no longer inherits it - and
            // DivinationService and DataRetentionService bind it back exactly.
            ("$campaignId", Canonical(campaignId)),
            ("$now", now)).ConfigureAwait(false);

        return sessionId;

    }

    /// <summary>
    /// Creates a Session with no <c>session_campaign_bindings</c> row at all, so a caller can drive a
    /// Saga write the production scope classifier resolves into
    /// <see cref="SagaMemoryScopeKind.LegacyUnresolved"/> — never by declaring that scope directly.
    /// </summary>
    /// <remarks>
    /// Deliberately the one thing <see cref="SessionBoundToNewCampaignAsync"/> does that this does not:
    /// no binding row. <see cref="SagaMemoryScopeClassifier"/> reads that absence, not a null Campaign
    /// column, as the "ownership never resolved" case.
    /// </remarks>
    public async Task<Guid> SessionWithUnresolvedBindingAsync()
    {

        Guid sessionId = Guid.NewGuid();

        string now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        await ExecuteAsync(
            """
            INSERT INTO "Sessions" ("Id", "CampaignId", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, NULL, 'active', $now, $now);
            """,
            ("$id", Canonical(sessionId)),
            ("$now", now)).ConfigureAwait(false);

        return sessionId;

    }

    /// <summary>
    /// The spelling every writer of these columns renders: uppercase, dashed, 36 characters.
    /// </summary>
    /// <remarks>
    /// The Campaign and the Session below are written by the object-relational writer in production, and
    /// the SQLite value binder uppercases a Guid unconditionally. A bare <c>ToString()</c> here seeded
    /// the one spelling no writer produces, which the version-5 identity guards now refuse outright.
    /// </remarks>
    private static string Canonical(Guid identity) => identity.ToString("D").ToUpperInvariant();

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {

        await using DbCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {

            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);

        }

        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

    }

    public async ValueTask DisposeAsync()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        await _db.DisposeAsync().ConfigureAwait(false);

        // Deletes the one copy CreateAsync made, including its -wal/-shm/.kdf siblings.
        _fixture.Dispose();

    }

}
