using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The evidence no Covenant erasure path may remove, and the one place all four of them assert it.
/// </summary>
/// <remarks>
/// Ordinary Covenant reset, healthy-catalog factory erasure, family reinitialize, and ordinary
/// credential cleanup all promise the same three things survive: every Campaign path marker, the
/// exact database host-tools taint row, and the exact operating-system host-tools marker. Four
/// suites each asserting that in their own words is four chances to describe it slightly
/// differently, and the arm whose wording drifted is the arm that stops testing the promise
/// (§10.20.5).
///
/// <para>Two kinds of assertion, because the four paths are not all observable the same way. A path
/// with a real transaction is asserted byte-for-byte against a snapshot taken before it ran;
/// <see cref="AssertNoProductionPathDeletesRetainedEvidence"/> covers all four at once, including
/// the ones whose storage owner does not exist yet, because it is a statement about which production
/// sources may issue the deletion at all rather than about one run.</para>
/// </remarks>
internal static class CovenantRetainedEvidence
{

    /// <summary>
    /// Every schema object a suite must install before it can assert retention.
    /// </summary>
    /// <remarks>
    /// The guard triggers are in the list on purpose. <c>campaign_path_marker_intents_guard_delete</c>
    /// is what actually makes a marker intent survive a family-maintenance authorization, and a suite
    /// that installed the table without it would assert retention against a table nothing protects.
    /// </remarks>
    internal static readonly string[] CoreObjects =
    [
        "campaign_path_identities",
        "campaign_path_marker_intents",
        "campaign_path_marker_intents_guard_insert",
        "campaign_path_marker_intents_guard_update",
        "campaign_path_marker_intents_guard_delete",
        "covenant_authority_state",
    ];

    /// <summary>The tables whose every row must survive, in the order a snapshot renders them.</summary>
    private static readonly (string Table, string Order)[] RetainedTables =
    [
        ("campaign_path_marker_intents", "IntentId"),
        ("campaign_path_identities", "CampaignId"),
        ("covenant_authority_state", "StateKey"),
    ];

    /// <summary>
    /// The production files entitled to name a deletion against any of the retained evidence.
    /// </summary>
    /// <remarks>
    /// The marker intent journal's own store, the one attested compare-deleter of the operating-system
    /// slot, and the restore reconciler that clears a staged, never-published generation. Every other
    /// production file that learned to delete one of these is the regression this inventory exists to
    /// report, and adding a name here is the deliberate act of saying a fifth path may.
    /// </remarks>
    private static readonly string[] EntitledDeleters =
    [
        "CampaignPathMarkerIntentStore.cs",
        "CampaignPathMarkerLifecycle.cs",
        "HostProcessToolsMarkerStore.cs",
        "BackupCovenantRestoreReconciler.cs",
    ];

    private static readonly string[] RetainedDeletionStatements =
    [
        "DELETE FROM campaign_path_marker_intents",
        "DELETE FROM campaign_path_identities",
        "DELETE FROM campaign_path_operation_receipts",
        "DELETE FROM covenant_authority_state",
    ];

    private const string InstallationIdentity = "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90";

    private static readonly Guid TransitionId = new("CCCCCCCC-DDDD-4EEE-8FFF-111111111111");

    private const uint TaintMasterKeyVersion = 4;

    /// <summary>
    /// Seeds one of each retained artifact, in the representation production actually writes.
    /// </summary>
    /// <remarks>
    /// The taint arm rather than the clean one. A clean authority row carries three NULL taint
    /// columns, so an erasure that wiped them would leave a row that still compares equal — the exact
    /// failure the pair exists to rule out — and the retention assertion would pass over it.
    /// </remarks>
    internal static async Task SeedAsync(
        SqliteConnection connection,
        IOsCredentialStore credentials,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(credentials);

        await SeedAuthorityAsync(connection, cancellationToken);

        await SeedCampaignPathIdentityAsync(connection, cancellationToken);

        await SeedMarkerIntentAsync(connection, cancellationToken);

        HostProcessToolsMarkerWriteStatus written = new HostProcessToolsMarkerStore(credentials).Write(
            InstallationIdentity,
            TransitionId,
            TaintMasterKeyVersion,
            Digest(0x40));

        Assert.Equal(HostProcessToolsMarkerWriteStatus.Written, written);

    }

    /// <summary>Renders every retained artifact, so a later render can be compared to it exactly.</summary>
    internal static async Task<CovenantRetainedEvidenceSnapshot> CaptureAsync(
        SqliteConnection connection,
        IOsCredentialStore credentials,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(credentials);

        List<string> rows = [];

        foreach ((string table, string order) in RetainedTables)
        {

            rows.AddRange(await RenderAsync(connection, table, order, cancellationToken));

        }

        OsCredentialStoreResult marker = credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount);

        return new CovenantRetainedEvidenceSnapshot(rows, marker.Status, marker.Value);

    }

    /// <summary>
    /// Asserts every retained artifact survived byte-for-byte, and that there was something to lose.
    /// </summary>
    /// <remarks>
    /// The non-empty check is not ceremony. A snapshot of an installation that never seeded a marker
    /// or a taint row compares equal to itself after an erasure that deleted everything, so without
    /// it the strongest-looking assertion in this file is the one that cannot fail.
    /// </remarks>
    internal static async Task AssertRetainedAsync(
        CovenantRetainedEvidenceSnapshot before,
        SqliteConnection connection,
        IOsCredentialStore credentials,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(before);

        Assert.NotEmpty(before.Rows);

        Assert.Equal(OsCredentialStoreStatus.Ok, before.MarkerStatus);

        Assert.False(string.IsNullOrEmpty(before.MarkerPayload));

        CovenantRetainedEvidenceSnapshot after = await CaptureAsync(connection, credentials, cancellationToken);

        Assert.Equal(before.Rows, after.Rows);

        Assert.Equal(before.MarkerStatus, after.MarkerStatus);

        Assert.Equal(before.MarkerPayload, after.MarkerPayload);

    }

    /// <summary>
    /// The one assertion every path reads, including the two whose storage owner does not exist yet.
    /// </summary>
    /// <remarks>
    /// Family reinitialize and ordinary credential cleanup cannot be asserted against a real
    /// transaction — the first reaches storage through a seam with no production implementation, and
    /// the second deletes named accounts from a store with no enumeration surface. What can be
    /// asserted for all four at once is that no production file outside a closed list is even able to
    /// issue the deletion. That is the same shape the restore-journal accounts are protected by, and
    /// it fails on the new call site rather than on the run that happens to exercise it.
    /// </remarks>
    internal static void AssertNoProductionPathDeletesRetainedEvidence()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => !EntitledDeleters.Any(source.Is))
                .Where(static source => RetainedDeletionStatements.Any(source.Names))
                .Select(static source => source.RelativePath),
        ];

        Assert.Empty(offenders);

        List<string> credentialOffenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => !source.Is("HostProcessToolsMarkerStore.cs"))
                .Where(static source => !source.Is("ArcanumCredentialIdentity.cs"))
                .Where(static source => !source.Is("InstallationResetCredentialCatalog.cs"))
                .Where(static source => source.Names(ArcanumCredentialIdentity.HostProcessToolsTaintAccount)
                    || source.Names("HostProcessToolsTaintAccount"))
                .Select(static source => source.RelativePath),
        ];

        // The marker store owns compare-deletion. The closed reset catalog may name the account only
        // in its explicit retained-identity filter; the catalog tests prove it never returns that name
        // to DeleteAndVerify. No other production file may spell the marker account.
        Assert.Empty(credentialOffenders);

    }

    internal static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[CovenantLimits.DigestBytes];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = unchecked((byte)(seed + index));

        }

        return new CovenantDigest(bytes);

    }

    private static async Task<List<string>> RenderAsync(
        SqliteConnection connection,
        string table,
        string order,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY \"{order}\";";

        List<string> rendered = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {

            StringBuilder row = new(table);

            for (int index = 0; index < reader.FieldCount; index++)
            {

                _ = row.Append('|').Append(reader.GetName(index)).Append('=').Append(Render(reader.GetValue(index)));

            }

            rendered.Add(row.ToString());

        }

        return rendered;

    }

    /// <summary>
    /// Renders one column value so a changed byte cannot compare equal to the one it replaced.
    /// </summary>
    private static string Render(object value) =>
        value switch
        {
            null or DBNull => "<null>",
            byte[] bytes => Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

    private static async Task SeedAuthorityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO covenant_authority_state (
                StateKey, InstallationIdentity, AuthorityEpoch, CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint, RecoveryEnvelopeEpoch, HostToolsStateCode,
                TaintTimeMasterVersion, TaintFingerprint, TransitionId, UpdatedAtUtc)
            VALUES (1, $identity, 3, 4, $fingerprint, 2, 3, 4, $taintFingerprint, $transition, $updated);
            """;

        _ = command.Parameters.AddWithValue("$identity", InstallationIdentity);

        _ = command.Parameters.AddWithValue("$fingerprint", Digest(0x10).Bytes);

        _ = command.Parameters.AddWithValue("$taintFingerprint", Digest(0x40).Bytes);

        _ = command.Parameters.AddWithValue("$transition", TransitionId.ToString().ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$updated", "2026-08-16T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private static async Task SeedCampaignPathIdentityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO campaign_path_identities (
                CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
            VALUES ($campaign, 1, 9, '/tmp/one', 2, $identity, $updated);
            """;

        _ = command.Parameters.AddWithValue("$campaign", CovenantOperationGateFixture.CampaignOne);

        _ = command.Parameters.AddWithValue("$identity", Digest(0x20).Bytes);

        _ = command.Parameters.AddWithValue("$updated", "2026-08-16T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    /// <summary>
    /// Seeds one completed marker intent, which is the only phase a delete could ever have retained.
    /// </summary>
    /// <remarks>
    /// Completed rather than in-flight on purpose. The journal's delete guard already refuses a
    /// non-terminal row outright, so seeding one would let an erasure that deletes marker intents pass
    /// this assertion for the wrong reason — it would be stopped by the phase check rather than by the
    /// missing authorization.
    /// </remarks>
    private static async Task SeedMarkerIntentAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {

        // The journal's insert guard demands the marker intent mutation scope, exactly as its delete
        // guard does. Seeding through the same authorization production uses is what makes the row
        // realistic; a suite that installed the table without its guards would seed one nothing
        // protects.
        using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.CampaignPathMarkerIntentMutation);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO campaign_path_marker_intents (
                IntentId, OwnerOperationId, CampaignId, IntentKindCode, ExclusiveOwnerOperationCode,
                OwnerEffectDigest, EncryptedMarkerPayload, MarkerDigest, ApplyRequestDigest,
                TemporaryBaseName, TemporaryPhysicalIdentityDigest, TargetDisplayPath, PriorRevision,
                TargetObservationCode, ReopenedTargetPhysicalIdentityDigest, PendingDispositionCode,
                PhaseCode, PhaseRevision, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                $intent, $owner, $campaign, 3, 5,
                $ownerDigest, NULL, $markerDigest, NULL,
                NULL, NULL, '/tmp/one', 8,
                1, $reopened, NULL,
                12, 4, $created, $updated);
            """;

        _ = command.Parameters.AddWithValue("$intent", "11111111-2222-4333-8444-555555555555");

        _ = command.Parameters.AddWithValue("$owner", "66666666-7777-4888-8999-aaaaaaaaaaaa");

        _ = command.Parameters.AddWithValue("$campaign", CovenantOperationGateFixture.CampaignOne.ToString("D"));

        _ = command.Parameters.AddWithValue("$ownerDigest", Digest(0x50).Bytes);

        _ = command.Parameters.AddWithValue("$markerDigest", Digest(0x60).Bytes);

        _ = command.Parameters.AddWithValue("$reopened", Digest(0x70).Bytes);

        _ = command.Parameters.AddWithValue("$created", "2026-08-16T00:00:00.0000000+00:00");

        _ = command.Parameters.AddWithValue("$updated", "2026-08-16T00:00:01.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

}

/// <summary>
/// One rendering of the retained evidence, taken before an erasure and compared to after it.
/// </summary>
internal sealed record CovenantRetainedEvidenceSnapshot(
    IReadOnlyList<string> Rows,
    OsCredentialStoreStatus MarkerStatus,
    string? MarkerPayload);
