using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The staged match a restore purges by: each archived row is fingerprinted with the destination's key
/// exactly as the chokepoints and the erase routes fingerprint it, and compared with the destination's
/// fingerprints.
/// </summary>
/// <remarks>
/// <para>An archive is somebody else's database, taken at some other time, so its identities arrive in
/// whatever spelling its writers produced. A Campaign is matched by its GUID bytes, never by its text, so
/// every spelling of one Campaign names one fingerprint; a Lexicon name is matched by the identity the
/// current rule derives from the row's <c>Name</c>, never by a stored normalization an older rule wrote.
/// A stored value no fingerprint can describe, in a store the destination holds fingerprints for, fails
/// closed rather than matching loosely or being skipped.</para>
///
/// <para>Each case runs over bare tables holding only the columns the match reads, so what is under test
/// is the match and nothing the schema would otherwise do for it.</para>
/// </remarks>
public sealed class BackupRestoreErasureMatchTests
{
    private const string Content = "The ward-stone lies under the mill.";

    private const string RowId = "6F1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C";

    private static readonly Guid Campaign = Guid.Parse("8a3c2e1f-5b7d-4c9e-a1f2-3b4c5d6e7f80");

    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData("upper-dashed")]
    [InlineData("lower-dashed")]
    [InlineData("upper-undashed")]
    [InlineData("lower-undashed")]
    public async Task Staged_campaign_spellings_all_match_one_fingerprint(string spelling)
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            "INSERT INTO saga_memories (Id, ScopeKindCode, CampaignId, Content) VALUES ($id, 2, $campaign, $content);",
            ("$id", RowId),
            ("$campaign", Spell(Campaign, spelling)),
            ("$content", Content));

        Result<BackupRestoreErasureMatches> matches = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Campaign, Content)),
            Token);

        Assert.True(matches.IsSuccess, matches.IsFailure ? matches.Error.Message : null);

        Assert.Equal([RowId], matches.Value.SagaIds);

        Assert.Empty(matches.Value.LexiconIds);

        Assert.Empty(matches.Value.CovenantEntryIds);

        Assert.False(matches.Value.IsEmpty);
    }

    /// <summary>
    /// The control the spellings above are measured against: the same row in another Campaign, another
    /// scope, or with other content is a different identity and does not match.
    /// </summary>
    [Fact]
    public async Task Another_campaign_scope_or_content_is_another_identity()
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_memories (Id, ScopeKindCode, CampaignId, Content) VALUES
                ('A0000000-0000-4000-8000-000000000001', 2, $other, $content),
                ('A0000000-0000-4000-8000-000000000002', 1, NULL, $content),
                ('A0000000-0000-4000-8000-000000000003', 2, $campaign, $trailing);
            """,
            ("$other", Guid.NewGuid().ToString("D")),
            ("$campaign", Campaign.ToString("D")),
            ("$content", Content),
            ("$trailing", Content + " "));

        Result<BackupRestoreErasureMatches> matches = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Campaign, Campaign, Content)),
            Token);

        Assert.True(matches.IsSuccess, matches.IsFailure ? matches.Error.Message : null);

        Assert.True(matches.Value.IsEmpty);

        // The same content erased in Global scope does not reach the Campaign rows either.
        Result<BackupRestoreErasureMatches> global = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, Content)),
            Token);

        Assert.True(global.IsSuccess, global.IsFailure ? global.Error.Message : null);

        Assert.Equal(["A0000000-0000-4000-8000-000000000002"], global.Value.SagaIds);
    }

    [Fact]
    public async Task An_unparseable_campaign_in_a_fingerprinted_store_fails_closed()
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            "INSERT INTO saga_memories (Id, ScopeKindCode, CampaignId, Content) VALUES ($id, 2, 'not-a-guid', $content);",
            ("$id", RowId),
            ("$content", Content));

        Result<BackupRestoreErasureMatches> refused = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForSaga(SagaMemoryScopeKind.Global, null, "something else")),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(BackupRestoreErasureCodes.VerificationFailed, refused.Error.Code);

        Assert.DoesNotContain("not-a-guid", refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(Content, refused.Error.Message, StringComparison.Ordinal);

        // A store the destination holds no fingerprint for is not read at all.
        Result<BackupRestoreErasureMatches> skipped = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForLexicon(null, "Vault Keeper")),
            Token);

        Assert.True(skipped.IsSuccess, skipped.IsFailure ? skipped.Error.Message : null);

        Assert.True(skipped.Value.IsEmpty);
    }

    [Fact]
    public async Task Lexicon_matches_on_the_name_not_the_stored_normalization()
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            "INSERT INTO lexicon_entries (Id, Name, NameNormalized, ScopeCampaignId) VALUES ($id, '  vault keeper ', 'SOMETHING ELSE', '');",
            ("$id", RowId));

        Result<BackupRestoreErasureMatches> matches = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForLexicon(null, "Vault Keeper")),
            Token);

        Assert.True(matches.IsSuccess, matches.IsFailure ? matches.Error.Message : null);

        Assert.Equal([RowId], matches.Value.LexiconIds);

        Assert.Empty(matches.Value.SagaIds);
    }

    /// <summary>A Lexicon scope is read the same way: the empty string is Global, anything else is a GUID.</summary>
    [Theory]
    [InlineData("lower-dashed")]
    [InlineData("upper-undashed")]
    public async Task A_lexicon_campaign_scope_matches_in_any_spelling(string spelling)
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            """
            INSERT INTO lexicon_entries (Id, Name, NameNormalized, ScopeCampaignId) VALUES
                ($id, 'Vault Keeper', 'VAULT KEEPER', $campaign),
                ('A0000000-0000-4000-8000-000000000009', 'Vault Keeper', 'VAULT KEEPER', '');
            """,
            ("$id", RowId),
            ("$campaign", Spell(Campaign, spelling)));

        Result<BackupRestoreErasureMatches> matches = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForLexicon(Campaign, "Vault Keeper")),
            Token);

        Assert.True(matches.IsSuccess, matches.IsFailure ? matches.Error.Message : null);

        Assert.Equal([RowId], matches.Value.LexiconIds);
    }

    [Fact]
    public async Task Covenant_matches_on_scope_campaign_and_normalized_key()
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        Guid entry = Guid.Parse(RowId);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, NormalizedKey) VALUES
                ($entry, 2, $campaign, 'preference.vault'),
                ('A0000000-0000-4000-8000-000000000004', 1, NULL, 'preference.vault'),
                ('A0000000-0000-4000-8000-000000000005', 2, $campaign, 'preference.harbor');
            """,
            ("$entry", entry.ToString("N")),
            ("$campaign", Campaign.ToString("N").ToUpperInvariant()));

        Result<BackupRestoreErasureMatches> matches = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForCovenant(CovenantScope.Campaign, Campaign, "preference.vault")),
            Token);

        Assert.True(matches.IsSuccess, matches.IsFailure ? matches.Error.Message : null);

        Assert.Equal([entry], matches.Value.CovenantEntryIds);
    }

    /// <summary>
    /// An entry id is parsed only for a row that matches: a matching row whose id cannot be named cannot be
    /// purged, so it fails closed, while the same row under an identity nothing erased is left alone.
    /// </summary>
    [Fact]
    public async Task An_unparseable_entry_id_that_matches_fails_closed()
    {
        using MemoryErasureKey key = MemoryErasureTestKeys.CreateKey(new InMemoryOsCredentialStore());

        await using SqliteConnection connection = await OpenAsync();

        await ExecuteAsync(
            connection,
            "INSERT INTO covenant_entries (EntryId, ScopeCode, CampaignId, NormalizedKey) VALUES ('not-an-entry', 1, NULL, 'preference.vault');");

        Result<BackupRestoreErasureMatches> refused = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForCovenant(CovenantScope.Global, null, "preference.vault")),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(BackupRestoreErasureCodes.VerificationFailed, refused.Error.Code);

        Result<BackupRestoreErasureMatches> unmatched = await BackupRestoreErasureEvidenceApplier.FindMatchesAsync(
            connection,
            null,
            key,
            Destination(key, MemoryErasureIdentity.ForCovenant(CovenantScope.Global, null, "preference.harbor")),
            Token);

        Assert.True(unmatched.IsSuccess, unmatched.IsFailure ? unmatched.Error.Message : null);

        Assert.True(unmatched.Value.IsEmpty);
    }

    private static MemoryErasureEvidenceSnapshot Destination(MemoryErasureKey key, MemoryErasureIdentity identity) =>
        new([new MemoryErasureFingerprintRow(key.Fingerprint(identity), identity.Store, key.KeyId.ToArray())], [], []);

    private static string Spell(Guid campaign, string spelling) =>
        spelling switch
        {
            "upper-dashed" => campaign.ToString("D").ToUpperInvariant(),
            "lower-dashed" => campaign.ToString("D"),
            "upper-undashed" => campaign.ToString("N").ToUpperInvariant(),
            "lower-undashed" => campaign.ToString("N"),
            _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null),
        };

    /// <summary>An in-memory database holding only the columns the match reads, in each store's table.</summary>
    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteNativeRuntime.Instance.Initialize();

        SqliteConnection connection = new("Data Source=:memory:");

        await connection.OpenAsync(Token);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE saga_memories (Id TEXT PRIMARY KEY, ScopeKindCode INTEGER NOT NULL, CampaignId TEXT NULL, Content TEXT NOT NULL);
            CREATE TABLE lexicon_entries (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, NameNormalized TEXT NOT NULL, ScopeCampaignId TEXT NOT NULL);
            CREATE TABLE covenant_entries (EntryId TEXT PRIMARY KEY, ScopeCode INTEGER NOT NULL, CampaignId TEXT NULL, NormalizedKey TEXT NOT NULL);
            """);

        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync(Token);
    }
}
