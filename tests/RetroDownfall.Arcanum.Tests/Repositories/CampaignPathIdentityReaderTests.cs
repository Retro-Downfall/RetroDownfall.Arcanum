using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The indexed identity lookup, against real SQLCipher and real temporary directories.
/// </summary>
public sealed class CampaignPathIdentityReaderTests : IDisposable
{

    private static readonly byte[] Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly Guid CampaignOne = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid CampaignTwo = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private readonly string _root = Directory.CreateTempSubdirectory("arcanum-path-reader-").FullName;

    private readonly PhysicalCampaignRootOpener _opener = new(new StubKeySource(Key));

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_registered_root_resolves_from_a_directory_inside_it()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        string nested = Directory.CreateDirectory(Path.Combine(_root, "src", "app")).FullName;

        await RegisterAsync(fixture, CampaignOne, _root, revision: 4, Token);

        CampaignPathIdentityReader reader = Reader(fixture);

        Result<RegisteredCampaignIdentity?> resolved =
            await reader.ResolveMostSpecificAsync(nested, Token);

        Assert.True(resolved.IsSuccess);
        Assert.Equal(CampaignOne, resolved.Value!.CampaignId);
        Assert.Equal(4, resolved.Value.Revision);

    }

    [Fact]
    public async Task The_most_specific_nested_registration_wins()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        string inner = Directory.CreateDirectory(Path.Combine(_root, "inner")).FullName;

        string deeper = Directory.CreateDirectory(Path.Combine(inner, "src")).FullName;

        await RegisterAsync(fixture, CampaignOne, _root, revision: 1, Token);

        await RegisterAsync(fixture, CampaignTwo, inner, revision: 1, Token);

        Result<RegisteredCampaignIdentity?> resolved =
            await Reader(fixture).ResolveMostSpecificAsync(deeper, Token);

        Assert.Equal(CampaignTwo, resolved.Value!.CampaignId);

    }

    [Fact]
    public async Task A_sibling_sharing_a_name_prefix_never_resolves()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        string app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;

        string legacy = Directory.CreateDirectory(Path.Combine(_root, "app-legacy")).FullName;

        await RegisterAsync(fixture, CampaignOne, app, revision: 1, Token);

        Result<RegisteredCampaignIdentity?> resolved =
            await Reader(fixture).ResolveMostSpecificAsync(legacy, Token);

        Assert.True(resolved.IsSuccess);
        Assert.Null(resolved.Value);

    }

    [Fact]
    public async Task A_registration_recorded_under_a_different_policy_version_is_skipped()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        await RegisterAsync(fixture, CampaignOne, _root, revision: 1, Token, policyVersion: 99);

        Result<RegisteredCampaignIdentity?> resolved =
            await Reader(fixture).ResolveMostSpecificAsync(_root, Token);

        Assert.True(resolved.IsSuccess);
        Assert.Null(resolved.Value);

    }

    [Fact]
    public async Task An_unregistered_or_absent_directory_resolves_to_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        CampaignPathIdentityReader reader = Reader(fixture);

        Assert.Null((await reader.ResolveMostSpecificAsync(_root, Token)).Value);
        Assert.Null((await reader.ResolveMostSpecificAsync(null, Token)).Value);
        Assert.Null((await reader.ResolveMostSpecificAsync(Path.Combine(_root, "missing"), Token)).Value);

    }

    [Fact]
    public async Task A_campaign_lookup_returns_its_own_registered_root()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        await RegisterAsync(fixture, CampaignOne, _root, revision: 7, Token);

        CampaignPathIdentityReader reader = Reader(fixture);

        Result<RegisteredCampaignIdentity?> found = await reader.FindByCampaignAsync(CampaignOne, Token);

        Assert.Equal(7, found.Value!.Revision);

        Assert.Null((await reader.FindByCampaignAsync(CampaignTwo, Token)).Value);
        Assert.Null((await reader.FindByCampaignAsync(Guid.Empty, Token)).Value);

    }

    [Fact]
    public async Task Availability_reports_a_generation_only_while_the_campaign_exists()
    {

        await using CovenantCanonicalFixture fixture = await CreateFixtureAsync();

        CampaignAvailabilityReader availability = new(new FixedCovenantConnectionSource(fixture.Connection));

        Result<long?> live = await availability.FindAvailabilityGenerationAsync(CampaignOne, Token);

        Assert.NotNull(live.Value);
        Assert.True(live.Value > 0);

        // The core registry epoch advances on Campaign insert and delete, which is what makes a
        // captured generation detect a Campaign that was created or removed underneath a turn.
        Result<long?> other = await availability.FindAvailabilityGenerationAsync(CampaignTwo, Token);

        Assert.Equal(live.Value, other.Value);

        await using (SqliteCommand delete = fixture.Connection.CreateCommand())
        {

            delete.CommandText = """DELETE FROM "Campaigns" WHERE "Id" = $id;""";

            _ = delete.Parameters.AddWithValue("$id", CampaignOne);

            _ = await delete.ExecuteNonQueryAsync(Token);

        }

        Result<long?> gone = await availability.FindAvailabilityGenerationAsync(CampaignOne, Token);

        Assert.True(gone.IsSuccess);
        Assert.Null(gone.Value);

        // The surviving Campaign is still available, but on a later generation: the deletion moved it.
        Result<long?> survivor = await availability.FindAvailabilityGenerationAsync(CampaignTwo, Token);

        Assert.NotNull(survivor.Value);
        Assert.True(survivor.Value > live.Value);

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a suite over.
        }

    }

    private async Task<CovenantCanonicalFixture> CreateFixtureAsync()
    {

        CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(
            Token,
            coreObjects: ["campaign_path_identities", "campaign_registry_state_campaign_delete"]);

        await fixture.AddCampaignAsync(CampaignOne, "one", Token);

        await fixture.AddCampaignAsync(CampaignTwo, "two", Token);

        return fixture;

    }

    private CampaignPathIdentityReader Reader(CovenantCanonicalFixture fixture) =>
        new(new FixedCovenantConnectionSource(fixture.Connection), _opener);

    private async Task RegisterAsync(
        CovenantCanonicalFixture fixture,
        Guid campaignId,
        string directory,
        long revision,
        CancellationToken cancellationToken,
        uint? policyVersion = null)
    {

        CovenantDigest identity = _opener.IdentifyExact(directory)
            ?? throw new InvalidOperationException("The test directory has no physical identity.");

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO campaign_path_identities
                (CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
            VALUES ($campaignId, $policyVersion, $revision, $displayPath, $depth, $digest, $updated);
            """;

        // CampaignId is REFERENCES "Campaigns"("Id"), so a row can only exist holding the exact text
        // the EF-owned parent holds. Binding the Guid produces that text; a formatted lowercase
        // literal is refused by the foreign key rather than silently registering an orphan.
        _ = command.Parameters.AddWithValue("$campaignId", campaignId);

        _ = command.Parameters.AddWithValue(
            "$policyVersion",
            (long)(policyVersion ?? CampaignPathIdentityPolicy.Version));

        _ = command.Parameters.AddWithValue("$revision", revision);

        _ = command.Parameters.AddWithValue("$displayPath", directory);

        _ = command.Parameters.AddWithValue("$depth", Math.Max(1, directory.Split(Path.DirectorySeparatorChar).Length));

        _ = command.Parameters.AddWithValue("$digest", identity.Bytes);

        _ = command.Parameters.AddWithValue("$updated", "2026-08-15T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

    }

    private sealed class StubKeySource(byte[]? key) : ICampaignRootIdentityKeyProvider
    {

        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {

            if (key is null || destination.Length < key.Length)
            {
                return false;
            }

            key.CopyTo(destination);

            return true;

        }

    }

}
