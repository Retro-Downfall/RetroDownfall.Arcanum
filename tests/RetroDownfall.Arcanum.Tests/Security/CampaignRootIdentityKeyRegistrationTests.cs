using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The registration flow's half of the root-identity key: the evidence it counts on its own Grimoire
/// connection decides whether a missing key may be created or is a lost one.
/// </summary>
/// <remarks>
/// The evidence is the registry and marker store <see cref="Infrastructure.Tower.PhysicalCampaignRootOpener"/>
/// identities are matched against: a <c>campaign_path_identities</c> row (a registered root) or a
/// <c>campaign_path_marker_intents</c> row (a marker written for one). Either means a key existed, so its
/// absence now is loss, and minting a new one would orphan the registration (§10.12).
/// </remarks>
public sealed class CampaignRootIdentityKeyRegistrationTests : IAsyncLifetime
{
    private static CancellationToken Token => CancellationToken.None;

    private CovenantSchemaScratchDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await CovenantSchemaScratchDatabase.CreateAsync(Token);

        await _database.InstallCoreObjectsAsync(["Campaigns", .. CovenantRetainedEvidence.CoreObjects], Token);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task A_lost_key_is_not_replaced_while_roots_are_registered()
    {
        await SeedCampaignAsync();

        await CovenantRetainedEvidence.SeedCampaignPathIdentityAsync(_database.Connection, Token);

        RecordingCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Result ensured = await CampaignRootIdentityKeyRegistration.EnsureKeyForRegistrationAsync(
            _database.Connection,
            provider,
            Token);

        Assert.True(ensured.IsFailure);

        Assert.Equal(ErrorCodes.Campaign.RootIdentityKeyLost, ensured.Error.Code);

        // The refusal names its recovery, as the erasure keyring's lost-key error does.
        Assert.Contains("campaign-root-identity-key", ensured.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, credentials.SetCount);

        Assert.False(provider.TryCopyRootIdentityKey(new byte[32]));
    }

    [Fact]
    public async Task A_marker_intent_alone_is_evidence_of_a_registered_root()
    {
        await CovenantRetainedEvidence.SeedMarkerIntentAsync(_database.Connection, Token);

        RecordingCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Result ensured = await CampaignRootIdentityKeyRegistration.EnsureKeyForRegistrationAsync(
            _database.Connection,
            provider,
            Token);

        Assert.Equal(ErrorCodes.Campaign.RootIdentityKeyLost, ensured.Error.Code);

        Assert.Equal(0, credentials.SetCount);
    }

    [Fact]
    public async Task The_first_registration_creates_the_key_when_nothing_is_registered()
    {
        RecordingCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Result ensured = await CampaignRootIdentityKeyRegistration.EnsureKeyForRegistrationAsync(
            _database.Connection,
            provider,
            Token);

        Assert.True(ensured.IsSuccess);

        Assert.Equal(1, credentials.SetCount);

        Assert.True(provider.TryCopyRootIdentityKey(new byte[32]));
    }

    [Fact]
    public async Task An_existing_key_is_adopted_while_roots_are_registered()
    {
        await SeedCampaignAsync();

        await CovenantRetainedEvidence.SeedCampaignPathIdentityAsync(_database.Connection, Token);

        RecordingCredentialStore credentials = new()
        {
            StoredValue = Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray()),
        };

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Result ensured = await CampaignRootIdentityKeyRegistration.EnsureKeyForRegistrationAsync(
            _database.Connection,
            provider,
            Token);

        Assert.True(ensured.IsSuccess);

        Assert.Equal(0, credentials.SetCount);
    }

    [Fact]
    public async Task An_unreadable_store_is_unavailable_and_creates_nothing()
    {
        RecordingCredentialStore credentials = new()
        {
            ReadResult = OsCredentialStoreResult.Failed("test read failure"),
        };

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        Result ensured = await CampaignRootIdentityKeyRegistration.EnsureKeyForRegistrationAsync(
            _database.Connection,
            provider,
            Token);

        Assert.Equal(ErrorCodes.Campaign.RootIdentityKeyUnavailable, ensured.Error.Code);

        Assert.Equal(0, credentials.SetCount);
    }

    /// <summary>
    /// The key is read from (and may be written to) the OS credential store, which can sit behind a
    /// prompt for as long as the operator leaves it there, so it is never done inside a SQLite
    /// transaction, exactly as the erasure key is not.
    /// </summary>
    [Fact]
    public async Task Key_access_is_refused_inside_a_transaction()
    {
        RecordingCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider provider = new(credentials);

        await using SqliteTransaction transaction = (SqliteTransaction)await _database.Connection.BeginTransactionAsync(Token);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CampaignRootIdentityKeyRegistration.EnsureKeyForRegistrationAsync(
                _database.Connection,
                provider,
                Token));

        Assert.Equal(0, credentials.GetCount);
    }

    private async Task SeedCampaignAsync()
    {
        await using SqliteCommand command = _database.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Campaigns" ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'one', 'one', '/tmp/one', 1, '{}', '2026-08-16T00:00:00Z', '2026-08-16T00:00:00Z');
            """;

        _ = command.Parameters.AddWithValue("$id", CovenantOperationGateFixture.CampaignOne);

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private sealed class RecordingCredentialStore : IOsCredentialStore
    {
        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public string? StoredValue { get; set; }

        public OsCredentialStoreResult? ReadResult { get; init; }

        public bool IsAvailable => true;

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            GetCount++;

            if (ReadResult is { } forced)
            {
                return forced;
            }

            return StoredValue is null
                ? OsCredentialStoreResult.NotFound()
                : OsCredentialStoreResult.Ok(StoredValue);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            SetCount++;

            StoredValue = secret;

            return OsCredentialStoreResult.Ok(secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            StoredValue = null;

            return OsCredentialStoreResult.Ok(string.Empty);
        }
    }
}
