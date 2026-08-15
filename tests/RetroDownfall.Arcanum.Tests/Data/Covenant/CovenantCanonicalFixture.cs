using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A scratch canonical Grimoire seeded with real compiled Covenant rows.
/// </summary>
/// <remarks>
/// Content is produced by the real <see cref="CovenantCompiler"/> rather than by hand. The store
/// verifies rendered hashes, byte costs, and policy versions before it will link anything, so a
/// hand-written row would either fail verification or force the fixture to duplicate the compiler's
/// rules and drift from them.
/// </remarks>
internal sealed class CovenantCanonicalFixture : IAsyncDisposable
{

    private static readonly DateTimeOffset SeedTime = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly CovenantSchemaScratchDatabase _database;

    private CovenantCanonicalFixture(CovenantSchemaScratchDatabase database) => _database = database;

    internal SqliteConnection Connection => _database.Connection;

    internal Task<SqliteConnection> OpenAdditionalConnectionAsync(CancellationToken cancellationToken) =>
        _database.OpenAdditionalConnectionAsync(cancellationToken);

    internal CovenantStore Store { get; private set; } = null!;

    internal static async Task<CovenantCanonicalFixture> CreateAsync(
        CancellationToken cancellationToken,
        bool withAccelerator = false,
        IReadOnlyList<string>? coreObjects = null)
    {

        CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase.CreateAsync(cancellationToken);

        CovenantCanonicalFixture fixture = new(database);

        try
        {

            await database.InstallCoreObjectsAsync(
                [.. (IReadOnlyList<string>)["Campaigns", "campaign_registry_state"], .. coreObjects ?? []],
                cancellationToken);

            await database.ExecuteAsync(
                "INSERT OR IGNORE INTO campaign_registry_state (StateKey, RegistryEpoch) VALUES (1, 1);",
                cancellationToken);

            await database.InstallCanonicalAsync(cancellationToken);

            if (withAccelerator)
            {

                await database.InstallAcceleratorAsync(cancellationToken);

            }

            fixture.Store = new CovenantStore(new FixedCovenantConnectionSource(database.Connection));

            return fixture;

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    internal async Task AddCampaignAsync(Guid campaignId, string name, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $nameLower, $root, 1, '{}', $created, $updated);
            """;

        _ = command.Parameters.AddWithValue("$id", campaignId.ToString("D"));

        _ = command.Parameters.AddWithValue("$name", name);

        _ = command.Parameters.AddWithValue("$nameLower", name.ToLowerInvariant());

        _ = command.Parameters.AddWithValue("$root", $"/tmp/{name}");

        _ = command.Parameters.AddWithValue("$created", Iso(SeedTime));

        _ = command.Parameters.AddWithValue("$updated", Iso(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

        await BumpRegistryEpochAsync(cancellationToken);

    }

    /// <summary>
    /// Appends one immutable version and advances its lane head, exactly as the mutation kernel
    /// will once Task 9 lands.
    /// </summary>
    internal async Task<SeededHead> SeedHeadAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        CovenantLane lane,
        CovenantOperation operation,
        string? authoredContent,
        CancellationToken cancellationToken,
        CovenantOrigin? origin = null,
        Guid? entryId = null,
        long laneRevision = 1,
        Guid? predecessorVersionId = null,
        ImmutableArray<MaterializationSourceDigestInput>? sources = null,
        string? corruptCompiledContent = null)
    {

        CovenantOrigin resolvedOrigin = origin
            ?? (lane == CovenantLane.Proposed ? CovenantOrigin.AgentProposed : CovenantOrigin.Operator);

        Guid resolvedEntryId = entryId ?? Guid.NewGuid();

        Guid versionId = Guid.NewGuid();

        if (entryId is null)
        {

            await InsertEntryAsync(resolvedEntryId, scope, campaignId, key, cancellationToken);

        }

        CovenantCompiledContent? compiled = operation == CovenantOperation.Set
            ? new CovenantCompiler().Compile(key, authoredContent ?? key)
            : null;

        string? storedCompiled = corruptCompiledContent ?? compiled?.Fragment;

        long byteCost = storedCompiled is null
            ? 0
            : System.Text.Encoding.UTF8.GetByteCount(storedCompiled);

        ImmutableArray<MaterializationSourceDigestInput> leaves = sources ?? [];

        CovenantDigest resolvedProvenanceDigest = CovenantDigests.Materialization(
            new MaterializationDigestInput(Unprovenanced: leaves.Length == 0, leaves));

        await InsertVersionAsync(
            versionId,
            resolvedEntryId,
            lane,
            laneRevision,
            operation,
            resolvedOrigin,
            compiled,
            storedCompiled,
            byteCost,
            predecessorVersionId,
            leaves.Length,
            resolvedProvenanceDigest,
            cancellationToken);

        for (int ordinal = 0; ordinal < leaves.Length; ordinal++)
        {

            await AddProvenanceAsync(versionId, ordinal, leaves[ordinal], cancellationToken);

        }

        // A projection row ID is allocated once per entry and lane and never moves, so an advance
        // over an existing head reuses it rather than claiming a second one.
        long searchRowId = await ExistingSearchRowIdAsync(resolvedEntryId, lane, cancellationToken)
            ?? await AllocateSearchRowIdAsync(cancellationToken);

        await UpsertHeadAsync(
            resolvedEntryId,
            lane,
            versionId,
            laneRevision,
            operation,
            scope,
            campaignId,
            key,
            byteCost,
            resolvedOrigin,
            searchRowId,
            cancellationToken);

        await AppendOutboxAsync(searchRowId, resolvedEntryId, lane, versionId, cancellationToken);

        return new SeededHead(resolvedEntryId, versionId, searchRowId, compiled);

    }

    internal async Task AddProvenanceAsync(
        Guid versionId,
        int ordinal,
        MaterializationSourceDigestInput source,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_version_attachment_provenance (
                VersionId, Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference)
            VALUES ($version, $ordinal, $attachment, $identity, $logical, $hash, $rangeKind, $start, $end, NULL, NULL);
            """;

        _ = command.Parameters.AddWithValue("$version", versionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$ordinal", ordinal);

        _ = command.Parameters.AddWithValue("$attachment", source.AttachmentId.ToString("D"));

        _ = command.Parameters.AddWithValue("$identity", source.AttachmentVersionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$logical", source.LogicalKey);

        _ = command.Parameters.AddWithValue("$hash", source.ContentDigest.Bytes);

        _ = command.Parameters.AddWithValue("$rangeKind", (int)source.SourceRange);

        _ = command.Parameters.AddWithValue(
            "$start",
            source.SourceStart is { } start ? start : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$end",
            source.SourceEnd is { } end ? end : DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    internal static CanonicalCampaignContext CampaignContext(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: 1,
            rootIdentityDigest: CovenantOperationGateFixture.Digest(3));

    internal async Task<Guid> ReadDatasetGenerationAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return new Guid((byte[])value!);

    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private async Task InsertEntryAsync(
        Guid entryId,
        CovenantScope scope,
        Guid? campaignId,
        string key,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
            VALUES ($entry, $scope, $campaign, $authored, $normalized, $created);
            """;

        _ = command.Parameters.AddWithValue("$entry", entryId.ToString("D"));

        _ = command.Parameters.AddWithValue("$scope", (int)scope);

        _ = command.Parameters.AddWithValue(
            "$campaign",
            campaignId is { } present ? present.ToString("D") : DBNull.Value);

        _ = command.Parameters.AddWithValue("$authored", key);

        _ = command.Parameters.AddWithValue("$normalized", key);

        _ = command.Parameters.AddWithValue("$created", Iso(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task InsertVersionAsync(
        Guid versionId,
        Guid entryId,
        CovenantLane lane,
        long laneRevision,
        CovenantOperation operation,
        CovenantOrigin origin,
        CovenantCompiledContent? compiled,
        string? storedCompiled,
        long byteCost,
        Guid? predecessorVersionId,
        int provenanceCount,
        CovenantDigest provenanceDigest,
        CancellationToken cancellationToken)
    {

        bool agentAuthored = origin is CovenantOrigin.AgentProposed or CovenantOrigin.AgentApproved;

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_versions (
                VersionId, EntryId, LaneCode, LaneRevision, OperationCode, AuthoredContent, CompiledContent,
                AuthoredHash, RenderedHash, CompiledByteCost, RequiredFenceLength, CompilerPolicyVersion,
                RendererPolicyVersion, OriginCode, SourceTurnId, SourceToolCallId, BasePlanDigest,
                AdmissionReceiptDigest, WardReceiptDigest, AuthorizationModeCode, MutationId,
                RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, PredecessorVersionId,
                AttachmentProvenanceCount, AttachmentProvenanceDigest, CreatedAtUtc)
            VALUES (
                $version, $entry, $lane, $revision, $operation, $authored, $compiled,
                $authoredHash, $renderedHash, $byteCost, $fence, 1,
                1, $origin, $turn, $toolCall, $basePlan,
                NULL, $wardReceipt, $authorizationMode, $mutation,
                $requestDigest, $authorizationDigest, $finalDigest, $predecessor,
                $provenanceCount, $provenanceDigest, $created);
            """;

        _ = command.Parameters.AddWithValue("$version", versionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$entry", entryId.ToString("D"));

        _ = command.Parameters.AddWithValue("$lane", (int)lane);

        _ = command.Parameters.AddWithValue("$revision", laneRevision);

        _ = command.Parameters.AddWithValue("$operation", (int)operation);

        _ = command.Parameters.AddWithValue(
            "$authored",
            compiled is null ? DBNull.Value : compiled.AuthoredContent);

        _ = command.Parameters.AddWithValue(
            "$compiled",
            storedCompiled is null ? DBNull.Value : storedCompiled);

        _ = command.Parameters.AddWithValue(
            "$authoredHash",
            compiled is null ? DBNull.Value : compiled.AuthoredHash.Bytes);

        _ = command.Parameters.AddWithValue(
            "$renderedHash",
            compiled is null ? DBNull.Value : compiled.FragmentHash.Bytes);

        _ = command.Parameters.AddWithValue("$byteCost", byteCost);

        _ = command.Parameters.AddWithValue("$fence", compiled?.RequiredFenceLength ?? 0);

        _ = command.Parameters.AddWithValue("$origin", (int)origin);

        _ = command.Parameters.AddWithValue(
            "$turn",
            agentAuthored ? Guid.NewGuid().ToString("D") : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$toolCall",
            agentAuthored ? "call-1" : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$basePlan",
            agentAuthored ? CovenantOperationGateFixture.Digest(11).Bytes : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$wardReceipt",
            origin == CovenantOrigin.AgentApproved ? CovenantOperationGateFixture.Digest(12).Bytes : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$authorizationMode",
            origin == CovenantOrigin.AgentApproved ? 2 : DBNull.Value);

        _ = command.Parameters.AddWithValue("$mutation", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$requestDigest", CovenantOperationGateFixture.Digest(13).Bytes);

        _ = command.Parameters.AddWithValue("$authorizationDigest", CovenantOperationGateFixture.Digest(14).Bytes);

        _ = command.Parameters.AddWithValue("$finalDigest", CovenantOperationGateFixture.Digest(15).Bytes);

        _ = command.Parameters.AddWithValue(
            "$predecessor",
            predecessorVersionId is { } present ? present.ToString("D") : DBNull.Value);

        _ = command.Parameters.AddWithValue("$provenanceCount", provenanceCount);

        _ = command.Parameters.AddWithValue("$provenanceDigest", provenanceDigest.Bytes);

        _ = command.Parameters.AddWithValue("$created", Iso(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private async Task UpsertHeadAsync(
        Guid entryId,
        CovenantLane lane,
        Guid versionId,
        long laneRevision,
        CovenantOperation operation,
        CovenantScope scope,
        Guid? campaignId,
        string key,
        long byteCost,
        CovenantOrigin origin,
        long searchRowId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_heads (
                EntryId, LaneCode, CurrentVersionId, CurrentLaneRevision, CurrentOperationCode, ScopeCode,
                CampaignId, NormalizedKey, CompiledByteCost, OriginCode, SearchRowId, UpdatedAtUtc)
            VALUES ($entry, $lane, $version, $revision, $operation, $scope, $campaign, $key, $cost, $origin, $row, $updated)
            ON CONFLICT (EntryId, LaneCode) DO UPDATE SET
                CurrentVersionId = excluded.CurrentVersionId,
                CurrentLaneRevision = excluded.CurrentLaneRevision,
                CurrentOperationCode = excluded.CurrentOperationCode,
                CompiledByteCost = excluded.CompiledByteCost,
                OriginCode = excluded.OriginCode,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        _ = command.Parameters.AddWithValue("$entry", entryId.ToString("D"));

        _ = command.Parameters.AddWithValue("$lane", (int)lane);

        _ = command.Parameters.AddWithValue("$version", versionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$revision", laneRevision);

        _ = command.Parameters.AddWithValue("$operation", (int)operation);

        _ = command.Parameters.AddWithValue("$scope", (int)scope);

        _ = command.Parameters.AddWithValue(
            "$campaign",
            campaignId is { } present ? present.ToString("D") : DBNull.Value);

        _ = command.Parameters.AddWithValue("$key", key);

        _ = command.Parameters.AddWithValue("$cost", byteCost);

        _ = command.Parameters.AddWithValue("$origin", (int)origin);

        _ = command.Parameters.AddWithValue("$row", searchRowId);

        _ = command.Parameters.AddWithValue("$updated", Iso(SeedTime));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Emits the projection delta and advances the canonical search sequence, exactly as the mutation
    /// kernel does. Seeding a head without its outbox row would leave the accelerator with nothing to
    /// apply and make an eligible-index test pass against an empty index.
    /// </summary>
    private async Task AppendOutboxAsync(
        long searchRowId,
        Guid entryId,
        CovenantLane lane,
        Guid versionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            UPDATE covenant_state
            SET CanonicalSearchSequence = CanonicalSearchSequence + 1, UpdatedAtUtc = $updated
            WHERE StateKey = 1;

            INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
            SELECT CanonicalSearchSequence, 0, $row, $entry, $lane, $version
            FROM covenant_state WHERE StateKey = 1;
            """;

        _ = command.Parameters.AddWithValue("$updated", Iso(SeedTime));

        _ = command.Parameters.AddWithValue("$row", searchRowId);

        _ = command.Parameters.AddWithValue("$entry", entryId.ToString("D"));

        _ = command.Parameters.AddWithValue("$lane", (int)lane);

        _ = command.Parameters.AddWithValue("$version", versionId.ToString("D"));

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Claims the next projection row ID exactly as the mutation kernel must: the counter advances
    /// first, because a head at or past it was never allocated and the insert trigger refuses it.
    /// </summary>
    private async Task<long?> ExistingSearchRowIdAsync(
        Guid entryId,
        CovenantLane lane,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "SELECT SearchRowId FROM covenant_heads WHERE EntryId = $entry AND LaneCode = $lane;";

        _ = command.Parameters.AddWithValue("$entry", entryId.ToString("D"));

        _ = command.Parameters.AddWithValue("$lane", (int)lane);

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private async Task<long> AllocateSearchRowIdAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = """
            UPDATE covenant_state SET NextSearchRowId = NextSearchRowId + 1 WHERE StateKey = 1
            RETURNING NextSearchRowId - 1;
            """;

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private async Task BumpRegistryEpochAsync(CancellationToken cancellationToken)
    {

        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "UPDATE campaign_registry_state SET RegistryEpoch = RegistryEpoch + 1 WHERE StateKey = 1;";

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

}

internal readonly record struct SeededHead(
    Guid EntryId,
    Guid VersionId,
    long SearchRowId,
    CovenantCompiledContent? Compiled);

/// <summary>
/// Hands the store one already-open scratch connection, standing in for the scoped Grimoire context.
/// </summary>
internal sealed class FixedCovenantConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
{

    public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(connection);

    }

}
