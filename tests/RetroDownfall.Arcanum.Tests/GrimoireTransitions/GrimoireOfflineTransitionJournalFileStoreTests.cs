using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

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

        RecordingWindowsExchangeLayoutPrimitives? windowsLayout = null;

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

                windowsLayout = new RecordingWindowsExchangeLayoutPrimitives(opened.Value);

                return Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(
                    windowsLayout);

            });

        Assert.True((await updating.ReplaceDurablyAsync(
            held,
            location,
            Bytes("revision-two"),
            current.Metadata.Identity,
            CancellationToken.None)).IsSuccess);

        Assert.NotNull(windowsLayout);

        Assert.Equal(
            (location.JournalLeaf, location.WorkingLeaf, location.PreviousLeaf),
            windowsLayout.ExchangeArguments);

        Assert.True(windowsLayout.PostCallIdentitiesMatched);

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
    public async Task Retiring_substitution_during_the_delegated_window_is_detected_and_never_reported_absent()
    {

        await Compare_unlink_detects_a_substitution_in_the_delegated_unlink_window();

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
    public void Production_windows_layout_uses_retained_handle_acl_and_synchronous_streams()
    {

        string parent = Path.Combine(_container, "windows-layout-parent");

        GrimoireOfflineTransitionWindowsReplaceFileArguments layout =
            GrimoireOfflineTransitionJournalFilePrimitives.MapWindowsReplaceFileArguments(
                parent,
                "journal",
                "working",
                "previous");

        Assert.Equal(Path.Combine(parent, "journal"), layout.ReplacedFileName);

        Assert.Equal(Path.Combine(parent, "working"), layout.ReplacementFileName);

        Assert.Equal(Path.Combine(parent, "previous"), layout.BackupFileName);

        Assert.False(GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildStreamsAreAsync);

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs"));

        Assert.Contains(
            "MapWindowsReplaceFileArguments(_parentPath, journalLeaf, workingLeaf, previousLeaf)",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            "VerifyWindowsOwnerOnlyHandle(expected.Handle)",
            source,
            StringComparison.Ordinal);

        Assert.Contains("GetSecurityInfoWindows(", source, StringComparison.Ordinal);

        Assert.Contains(
            "isAsync: WindowsChildStreamsAreAsync",
            source,
            StringComparison.Ordinal);

        int permissionsStart = source.IndexOf(
            "public Result ApplyOwnerOnlyAndVerify(",
            StringComparison.Ordinal);

        int permissionsEnd = source.IndexOf(
            "public Result CompareUnlink(",
            permissionsStart,
            StringComparison.Ordinal);

        string permissionsMethod = source[permissionsStart..permissionsEnd];

        Assert.DoesNotContain("ChildPath(", permissionsMethod, StringComparison.Ordinal);

        Assert.Contains("OpenChild(", permissionsMethod, StringComparison.Ordinal);

    }

    [Fact]
    public void Windows_access_masks_and_replace_invocation_are_exact()
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

        GrimoireOfflineTransitionWindowsReplaceFileArguments mapping = new(
            "literal-J",
            "literal-W",
            "literal-P");

        (string Replaced, string Replacement, string Backup)? observed = null;

        bool invoked = GrimoireOfflineTransitionJournalFilePrimitives.InvokeWindowsReplaceFile(
            mapping,
            (replaced, replacement, backup, flags, exclude, reserved) =>
            {

                observed = (replaced, replacement, backup);

                Assert.Equal(0U, flags);

                Assert.Equal(IntPtr.Zero, exclude);

                Assert.Equal(IntPtr.Zero, reserved);

                return true;

            });

        Assert.True(invoked);

        Assert.Equal(
            ("literal-J", "literal-W", "literal-P"),
            observed);

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs"));

        string parentOpen = SourceMethod(
            source,
            "private static SafeFileHandle? OpenParentNoFollow(",
            "private SecureFileOpenStatus OpenChild(");

        Assert.Contains("WindowsParentDesiredAccess", parentOpen, StringComparison.Ordinal);

        Assert.Contains("WindowsParentShareMode", parentOpen, StringComparison.Ordinal);

        string childOpen = SourceMethod(
            source,
            "private unsafe SecureFileOpenStatus OpenWindowsChild(",
            "private List<string>? EnumerateNames(");

        Assert.Contains("WindowsChildReadDesiredAccess", childOpen, StringComparison.Ordinal);

        Assert.Contains("WindowsChildWritableDesiredAccess", childOpen, StringComparison.Ordinal);

        Assert.Contains("WindowsChildShareMode", childOpen, StringComparison.Ordinal);

        string exchange = SourceMethod(
            source,
            "public Result<GrimoireOfflineTransitionExchangeResult> ExchangeRetainingPrevious(",
            "internal static GrimoireOfflineTransitionWindowsReplaceFileArguments");

        Assert.Contains("InvokeWindowsReplaceFile(", exchange, StringComparison.Ordinal);

        Assert.DoesNotContain("ReplaceFileWindows(", exchange, StringComparison.Ordinal);

    }

    [Fact]
    public void Production_evidence_read_uses_handle_posture_without_path_owner_verification()
    {

        string root = FindRepositoryRoot();

        string store = File.ReadAllText(Path.Combine(
            root,
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs"));

        string primitives = File.ReadAllText(Path.Combine(
            root,
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs"));

        string readMethod = SourceMethod(
            store,
            "private static async Task<Result<GrimoireOfflineTransitionJournalFileRead>> ReadAsync(",
            "private async Task<bool> ProveResidueAbsentAsync(");

        Assert.Contains("requireOwnerControlled: false", readMethod, StringComparison.Ordinal);

        Assert.DoesNotContain("requireOwnerControlled: true", readMethod, StringComparison.Ordinal);

        Assert.Equal(
            2,
            CountOccurrences(
                readMethod,
                "VerifyOwnerControlledOpenedFileHandle(child)"));

        Assert.Contains(
            "primitives.EnumerateExactChildren([relativeLeaf])",
            readMethod,
            StringComparison.Ordinal);

        Assert.DoesNotContain("ChildPath(", readMethod, StringComparison.Ordinal);

        string handleVerifier = SourceMethod(
            primitives,
            "internal static bool VerifyOwnerControlledOpenedFileHandle(",
            "private static bool HasStrictOwnerOnlyParentHandlePosture(");

        Assert.Contains("VerifyWindowsOwnerOnlyHandle", handleVerifier, StringComparison.Ordinal);

        Assert.DoesNotContain("ChildPath(", handleVerifier, StringComparison.Ordinal);

    }

    [Fact]
    public void Production_never_creates_a_generic_arcanum_cleanup_quarantine()
    {

        string root = FindRepositoryRoot();

        string store = File.ReadAllText(Path.Combine(
            root,
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFileStore.cs"));

        string primitives = File.ReadAllText(Path.Combine(
            root,
            "src/RetroDownfall.Arcanum.Infrastructure/GrimoireTransitions/GrimoireOfflineTransitionJournalFilePrimitives.cs"));

        Assert.DoesNotContain("IdentityOwnedFileSystemCleanup", store, StringComparison.Ordinal);

        Assert.DoesNotContain("IdentityOwnedFileSystemCleanup", primitives, StringComparison.Ordinal);

        Assert.DoesNotContain(".arcanum-cleanup-", store, StringComparison.Ordinal);

        Assert.DoesNotContain(".arcanum-cleanup-", primitives, StringComparison.Ordinal);

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

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? cursor = new(AppContext.BaseDirectory);

        while (cursor is not null)
        {

            if (File.Exists(Path.Combine(cursor.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return cursor.FullName;

            }

            cursor = cursor.Parent;

        }

        throw new Xunit.Sdk.XunitException("The repository root could not be located.");

    }

    private static string SourceMethod(string source, string startMarker, string endMarker)
    {

        int start = source.IndexOf(startMarker, StringComparison.Ordinal);

        Assert.True(start >= 0, "The production start marker was not found: " + startMarker);

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);

        Assert.True(end > start, "The production end marker was not found: " + endMarker);

        return source[start..end];

    }

    private static int CountOccurrences(string source, string value)
    {

        int count = 0;

        int offset = 0;

        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {

            count++;

            offset += value.Length;

        }

        return count;

    }

    private sealed class RecordingWindowsExchangeLayoutPrimitives(
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

        public Result FlushParent() => inner.FlushParent();

        public void Dispose() => inner.Dispose();

    }

}
