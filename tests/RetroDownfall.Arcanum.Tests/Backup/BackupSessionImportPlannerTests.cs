using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// How a selective import decides which Campaign an archived Session lands in (§10.19.12).
/// </summary>
/// <remarks>
/// The interesting case is the archived Campaign nobody mapped. Dropping the binding would import a
/// Session whose standing Campaign instructions silently stop applying to it, and guessing one would
/// attach it to a Campaign the operator never chose — so the only supported answer is a refusal that
/// says which archived Campaign is unaccounted for.
/// </remarks>
public sealed class BackupSessionImportPlannerTests : IDisposable
{

    private static readonly Guid BoundSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid UnboundSessionId =
        Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static readonly Guid ArchivedCampaignId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid DestinationCampaignId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-import-planner-" + Guid.NewGuid().ToString("N"));

    public BackupSessionImportPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task An_unmapped_Campaign_bound_Session_names_the_Campaign_rather_than_arriving_unbound()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            BoundSessionId,
            [],
            CancellationToken.None);

        Assert.True(planned.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, planned.Error.Code);

        // The archived Campaign, by identity. It is the only thing the operator can put on the left of
        // the mapping they now have to supply, and a refusal that withheld it would leave them guessing.
        Assert.Contains(
            ArchivedCampaignId.ToString("D"),
            planned.Error.Message,
            StringComparison.Ordinal);

        Assert.Contains("--map-campaign", planned.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task An_explicit_mapping_binds_the_import_to_the_destination_Campaign()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            BoundSessionId,
            [new BackupSessionCampaignMapping(ArchivedCampaignId, DestinationCampaignId)],
            CancellationToken.None);

        Assert.True(planned.IsSuccess);

        BackupSessionCampaignMapping mapping = Assert.IsType<BackupSessionCampaignMapping>(
            planned.Value.CampaignMapping);

        Assert.Equal(ArchivedCampaignId, mapping.SourceCampaignId);

        Assert.Equal(DestinationCampaignId, mapping.DestinationCampaignId);

        // The digest, not just the echoed argument. This is the value the transfer store scopes the
        // whole import by, and it is Campaign-scoped precisely because a mapping exists — a binding
        // computed as Global here would hand an exclusive owner a plan the request does not describe.
        Assert.Equal(
            ProtectedSessionTransferDigests.DestinationBinding(
                CovenantScope.Campaign,
                DestinationCampaignId,
                ArchivedCampaignId),
            planned.Value.DestinationBindingDigest);

    }

    [Fact]
    public async Task A_mapping_for_some_other_archived_Campaign_does_not_satisfy_this_one()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            BoundSessionId,
            [
                new BackupSessionCampaignMapping(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    DestinationCampaignId),
            ],
            CancellationToken.None);

        Assert.True(planned.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, planned.Error.Code);

        Assert.Contains(
            ArchivedCampaignId.ToString("D"),
            planned.Error.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_Session_that_was_never_Campaign_bound_needs_no_mapping_at_all()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result<ImportedSessionTransferRequest> planned = await BackupSessionImportPlanner.PlanAsync(
            lease,
            UnboundSessionId,
            [],
            CancellationToken.None);

        Assert.True(planned.IsSuccess);

        Assert.Null(planned.Value.CampaignMapping);

        // Global scope and no identities on either side, which is a different preimage from every
        // Campaign-scoped binding — an unbound import and a bound one can never produce one owner.
        Assert.Equal(
            ProtectedSessionTransferDigests.DestinationBinding(
                CovenantScope.Global,
                destinationCampaignId: null,
                sourceCampaignId: null),
            planned.Value.DestinationBindingDigest);

    }

    [Fact]
    public async Task The_coverage_pass_refuses_the_whole_selection_when_one_Session_is_unmapped()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        // Ordered so the mapped Session comes first: a check that only looked at the Session it was
        // about to commit would let this one through and refuse on the second, after the first landed.
        Result coverage = await BackupSessionImportPlanner.ValidateCampaignCoverageAsync(
            lease.Snapshot,
            [UnboundSessionId, BoundSessionId],
            [],
            CancellationToken.None);

        Assert.True(coverage.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, coverage.Error.Code);

        Assert.Contains(
            ArchivedCampaignId.ToString("D"),
            coverage.Error.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            BoundSessionId.ToString("D"),
            coverage.Error.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_coverage_pass_accepts_a_selection_whose_every_binding_is_mapped()
    {

        await using ImportedSessionSourceLease lease = await OpenSourceAsync();

        Result coverage = await BackupSessionImportPlanner.ValidateCampaignCoverageAsync(
            lease.Snapshot,
            [BoundSessionId, UnboundSessionId],
            [new BackupSessionCampaignMapping(ArchivedCampaignId, DestinationCampaignId)],
            CancellationToken.None);

        Assert.True(coverage.IsSuccess);

    }

    private async Task<ImportedSessionSourceLease> OpenSourceAsync()
    {

        string secret = await SeedSourceAsync();

        string attachments = Path.Combine(_root, "attachments");

        Directory.CreateDirectory(attachments);

        return ImportedSessionSourceLease.Adopt(
            await BackupRestoreDatabaseWorker.OpenAsync(
                Path.Combine(_root, "arcanum.db"),
                secret,
                readOnly: true,
                CancellationToken.None),
            attachments);

    }

    private async Task<string> SeedSourceAsync()
    {

        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string databasePath = Path.Combine(_root, "arcanum.db");

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(databasePath, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(secret, salt);

        CryptographicOperations.ZeroMemory(salt);

        SqliteNativeRuntime.Instance.Initialize();

        await using (SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Pooling = false,

            }.ToString(),
            CancellationToken.None))
        {

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

            const string emptyJson = "{}";

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO "Campaigns"
                    ("Id", "Name", "NameLower", "Path", "Type", "Description", "Settings",
                     "SanctumConfigJson", "CreatedAt", "UpdatedAt")
                VALUES ('{ArchivedCampaignId:D}', 'Alpha', 'alpha', '/archived/alpha', 0, NULL,
                        '{emptyJson}', '{emptyJson}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{BoundSessionId:D}', '{ArchivedCampaignId:D}', 'Bound session', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{UnboundSessionId:D}', NULL, 'Unbound session', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                """;

            _ = await seed.ExecuteNonQueryAsync();

        }

        return secret;

    }

}
