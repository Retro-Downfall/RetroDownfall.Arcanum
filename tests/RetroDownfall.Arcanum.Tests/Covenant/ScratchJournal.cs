using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// One scratch Campaign path marker journal, written through the same connection-local authorization
/// production uses.
/// </summary>
/// <remarks>
/// Every write here goes through <see cref="CovenantSqliteAuthorizationKind"/> rather than around it.
/// A helper that reached the tables with a raw connection would seed rows the guards never saw, and a
/// suite asserting a guard against such a row proves only that the helper agrees with itself.
/// </remarks>
internal sealed class ScratchJournal : IAsyncDisposable
{

    private const int FullInstallationResetCleanup = 4;

    private const int Opened = 1;

    private const int Prepared = 1;

    /// <summary>The companion digests that have no legal null or short form.</summary>
    internal static readonly string[] RequiredDigestColumns =
    [
        "CampaignInventoryEntryDigest",
        "IndexedPhysicalIdentityDigest",
        "CanonicalDisplayPathDigest",
        "SameHandleOwnershipEvidenceDigest",
        "ObservationDigest",
    ];

    /// <summary>
    /// The parent columns a kind-four row may never set, each paired with a value that would be legal
    /// for some other kind. Kind four has no in-process gate to dispose, no marker payload of its own
    /// to destroy, and no public request to answer, so any of these would be authority it never
    /// received.
    /// </summary>
    internal static readonly (string Column, object Value)[] ForbiddenKindFourColumns =
    [
        ("ExclusiveOwnerOperationCode", 1L),
        ("ApplyRequestDigest", Digest(0x31)),
        ("EncryptedMarkerPayload", new byte[] { 1, 2, 3 }),
        ("TemporaryBaseName", "temp-name"),
        ("TemporaryPhysicalIdentityDigest", Digest(0x32)),
        ("TargetObservationCode", 1L),
        ("ReopenedTargetPhysicalIdentityDigest", Digest(0x33)),
        ("PendingDispositionCode", 2L),
    ];

    private static readonly string[] SchemaObjects =
    [
        "campaign_path_marker_intents",
        "campaign_path_marker_intents_guard_insert",
        "campaign_path_marker_intents_guard_update",
        "campaign_path_marker_intents_guard_delete",
        "campaign_path_full_reset_cleanup_evidence",
        "campaign_path_full_reset_cleanup_evidence_guard_insert",
        "campaign_path_full_reset_cleanup_evidence_guard_update",
        "campaign_path_full_reset_cleanup_evidence_guard_delete",
    ];

    private readonly CovenantSchemaScratchDatabase _database;

    private ScratchJournal(CovenantSchemaScratchDatabase database) => _database = database;

    internal static async Task<ScratchJournal> CreateAsync()
    {

        CovenantSchemaScratchDatabase database =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        try
        {

            await database.InstallCoreObjectsAsync(SchemaObjects, CancellationToken.None);

            return new ScratchJournal(database);

        }
        catch
        {

            await database.DisposeAsync();

            throw;

        }

    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    internal static byte[] Digest(byte fill)
    {

        byte[] bytes = new byte[32];

        Array.Fill(bytes, fill);

        return bytes;

    }

    internal Task<string> InsertParentKindFourAsync(string? displayPath) =>
        InsertParentAsync(
            kind: FullInstallationResetCleanup,
            gateOwner: null,
            displayPath: displayPath,
            phase: Prepared);

    /// <summary>Seeds a kind-four parent and the companion observation that belongs to it.</summary>
    internal async Task<string> InsertKindFourAsync(int observation)
    {

        string intent = await InsertParentKindFourAsync(
            displayPath: observation == Opened ? "/tmp/one" : null);

        await InsertCompanionAsync(intent, observation);

        return intent;

    }

    internal async Task<string> InsertParentAsync(
        int kind,
        long? gateOwner,
        string? displayPath,
        int phase,
        Action<Dictionary<string, object>>? mutate = null)
    {

        string intent = Guid.NewGuid().ToString("D");

        Dictionary<string, object> values = new(StringComparer.Ordinal)
        {
            ["IntentId"] = intent,
            ["OwnerOperationId"] = "66666666-7777-4888-8999-aaaaaaaaaaaa",
            ["CampaignId"] = Guid.NewGuid().ToString("D"),
            ["IntentKindCode"] = (long)kind,
            ["ExclusiveOwnerOperationCode"] = gateOwner is null ? DBNull.Value : gateOwner.Value,
            ["OwnerEffectDigest"] = Digest(0x50),
            ["EncryptedMarkerPayload"] = DBNull.Value,
            ["MarkerDigest"] = Digest(0x60),
            ["ApplyRequestDigest"] = DBNull.Value,
            ["TemporaryBaseName"] = DBNull.Value,
            ["TemporaryPhysicalIdentityDigest"] = DBNull.Value,
            ["TargetDisplayPath"] = displayPath is null ? DBNull.Value : displayPath,
            ["PriorRevision"] = 8L,
            ["TargetObservationCode"] = DBNull.Value,
            ["ReopenedTargetPhysicalIdentityDigest"] = DBNull.Value,
            ["PendingDispositionCode"] = DBNull.Value,
            ["PhaseCode"] = (long)phase,
            ["PhaseRevision"] = 1L,
            ["CreatedAtUtc"] = "2026-08-23T00:00:00.0000000+00:00",
            ["UpdatedAtUtc"] = "2026-08-23T00:00:00.0000000+00:00",
        };

        mutate?.Invoke(values);

        // The payload and temporary-name capability are one pair the table refuses to see half of,
        // so a mutation that supplies either alone gets its partner here rather than failing on an
        // unrelated constraint and passing a test for the wrong reason.
        if (values["EncryptedMarkerPayload"] is not DBNull && values["TemporaryBaseName"] is DBNull)
        {

            values["TemporaryBaseName"] = "temp-name";

        }
        else if (values["TemporaryBaseName"] is not DBNull && values["EncryptedMarkerPayload"] is DBNull)
        {

            values["EncryptedMarkerPayload"] = new byte[] { 1, 2, 3 };

        }

        await ExecuteAuthorizedAsync(
            $"INSERT INTO campaign_path_marker_intents ({string.Join(", ", values.Keys)}) "
            + $"VALUES ({string.Join(", ", values.Keys.Select(static key => "$" + key))});",
            values);

        return intent;

    }

    internal Task InsertCompanionAsync(
        string intentId,
        int observation,
        Action<Dictionary<string, object>>? mutate = null)
    {

        Dictionary<string, object> values = new(StringComparer.Ordinal)
        {
            ["IntentId"] = intentId,
            ["CampaignInventoryEntryDigest"] = Digest(0x71),
            ["IndexedPhysicalIdentityDigest"] = Digest(0x72),
            ["CanonicalDisplayPathDigest"] = Digest(0x73),
            ["SameHandleOwnershipEvidenceDigest"] = Digest(0x74),
            ["ObservationCode"] = (long)observation,
            ["OpenedSameHandleOwnershipEvidenceDigest"] =
                observation == Opened ? Digest(0x74) : DBNull.Value,
            ["ObservationDigest"] = Digest(0x75),
        };

        mutate?.Invoke(values);

        return ExecuteAuthorizedAsync(
            $"INSERT INTO campaign_path_full_reset_cleanup_evidence ({string.Join(", ", values.Keys)}) "
            + $"VALUES ({string.Join(", ", values.Keys.Select(static key => "$" + key))});",
            values);

    }

    internal Task<SqliteException> ExpectCompanionInsertFailureAsync(
        string intentId,
        int observation,
        Action<Dictionary<string, object>>? mutate = null) =>
        Assert.ThrowsAsync<SqliteException>(
            () => InsertCompanionAsync(intentId, observation, mutate));

    internal async Task<int> CountCompanionsAsync(string intentId)
    {

        await using SqliteCommand command = _database.Connection.CreateCommand();

        command.CommandText =
            "SELECT COUNT(*) FROM campaign_path_full_reset_cleanup_evidence WHERE IntentId = $intent;";

        _ = command.Parameters.AddWithValue("$intent", intentId);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

    internal async Task<int> ReadPhaseAsync(string intentId)
    {

        await using SqliteCommand command = _database.Connection.CreateCommand();

        command.CommandText =
            "SELECT PhaseCode FROM campaign_path_marker_intents WHERE IntentId = $intent;";

        _ = command.Parameters.AddWithValue("$intent", intentId);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

    internal Task AdvancePhaseAsync(string intentId, int phase) =>
        ExecuteAuthorizedAsync(
            """
            UPDATE campaign_path_marker_intents
            SET PhaseCode = $phase,
                PhaseRevision = PhaseRevision + 1,
                UpdatedAtUtc = '2026-08-23T00:00:01.0000000+00:00'
            WHERE IntentId = $intent;
            """,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["phase"] = (long)phase,
                ["intent"] = intentId,
            });

    internal Task UpdateTargetDisplayPathAsync(string intentId, string? newPath) =>
        ExecuteAuthorizedAsync(
            """
            UPDATE campaign_path_marker_intents
            SET TargetDisplayPath = $path,
                PhaseRevision = PhaseRevision + 1,
                UpdatedAtUtc = '2026-08-23T00:00:01.0000000+00:00'
            WHERE IntentId = $intent;
            """,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["path"] = newPath is null ? DBNull.Value : newPath,
                ["intent"] = intentId,
            });

    internal Task UpdateCompanionObservationDigestAsync(string intentId, byte[] digest) =>
        ExecuteAuthorizedAsync(
            """
            UPDATE campaign_path_full_reset_cleanup_evidence
            SET ObservationDigest = $digest
            WHERE IntentId = $intent;
            """,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["digest"] = digest,
                ["intent"] = intentId,
            });

    internal Task DeleteCompanionAsync(string intentId) =>
        ExecuteAuthorizedAsync(
            "DELETE FROM campaign_path_full_reset_cleanup_evidence WHERE IntentId = $intent;",
            new Dictionary<string, object>(StringComparer.Ordinal) { ["intent"] = intentId });

    /// <summary>Retention's own authorization pair: the mutation scope plus an owner cleanup.</summary>
    internal Task DeleteParentAsync(string intentId) =>
        DeleteParentAsync(intentId, CovenantSqliteAuthorizationKind.OwnerCleanup);

    internal Task DeleteParentUnderFamilyMaintenanceAsync(string intentId) =>
        DeleteParentAsync(intentId, CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

    private async Task DeleteParentAsync(string intentId, CovenantSqliteAuthorizationKind cleanup)
    {

        using CovenantSqliteAuthorizationScope mutation =
            CovenantSqliteConnectionInitializer.Instance.Authorize(
                _database.Connection,
                CovenantSqliteAuthorizationKind.CampaignPathMarkerIntentMutation);

        using CovenantSqliteAuthorizationScope cleanupScope =
            CovenantSqliteConnectionInitializer.Instance.Authorize(_database.Connection, cleanup);

        await using SqliteCommand command = _database.Connection.CreateCommand();

        command.CommandText = "DELETE FROM campaign_path_marker_intents WHERE IntentId = $intent;";

        _ = command.Parameters.AddWithValue("$intent", intentId);

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task ExecuteAuthorizedAsync(string sql, Dictionary<string, object> values)
    {

        using CovenantSqliteAuthorizationScope authorization =
            CovenantSqliteConnectionInitializer.Instance.Authorize(
                _database.Connection,
                CovenantSqliteAuthorizationKind.CampaignPathMarkerIntentMutation);

        await using SqliteCommand command = _database.Connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string key, object value) in values)
        {

            _ = command.Parameters.AddWithValue("$" + key, value);

        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

}
