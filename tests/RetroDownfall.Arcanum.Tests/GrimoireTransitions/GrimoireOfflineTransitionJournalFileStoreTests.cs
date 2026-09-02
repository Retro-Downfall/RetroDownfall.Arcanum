using System.Runtime.InteropServices;

using System.Runtime.Versioning;

using System.Security.AccessControl;

using System.Security.Principal;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

[Collection(ProcessGlobalSeamCollectionName.Value)]
public sealed partial class GrimoireOfflineTransitionJournalFileStoreTests : IDisposable
{

    private readonly string _container = Path.Combine(
        Path.GetTempPath(),
        "arcanum-offline-transition-file-store-" + Guid.NewGuid().ToString("N"));

    private readonly string _guarded;

    public GrimoireOfflineTransitionJournalFileStoreTests()
    {

        Directory.CreateDirectory(_container);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                _container,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }
        else
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_container);

        }

        _guarded = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_guarded);

    }

    public void Dispose()
    {

        if (!OperatingSystem.IsWindows() && Directory.Exists(_container))
        {

            File.SetUnixFileMode(
                _container,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Fact]
    public void Location_is_the_maintenance_lock_sibling_with_the_exact_suffix()
    {

        GrimoireOfflineTransitionJournalLocation location = Location();

        string expectedLock = ArcanumMaintenanceLock.LockPathFor(_guarded);

        Assert.Equal(expectedLock, location.MaintenanceLockPath);

        Assert.Equal(
            expectedLock + ".grimoire-transition.active.json",
            location.JournalPath);

        Assert.Equal(location.JournalPath + ".publish", location.WorkingPath);

        Assert.Equal(location.JournalPath + ".previous", location.PreviousPath);

        Assert.Equal(location.JournalPath + ".retiring", location.RetiringPath);

        Assert.Equal(Path.GetDirectoryName(expectedLock), location.GuardedDirectory is not null
            ? Path.GetDirectoryName(location.JournalPath)
            : null);

        Assert.Equal(Path.GetFileName(location.JournalPath), location.JournalLeaf);

        Assert.Equal(Path.GetFileName(location.WorkingPath), location.WorkingLeaf);

        Assert.Equal(Path.GetFileName(location.PreviousPath), location.PreviousLeaf);

        Assert.Equal(Path.GetFileName(location.RetiringPath), location.RetiringLeaf);

    }

    [Fact]
    public void Location_digest_changes_with_profile_parent_identity_or_leaf()
    {

        GrimoireOfflineTransitionJournalLocation baseline = Location();

        string siblingGuarded = Path.Combine(_container, "different-profile");

        Directory.CreateDirectory(siblingGuarded);

        GrimoireOfflineTransitionJournalLocation differentLeaf = Value(
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(siblingGuarded));

        string otherContainer = Path.Combine(_container, "other-parent");

        string sameLeafOtherParent = Path.Combine(otherContainer, "arcanum");

        Directory.CreateDirectory(sameLeafOtherParent);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                otherContainer,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }
        else
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(otherContainer);

        }

        GrimoireOfflineTransitionJournalLocation differentParent = Value(
            new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(sameLeafOtherParent));

        Assert.NotEqual(baseline.JournalLocationDigest, differentLeaf.JournalLocationDigest);

        Assert.NotEqual(baseline.JournalLocationDigest, differentParent.JournalLocationDigest);

        Assert.NotEqual(
            baseline.GuardedParentPhysicalIdentityDigest,
            differentParent.GuardedParentPhysicalIdentityDigest);

    }

    [Fact]
    public void Location_refuses_insecure_or_foreign_existing_parent_posture()
    {

        if (!OperatingSystem.IsMacOS())
        {

            return;

        }

        GrimoireOfflineTransitionJournalFileStore store = new();

        File.SetUnixFileMode(
            _container,
            UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead);

        try
        {

            Assert.True(store.ResolveLocation(_guarded).IsFailure);

        }
        finally
        {

            File.SetUnixFileMode(
                _container,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        string foreignGuarded = Path.Combine(
            "/private/tmp",
            "arcanum-offline-transition-foreign-parent-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(foreignGuarded);

        try
        {

            Assert.True(store.ResolveLocation(foreignGuarded).IsFailure);

        }
        finally
        {

            Directory.Delete(foreignGuarded);

        }

    }

    [Fact]
    public async Task Every_store_entry_point_rejects_tampered_location_commitments()
    {

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        string redirectedGuarded = _guarded + "-redirected";

        Directory.CreateDirectory(redirectedGuarded);

        CovenantDigest tamperedDigest = Digest(0xD3);

        BackupRestoreProfileNamespace profile = location.ProfileNamespace;

        (string Name, GrimoireOfflineTransitionJournalLocation Location)[] tampered =
        [
            ("profile-digest", location with
            {
                ProfileNamespace = profile with { Digest = tamperedDigest },
            }),
            ("profile-parent-digest", location with
            {
                ProfileNamespace = profile with
                {
                    ParentPhysicalIdentityDigest = tamperedDigest,
                },
            }),
            ("profile-child-leaf", location with
            {
                ProfileNamespace = profile with { ChildLeaf = profile.ChildLeaf + "-redirected" },
            }),
            ("guarded-directory", location with { GuardedDirectory = redirectedGuarded }),
            ("maintenance-lock-path", location with
            {
                MaintenanceLockPath = location.MaintenanceLockPath + ".redirected",
            }),
            ("journal-path", location with { JournalPath = location.JournalPath + ".redirected" }),
            ("journal-leaf", location with { JournalLeaf = "redirected-" + location.JournalLeaf }),
            ("working-path", location with { WorkingPath = location.WorkingPath + ".redirected" }),
            ("working-leaf", location with { WorkingLeaf = "redirected-" + location.WorkingLeaf }),
            ("previous-path", location with { PreviousPath = location.PreviousPath + ".redirected" }),
            ("previous-leaf", location with { PreviousLeaf = "redirected-" + location.PreviousLeaf }),
            ("retiring-path", location with { RetiringPath = location.RetiringPath + ".redirected" }),
            ("retiring-leaf", location with { RetiringLeaf = "redirected-" + location.RetiringLeaf }),
            ("parent-identity-digest", location with
            {
                GuardedParentPhysicalIdentityDigest = tamperedDigest,
            }),
            ("location-digest", location with { JournalLocationDigest = tamperedDigest }),
        ];

        foreach ((string name, GrimoireOfflineTransitionJournalLocation candidate) in tampered)
        {

            await AssertEveryEntryPointRejectsAsync(store, location, candidate, name);

        }

    }

    [Fact]
    public async Task Publication_orders_create_write_file_fsync_rename_permissions_parent_fsync()
    {

        List<string> events = [];

        GrimoireOfflineTransitionJournalFileStore store = new(events.Add);

        GrimoireOfflineTransitionJournalLocation location = Value(store.ResolveLocation(_guarded));

        using ArcanumMaintenanceLock held = HeldLock();

        Result firstPublication = await store.ReplaceDurablyAsync(
            held,
            location,
            Bytes("revision-one"),
            expectedCurrentIdentity: null,
            CancellationToken.None);

        Assert.True(
            firstPublication.IsSuccess,
            firstPublication.Error.Code + ":" + string.Join(",", events));

        Assert.Equal(
            (string[])
            [
                "file:temporary-created",
                "file:temporary-written",
                "file:temporary-flushed",
                "file:atomic-replace",
                "file:permissions-verified",
                "file:parent-flushed",
                "file:residue-absence-proved",
            ],
            events);

        events.Clear();

        using GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
            GrimoireOfflineTransitionJournalFileRead>(
            Value(await store.ReadIfPresentAsync(location, CancellationToken.None)));

        events.Clear();

        RetentionShapeRecordingPrimitives? retentionShape = null;

        GrimoireOfflineTransitionJournalFileStore updating = new(
            events.Add,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                retentionShape = new RetentionShapeRecordingPrimitives(opened.Value);

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(
                    retentionShape);

            });

        Assert.True((await updating.ReplaceDurablyAsync(
            held,
            location,
            Bytes("revision-two"),
            current.Metadata.Identity,
            CancellationToken.None)).IsSuccess);

        Assert.NotNull(retentionShape);

        Assert.Equal(
            (location.JournalLeaf, location.WorkingLeaf, location.PreviousLeaf),
            retentionShape.ExchangeArguments);

        Assert.True(retentionShape.PostCallIdentitiesMatched);

        Assert.Equal(
            (string[])
            [
                "file:temporary-created",
                "file:temporary-written",
                "file:temporary-flushed",
                "file:atomic-replace",
                "file:previous-retained",
                "file:permissions-verified",
                "file:parent-flushed",
                "file:previous-retiring",
                "file:previous-retiring-verified",
                "file:previous-unlinked",
                "file:previous-zero-link-verified",
                "file:previous-delete-parent-flushed",
                "file:residue-absence-proved",
            ],
            events);

    }

    [Fact]
    public async Task Secure_reread_returns_the_exact_published_identity_and_bytes()
    {

        List<string> events = [];

        GrimoireOfflineTransitionJournalFileStore store = new(events.Add);

        GrimoireOfflineTransitionJournalLocation location = Value(store.ResolveLocation(_guarded));

        byte[] expected = Bytes("authenticated-envelope").ToArray();

        using ArcanumMaintenanceLock held = HeldLock();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            expected,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        events.Clear();

        using GrimoireOfflineTransitionJournalFileRead read = Assert.IsType<
            GrimoireOfflineTransitionJournalFileRead>(
            Value(await store.ReadIfPresentAsync(location, CancellationToken.None)));

        Assert.Equal(expected, read.Bytes.ToArray());

        Assert.Equal("file:secure-reread", Assert.Single(events));

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            location.JournalPath,
            out FileHandleMetadata landed));

        Assert.Equal(landed.Identity, read.Metadata.Identity);

        read.Dispose();

        Assert.True(read.Bytes.IsEmpty);

    }

    [Fact]
    public async Task Evidence_read_refuses_relative_name_substitution_during_bounded_read()
    {

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            Bytes("original-evidence"),
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        string preserved = Path.Combine(_container, "preserved-during-bounded-read");

        int substituted = 0;

        SecureFileReader.AfterOpenForTests = _ =>
        {

            if (Interlocked.Exchange(ref substituted, 1) != 0)
            {

                return;

            }

            File.Move(location.JournalPath, preserved);

            File.WriteAllBytes(location.JournalPath, Bytes("substitute-evidence").Span);

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(
                    location.JournalPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);

            }

        };

        try
        {

            Result<GrimoireOfflineTransitionJournalFileRead?> read =
                await store.ReadIfPresentAsync(location, CancellationToken.None);

            Assert.True(read.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, read.Error.Code);

            Assert.Equal(Bytes("original-evidence").ToArray(), File.ReadAllBytes(preserved));

            Assert.Equal(
                Bytes("substitute-evidence").ToArray(),
                File.ReadAllBytes(location.JournalPath));

        }
        finally
        {

            SecureFileReader.AfterOpenForTests = null;

        }

    }

    [Fact]
    public async Task Publication_preserves_and_refuses_a_target_substituted_after_final_validation()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Value(initial.ResolveLocation(_guarded));

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] previousBytes = Bytes("expected-predecessor").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            previousBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity previousIdentity;

        using (GrimoireOfflineTransitionJournalFileRead previous = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            previousIdentity = previous.Metadata.Identity;

        }

        string preservedExpected = Path.Combine(_container, "preserved-expected-predecessor");

        byte[] substituteBytes = Bytes("last-window-substitute").ToArray();

        GrimoireOfflineTransitionJournalFileStore attacked = new(
            beforeAtomicReplace: () =>
            {

                File.Move(location.JournalPath, preservedExpected);

                File.WriteAllBytes(location.JournalPath, substituteBytes);

                if (!OperatingSystem.IsWindows())
                {

                    File.SetUnixFileMode(
                        location.JournalPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);

                }

            });

        Result result = await attacked.ReplaceDurablyAsync(
            held,
            location,
            Bytes("candidate-revision"),
            previousIdentity,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        Assert.Equal(previousBytes, File.ReadAllBytes(preservedExpected));

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await attacked.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.NotNull(evidence.Canonical);

        GrimoireOfflineTransitionJournalFileRead displaced =
            evidence.Previous ?? Assert.IsType<GrimoireOfflineTransitionJournalFileRead>(evidence.Working);

        Assert.Equal(substituteBytes, displaced.Bytes.ToArray());

    }

    [Fact]
    public async Task Publication_crash_after_atomic_exchange_retains_authentic_predecessor_evidence()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] predecessorBytes = Bytes("predecessor").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            predecessorBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity predecessorIdentity;

        using (GrimoireOfflineTransitionJournalFileRead predecessor = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            predecessorIdentity = predecessor.Metadata.Identity;

        }

        GrimoireOfflineTransitionJournalFileStore crashing = new(
            afterStep: step =>
            {

                if (step == "file:atomic-replace")
                {

                    throw new IOException("synthetic crash seam");

                }

            });

        Result result = await crashing.ReplaceDurablyAsync(
            held,
            location,
            Bytes("new-revision"),
            predecessorIdentity,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await initial.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.Equal(Bytes("new-revision").ToArray(), evidence.Canonical?.Bytes.ToArray());

        GrimoireOfflineTransitionJournalFileRead retained =
            evidence.Working ?? Assert.IsType<GrimoireOfflineTransitionJournalFileRead>(evidence.Previous);

        Assert.Equal(predecessorBytes, retained.Bytes.ToArray());

        Assert.Equal(predecessorIdentity, retained.Metadata.Identity);

    }

    [Fact]
    public async Task Read_refuses_symlink_hardlink_non_owner_and_non_regular_evidence()
    {

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        string outside = Path.Combine(_container, "outside");

        File.WriteAllText(outside, "outside");

        File.CreateSymbolicLink(location.JournalPath, outside);

        await AssertUnsafeEvidenceAsync(store, location);

        File.Delete(location.JournalPath);

        File.WriteAllText(location.JournalPath, "hard-linked");

        string alias = Path.Combine(_container, "non-journal-prefixed-hardlink");

        Assert.True(HardLinkTestSupport.TryCreate(alias, location.JournalPath));

        await AssertUnsafeEvidenceAsync(store, location);

        File.Delete(alias);

        File.Delete(location.JournalPath);

        File.WriteAllText(location.JournalPath, "not-owner-only");

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                location.JournalPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

            await AssertUnsafeEvidenceAsync(store, location);

            File.Delete(location.JournalPath);

        }
        else
        {

            File.Delete(location.JournalPath);

        }

        Directory.CreateDirectory(location.JournalPath);

        await AssertUnsafeEvidenceAsync(store, location);

    }

    [Fact]
    public void Absence_refuses_case_alias_working_previous_retiring_legacy_temp_and_unreadable_parent_evidence()
    {

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        foreach (string residue in (string[])
                 [
                     location.JournalLeaf.ToUpperInvariant(),
                     location.WorkingLeaf,
                     location.PreviousLeaf,
                     location.RetiringLeaf,
                     location.JournalLeaf + ".tmp.0123456789abcdef",
                     location.JournalLeaf + ".unknown-residue",
                 ])
        {

            string path = Path.Combine(Path.GetDirectoryName(location.JournalPath)!, residue);

            File.WriteAllText(path, "residue");

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            }

            Assert.True(store.RequireNoEvidence(location).IsFailure);

            File.Delete(path);

        }

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(_container, UnixFileMode.None);

            try
            {

                Assert.True(store.RequireNoEvidence(location).IsFailure);

            }
            finally
            {

                File.SetUnixFileMode(
                    _container,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            }

        }

    }

    [Fact]
    public async Task Delete_moves_to_retiring_authenticates_compare_unlinks_and_proves_the_handle_unlinked()
    {

        List<string> events = [];

        GrimoireOfflineTransitionJournalFileStore store = new(events.Add);

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("delete-me").ToArray();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleMetadata metadata;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await store.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            metadata = current.Metadata;

        }

        events.Clear();

        Assert.True(store.DeleteDurably(held, location, metadata, bytes).IsSuccess);

        Assert.Equal(
            (string[])
            [
                "file:previous-retiring",
                "file:previous-retiring-verified",
                "file:previous-unlinked",
                "file:previous-zero-link-verified",
                "file:previous-delete-parent-flushed",
                "file:residue-absence-proved",
            ],
            events);

        Assert.False(File.Exists(location.JournalPath));

        Assert.False(File.Exists(location.WorkingPath));

        Assert.False(File.Exists(location.PreviousPath));

        Assert.False(File.Exists(location.RetiringPath));

    }

    [Fact]
    public async Task Compare_unlink_detects_a_substitution_in_the_delegated_unlink_window()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("expected-delete-target").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleMetadata metadata;

        using (GrimoireOfflineTransitionJournalFileRead read = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            metadata = read.Metadata;

        }

        string displaced = location.RetiringPath + ".expected";

        GrimoireOfflineTransitionJournalFileStore attacked = new(
            failBeforeStep: step =>
            {

                if (step != "file:previous-unlinked")
                {

                    return false;

                }

                File.Move(location.RetiringPath, displaced);

                File.WriteAllBytes(location.RetiringPath, bytes);

                if (!OperatingSystem.IsWindows())
                {

                    File.SetUnixFileMode(
                        location.RetiringPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);

                }

                return false;

            });

        Result result = attacked.DeleteDurably(held, location, metadata, bytes);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        Assert.True(File.Exists(displaced));

        Assert.True(File.Exists(location.RetiringPath));

    }

    [Fact]
    public async Task Delete_refuses_identity_substitution_before_the_delegated_unlink_window()
    {

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("identity-substitution").ToArray();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        using GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
            GrimoireOfflineTransitionJournalFileRead>(
            Value(await store.ReadIfPresentAsync(location, CancellationToken.None)));

        FileHandleMetadata substituted = current.Metadata with
        {
            Identity = new FileHandleIdentity(
                current.Metadata.Identity.VolumeId,
                current.Metadata.Identity.FileId + 1),
        };

        Assert.True(store.DeleteDurably(held, location, substituted, bytes).IsFailure);

        Assert.True(File.Exists(location.JournalPath));

    }

    [Fact]
    public async Task Publication_failure_before_exchange_preserves_current_and_removes_exact_working_file()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] currentBytes = Bytes("current").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            currentBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity identity;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            identity = current.Metadata.Identity;

        }

        GrimoireOfflineTransitionJournalFileStore failing = new(
            failBeforeStep: step => step == "file:atomic-replace");

        Result result = await failing.ReplaceDurablyAsync(
            held,
            location,
            Bytes("not-published"),
            identity,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(currentBytes, File.ReadAllBytes(location.JournalPath));

        Assert.False(File.Exists(location.WorkingPath));

        Assert.False(File.Exists(location.PreviousPath));

        Assert.False(File.Exists(location.RetiringPath));

    }

    [Fact]
    public async Task Publication_failure_after_exchange_returns_recovery_required_and_preserves_all_evidence()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] oldBytes = Bytes("old").ToArray();

        byte[] newBytes = Bytes("new").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            oldBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity oldIdentity;

        using (GrimoireOfflineTransitionJournalFileRead old = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            oldIdentity = old.Metadata.Identity;

        }

        GrimoireOfflineTransitionJournalFileStore failing = new(
            failBeforeStep: step => step == "file:permissions-verified");

        Result result = await failing.ReplaceDurablyAsync(
            held,
            location,
            newBytes,
            oldIdentity,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await initial.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.Equal(newBytes, evidence.Canonical?.Bytes.ToArray());

        GrimoireOfflineTransitionJournalFileRead retained =
            evidence.Previous ?? Assert.IsType<GrimoireOfflineTransitionJournalFileRead>(evidence.Working);

        Assert.Equal(oldBytes, retained.Bytes.ToArray());

        Assert.Equal(oldIdentity, retained.Metadata.Identity);

    }

    [Fact]
    public async Task Publication_failures_at_parent_fsync_and_predecessor_retirement_boundaries_require_recovery()
    {

        (string Step, string? RetainedLeaf)[] boundaries =
        [
            ("file:parent-flushed", "previous"),
            ("file:previous-retiring", "previous"),
            ("file:previous-retiring-verified", "retiring"),
            ("file:previous-unlinked", "retiring"),
            ("file:previous-delete-parent-flushed", null),
        ];

        foreach ((string step, string? retainedLeaf) in boundaries)
        {

            string guarded = Path.Combine(_container, "boundary-" + step.Replace(':', '-'));

            Directory.CreateDirectory(guarded);

            GrimoireOfflineTransitionJournalFileStore initial = new();

            GrimoireOfflineTransitionJournalLocation location = Value(
                initial.ResolveLocation(guarded));

            using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.TryAcquire(guarded)
                ?? throw new Xunit.Sdk.XunitException("The boundary lock could not be acquired.");

            byte[] oldBytes = Bytes("old-" + step).ToArray();

            byte[] newBytes = Bytes("new-" + step).ToArray();

            Assert.True((await initial.ReplaceDurablyAsync(
                held,
                location,
                oldBytes,
                expectedCurrentIdentity: null,
                CancellationToken.None)).IsSuccess);

            FileHandleIdentity oldIdentity;

            using (GrimoireOfflineTransitionJournalFileRead old = Assert.IsType<
                       GrimoireOfflineTransitionJournalFileRead>(
                       Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
            {

                oldIdentity = old.Metadata.Identity;

            }

            GrimoireOfflineTransitionJournalFileStore failing = new(
                failBeforeStep: candidate => candidate == step);

            Result result = await failing.ReplaceDurablyAsync(
                held,
                location,
                newBytes,
                oldIdentity,
                CancellationToken.None);

            Assert.True(result.IsFailure, step);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

            using GrimoireOfflineTransitionJournalEvidence evidence = Value(
                await initial.InspectEvidenceAsync(location, CancellationToken.None));

            Assert.Equal(newBytes, evidence.Canonical?.Bytes.ToArray());

            GrimoireOfflineTransitionJournalFileRead? retained = retainedLeaf switch
            {
                "previous" => evidence.Previous,
                "retiring" => evidence.Retiring,
                _ => null,
            };

            if (retainedLeaf is null)
            {

                Assert.Null(evidence.Working);

                Assert.Null(evidence.Previous);

                Assert.Null(evidence.Retiring);

            }
            else
            {

                Assert.NotNull(retained);

                Assert.Equal(oldBytes, retained.Bytes.ToArray());

                Assert.Equal(oldIdentity, retained.Metadata.Identity);

            }

        }

    }

    [Fact]
    public async Task Cancellation_requested_after_first_publication_lands_does_not_convert_success_to_recovery_required()
    {

        using CancellationTokenSource cts = new();

        GrimoireOfflineTransitionJournalFileStore cancelling = new(
            afterStep: step =>
            {

                if (step == "file:atomic-replace")
                {

                    cts.Cancel();

                }

            });

        GrimoireOfflineTransitionJournalLocation location = Location(cancelling);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("cancel-after-first-publish").ToArray();

        Result result = await cancelling.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            cts.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "success");

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await cancelling.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.Equal(bytes, evidence.Canonical?.Bytes.ToArray());

        Assert.Null(evidence.Working);

        Assert.Null(evidence.Previous);

        Assert.Null(evidence.Retiring);

    }

    [Fact]
    public async Task Cancellation_requested_after_the_exchange_lands_does_not_convert_success_to_recovery_required()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] firstBytes = Bytes("cancel-after-exchange-first").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            firstBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity firstIdentity;

        using (GrimoireOfflineTransitionJournalFileRead first = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            firstIdentity = first.Metadata.Identity;

        }

        using CancellationTokenSource cts = new();

        GrimoireOfflineTransitionJournalFileStore cancelling = new(
            afterStep: step =>
            {

                if (step == "file:atomic-replace")
                {

                    cts.Cancel();

                }

            });

        byte[] secondBytes = Bytes("cancel-after-exchange-second").ToArray();

        Result result = await cancelling.ReplaceDurablyAsync(
            held,
            location,
            secondBytes,
            firstIdentity,
            cts.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "success");

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await initial.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.Equal(secondBytes, evidence.Canonical?.Bytes.ToArray());

        Assert.Null(evidence.Working);

        Assert.Null(evidence.Previous);

        Assert.Null(evidence.Retiring);

    }

    [Fact]
    public async Task Cancellation_requested_after_the_retirement_unlink_lands_does_not_convert_success_to_recovery_required()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("cancel-after-retirement-unlink").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleMetadata metadata;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            metadata = current.Metadata;

        }

        using CancellationTokenSource cts = new();

        GrimoireOfflineTransitionJournalFileStore cancelling = new(
            afterStep: step =>
            {

                if (step == "file:delete-parent-flushed")
                {

                    cts.Cancel();

                }

            });

        Result result = await cancelling.CompleteRetirementAsync(
            held,
            location,
            GrimoireOfflineTransitionJournalRetirementSource.Canonical,
            metadata,
            bytes,
            requireCanonicalAfter: false,
            cts.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "success");

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await cancelling.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.Null(evidence.Canonical);

        Assert.Null(evidence.Working);

        Assert.Null(evidence.Previous);

        Assert.Null(evidence.Retiring);

    }

    [Fact]
    public async Task Resume_after_a_crash_before_permissions_verification_returns_recovery_required_rather_than_throwing()
    {

        (ArcanumMaintenanceLock held, GrimoireOfflineTransitionJournalLocation location,
                FileHandleMetadata expectedCurrent, byte[] expectedCurrentBytes,
                FileHandleMetadata expectedNext, byte[] expectedNextBytes) =
            await ArrangeCanonicalBesideWorkingAsync();

        using (held)
        {

            GrimoireOfflineTransitionJournalFileStore resuming = new(
                failBeforeStep: step => step == "file:permissions-verified");

            Result result = await resuming.ResumeWorkingPublicationAsync(
                held,
                location,
                expectedCurrent,
                expectedCurrentBytes,
                expectedNext,
                expectedNextBytes,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        }

    }

    [Fact]
    public async Task Resume_reports_recovery_required_when_enumeration_fails_with_an_io_exception()
    {

        (ArcanumMaintenanceLock held, GrimoireOfflineTransitionJournalLocation location,
                FileHandleMetadata expectedCurrent, byte[] expectedCurrentBytes,
                FileHandleMetadata expectedNext, byte[] expectedNextBytes) =
            await ArrangeCanonicalBesideWorkingAsync();

        using (held)
        {

            GrimoireOfflineTransitionJournalFileStore resuming = new(
                afterStep: null,
                failBeforeStep: null,
                beforeAtomicReplace: null,
                openPrimitives: currentLocation =>
                {

                    Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                        GrimoireOfflineTransitionJournalFilePrimitives.Open(
                            Path.GetDirectoryName(currentLocation.JournalPath)!,
                            currentLocation.GuardedParentPhysicalIdentityDigest);

                    if (opened.IsFailure)
                    {

                        return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                            opened.Error);

                    }

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(
                        new ThrowingEnumerationPrimitives(opened.Value));

                });

            Result result = await resuming.ResumeWorkingPublicationAsync(
                held,
                location,
                expectedCurrent,
                expectedCurrentBytes,
                expectedNext,
                expectedNextBytes,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        }

    }

    [Fact]
    public async Task Normalize_working_predecessor_with_a_pre_cancelled_token_returns_recovery_required_rather_than_throwing()
    {

        (ArcanumMaintenanceLock held, GrimoireOfflineTransitionJournalLocation location,
                FileHandleMetadata expectedCurrent, byte[] expectedCurrentBytes,
                FileHandleMetadata expectedNext, byte[] expectedNextBytes) =
            await ArrangeCanonicalBesideWorkingAsync();

        using (held)
        {

            using CancellationTokenSource cancelled = new();

            cancelled.Cancel();

            GrimoireOfflineTransitionJournalFileStore normalizing = new();

            Result result = await normalizing.NormalizeWorkingPredecessorAsync(
                held,
                location,
                expectedCurrent,
                expectedCurrentBytes,
                expectedNext,
                expectedNextBytes,
                cancelled.Token);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

        }

    }

    /// <summary>
    /// Publishes a genesis revision, then crashes a second publication through the production
    /// <c>afterStep</c> seam right after the working file is written and flushed but before the
    /// atomic exchange, with an exception the store's own catch clause does not filter. That leaves
    /// an authentic canonical and an authentic working file side by side on disk — the exact
    /// precondition <c>ResumeWorkingPublicationAsync</c> and <c>NormalizeWorkingPredecessorAsync</c>
    /// both require — without hand-building either file.
    /// </summary>
    private async Task<(
        ArcanumMaintenanceLock Held,
        GrimoireOfflineTransitionJournalLocation Location,
        FileHandleMetadata Canonical,
        byte[] CanonicalBytes,
        FileHandleMetadata Working,
        byte[] WorkingBytes)> ArrangeCanonicalBesideWorkingAsync()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        ArcanumMaintenanceLock held = HeldLock();

        byte[] currentBytes = Bytes("resume-current-" + Guid.NewGuid().ToString("N")).ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            currentBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity currentIdentity;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            currentIdentity = current.Metadata.Identity;

        }

        GrimoireOfflineTransitionJournalFileStore crashing = new(
            afterStep: step =>
            {

                if (step == "file:temporary-flushed")
                {

                    throw new InvalidOperationException(
                        "synthetic hard crash leaving working beside canonical");

                }

            });

        byte[] nextBytes = Bytes("resume-next-" + Guid.NewGuid().ToString("N")).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crashing.ReplaceDurablyAsync(held, location, nextBytes, currentIdentity, CancellationToken.None));

        using GrimoireOfflineTransitionJournalEvidence crashed = Value(
            await initial.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.NotNull(crashed.Canonical);

        Assert.NotNull(crashed.Working);

        Assert.Null(crashed.Previous);

        Assert.Null(crashed.Retiring);

        return (
            held,
            location,
            crashed.Canonical.Metadata,
            crashed.Canonical.Bytes.ToArray(),
            crashed.Working.Metadata,
            crashed.Working.Bytes.ToArray());

    }

    /// <summary>
    /// Exercises <c>OpenChild</c> directly (below <c>CreateWorkingExclusive</c>'s <c>fchmod</c> repair)
    /// so the mode the kernel actually assigned at creation time is observable. Apple's arm64 ABI
    /// passes every variadic argument of a variadic call on the stack rather than in a register, so a
    /// fixed-arity P/Invoke declaration of <c>openat</c> that places the creation mode in a register can
    /// deliver unrelated register contents to the kernel instead.
    /// </summary>
    [SkippableFact]
    public void Working_file_creation_delivers_owner_only_mode_before_the_fchmod_repair()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only: exercises the openat exclusive-create path fchmod repairs afterwards.");

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        GrimoireOfflineTransitionJournalLocation location = Location();

        using GrimoireOfflineTransitionJournalFilePrimitives primitives = Value(
            GrimoireOfflineTransitionJournalFilePrimitives.Open(
                Path.GetDirectoryName(location.JournalPath)!,
                location.GuardedParentPhysicalIdentityDigest));

        int previousUmask = UmaskUnix(0);

        try
        {

            SecureFileOpenStatus status = primitives.OpenChild(
                "openat-mode-probe",
                createExclusive: true,
                writable: true,
                out var handle);

            using (handle)
            {

                Assert.Equal(SecureFileOpenStatus.Success, status);

                Assert.NotNull(handle);

                UnixFileMode observed = File.GetUnixFileMode(handle);

                Assert.True(
                    observed == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "observed pre-fchmod mode: " + observed);

            }

        }
        finally
        {

            UmaskUnix(previousUmask);

        }

    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern int UmaskUnix(int mask);

    /// <summary>
    /// Pins the durability barriers through the primitives interface the store actually calls, rather
    /// than the step names it announces: <see cref="RecordingJournalFilePrimitives.BarrierCalls"/> is
    /// appended to only from inside the real <c>FlushWorking</c>, <c>ExchangeRetainingPrevious</c> or
    /// <c>PublishFirstNoReplace</c>, and <c>FlushParent</c> implementations, so this is evidence the
    /// calls actually happened, in this order, not that the store merely claims they did.
    /// </summary>
    [Fact]
    public async Task Publication_invokes_the_working_flush_before_the_atomic_replace_and_the_parent_flush_after()
    {

        RecordingJournalFilePrimitives? recording = null;

        GrimoireOfflineTransitionJournalFileStore store = new(
            afterStep: null,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                recording = new RecordingJournalFilePrimitives(opened.Value);

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(recording);

            });

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("durability-order-genesis").ToArray();

        Result result = await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "success");

        Assert.NotNull(recording);

        Assert.Equal((string[])["working", "replace", "parent"], recording.BarrierCalls);

    }

    [Fact]
    public async Task Publication_fails_closed_when_the_working_flush_barrier_reports_failure()
    {

        RecordingJournalFilePrimitives? recording = null;

        GrimoireOfflineTransitionJournalFileStore store = new(
            afterStep: null,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                recording = new RecordingJournalFilePrimitives(opened.Value)
                {
                    FlushWorkingOverride = () => new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "synthetic working-flush barrier failure"),
                };

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(recording);

            });

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("durability-working-failure").ToArray();

        Result result = await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.False(File.Exists(location.WorkingPath));

        Assert.False(File.Exists(location.JournalPath));

    }

    [Fact]
    public async Task Publication_fails_closed_when_the_parent_flush_barrier_reports_failure()
    {

        RecordingJournalFilePrimitives? recording = null;

        GrimoireOfflineTransitionJournalFileStore store = new(
            afterStep: null,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                recording = new RecordingJournalFilePrimitives(opened.Value)
                {
                    FlushParentOverride = () => new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "synthetic parent-flush barrier failure"),
                };

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(recording);

            });

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("durability-parent-failure").ToArray();

        Result result = await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Error.Code);

    }

    /// <summary>
    /// A revision publication issues six evidence inspections through the primitives layer (the
    /// initial precondition check, the pre-replace validation, the post-exchange landed check, the
    /// permissions reread, the predecessor retirement, and the residue-absence proof). Each inspection
    /// performs one broad EnumerateExactChildren call across the four slot names, then one further
    /// targeted EnumerateExactChildren call per file slot it found present, to reread and reverify
    /// that file -- so an inspection touching two slots costs three enumerations, not one, and the
    /// observed total for one revision is 14, not six. This bounds the total rather than pinning it
    /// exactly, so an implementation that legitimately trades one inspection for another does not have
    /// to touch this test, but a pattern that starts re-scanning per file (rather than per inspection)
    /// will still trip it.
    /// </summary>
    [Fact]
    public async Task Publication_bounds_the_number_of_directory_enumerations_per_revision()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] firstBytes = Bytes("enumeration-bound-first").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            firstBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity firstIdentity;

        using (GrimoireOfflineTransitionJournalFileRead first = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            firstIdentity = first.Metadata.Identity;

        }

        RecordingJournalFilePrimitives? recording = null;

        GrimoireOfflineTransitionJournalFileStore revising = new(
            afterStep: null,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                recording = new RecordingJournalFilePrimitives(opened.Value);

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(recording);

            });

        byte[] secondBytes = Bytes("enumeration-bound-second").ToArray();

        Assert.True((await revising.ReplaceDurablyAsync(
            held,
            location,
            secondBytes,
            firstIdentity,
            CancellationToken.None)).IsSuccess);

        Assert.NotNull(recording);

        Assert.True(
            recording.EnumerateExactChildrenCallCount <= 16,
            $"observed {recording.EnumerateExactChildrenCallCount} EnumerateExactChildren calls for one revision");

    }

    /// <summary>
    /// DeleteDurably called ProveAllAbsentAsync twice back to back with nothing between the two calls
    /// that could change the filesystem state, unlike ProveAbsentDurably's sibling pair which
    /// interleaves a FlushParent. Pins the exact count of directory enumerations DeleteDurably performs
    /// so a reintroduced duplicate call reddens this immediately rather than only showing up as slower
    /// deletes.
    /// </summary>
    [Fact]
    public async Task Deletion_does_not_repeat_the_absence_proof_without_an_intervening_state_change()
    {

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("dedup-absence-proof").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleMetadata metadata;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            metadata = current.Metadata;

        }

        RecordingJournalFilePrimitives? recording = null;

        GrimoireOfflineTransitionJournalFileStore deleting = new(
            afterStep: null,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                recording = new RecordingJournalFilePrimitives(opened.Value);

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(recording);

            });

        Assert.True(deleting.DeleteDurably(held, location, metadata, bytes).IsSuccess);

        Assert.NotNull(recording);

        Assert.Equal(5, recording.EnumerateExactChildrenCallCount);

    }

    /// <summary>
    /// CompareUnlink's post-condition requires the retained handle's HardLinkCount to reach zero after
    /// the POSIX-semantics delete on Windows, re-read through the still-open handle that is keeping the
    /// deleted file object alive. Whether NTFS reports zero before the last handle closes cannot be
    /// settled by reading Microsoft's documentation or by running anything on this host; only an actual
    /// Windows run of DeleteDurably answers it.
    /// </summary>
    [SkippableFact]
    public async Task Windows_delete_durably_succeeds_after_publication()
    {

        Skip.If(
            !OperatingSystem.IsWindows(),
            "Windows-only: settles whether NTFS reports the retained handle's link count as zero "
                + "after a POSIX-semantics delete on a still-open handle.");

        if (!OperatingSystem.IsWindows())
        {

            return;

        }

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("windows-delete-durably").ToArray();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleMetadata metadata;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await store.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            metadata = current.Metadata;

        }

        Result result = store.DeleteDurably(held, location, metadata, bytes);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "success");

    }

    [Fact]
    public void Windows_desired_access_and_share_mode_constants_are_exact()
    {

        const uint fileShareDelete = 0x00000004;

        const uint readControl = 0x00020000;

        const uint writeDac = 0x00040000;

        const uint writeOwner = 0x00080000;

        Assert.Equal(0x00120081U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsParentDesiredAccess);

        Assert.Equal(0x00000003U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsParentShareMode);

        Assert.NotEqual(
            0U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsParentDesiredAccess
                & readControl);

        Assert.Equal(
            0U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsParentShareMode
                & fileShareDelete);

        Assert.Equal(0x00130081U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildReadDesiredAccess);

        Assert.Equal(0x001F0083U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildWritableDesiredAccess);

        Assert.Equal(0x00000007U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildShareMode);

        Assert.Equal(
            0U,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildReadDesiredAccess
                & (writeDac | writeOwner));

        Assert.Equal(
            writeDac | writeOwner,
            GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildWritableDesiredAccess
                & (writeDac | writeOwner));

        Assert.False(GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildStreamsAreAsync);

    }

    /// <summary>
    /// Runs a real second publication end to end on an actual Windows host: no double stands in for
    /// <c>ExchangeRetainingPrevious</c>, so this is the Windows exchange mechanism itself, not a
    /// retention-shape recording of whatever the current host implements. The recording decorator
    /// observes the retention shape through re-enumeration after the real call returns, the same
    /// technique <see cref="Publication_orders_create_write_file_fsync_rename_permissions_parent_fsync"/>
    /// uses on every platform, so a passing run is genuine evidence for the exchange this host actually
    /// executed rather than an injected outcome.
    /// </summary>
    [SkippableFact]
    public async Task Windows_exchange_through_the_real_primitives_retains_authentic_predecessor_identity()
    {

        Skip.If(
            !OperatingSystem.IsWindows(),
            "Windows-only: exercises the Windows atomic-exchange mechanism against the real kernel.");

        if (!OperatingSystem.IsWindows())
        {

            return;

        }

        GrimoireOfflineTransitionJournalFileStore initial = new();

        GrimoireOfflineTransitionJournalLocation location = Location(initial);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] firstBytes = Bytes("windows-real-exchange-first").ToArray();

        Assert.True((await initial.ReplaceDurablyAsync(
            held,
            location,
            firstBytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleIdentity firstIdentity;

        using (GrimoireOfflineTransitionJournalFileRead first = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await initial.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            firstIdentity = first.Metadata.Identity;

        }

        RetentionShapeRecordingPrimitives? recorded = null;

        GrimoireOfflineTransitionJournalFileStore exchanging = new(
            afterStep: null,
            failBeforeStep: null,
            beforeAtomicReplace: null,
            openPrimitives: currentLocation =>
            {

                Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                    GrimoireOfflineTransitionJournalFilePrimitives.Open(
                        Path.GetDirectoryName(currentLocation.JournalPath)!,
                        currentLocation.GuardedParentPhysicalIdentityDigest);

                if (opened.IsFailure)
                {

                    return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(
                        opened.Error);

                }

                recorded = new RetentionShapeRecordingPrimitives(opened.Value);

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(recorded);

            });

        byte[] secondBytes = Bytes("windows-real-exchange-second").ToArray();

        Assert.True((await exchanging.ReplaceDurablyAsync(
            held,
            location,
            secondBytes,
            firstIdentity,
            CancellationToken.None)).IsSuccess);

        Assert.NotNull(recorded);

        Assert.Equal(
            (location.JournalLeaf, location.WorkingLeaf, location.PreviousLeaf),
            recorded.ExchangeArguments);

        Assert.True(recorded.PostCallIdentitiesMatched);

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(
            await initial.InspectEvidenceAsync(location, CancellationToken.None));

        Assert.Equal(secondBytes, evidence.Canonical?.Bytes.ToArray());

        Assert.Null(evidence.Working);

        Assert.Null(evidence.Previous);

        Assert.Null(evidence.Retiring);

    }

    /// <summary>
    /// D6-6's own RED case: weakening a published file's ACL is untested on the platform the owner-only
    /// posture is named after. Granting a second SID read access, rather than gutting a method, is the
    /// change an actual attacker or misconfiguration would produce.
    /// </summary>
    [SkippableFact]
    public async Task Windows_read_fails_closed_when_the_published_file_acl_is_weakened()
    {

        Skip.If(
            !OperatingSystem.IsWindows(),
            "Windows-only: exercises VerifyWindowsOwnerOnlyHandle's DACL refusal against a real ACL.");

        if (!OperatingSystem.IsWindows())
        {

            return;

        }

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("windows-acl-weakened").ToArray();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        Assert.True((await store.ReadIfPresentAsync(location, CancellationToken.None)).IsSuccess);

        GrantWindowsWorldRead(location.JournalPath);

        Result<GrimoireOfflineTransitionJournalFileRead?> read =
            await store.ReadIfPresentAsync(location, CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, read.Error.Code);

    }

    [SupportedOSPlatform("windows")]
    private static void GrantWindowsWorldRead(string path)
    {

        FileInfo file = new(path);

        FileSecurity security = file.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            FileSystemRights.Read,
            AccessControlType.Allow));

        file.SetAccessControl(security);

    }

    [Fact]
    public async Task Deletion_and_retirement_never_leave_a_generic_cleanup_quarantine_artifact()
    {

        GrimoireOfflineTransitionJournalFileStore store = new();

        GrimoireOfflineTransitionJournalLocation location = Location(store);

        using ArcanumMaintenanceLock held = HeldLock();

        byte[] bytes = Bytes("no-generic-cleanup-quarantine").ToArray();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            location,
            bytes,
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsSuccess);

        FileHandleMetadata metadata;

        using (GrimoireOfflineTransitionJournalFileRead current = Assert.IsType<
                   GrimoireOfflineTransitionJournalFileRead>(
                   Value(await store.ReadIfPresentAsync(location, CancellationToken.None))))
        {

            metadata = current.Metadata;

        }

        Assert.True(store.DeleteDurably(held, location, metadata, bytes).IsSuccess);

        string parent = Path.GetDirectoryName(location.JournalPath)!;

        foreach (string entry in Directory.EnumerateFileSystemEntries(parent, "*", SearchOption.AllDirectories))
        {

            Assert.DoesNotContain(
                "arcanum-cleanup",
                Path.GetFileName(entry),
                StringComparison.OrdinalIgnoreCase);

        }

    }

    private static async Task AssertUnsafeEvidenceAsync(
        GrimoireOfflineTransitionJournalFileStore store,
        GrimoireOfflineTransitionJournalLocation location)
    {

        Result<GrimoireOfflineTransitionJournalFileRead?> read =
            await store.ReadIfPresentAsync(location, CancellationToken.None);

        Assert.True(read.IsFailure);

        Result<GrimoireOfflineTransitionJournalEvidence> evidence =
            await store.InspectEvidenceAsync(location, CancellationToken.None);

        Assert.True(evidence.IsFailure);

    }

    private async Task AssertEveryEntryPointRejectsAsync(
        GrimoireOfflineTransitionJournalFileStore store,
        GrimoireOfflineTransitionJournalLocation authentic,
        GrimoireOfflineTransitionJournalLocation tampered,
        string field)
    {

        Assert.True(store.RequireNoEvidence(tampered).IsFailure, field + ": require-none");

        Assert.True((await store.InspectEvidenceAsync(
            tampered,
            CancellationToken.None)).IsFailure, field + ": inspect");

        Assert.True((await store.ReadIfPresentAsync(
            tampered,
            CancellationToken.None)).IsFailure, field + ": read");

        using ArcanumMaintenanceLock held = HeldLock();

        Assert.True((await store.ReplaceDurablyAsync(
            held,
            tampered,
            Bytes("must-not-publish"),
            expectedCurrentIdentity: null,
            CancellationToken.None)).IsFailure, field + ": replace");

        Assert.True(store.DeleteDurably(
            held,
            tampered,
            default,
            Bytes("must-not-delete")).IsFailure, field + ": delete");

        Assert.True(store.ProveAbsentDurably(held, tampered).IsFailure, field + ": absent");

        Assert.False(File.Exists(authentic.JournalPath), field + ": canonical mutation");

        Assert.False(File.Exists(authentic.WorkingPath), field + ": working mutation");

        Assert.False(File.Exists(authentic.PreviousPath), field + ": previous mutation");

        Assert.False(File.Exists(authentic.RetiringPath), field + ": retiring mutation");

    }

    private GrimoireOfflineTransitionJournalLocation Location(
        GrimoireOfflineTransitionJournalFileStore? store = null) =>
        Value((store ?? new GrimoireOfflineTransitionJournalFileStore()).ResolveLocation(_guarded));

    private ArcanumMaintenanceLock HeldLock() =>
        ArcanumMaintenanceLock.TryAcquire(_guarded)
        ?? throw new Xunit.Sdk.XunitException("The test maintenance lock could not be acquired.");

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static CovenantDigest Digest(byte value) => new(
        Enumerable.Repeat(value, 32).ToArray());

    private static T Value<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException(result.Error.Message);

    /// <summary>
    /// Delegates every operation to the real primitives it wraps and records the retention shape the
    /// exchange produced. On whatever platform runs it, <see cref="ExchangeRetainingPrevious"/> calls
    /// the host's real exchange (RENAME_EXCHANGE on Linux, <c>renameatx_np</c> on macOS, the
    /// handle-relative rename pair on Windows) and, when the host reports <c>Working</c> retention,
    /// performs the same follow-up move production does; it names the shape it observed, not "Windows",
    /// because the shape is real on every platform this runs on and the exchange mechanism underneath
    /// is real only on whichever platform the test executes on.
    /// </summary>
    private sealed class RetentionShapeRecordingPrimitives(
        IGrimoireOfflineTransitionJournalFilePrimitives inner)
        : IGrimoireOfflineTransitionJournalFilePrimitives
    {

        internal (string Journal, string Working, string Previous)? ExchangeArguments { get; private set; }

        internal bool PostCallIdentitiesMatched { get; private set; }

        public FileHandleMetadata ParentMetadata => inner.ParentMetadata;

        public Result<GrimoireOfflineTransitionJournalOpenedFile> CreateWorkingExclusive(
            string workingLeaf) => inner.CreateWorkingExclusive(workingLeaf);

        public Result PublishFirstNoReplace(string journalLeaf, string workingLeaf) =>
            inner.PublishFirstNoReplace(journalLeaf, workingLeaf);

        public Result<GrimoireOfflineTransitionExchangeResult> ExchangeRetainingPrevious(
            string journalLeaf,
            string workingLeaf,
            string previousLeaf)
        {

            ExchangeArguments = (journalLeaf, workingLeaf, previousLeaf);

            Result<GrimoireOfflineTransitionJournalChildEnumeration> beforeResult =
                inner.EnumerateExactChildren([journalLeaf, workingLeaf, previousLeaf]);

            if (beforeResult.IsFailure)
            {

                return Result<GrimoireOfflineTransitionExchangeResult>.Failure(
                    beforeResult.Error);

            }

            using GrimoireOfflineTransitionJournalChildEnumeration before = beforeResult.Value;

            if (!before.ExactChildren.TryGetValue(
                    journalLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? oldJournal)
                || !before.ExactChildren.TryGetValue(
                    workingLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? replacement))
            {

                return new Error(
                    ErrorCodes.Data.RecoveryRequired,
                    "The deterministic Windows exchange layout could not capture both inputs.");

            }

            FileHandleIdentity oldJournalIdentity = oldJournal.Metadata.Identity;

            FileHandleIdentity replacementIdentity = replacement.Metadata.Identity;

            Result<GrimoireOfflineTransitionExchangeResult> exchanged =
                inner.ExchangeRetainingPrevious(journalLeaf, workingLeaf, previousLeaf);

            if (exchanged.IsFailure)
            {

                return exchanged;

            }

            if (exchanged.Value.Retention is GrimoireOfflineTransitionPreviousRetention.Working)
            {

                Result moved = inner.MoveNoReplace(workingLeaf, previousLeaf);

                if (moved.IsFailure)
                {

                    return Result<GrimoireOfflineTransitionExchangeResult>.Failure(moved.Error);

                }

            }

            Result<GrimoireOfflineTransitionJournalChildEnumeration> afterResult =
                inner.EnumerateExactChildren([journalLeaf, workingLeaf, previousLeaf]);

            if (afterResult.IsFailure)
            {

                return Result<GrimoireOfflineTransitionExchangeResult>.Failure(
                    afterResult.Error);

            }

            using GrimoireOfflineTransitionJournalChildEnumeration after = afterResult.Value;

            PostCallIdentitiesMatched =
                after.ExactChildren.TryGetValue(
                    journalLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? published)
                && after.ExactChildren.TryGetValue(
                    previousLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? retained)
                && !after.ExactChildren.ContainsKey(workingLeaf)
                && FileHandleIdentity.IdentitiesMatch(
                    replacementIdentity,
                    published.Metadata.Identity)
                && FileHandleIdentity.IdentitiesMatch(
                    oldJournalIdentity,
                    retained.Metadata.Identity);

            return new GrimoireOfflineTransitionExchangeResult(
                GrimoireOfflineTransitionPreviousRetention.Previous);

        }

        public Result MoveNoReplace(string sourceLeaf, string destinationLeaf) =>
            inner.MoveNoReplace(sourceLeaf, destinationLeaf);

        public Result ApplyOwnerOnlyAndVerify(
            GrimoireOfflineTransitionJournalOpenedFile expected,
            string relativeLeaf) => inner.ApplyOwnerOnlyAndVerify(expected, relativeLeaf);

        public Result CompareUnlink(
            GrimoireOfflineTransitionJournalOpenedFile expected,
            string relativeLeaf) => inner.CompareUnlink(expected, relativeLeaf);

        public Result<GrimoireOfflineTransitionJournalChildEnumeration> EnumerateExactChildren(
            IReadOnlyList<string> exactLeaves) => inner.EnumerateExactChildren(exactLeaves);

        public Result FlushWorking(GrimoireOfflineTransitionJournalOpenedFile file) =>
            inner.FlushWorking(file);

        public Result FlushParent() => inner.FlushParent();

        public void Dispose() => inner.Dispose();

    }

    /// <summary>
    /// Wraps the real primitives capability and records what the store actually invoked, rather than
    /// what it announced through <c>afterStep</c>. <see cref="BarrierCalls"/> is appended to only from
    /// inside <see cref="FlushWorking"/>, <see cref="ExchangeRetainingPrevious"/>,
    /// <see cref="PublishFirstNoReplace"/>, and <see cref="FlushParent"/>, so a call that never happens
    /// never appears, and the override delegates let a test make either durability barrier report
    /// failure without touching the filesystem.
    /// </summary>
    private sealed class RecordingJournalFilePrimitives(IGrimoireOfflineTransitionJournalFilePrimitives inner)
        : IGrimoireOfflineTransitionJournalFilePrimitives
    {

        internal List<string> BarrierCalls { get; } = [];

        internal int EnumerateExactChildrenCallCount { get; private set; }

        internal Func<Result>? FlushWorkingOverride { get; set; }

        internal Func<Result>? FlushParentOverride { get; set; }

        public FileHandleMetadata ParentMetadata => inner.ParentMetadata;

        public Result<GrimoireOfflineTransitionJournalOpenedFile> CreateWorkingExclusive(
            string workingLeaf) => inner.CreateWorkingExclusive(workingLeaf);

        public Result PublishFirstNoReplace(string journalLeaf, string workingLeaf)
        {

            BarrierCalls.Add("replace");

            return inner.PublishFirstNoReplace(journalLeaf, workingLeaf);

        }

        public Result<GrimoireOfflineTransitionExchangeResult> ExchangeRetainingPrevious(
            string journalLeaf,
            string workingLeaf,
            string previousLeaf)
        {

            BarrierCalls.Add("replace");

            return inner.ExchangeRetainingPrevious(journalLeaf, workingLeaf, previousLeaf);

        }

        public Result MoveNoReplace(string sourceLeaf, string destinationLeaf) =>
            inner.MoveNoReplace(sourceLeaf, destinationLeaf);

        public Result ApplyOwnerOnlyAndVerify(
            GrimoireOfflineTransitionJournalOpenedFile expected,
            string relativeLeaf) => inner.ApplyOwnerOnlyAndVerify(expected, relativeLeaf);

        public Result CompareUnlink(
            GrimoireOfflineTransitionJournalOpenedFile expected,
            string relativeLeaf) => inner.CompareUnlink(expected, relativeLeaf);

        public Result<GrimoireOfflineTransitionJournalChildEnumeration> EnumerateExactChildren(
            IReadOnlyList<string> exactLeaves)
        {

            EnumerateExactChildrenCallCount++;

            return inner.EnumerateExactChildren(exactLeaves);

        }

        public Result FlushWorking(GrimoireOfflineTransitionJournalOpenedFile file)
        {

            BarrierCalls.Add("working");

            return FlushWorkingOverride?.Invoke() ?? inner.FlushWorking(file);

        }

        public Result FlushParent()
        {

            BarrierCalls.Add("parent");

            return FlushParentOverride?.Invoke() ?? inner.FlushParent();

        }

        public void Dispose() => inner.Dispose();

    }

    private sealed class ThrowingEnumerationPrimitives(IGrimoireOfflineTransitionJournalFilePrimitives inner)
        : IGrimoireOfflineTransitionJournalFilePrimitives
    {

        public FileHandleMetadata ParentMetadata => inner.ParentMetadata;

        public Result<GrimoireOfflineTransitionJournalOpenedFile> CreateWorkingExclusive(
            string workingLeaf) => inner.CreateWorkingExclusive(workingLeaf);

        public Result PublishFirstNoReplace(string journalLeaf, string workingLeaf) =>
            inner.PublishFirstNoReplace(journalLeaf, workingLeaf);

        public Result<GrimoireOfflineTransitionExchangeResult> ExchangeRetainingPrevious(
            string journalLeaf,
            string workingLeaf,
            string previousLeaf) =>
            inner.ExchangeRetainingPrevious(journalLeaf, workingLeaf, previousLeaf);

        public Result MoveNoReplace(string sourceLeaf, string destinationLeaf) =>
            inner.MoveNoReplace(sourceLeaf, destinationLeaf);

        public Result ApplyOwnerOnlyAndVerify(
            GrimoireOfflineTransitionJournalOpenedFile expected,
            string relativeLeaf) => inner.ApplyOwnerOnlyAndVerify(expected, relativeLeaf);

        public Result CompareUnlink(
            GrimoireOfflineTransitionJournalOpenedFile expected,
            string relativeLeaf) => inner.CompareUnlink(expected, relativeLeaf);

        public Result<GrimoireOfflineTransitionJournalChildEnumeration> EnumerateExactChildren(
            IReadOnlyList<string> exactLeaves) =>
            throw new IOException("synthetic enumeration failure for catch-breadth pinning");

        public Result FlushWorking(GrimoireOfflineTransitionJournalOpenedFile file) =>
            inner.FlushWorking(file);

        public Result FlushParent() => inner.FlushParent();

        public void Dispose() => inner.Dispose();

    }

}
