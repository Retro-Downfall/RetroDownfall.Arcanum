using System.Globalization;

using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Performance;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Covenant.Benchmarks;

/// <summary>
/// A real encrypted installation holding the pinned corpus, composed of production services.
/// </summary>
/// <remarks>
/// Nothing here reimplements a Covenant rule. The schema comes from the production catalog and its
/// own initializer, the content from the production compiler, and every seeded entry goes in through
/// the same prepare-and-commit path an operator's <c>memory covenant set</c> takes. A bed that wrote
/// rows directly would let the benchmark and the product disagree about what a row costs, and the
/// number the gate published would be the cost of the bed.
/// </remarks>
internal sealed class CovenantWorkloadBed : IAsyncDisposable
{

    /// <summary>Fixed, and not a secret: this installation exists for the length of one benchmark run.</summary>
    private const string Passphrase = "covenant-benchmark-passphrase";

    private static readonly Guid DatasetGeneration = new("5b1f7c48-2d63-4a91-9e07-8c4b6d2a1f33");

    private static readonly Guid InstallationIdentity = new("6f1c0b2e-9a44-4e1d-8b7a-2c5d3f6a8e90");

    private readonly string _directory;

    private readonly SqliteConnection _connection;

    private CovenantWorkloadBed(string directory, SqliteConnection connection)
    {

        _directory = directory;

        _connection = connection;

    }

    internal CovenantStore Store { get; private set; } = null!;

    internal CovenantOperationGate Gate { get; private set; } = null!;

    internal CovenantMutationService Mutations { get; private set; } = null!;

    internal CovenantManagementService Management { get; private set; } = null!;

    internal CovenantContextProvider Context { get; private set; } = null!;

    internal BenchmarkAvailability Availability { get; } = new(DatasetGeneration);

    internal BenchmarkAuthority Authority { get; } = new(InstallationIdentity);

    internal Guid[] Campaigns { get; private set; } = [];

    /// <summary>The digest of the exact corpus that was seeded, over keys and authored content in order.</summary>
    internal string CorpusDigest { get; private set; } = string.Empty;

    internal static async Task<CovenantWorkloadBed> CreateAsync(
        WorkloadManifest manifest,
        CancellationToken cancellationToken)
    {

        SqliteNativeRuntime.Instance.Initialize();

        string directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"covenant-benchmark-{Guid.NewGuid():N}")).FullName;

        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "arcanum.db"),

                Password = Passphrase,

                // Pooling would hand the same native handle back with another run's authorization
                // state already applied, and a benchmark cannot tell that apart from a slow query.
                Pooling = false,
            }.ToString());

        CovenantWorkloadBed bed = new(directory, connection);

        try
        {

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await CovenantSqliteConnectionInitializer.Instance
                .InitializeAsync(connection, CovenantSqliteConnectionMode.ReadWrite, cancellationToken)
                .ConfigureAwait(false);

            await bed.InstallSchemaAsync(cancellationToken).ConfigureAwait(false);

            bed.Compose();

            await bed.SeedAsync(manifest, cancellationToken).ConfigureAwait(false);

            return bed;

        }
        catch
        {

            await bed.DisposeAsync().ConfigureAwait(false);

            throw;

        }

    }

    private async Task InstallSchemaAsync(CancellationToken cancellationToken)
    {

        foreach (string name in (string[])["Campaigns", "campaign_registry_state", "owner_deletion_events"])
        {

            await ExecuteAsync(CoreObjectSql(name), cancellationToken).ConfigureAwait(false);

        }

        await ExecuteAsync(
            "INSERT OR IGNORE INTO campaign_registry_state (StateKey, RegistryEpoch) VALUES (1, 1);",
            cancellationToken).ConfigureAwait(false);

        foreach (GrimoireSchemaObject definition in GrimoireSchemaCatalog.CovenantCanonicalObjects)
        {

            await ExecuteAsync(
                GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null),
                cancellationToken).ConfigureAwait(false);

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await new CovenantCanonicalSchemaDataInitializer()
            .InitializeAsync(
                _connection,
                transaction,
                new GrimoireSchemaInitializationContext(
                    InstallationIdentity.ToString("D").ToUpperInvariant(),
                    AuthorityEpoch: 1,
                    MasterKeyVersion: 1,
                    MasterKeyFingerprint: SHA256.HashData("covenant-benchmark-master-material"u8),
                    RecoveryEnvelopeEpoch: 1,
                    DateTimeOffset.UnixEpoch),
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    }

    private void Compose()
    {

        Store = new CovenantStore(new FixedConnectionSource(_connection));

        CovenantRuntimeGenerationProvider runtime = new();

        CovenantEnvelopeMasterKeyProvider keys = new(runtime);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = keys.PrepareInitial(
            Encoding.UTF8.GetBytes("covenant-benchmark-master-material"),
            new CovenantEnvelopeBootstrapKeyInput(
                Authority.Current!.InstallationIdentity,
                Authority.Current.MasterKeyVersion,
                canonicalEnvelopeEpoch: 1,
                Authority.Current.RecoveryEnvelopeEpoch,
                DatasetGeneration));

        if (prepared.IsFailure)
        {

            throw new InvalidOperationException(prepared.Error.Message);

        }

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        CovenantAvailabilitySnapshot boot = runtime.PublishAvailability(_ => Availability.Current);

        Result initialized = runtime.Initialize(runtime.Current, owned, Authority.Current, boot);

        if (initialized.IsFailure)
        {

            throw new InvalidOperationException(initialized.Error.Message);

        }

        Availability.Bind(runtime);

        Authority.Bind(runtime);

        Gate = new CovenantOperationGate(runtime, new BenchmarkCampaignScopeProbe(() => Campaigns));

        CovenantEnvelopeCodec codec = new(keys, TimeProvider.System);

        Mutations = new CovenantMutationService(
            Store,
            new CovenantCompiler(),
            codec,
            new FixedConnectionSource(_connection),
            new CovenantMutationKernel(new CovenantQuotaGuard(CovenantSqliteConnectionInitializer.Instance)),
            new CovenantCurationKernel(),
            Authority,
            TimeProvider.System);

        Management = new CovenantManagementService(
            Store,
            new CovenantLinker(),
            Gate,
            Availability,
            codec,
            new BenchmarkCampaignAvailabilityReader(() => Campaigns));

        Context = new CovenantContextProvider(Availability, Gate, Store, new CovenantLinker());

    }

    private async Task SeedAsync(WorkloadManifest manifest, CancellationToken cancellationToken)
    {

        Campaigns = [.. Enumerable.Range(0, manifest.Corpus.Campaigns).Select(CampaignIdentity)];

        foreach (Guid campaignId in Campaigns)
        {

            await AddCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        }

        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // The expansion comes from Core rather than from a copy here, so the digest the ordinary suite
        // recomputes from the manifest and the digest this run reports over what it actually wrote are
        // the same arithmetic. Two copies would agree with each other and drift from the file.
        foreach (BenchmarkCorpusEntry entry in BenchmarkCorpus.Entries(manifest.Corpus))
        {

            digest.AppendData(Encoding.UTF8.GetBytes(entry.Key));

            digest.AppendData(Encoding.UTF8.GetBytes(entry.Content));

            await WriteAsync(
                entry.CampaignOrdinal < 0 ? CovenantScope.Global : CovenantScope.Campaign,
                entry.CampaignOrdinal < 0 ? null : Campaigns[entry.CampaignOrdinal],
                entry.Key,
                entry.Content,
                cancellationToken).ConfigureAwait(false);

        }

        CorpusDigest = Convert.ToHexStringLower(digest.GetHashAndReset());

    }

    private async Task WriteAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        string content,
        CancellationToken cancellationToken)
    {

        CovenantOperationScope operationScope = campaignId is { } campaign
            ? CovenantOperationScope.ForCampaign(campaign)
            : CovenantOperationScope.Global;

        Guid mutationId = Guid.CreateVersion7();

        string preflight;

        if (campaignId is null)
        {

            await using CovenantInstallationReadLease read =
                Unwrap(await Gate.AcquireInstallationReadAsync(cancellationToken).ConfigureAwait(false));

            preflight = Unwrap(await Mutations.PrepareSetAsync(
                new CovenantSetPrepareRequest(scope, campaignId, key, content, 0, mutationId, false),
                read,
                cancellationToken).ConfigureAwait(false)).PreflightToken;

        }
        else
        {

            await using CovenantReadLease read =
                Unwrap(await Gate.AcquireReadAsync(operationScope, cancellationToken).ConfigureAwait(false));

            preflight = Unwrap(await Mutations.PrepareSetAsync(
                new CovenantSetPrepareRequest(scope, campaignId, key, content, 0, mutationId, false),
                read,
                cancellationToken).ConfigureAwait(false)).PreflightToken;

        }

        await using CovenantWriteLease write =
            Unwrap(await Gate.AcquireWriteAsync(operationScope, cancellationToken).ConfigureAwait(false));

        _ = Unwrap(await Mutations.SetAsync(
            new CovenantSetRequest(scope, campaignId, key, content, 0, mutationId, false, preflight),
            write,
            cancellationToken).ConfigureAwait(false));

    }

    /// <summary>A stable Campaign identity per ordinal, so two runs seed the same installation.</summary>
    private static Guid CampaignIdentity(int ordinal)
    {

        Span<byte> bytes = stackalloc byte[16];

        "7e1d9c4205b84f63"u8.CopyTo(bytes);

        "9a0e3c8b"u8.CopyTo(bytes[8..]);

        bytes[12] = 0x27;

        bytes[13] = 0xd6;

        bytes[14] = 0x41;

        bytes[15] = (byte)ordinal;

        return new Guid(bytes);

    }

    private async Task AddCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        // The identity is bound as a Guid rather than as formatted text, so the provider stores exactly
        // the uppercase D-format string EF's SQLite mapping produces. Seeding lowercase would hide
        // every query that compares its own identity text against this EF-owned column.
        command.CommandText = """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $nameLower, $path, 1, '{}', $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$id", campaignId);

        _ = command.Parameters.AddWithValue("$name", $"Benchmark {campaignId:N}");

        _ = command.Parameters.AddWithValue("$nameLower", $"benchmark {campaignId:N}");

        _ = command.Parameters.AddWithValue("$path", $"/benchmark/{campaignId:N}");

        _ = command.Parameters.AddWithValue("$now", DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static string CoreObjectSql(string name)
    {

        GrimoireSchemaObject definition = GrimoireSchemaCatalog.CoreObjects
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The core schema catalog has no object named '{name}'.");

        return GrimoireSchemaCatalog.Resolve(definition, embeddingDimensions: null);

    }

    /// <summary>Hands every service the one open connection this benchmark installation has.</summary>
    /// <remarks>
    /// One long-lived connection rather than the per-scope one production resolves, because a
    /// benchmark process has no request scope to resolve it from. It is one of the four substituted
    /// seams DESIGN enumerates, and the residence latch below is why the substitution does not also
    /// move work out of the measured path.
    /// </remarks>
    private sealed class FixedConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {

            // The production source latches this on every acquisition, so a bed that skipped it would
            // measure a canonical read that costs slightly less than the one the product performs.
            CovenantProcessResidence.MarkOpened();

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(connection);

        }

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            GetOpenConnectionAsync(cancellationToken);

    }

    internal static T Unwrap<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException(result.Error.Message);

    public async ValueTask DisposeAsync()
    {

        await _connection.DisposeAsync().ConfigureAwait(false);

        SqliteConnection.ClearAllPools();

        try
        {

            Directory.Delete(_directory, recursive: true);

        }
        catch (IOException)
        {

            // A benchmark that failed to clean up has still produced its numbers, and refusing to
            // report them because a temp directory was busy would be the wrong failure to surface.

        }

    }

}
