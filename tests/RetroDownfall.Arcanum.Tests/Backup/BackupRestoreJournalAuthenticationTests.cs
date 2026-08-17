using System.Collections.Immutable;

using System.Security.Cryptography;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The authenticated V2 restore journal, its OS-secret anti-rollback anchor, and the three
/// profile-namespaced credential accounts that bind them to one installation and one profile root.
/// </summary>
/// <remarks>
/// Every assertion here exists because the journal is the only durable evidence a restore leaves
/// behind, and a restart believes it before it renames a live installation. A journal that could be
/// replayed from another profile, rolled back to an earlier revision, or adopted by an operation that
/// did not create it would be authority to destroy a tree, so each of those has to be a typed refusal
/// rather than an absence — absence means "nothing to recover" and is the one answer that must never
/// be reachable by tampering (§10.19.6).
/// </remarks>
public sealed class BackupRestoreJournalAuthenticationTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-restore-journal-v2-" + Guid.NewGuid().ToString("N"));

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly string _guarded;

    private readonly ArcanumMaintenanceLock _lock;

    public BackupRestoreJournalAuthenticationTests()
    {

        Directory.CreateDirectory(_root);

        _guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its own maintenance lock.");

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    // ---------------------------------------------------------------- profile namespace

    [Fact]
    public void Restore_profile_namespace_binds_retained_parent_identity_and_root_leaf_without_path_authority()
    {

        CovenantDigest parent = Digest(3);

        CovenantDigest other = Digest(4);

        CovenantDigest baseline = Value(
            BackupRestoreJournalAuthenticator.ProfileNamespace(parent, "arcanum"));

        Assert.Equal(
            baseline,
            Value(BackupRestoreJournalAuthenticator.ProfileNamespace(parent, "arcanum")));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.ProfileNamespace(other, "arcanum")));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.ProfileNamespace(parent, "arcanum2")));

        // A leaf is one bounded child name. Anything that could redirect the namespace onto another
        // directory is refused before hashing rather than normalized into something plausible.
        Assert.True(
            BackupRestoreJournalAuthenticator.ProfileNamespace(parent, "..").IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator.ProfileNamespace(parent, "a/b").IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator.ProfileNamespace(parent, string.Empty).IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator.ProfileNamespace(default, "arcanum").IsFailure);

    }

    [Fact]
    public void Restore_profile_namespace_resolves_from_the_no_follow_parent_handle_of_the_configured_root()
    {

        BackupRestoreProfileNamespace resolved = Namespace();

        Assert.Equal(64, resolved.AccountSuffix.Length);

        Assert.Equal(resolved.AccountSuffix, resolved.AccountSuffix.ToLowerInvariant());

        // Recomputation from the same parent handle is what makes the digest a binding rather than a
        // stored value that could drift from the directory it names.
        Assert.Equal(resolved.Digest, Namespace().Digest);

        Assert.Equal(
            Value(BackupRestoreJournalAuthenticator.ProfileNamespace(
                resolved.ParentPhysicalIdentityDigest,
                resolved.ChildLeaf)),
            resolved.Digest);

        string sibling = Path.Combine(_root, "arcanum-other");

        Directory.CreateDirectory(sibling);

        Assert.NotEqual(
            resolved.Digest,
            Value(BackupRestoreJournalAuthenticator.ResolveProfileNamespace(sibling)).Digest);

    }

    [Fact]
    public void Restore_journal_location_digest_binds_retained_parent_identity_leaf_installation_and_operation()
    {

        CovenantDigest profile = Digest(9);

        Guid installation = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid operation = Guid.Parse("22222222-2222-2222-2222-222222222222");

        CovenantDigest parent = Digest(5);

        CovenantDigest baseline = Value(BackupRestoreJournalAuthenticator.JournalLocation(
            profile,
            installation,
            operation,
            parent,
            BackupRestoreJournalAnchorStore.JournalFileName));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.JournalLocation(
                Digest(10),
                installation,
                operation,
                parent,
                BackupRestoreJournalAnchorStore.JournalFileName)));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.JournalLocation(
                profile,
                Guid.NewGuid(),
                operation,
                parent,
                BackupRestoreJournalAnchorStore.JournalFileName)));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.JournalLocation(
                profile,
                installation,
                Guid.NewGuid(),
                parent,
                BackupRestoreJournalAnchorStore.JournalFileName)));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.JournalLocation(
                profile,
                installation,
                operation,
                Digest(6),
                BackupRestoreJournalAnchorStore.JournalFileName)));

        Assert.NotEqual(
            baseline,
            Value(BackupRestoreJournalAuthenticator.JournalLocation(
                profile,
                installation,
                operation,
                parent,
                "restore-journal.v2.json.other")));

    }

    // ---------------------------------------------------------------- key material

    [Fact]
    public void Restore_journal_key_requires_canonical_unpadded_base64url_exactly_32_bytes_and_zeroizes_after_use()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalKeyProvider keys = new(_credentials);

        using BackupRestoreJournalKeyLease created = Value(
            keys.CreateOrOpen(_lock, _guarded, profile));

        string account = ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profile.AccountSuffix);

        string stored = _credentials.TryGet(ArcanumCredentialIdentity.Service, account).Value!;

        Assert.Equal(BackupRestoreJournalKeyProvider.EncodedKeyCharacters, stored.Length);

        Assert.DoesNotContain('=', stored);

        Assert.DoesNotContain('+', stored);

        Assert.DoesNotContain('/', stored);

        // Single take, then the buffer is gone: a lease that could be read twice is a key a
        // coordinator could keep after the one bounded operation it was minted for.
        Assert.True(created.TryTakeKey(out byte[]? material));

        Assert.Equal(BackupRestoreJournalKeyProvider.KeyBytes, material!.Length);

        Assert.False(created.TryTakeKey(out _));

        Assert.True(created.IsSpent);

        // Nonserializable by construction: no public constructor to rehydrate one through, and no
        // entry in the AOT context that could give a key material a wire form at all.
        Assert.Empty(typeof(BackupRestoreJournalKeyLease).GetConstructors());

        Assert.Null(
            BackupJsonContext.Default.GetTypeInfo(typeof(BackupRestoreJournalKeyLease)));

        using BackupRestoreJournalKeyLease disposed = Value(keys.OpenExisting(profile));

        disposed.Dispose();

        Assert.False(disposed.TryTakeKey(out _));

        // Every other encoding of thirty-two bytes is refused rather than repaired.
        foreach (string malformed in (string[])
                 [
                     Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                     stored + "A",
                     stored[..42],
                     stored[..42] + "=",
                     "!" + stored[1..],
                 ])
        {

            _ = _credentials.Set(ArcanumCredentialIdentity.Service, account, malformed);

            Assert.True(
                keys.OpenExisting(profile).IsFailure,
                "A noncanonical stored key must not open: " + malformed);

        }

    }

    [Fact]
    public void Restore_journal_key_recovery_never_creates_or_substitutes_a_missing_key()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalKeyProvider keys = new(_credentials);

        Assert.True(keys.OpenExisting(profile).IsFailure);

        Assert.False(Value(keys.IsPresent(profile)));

        using BackupRestoreJournalKeyLease created = Value(
            keys.CreateOrOpen(_lock, _guarded, profile));

        Assert.True(Value(keys.IsPresent(profile)));

        // A second CreateOrOpen must open, never replace: replacing the key would strand every
        // envelope written under the previous one while looking like a healthy first run.
        string before = _credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profile.AccountSuffix)).Value!;

        using BackupRestoreJournalKeyLease reopened = Value(
            keys.CreateOrOpen(_lock, _guarded, profile));

        Assert.Equal(
            before,
            _credentials.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profile.AccountSuffix)).Value);

    }

    [Fact]
    public void Restore_journal_installation_identity_is_one_canonical_uppercase_uuid_and_seeds_only_when_absent()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalInstallationIdentityProvider identities = new(_credentials);

        Assert.Equal(
            BackupRestoreJournalIdentityPresence.Absent,
            Value(identities.Probe(profile)).Presence);

        // Deliberately carries hex letters: an all-digit identity would spell the same in both cases
        // and would not exercise the canonical-form rule at all.
        Guid database = Guid.Parse("3a3b3c3d-3e3f-4a4b-8c4d-5e6f7a8b9c0d");

        Assert.Equal(database, Value(identities.SeedFromDatabase(_lock, _guarded, profile, database)));

        string account =
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profile.AccountSuffix);

        Assert.Equal(
            database.ToString("D").ToUpperInvariant(),
            _credentials.TryGet(ArcanumCredentialIdentity.Service, account).Value);

        // Seeding is idempotent against the same row and refuses a different one outright.
        Assert.Equal(database, Value(identities.SeedFromDatabase(_lock, _guarded, profile, database)));

        Assert.True(identities.SeedFromDatabase(_lock, _guarded, profile, Guid.NewGuid()).IsFailure);

        Assert.True(identities.RequireMatchesDatabase(profile, database).IsSuccess);

        Assert.True(identities.RequireMatchesDatabase(profile, Guid.NewGuid()).IsFailure);

        foreach (string malformed in (string[])
                 [
                     database.ToString("D"),
                     database.ToString("N").ToUpperInvariant(),
                     database.ToString("B").ToUpperInvariant(),
                     "not-a-uuid",
                     string.Empty,
                 ])
        {

            _ = _credentials.Set(ArcanumCredentialIdentity.Service, account, malformed);

            Assert.True(
                identities.Probe(profile).IsFailure,
                "A noncanonical installation identity must not be believed: " + malformed);

        }

    }

    // ---------------------------------------------------------------- envelope authentication

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("profile-namespace")]
    [InlineData("installation")]
    [InlineData("operation")]
    [InlineData("revision")]
    [InlineData("previous-digest")]
    [InlineData("live-root")]
    [InlineData("staged-root")]
    [InlineData("displaced-root")]
    [InlineData("marker-checkpoint")]
    public void Restore_journal_tamper_blocks_before_topology_mutation(string field)
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Commit, MarkerCheckpoint([Guid.NewGuid()]));

        BackupRestoreJournalEnvelopeV2 tampered = field switch
        {
            "ciphertext" => fixture.Publication.Envelope with
            {
                CiphertextBase64Url = FlipFirstCharacter(fixture.Publication.Envelope.CiphertextBase64Url),
            },

            "tag" => fixture.Publication.Envelope with
            {
                AuthenticationTagBase64Url =
                    FlipFirstCharacter(fixture.Publication.Envelope.AuthenticationTagBase64Url),
            },

            "profile-namespace" => fixture.Publication.Envelope with { ProfileNamespaceDigest = Digest(200) },

            "installation" => fixture.Publication.Envelope with { InstallationId = Guid.NewGuid() },

            "operation" => fixture.Publication.Envelope with { OperationId = Guid.NewGuid() },

            "revision" => fixture.Publication.Envelope with { Revision = fixture.Publication.Envelope.Revision + 1 },

            "previous-digest" => fixture.Publication.Envelope with { PreviousEnvelopeDigest = Digest(201) },

            // The three topology roots and the marker checkpoint live inside the ciphertext, so the
            // only way to change one is to reseal the payload. That is exactly the attack the anchor
            // exists for: a resealed envelope at the same revision has a different envelope digest,
            // and the anchor still commits to the one the restore actually published.
            _ => Reseal(fixture, field),
        };

        WriteJournalFile(fixture.Publication.Location.StagingRoot, tampered);

        Result<BackupRestoreJournalRecoveryState> recovered = fixture.Store.Recover(
            _lock,
            _guarded,
            fixture.Profile,
            [fixture.Publication.Location.StagingRoot]);

        Assert.True(
            recovered.IsFailure,
            "A tampered " + field + " must be a typed blocker, not a recoverable journal.");

        Assert.NotEqual(ErrorCodes.Covenant.NotFound, recovered.Error.Code);

    }

    [Fact]
    public void Restore_journal_rejects_unknown_version_wrong_key_cross_installation_replay_and_rollback()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        // Unknown version.
        WriteJournalFile(
            fixture.Publication.Location.StagingRoot,
            fixture.Publication.Envelope with { Version = 3 });

        Assert.True(Recover(fixture).IsFailure);

        // Wrong key: the same profile's account holding different material can never open the
        // envelope this installation wrote.
        WriteJournalFile(fixture.Publication.Location.StagingRoot, fixture.Publication.Envelope);

        _ = _credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(fixture.Profile.AccountSuffix),
            EncodeKey(RandomNumberGenerator.GetBytes(32)));

        Assert.True(Recover(fixture).IsFailure);

        RestoreKey(fixture);

        // Cross-installation replay: the OS-secret installation identity moved on, so the envelope
        // belongs to an installation this profile no longer is.
        _ = _credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(fixture.Profile.AccountSuffix),
            Guid.NewGuid().ToString("D").ToUpperInvariant());

        Assert.True(Recover(fixture).IsFailure);

        _ = _credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(fixture.Profile.AccountSuffix),
            fixture.InstallationId.ToString("D").ToUpperInvariant());

        Assert.Equal(
            BackupRestoreJournalRecoveryOutcome.Authenticated,
            Value(Recover(fixture)).Outcome);

        // Rollback: an authentic earlier envelope replayed under an advanced anchor.
        BackupRestoreJournalEnvelopeV2 first = fixture.Publication.Envelope;

        BackupRestoreJournalPublication advanced = Value(fixture.Store.Advance(
            _lock,
            _guarded,
            fixture.Profile,
            fixture.Publication,
            fixture.Publication.Payload with { Phase = BackupRestorePhase.Validate }));

        WriteJournalFile(advanced.Location.StagingRoot, first);

        Assert.True(Recover(fixture with { Publication = advanced }).IsFailure);

    }

    [Fact]
    public void Restore_journal_rejects_cross_profile_key_anchor_envelope_and_staged_root_replay()
    {

        JournalFixture mine = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        string otherRootParent = Path.Combine(_root, "other-parent");

        Directory.CreateDirectory(otherRootParent);

        string otherGuarded = Path.Combine(otherRootParent, "arcanum");

        Directory.CreateDirectory(otherGuarded);

        BackupRestoreProfileNamespace theirs =
            Value(BackupRestoreJournalAuthenticator.ResolveProfileNamespace(otherGuarded));

        Assert.NotEqual(mine.Profile.Digest, theirs.Digest);

        // Another profile's accounts are never consulted for this one: a suffix mismatch is a
        // different account name, so their key and anchor simply do not exist here.
        Assert.True(new BackupRestoreJournalKeyProvider(_credentials).OpenExisting(theirs).IsFailure);

        Assert.Null(Value(mine.Store.TryReadAnchor(theirs)));

        // Their envelope presented in our staging root fails on the profile namespace before the key
        // is ever consulted.
        BackupRestoreJournalEnvelopeV2 foreignEnvelope =
            mine.Publication.Envelope with { ProfileNamespaceDigest = theirs.Digest };

        WriteJournalFile(mine.Publication.Location.StagingRoot, foreignEnvelope);

        Assert.True(Recover(mine).IsFailure);

        // A staged root copied from another profile has a different physical identity, so its journal
        // location digest cannot match the one this anchor committed to.
        WriteJournalFile(mine.Publication.Location.StagingRoot, mine.Publication.Envelope);

        string decoy = Path.Combine(
            Path.GetDirectoryName(mine.Publication.Location.StagingRoot)!,
            BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(decoy);

        WriteJournalFile(decoy, mine.Publication.Envelope);

        Assert.True(
            mine.Store.Recover(_lock, _guarded, mine.Profile, [decoy]).IsFailure,
            "A canonical lookalike that the anchor does not name is a blocker, never a recovery.");

    }

    [Fact]
    public void Restore_journal_advance_accepts_one_revision_ahead_and_closes_that_window()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        Assert.Equal(1UL, fixture.Publication.Envelope.Revision);

        Assert.Equal(1UL, fixture.Publication.Anchor.Revision);

        BackupRestoreJournalPublication second = Value(fixture.Store.Advance(
            _lock,
            _guarded,
            fixture.Profile,
            fixture.Publication,
            fixture.Publication.Payload with { Phase = BackupRestorePhase.Validate }));

        Assert.Equal(2UL, second.Envelope.Revision);

        Assert.Equal(2UL, second.Anchor.Revision);

        Assert.Equal(fixture.Publication.EnvelopeDigest, second.Envelope.PreviousEnvelopeDigest);

        // The one permitted crash window: the envelope reached disk and the anchor did not. Recovery
        // adopts it and closes the window through the same checked write before any effect.
        BackupRestoreJournalPublication third = Value(fixture.Store.Advance(
            _lock,
            _guarded,
            fixture.Profile,
            second,
            second.Payload with { Phase = BackupRestorePhase.SafetyPoint, MarkerCleanup = MarkerCheckpoint([]) }));

        WriteAnchor(fixture.Profile, second.Anchor);

        BackupRestoreJournalRecoveryState recovered = Value(Recover(fixture with { Publication = third }));

        Assert.Equal(BackupRestoreJournalRecoveryOutcome.Authenticated, recovered.Outcome);

        Assert.Equal(3UL, recovered.Publication!.Anchor.Revision);

        Assert.Equal(third.EnvelopeDigest, recovered.Publication.Anchor.EnvelopeDigest);

        Assert.Equal(
            3UL,
            Value(fixture.Store.TryReadAnchor(fixture.Profile))!.Revision);

        // Two revisions ahead is a skipped write, not a crash window.
        BackupRestoreJournalPublication fourth = Value(fixture.Store.Advance(
            _lock,
            _guarded,
            fixture.Profile,
            recovered.Publication,
            recovered.Publication.Payload with { Phase = BackupRestorePhase.Commit }));

        WriteAnchor(fixture.Profile, second.Anchor);

        Assert.True(Recover(fixture with { Publication = fourth }).IsFailure);

    }

    [Fact]
    public void Restore_journal_terminal_close_leaves_the_closed_anchor_tombstone_and_deletes_the_journal()
    {

        JournalFixture fixture = Publish(
            BackupRestorePhase.Cleanup,
            MarkerCheckpoint([]));

        Assert.True(
            fixture.Store.Close(_lock, _guarded, fixture.Profile, fixture.Publication).IsSuccess);

        Assert.False(File.Exists(Path.Combine(
            fixture.Publication.Location.StagingRoot,
            BackupRestoreJournalAnchorStore.JournalFileName)));

        BackupRestoreJournalAnchorV1 tombstone = Value(fixture.Store.TryReadAnchor(fixture.Profile))!;

        Assert.Equal(BackupRestoreJournalAnchorState.Closed, tombstone.State);

        Assert.Equal(fixture.Publication.Anchor.OperationId, tombstone.OperationId);

        // The closed anchor is the anti-replay tombstone: the same envelope reappearing on disk is a
        // blocker, and the state alone never becomes "nothing happened".
        WriteJournalFile(fixture.Publication.Location.StagingRoot, fixture.Publication.Envelope);

        Assert.True(Recover(fixture).IsFailure);

    }

    // ---------------------------------------------------------------- absence

    [Fact]
    public void Startup_restore_recovery_without_key_anchor_journal_or_staging_evidence_returns_no_active_journal()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalAnchorStore store = Store();

        BackupRestoreJournalRecoveryState state = Value(
            store.Recover(_lock, _guarded, profile, []));

        Assert.Equal(BackupRestoreJournalRecoveryOutcome.NoActiveJournal, state.Outcome);

        Assert.Null(state.Publication);

    }

    [Fact]
    public void Startup_restore_recovery_with_external_identity_but_no_active_evidence_returns_no_active_journal()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalAnchorStore store = Store();

        _ = Value(new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(_lock, _guarded, profile, Guid.NewGuid()));

        using BackupRestoreJournalKeyLease unused = Value(
            new BackupRestoreJournalKeyProvider(_credentials).CreateOrOpen(_lock, _guarded, profile));

        Assert.Equal(
            BackupRestoreJournalRecoveryOutcome.NoActiveJournal,
            Value(store.Recover(_lock, _guarded, profile, [])).Outcome);

    }

    [Fact]
    public void Restore_journal_missing_key_or_identity_beside_active_evidence_is_a_typed_manual_blocker()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        _ = _credentials.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(fixture.Profile.AccountSuffix));

        Result<BackupRestoreJournalRecoveryState> withoutKey = Recover(fixture);

        Assert.True(withoutKey.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, withoutKey.Error.Code);

        RestoreKey(fixture);

        _ = _credentials.Delete(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(fixture.Profile.AccountSuffix));

        Result<BackupRestoreJournalRecoveryState> withoutIdentity = Recover(fixture);

        Assert.True(withoutIdentity.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, withoutIdentity.Error.Code);

    }

    [Fact]
    public void Restore_journal_truncated_encoding_and_canonical_lookalike_are_typed_blockers()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        string journal = Path.Combine(
            fixture.Publication.Location.StagingRoot,
            BackupRestoreJournalAnchorStore.JournalFileName);

        File.WriteAllBytes(journal, File.ReadAllBytes(journal)[..40]);

        Assert.True(Recover(fixture).IsFailure);

        File.WriteAllText(journal, "{\"version\":2}");

        Assert.True(Recover(fixture).IsFailure);

        File.Delete(journal);

        // An active anchor with no journal at all is still a blocker: absence of the file it names is
        // not proof that the restore never ran.
        Result<BackupRestoreJournalRecoveryState> missing = Recover(fixture);

        Assert.True(missing.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, missing.Error.Code);

    }

    [Fact]
    public void Restore_journal_malformed_digest_and_noncanonical_encoding_are_typed_blockers()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        string envelope = System.Text.Encoding.UTF8.GetString(
            Value(BackupRestoreJournalAuthenticator.EncodeEnvelope(fixture.Publication.Envelope)));

        string anchor = Value(
            BackupRestoreJournalAuthenticator.EncodeAnchor(fixture.Publication.Anchor));

        // A short or absent digest makes the Core digest constructor throw, and that has to arrive as
        // a refusal rather than as an unhandled exception in the pre-database bootstrap.
        foreach (string malformed in (string[])
                 [
                     Replace(envelope, "\"previousEnvelopeDigest\":{", "\"previousEnvelopeDigest\":{\"bytes\":\"AAAA\"},\"ignored\":{"),
                     envelope.Replace("\"bytes\":", "\"bytes\":null,\"was\":", StringComparison.Ordinal),
                 ])
        {

            Result<BackupRestoreJournalEnvelopeV2> decoded =
                BackupRestoreJournalAuthenticator.DecodeEnvelope(
                    System.Text.Encoding.UTF8.GetBytes(malformed));

            Assert.True(decoded.IsFailure, "A malformed digest must refuse, not throw.");

        }

        Assert.True(
            BackupRestoreJournalAuthenticator
                .DecodeAnchor(anchor.Replace("\"bytes\":", "\"bytes\":\"AAAA\",\"was\":", StringComparison.Ordinal))
                .IsFailure);

        // One canonical encoding only. Whitespace, reordering, and an unknown member nested inside a
        // digest all round-trip to something other than what was read, so all three are refused —
        // record-level unknown-member handling never reaches inside a nested Core type.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .DecodeEnvelope(System.Text.Encoding.UTF8.GetBytes("  " + envelope))
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .DecodeAnchor(anchor.Replace("{\"version\":1,", "{ \"version\":1,", StringComparison.Ordinal))
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator.DecodeEnvelope(
                System.Text.Encoding.UTF8.GetBytes(envelope)).IsSuccess);

        Assert.True(BackupRestoreJournalAuthenticator.DecodeAnchor(anchor).IsSuccess);

    }

    [Fact]
    public void Restore_journal_anchor_revision_beyond_its_bound_is_a_typed_blocker()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        // A stored revision at the numeric ceiling would make the checked n+1 of every advance and
        // recovery path overflow. Bounding it keeps that a refusal rather than a crash.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .EncodeAnchor(fixture.Publication.Anchor with { Revision = ulong.MaxValue })
                .IsFailure);

        WriteAnchor(
            fixture.Profile,
            fixture.Publication.Anchor with
            {
                Revision = BackupRestoreJournalAuthenticator.MaxRevision + 1,
            },
            validate: false);

        Assert.True(fixture.Store.TryReadAnchor(fixture.Profile).IsFailure);

        Assert.True(Recover(fixture).IsFailure);

    }

    [Fact]
    public void Restore_journal_recovery_treats_one_root_named_twice_as_one_root()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        string root = fixture.Publication.Location.StagingRoot;

        // The caller's candidate set is the canonical sweep unioned with the staging index, and for a
        // replace-installation restore those two name the same directory. Counting list entries
        // instead of directories would refuse the ordinary case as two rival staging roots.
        Assert.Equal(
            BackupRestoreJournalRecoveryOutcome.Authenticated,
            Value(fixture.Store.Recover(
                _lock,
                _guarded,
                fixture.Profile,
                [root, root + Path.DirectorySeparatorChar, Path.Combine(root, ".")])).Outcome);

    }

    [Fact]
    public void Restore_journal_open_that_never_published_retires_its_anchor_instead_of_wedging()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalAnchorStore store = Store();

        Guid installation = Guid.Parse("77777777-7777-7777-7777-777777777777");

        _ = Value(new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(_lock, _guarded, profile, installation));

        string stagingRoot = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(stagingRoot);

        BackupRestoreJournalPayloadV2 payload = Payload(BackupRestorePhase.Stage, null);

        BackupRestoreJournalLocation location = Value(store.ResolveLocation(
            profile,
            installation,
            payload.OwnerOperationId,
            stagingRoot));

        // No journal key exists yet, so the first revision can never be sealed. The opening anchor
        // must not survive as a permanent blocker for a failure that reached no evidence at all.
        Assert.True(store.Begin(_lock, _guarded, profile, location, payload).IsFailure);

        Assert.Equal(
            BackupRestoreJournalAnchorState.Closed,
            Value(store.TryReadAnchor(profile))!.State);

        Assert.Equal(
            BackupRestoreJournalRecoveryOutcome.NoActiveJournal,
            Value(store.Recover(_lock, _guarded, profile, [stagingRoot])).Outcome);

        // And the profile is still usable: a new operation opens over the retired tombstone.
        Value(new BackupRestoreJournalKeyProvider(_credentials)
            .CreateOrOpen(_lock, _guarded, profile))
            .Dispose();

        BackupRestoreJournalPayloadV2 retry = payload with { OwnerOperationId = Guid.NewGuid() };

        Assert.True(store.Begin(
            _lock,
            _guarded,
            profile,
            Value(store.ResolveLocation(profile, installation, retry.OwnerOperationId, stagingRoot)),
            retry).IsSuccess);

    }

    [Fact]
    public void Restore_journal_close_finishes_after_its_own_partial_failure()
    {

        JournalFixture fixture = Publish(BackupRestorePhase.Cleanup, MarkerCheckpoint([]));

        Assert.True(
            fixture.Store.Close(_lock, _guarded, fixture.Profile, fixture.Publication).IsSuccess);

        // A delete that failed leaves the tombstone written and the journal present, so the retry has
        // to be able to finish rather than report a concurrency conflict against its own earlier half.
        Assert.True(
            fixture.Store.Close(_lock, _guarded, fixture.Profile, fixture.Publication).IsSuccess);

        Assert.Equal(
            BackupRestoreJournalAnchorState.Closed,
            Value(fixture.Store.TryReadAnchor(fixture.Profile))!.State);

    }

    [SkippableFact]
    public void Restore_journal_unreadable_staging_root_is_never_reported_as_proven_absence()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Directory read permission is the Unix mode bit this asserts against.");

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalAnchorStore store = Store();

        string decoy = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(decoy);

        File.WriteAllText(Path.Combine(decoy, BackupRestoreJournalAnchorStore.JournalFileName), "{}");

        File.SetUnixFileMode(decoy, UnixFileMode.None);

        try
        {

            Skip.If(
                CanStillRead(decoy),
                "The test process can read a mode-000 directory, so this cannot be exercised here.");

            // An unanchored journal hidden behind an unreadable directory must not read as absence:
            // absence is what lets startup continue over a half-restored installation.
            Assert.True(store.Recover(_lock, _guarded, profile, [decoy]).IsFailure);

        }
        finally
        {

            File.SetUnixFileMode(
                decoy,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

    }

    private static bool CanStillRead(string directory)
    {

        try
        {

            _ = Directory.EnumerateFileSystemEntries(directory).Any();

            return true;

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return false;

        }

    }

    // ---------------------------------------------------------------- payload shape

    [Theory]
    [InlineData(BackupRestorePhase.Authenticate)]
    [InlineData(BackupRestorePhase.Inventory)]
    [InlineData(BackupRestorePhase.Capacity)]
    [InlineData(BackupRestorePhase.Stage)]
    [InlineData(BackupRestorePhase.Migrate)]
    [InlineData(BackupRestorePhase.RemapPaths)]
    [InlineData(BackupRestorePhase.RewrapSecrets)]
    [InlineData(BackupRestorePhase.Validate)]
    public void Restore_initial_journal_allows_only_the_pre_preparation_null_marker_shape(
        BackupRestorePhase phase)
    {

        Assert.True(
            BackupRestoreJournalAuthenticator.ValidatePayload(Payload(phase, markerCleanup: null)).IsSuccess);

        // Nonnull before preparation would claim children that do not exist yet.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(Payload(phase, MarkerCheckpoint([])))
                .IsFailure);

    }

    [Fact]
    public void Restore_requires_nonnull_zero_or_nonzero_marker_checkpoint_before_safety_point_or_displacement()
    {

        foreach (BackupRestorePhase phase in (BackupRestorePhase[])
                 [
                     BackupRestorePhase.SafetyPoint,
                     BackupRestorePhase.Commit,
                     BackupRestorePhase.Reconcile,
                     BackupRestorePhase.Cleanup,
                 ])
        {

            Assert.True(
                BackupRestoreJournalAuthenticator.ValidatePayload(Payload(phase, null)).IsFailure,
                "An unprepared marker checkpoint cannot reach " + phase + ".");

            Assert.True(
                BackupRestoreJournalAuthenticator
                    .ValidatePayload(Payload(phase, MarkerCheckpoint([])))
                    .IsSuccess,
                "A proven zero inventory is a complete checkpoint at " + phase + ".");

            Assert.True(
                BackupRestoreJournalAuthenticator
                    .ValidatePayload(Payload(phase, MarkerCheckpoint([Guid.NewGuid(), Guid.NewGuid()])))
                    .IsSuccess);

        }

        // The zero arm has exactly one legal digest, and it is the frozen literal nothing else can
        // present. A count or digest that disagrees with the vector fails closed.
        Assert.Equal(
            CampaignPathRestoreCleanupIntentVector.EmptyDigest,
            MarkerCheckpoint([]).IntentVectorDigest);

        BackupRestoreMarkerCleanupCheckpointV1 zero = MarkerCheckpoint([]);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(Payload(
                    BackupRestorePhase.Commit,
                    zero with { IntentVectorDigest = Digest(77) }))
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(Payload(BackupRestorePhase.Commit, zero with { IntentCount = 1 }))
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(Payload(
                    BackupRestorePhase.Commit,
                    zero with { OrderedIntentIds = default }))
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(Payload(
                    BackupRestorePhase.Commit,
                    zero with { OwnerOperation = CovenantExclusiveOperation.CovenantReset }))
                .IsFailure);

    }

    [Fact]
    public void Restore_journal_node_identity_requires_matching_kind_presence_and_digest_shape()
    {

        BackupRestoreJournalPayloadV2 payload = Payload(BackupRestorePhase.Stage, null);

        Assert.True(BackupRestoreJournalAuthenticator.ValidatePayload(payload).IsSuccess);

        // Directory slots are directories, archive slots are regular files.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    LiveRoot = payload.LiveRoot with { NodeKind = BackupRestoreNodeKind.RegularFile },
                })
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    ArchiveSource = payload.ArchiveSource with
                    {
                        NodeKind = BackupRestoreNodeKind.Directory,
                    },
                })
                .IsFailure);

        // A present node carries its exact same-handle identity; an absent one carries none.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    LiveRoot = payload.LiveRoot with { NodePhysicalIdentityDigest = null },
                })
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    DisplacedRoot = payload.DisplacedRoot with
                    {
                        NodePhysicalIdentityDigest = Digest(31),
                    },
                })
                .IsFailure);

        // Only a present regular file may carry a content digest.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    LiveRoot = payload.LiveRoot with { ContentDigest = Digest(32) },
                })
                .IsFailure);

        // Parent paths are bounded absolute paths; leaves are one bounded child name.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    StagedRoot = payload.StagedRoot with { CanonicalParentPath = "relative/parent" },
                })
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    StagedRoot = payload.StagedRoot with { ChildLeaf = ".." },
                })
                .IsFailure);

        // The phase-frozen topology shape: nothing is displaced before the displacement window.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    DisplacedRoot = Present(payload.DisplacedRoot),
                })
                .IsFailure);

        // And the owner has to be this operation.
        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    OwnerOperation = CovenantExclusiveOperation.SchemaRepair,
                })
                .IsFailure);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .ValidatePayload(payload with
                {
                    ConflictMode = BackupRestoreConflictMode.ImportSelectedSessions,
                })
                .IsFailure);

    }

    [Fact]
    public void Restore_journal_payload_envelope_anchor_and_nested_types_have_complete_AOT_context_coverage()
    {

        Assert.NotNull(BackupJsonContext.Default.BackupRestoreJournalPayloadV2);

        Assert.NotNull(BackupJsonContext.Default.BackupRestoreJournalEnvelopeV2);

        Assert.NotNull(BackupJsonContext.Default.BackupRestoreJournalAnchorV1);

        Assert.NotNull(BackupJsonContext.Default.BackupRestoreDurableNodeIdentityV1);

        Assert.NotNull(BackupJsonContext.Default.BackupRestoreMarkerCleanupCheckpointV1);

        BackupRestoreJournalPayloadV2 payload =
            Payload(BackupRestorePhase.Commit, MarkerCheckpoint([Guid.NewGuid()]));

        string json = JsonSerializer.Serialize(
            payload,
            BackupJsonContext.Default.BackupRestoreJournalPayloadV2);

        Assert.Equal(
            payload,
            JsonSerializer.Deserialize(json, BackupJsonContext.Default.BackupRestoreJournalPayloadV2));

        // Strict about unknown fields: a decoder that ignored them would let a newer producer add a
        // meaning this build silently drops while still holding a valid tag.
        _ = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            json[..^1] + ",\"unexpected\":1}",
            BackupJsonContext.Default.BackupRestoreJournalPayloadV2));

        // The same strictness on the two shapes a hostile file or credential slot can present
        // directly, where it is the decoder rather than the tag that has to refuse first.
        JournalFixture fixture = Publish(BackupRestorePhase.Stage, markerCleanup: null);

        byte[] envelope = Value(
            BackupRestoreJournalAuthenticator.EncodeEnvelope(fixture.Publication.Envelope));

        Assert.True(BackupRestoreJournalAuthenticator.DecodeEnvelope(envelope).IsSuccess);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .DecodeEnvelope(System.Text.Encoding.UTF8.GetBytes(
                    System.Text.Encoding.UTF8.GetString(envelope)[..^1] + ",\"unexpected\":1}"))
                .IsFailure);

        string anchor = Value(
            BackupRestoreJournalAuthenticator.EncodeAnchor(fixture.Publication.Anchor));

        Assert.True(BackupRestoreJournalAuthenticator.DecodeAnchor(anchor).IsSuccess);

        Assert.True(
            BackupRestoreJournalAuthenticator
                .DecodeAnchor(anchor[..^1] + ",\"unexpected\":1}")
                .IsFailure);

    }

    // ---------------------------------------------------------------- credential retention

    [Fact]
    public void Restore_journal_accounts_are_profile_namespaced_and_never_removed_by_ordinary_cleanup()
    {

        BackupRestoreProfileNamespace profile = Namespace();

        string[] journalAccounts =
        [
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profile.AccountSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profile.AccountSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(profile.AccountSuffix),
        ];

        Assert.All(
            journalAccounts,
            account => Assert.EndsWith(profile.AccountSuffix, account, StringComparison.Ordinal));

        Assert.All(journalAccounts, account => Assert.True(
            ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(account)));

        // A bare prefix, an unnamespaced alias, or another profile's suffix is never one of ours.
        Assert.False(ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccountPrefix));

        Assert.False(ArcanumCredentialIdentity.IsBackupRestoreJournalAccount("backup-restore-journal-key"));

        Assert.False(ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccountPrefix + new string('Z', 64)));

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount("too-short"));

        _ = Assert.Throws<ArgumentException>(
            () => ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(
                profile.AccountSuffix.ToUpperInvariant()));

        // Ordinary credential cleanup, Covenant reset, family reinitialize, and restore all iterate
        // the closed catalog. Retention is that the three accounts are never in it.
        foreach (string account in journalAccounts)
        {

            _ = _credentials.Set(ArcanumCredentialIdentity.Service, account, "retained");

        }

        string[] catalog = InstallationResetCredentialCatalog.CollectAccounts(
            new Core.Configuration.ArcanumSettings(),
            Path.Combine(_root, "secrets"));

        Assert.All(journalAccounts, account => Assert.DoesNotContain(account, catalog));

        InstallationResetCredentialCatalog cleanup = new(_credentials);

        _ = cleanup.DeleteAndVerify(catalog);

        Assert.All(journalAccounts, account => Assert.Equal(
            "retained",
            _credentials.TryGet(ArcanumCredentialIdentity.Service, account).Value));

    }

    // ---------------------------------------------------------------- helpers

    private sealed record JournalFixture(
        BackupRestoreProfileNamespace Profile,
        BackupRestoreJournalAnchorStore Store,
        Guid InstallationId,
        byte[] Key,
        BackupRestoreJournalPublication Publication);

    private BackupRestoreProfileNamespace Namespace() =>
        Value(BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guarded));

    private BackupRestoreJournalAnchorStore Store() =>
        new(
            _credentials,
            new BackupRestoreJournalKeyProvider(_credentials),
            new BackupRestoreJournalInstallationIdentityProvider(_credentials));

    private JournalFixture Publish(
        BackupRestorePhase phase,
        BackupRestoreMarkerCleanupCheckpointV1? markerCleanup)
    {

        BackupRestoreProfileNamespace profile = Namespace();

        BackupRestoreJournalAnchorStore store = Store();

        Guid installation = Guid.Parse("44444444-4444-4444-4444-444444444444");

        _ = Value(new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(_lock, _guarded, profile, installation));

        Value(new BackupRestoreJournalKeyProvider(_credentials)
            .CreateOrOpen(_lock, _guarded, profile))
            .Dispose();

        byte[] key = DecodeKey(_credentials.TryGet(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(profile.AccountSuffix)).Value!);

        string stagingRoot = Path.Combine(_root, BackupRestoreJournal.CreateStagingName());

        Directory.CreateDirectory(stagingRoot);

        BackupRestoreJournalPayloadV2 payload = Payload(phase, markerCleanup);

        BackupRestoreJournalLocation location = Value(store.ResolveLocation(
            profile,
            installation,
            payload.OwnerOperationId,
            stagingRoot));

        BackupRestoreJournalPublication publication = Value(
            store.Begin(_lock, _guarded, profile, location, payload));

        return new JournalFixture(profile, store, installation, key, publication);

    }

    private Result<BackupRestoreJournalRecoveryState> Recover(JournalFixture fixture) =>
        fixture.Store.Recover(
            _lock,
            _guarded,
            fixture.Profile,
            [fixture.Publication.Location.StagingRoot]);

    private void RestoreKey(JournalFixture fixture) =>
        _ = _credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalKeyAccount(fixture.Profile.AccountSuffix),
            EncodeKey(fixture.Key));

    private void WriteAnchor(
        BackupRestoreProfileNamespace profile,
        BackupRestoreJournalAnchorV1 anchor,
        bool validate = true)
    {

        // A bounded encoder cannot express an out-of-range anchor, so a test that has to plant one
        // serializes it directly. That is the only way to reach the decode path from a slot a
        // partial write or a tamper could produce.
        string encoded = validate
            ? Value(BackupRestoreJournalAuthenticator.EncodeAnchor(anchor))
            : JsonSerializer.Serialize(anchor, BackupJsonContext.Default.BackupRestoreJournalAnchorV1);

        _ = _credentials.Set(
            ArcanumCredentialIdentity.Service,
            ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(profile.AccountSuffix),
            encoded);

    }

    private static string Replace(string value, string find, string replacement) =>
        value.Replace(find, replacement, StringComparison.Ordinal);

    private static void WriteJournalFile(string stagingRoot, BackupRestoreJournalEnvelopeV2 envelope) =>
        File.WriteAllBytes(
            Path.Combine(stagingRoot, BackupRestoreJournalAnchorStore.JournalFileName),
            Value(BackupRestoreJournalAuthenticator.EncodeEnvelope(envelope)));

    private static BackupRestoreJournalEnvelopeV2 Reseal(JournalFixture fixture, string field)
    {

        BackupRestoreJournalPayloadV2 payload = fixture.Publication.Payload;

        payload = field switch
        {
            "live-root" => payload with
            {
                LiveRoot = payload.LiveRoot with { NodePhysicalIdentityDigest = Digest(41) },
            },

            "staged-root" => payload with
            {
                StagedRoot = payload.StagedRoot with { NodePhysicalIdentityDigest = Digest(42) },
            },

            "displaced-root" => payload with
            {
                DisplacedRoot = payload.DisplacedRoot with { CanonicalParentPath = payload.LiveRoot.CanonicalParentPath },
            },

            _ => payload with
            {
                MarkerCleanup = MarkerCheckpoint([Guid.Parse("55555555-5555-5555-5555-555555555555")]),
            },
        };

        using BackupRestoreJournalKeyLease lease =
            BackupRestoreJournalKeyLease.Mint([.. fixture.Key]);

        return Value(BackupRestoreJournalAuthenticator.Seal(
            lease,
            fixture.Publication.Envelope.ProfileNamespaceDigest,
            fixture.Publication.Envelope.InstallationId,
            fixture.Publication.Envelope.Revision,
            fixture.Publication.Envelope.PreviousEnvelopeDigest,
            payload));

    }

    private BackupRestoreJournalPayloadV2 Payload(
        BackupRestorePhase phase,
        BackupRestoreMarkerCleanupCheckpointV1? markerCleanup)
    {

        BackupRestoreDurableNodeIdentityV1 live = new(
            _root,
            Digest(11),
            "arcanum",
            BackupRestoreNodeKind.Directory,
            BackupRestoreNodePresence.Present,
            Digest(12),
            null);

        BackupRestoreDurableNodeIdentityV1 staged = new(
            Path.Combine(_root, ".arcanum-restore-0123456789abcdef0123456789abcdef"),
            Digest(13),
            "staged",
            BackupRestoreNodeKind.Directory,
            BackupRestoreNodePresence.Present,
            Digest(14),
            null);

        BackupRestoreDurableNodeIdentityV1 displaced = new(
            Path.Combine(_root, ".arcanum-restore-0123456789abcdef0123456789abcdef"),
            Digest(13),
            "previous",
            BackupRestoreNodeKind.Directory,
            BackupRestoreNodePresence.Absent,
            null,
            null);

        BackupRestoreDurableNodeIdentityV1 archive = new(
            _root,
            Digest(15),
            "source.arcbackup",
            BackupRestoreNodeKind.RegularFile,
            BackupRestoreNodePresence.Present,
            Digest(16),
            Digest(17));

        bool afterSwap = phase >= BackupRestorePhase.Reconcile;

        return new BackupRestoreJournalPayloadV2(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            CovenantExclusiveOperation.BackupRestore,
            Digest(18),
            BackupRestoreConflictMode.ReplaceInstallation,
            phase,
            Digest(19),
            live,
            afterSwap ? Absent(staged) : staged,
            afterSwap ? Present(displaced) : displaced,
            archive,
            null,
            markerCleanup);

    }

    private static BackupRestoreDurableNodeIdentityV1 Present(BackupRestoreDurableNodeIdentityV1 node) =>
        node with
        {
            Presence = BackupRestoreNodePresence.Present,
            NodePhysicalIdentityDigest = Digest(21),
        };

    private static BackupRestoreDurableNodeIdentityV1 Absent(BackupRestoreDurableNodeIdentityV1 node) =>
        node with
        {
            Presence = BackupRestoreNodePresence.Absent,
            NodePhysicalIdentityDigest = null,
            ContentDigest = null,
        };

    private static BackupRestoreMarkerCleanupCheckpointV1 MarkerCheckpoint(ImmutableArray<Guid> intentIds) =>
        new(
            1,
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            CovenantExclusiveOperation.BackupRestore,
            Digest(18),
            intentIds,
            checked((ulong)intentIds.Length),
            Value(CampaignPathRestoreCleanupIntentVector.Compute(intentIds)));

    private static string FlipFirstCharacter(string value) =>
        (value[0] == 'A' ? 'B' : 'A') + value[1..];

    private static string EncodeKey(byte[] key) => System.Buffers.Text.Base64Url.EncodeToString(key);

    private static byte[] DecodeKey(string key) => System.Buffers.Text.Base64Url.DecodeFromChars(key);

    private static CovenantDigest Digest(byte seed) =>
        new([.. Enumerable.Repeat(seed, 32)]);

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code + ": " + result.Error.Message : string.Empty);

        return result.Value;

    }

}
