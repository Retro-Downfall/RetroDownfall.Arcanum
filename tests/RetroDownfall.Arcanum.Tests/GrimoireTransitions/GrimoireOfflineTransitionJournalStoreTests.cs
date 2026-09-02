using System.Reflection;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

public sealed class GrimoireOfflineTransitionJournalStoreTests : IDisposable
{

    private static readonly Guid Installation =
        Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Operation =
        Guid.Parse("22222222-2222-4222-8222-222222222222");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-offline-transition-store-" + Guid.NewGuid().ToString("N"));

    private readonly string _guarded;

    private readonly InMemoryOsCredentialStore _credentials = new();

    private readonly ArcanumMaintenanceLock _lock;

    public GrimoireOfflineTransitionJournalStoreTests()
    {

        Directory.CreateDirectory(_root);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                _root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

        _guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(_guarded);

        _lock = ArcanumMaintenanceLock.TryAcquire(_guarded)
            ?? throw new InvalidOperationException("The test could not take its maintenance lock.");

    }

    public void Dispose()
    {

        _lock.Dispose();

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Begin_provisions_closed_genesis_then_active_zero_before_file_revision_one()
    {

        List<string> events = [];

        GrimoireOfflineTransitionJournalStore store = ReadyStore(events);

        GrimoireOfflineTransitionJournalPublication publication = Value(await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            Bytes("first"),
            CancellationToken.None));

        Assert.Equal(1UL, publication.Envelope.SlotEpoch);

        Assert.Equal(1UL, publication.Envelope.Revision);

        Assert.Equal(ZeroDigest(), publication.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Active, publication.Anchor.State);

        Assert.Equal(1UL, publication.Anchor.SlotEpoch);

        Assert.Equal(1UL, publication.Anchor.Revision);

        Assert.Equal(publication.EnvelopeDigest, publication.Anchor.EnvelopeDigest);

        Assert.Equal(
            (string[])
            [
                "key:read-or-created",
                "anchor:genesis-written",
                "anchor:genesis-readback",
                "anchor:opening-written",
                "anchor:opening-readback",
                "file:temporary-created",
                "file:temporary-written",
                "file:temporary-flushed",
                "file:atomic-replace",
                "file:permissions-verified",
                "file:parent-flushed",
                "file:secure-reread",
                "anchor:advance-written",
                "anchor:advance-readback",
            ],
            events);

    }

    [Fact]
    public async Task Begin_propagates_a_post_genesis_anchor_reread_failure()
    {

        SeedIdentity(Installation);

        GrimoireOfflineTransitionJournalLocation location = Location();

        ArmedPrefixThrowingCredentialStore anchorCredentials = new(
            _credentials,
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccountPrefix);

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(
            anchorCredentials,
            afterStep: step =>
            {

                if (step == "anchor:genesis-readback")
                {

                    anchorCredentials.Arm();

                }

            });

        GrimoireOfflineTransitionJournalStore probing = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(),
            anchors);

        Result<GrimoireOfflineTransitionJournalPublication> result = await probing.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

        GrimoireOfflineTransitionAnchorV1 genesis = Assert.IsType<
            GrimoireOfflineTransitionAnchorV1>(
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, genesis.State);

        Assert.Equal(0UL, genesis.SlotEpoch);

        Assert.False(File.Exists(location.JournalPath));

    }

    [Fact]
    public async Task Begin_requires_external_installation_identity_to_match_the_database_identity()
    {

        GrimoireOfflineTransitionJournalStore store = Store();

        SeedIdentity(Installation);

        Result<GrimoireOfflineTransitionJournalPublication> result = await store.BeginAsync(
            _lock,
            _guarded,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);

        GrimoireOfflineTransitionJournalLocation location = Location();

        Assert.Null(Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

        Assert.False(File.Exists(location.JournalPath));

    }

    [Fact]
    public async Task Begin_publishes_file_then_secure_reread_then_anchor_revision_one()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication publication = await BeginAsync(store);

        byte[] expected = Value(
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(publication.Envelope));

        Assert.Equal(expected, File.ReadAllBytes(publication.Location.JournalPath));

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            publication.Location.JournalPath,
            out FileHandleMetadata metadata));

        Assert.Equal(metadata.Identity, publication.FileMetadata.Identity);

        Assert.Equal(
            publication.Anchor,
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(
                publication.Location)));

    }

    [Fact]
    public async Task Begin_active_exact_same_operation_resumes_only_byte_identical_payload()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        GrimoireOfflineTransitionJournalPublication resumed = await BeginAsync(store);

        Assert.Equal(first.Anchor, resumed.Anchor);

        Assert.Equal(first.EnvelopeDigest, resumed.EnvelopeDigest);

        Assert.Equal(first.FileMetadata.Identity, resumed.FileMetadata.Identity);

        byte[] before = File.ReadAllBytes(first.Location.JournalPath);

        Result<GrimoireOfflineTransitionJournalPublication> changed = await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("different"),
            CancellationToken.None);

        Assert.True(changed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, changed.Error.Code);

        Assert.Equal(before, File.ReadAllBytes(first.Location.JournalPath));

    }

    [Fact]
    public async Task Begin_active_different_operation_conflicts_without_mutation()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        byte[] before = File.ReadAllBytes(first.Location.JournalPath);

        Result<GrimoireOfflineTransitionJournalPublication> result = await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, result.Error.Code);

        Assert.Equal(first.Anchor, Value(new GrimoireOfflineTransitionJournalAnchorStore(
            _credentials).Read(first.Location)));

        Assert.Equal(before, File.ReadAllBytes(first.Location.JournalPath));

    }

    [Fact]
    public async Task Begin_closed_same_operation_never_reopens()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(_credentials);

        GrimoireOfflineTransitionAnchorV1 closed = first.Anchor with
        {
            State = GrimoireOfflineTransitionAnchorState.Closed,
        };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            first.Location,
            first.Anchor,
            closed,
            GrimoireOfflineTransitionAnchorWriteStage.Closed).IsSuccess);

        Result<GrimoireOfflineTransitionJournalPublication> result = await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, result.Error.Code);

        Assert.Equal(closed, Value(anchors.Read(first.Location)));

    }

    [Fact]
    public async Task Begin_closed_epoch_opens_only_next_epoch_for_a_different_operation()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(_credentials);

        GrimoireOfflineTransitionAnchorV1 closed = first.Anchor with
        {
            State = GrimoireOfflineTransitionAnchorState.Closed,
        };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            first.Location,
            first.Anchor,
            closed,
            GrimoireOfflineTransitionAnchorWriteStage.Closed).IsSuccess);

        byte[] encoded = Value(
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(first.Envelope));

        Assert.True(new GrimoireOfflineTransitionJournalFileStore().DeleteDurably(
            _lock,
            first.Location,
            first.FileMetadata,
            encoded).IsSuccess);

        Guid nextOperation = Guid.Parse("55555555-5555-4555-8555-555555555555");

        GrimoireOfflineTransitionJournalPublication next = Value(await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            nextOperation,
            GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            2,
            Bytes("next"),
            CancellationToken.None));

        Assert.Equal(2UL, next.Anchor.SlotEpoch);

        Assert.Equal(nextOperation, next.Anchor.OperationId);

        Assert.Equal(GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure, next.Anchor.Kind);

        Assert.Equal((byte)2, next.Anchor.PayloadVersion);

    }

    [Fact]
    public async Task Advance_keeps_epoch_operation_kind_payload_version_and_chains_previous_digest()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        GrimoireOfflineTransitionJournalPublication second = Value(await store.AdvanceAsync(
            _lock,
            first,
            Bytes("second"),
            CancellationToken.None));

        Assert.Equal(first.Envelope.SlotEpoch, second.Envelope.SlotEpoch);

        Assert.Equal(first.Envelope.OperationId, second.Envelope.OperationId);

        Assert.Equal(first.Envelope.Kind, second.Envelope.Kind);

        Assert.Equal(first.Envelope.PayloadVersion, second.Envelope.PayloadVersion);

        Assert.Equal(first.Envelope.Revision + 1, second.Envelope.Revision);

        Assert.Equal(first.EnvelopeDigest, second.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(second.EnvelopeDigest, second.Anchor.EnvelopeDigest);

        Assert.Equal(Bytes("second").ToArray(), second.PayloadBytes);

    }

    [Fact]
    public async Task Advance_compares_current_file_identity_and_anchor_before_writing()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        byte[] before = File.ReadAllBytes(first.Location.JournalPath);

        GrimoireOfflineTransitionJournalPublication wrongIdentity = first with
        {
            FileMetadata = first.FileMetadata with
            {
                Identity = new FileHandleIdentity(
                    first.FileMetadata.Identity.VolumeId,
                    first.FileMetadata.Identity.FileId + 1),
            },
        };

        Assert.True((await store.AdvanceAsync(
            _lock,
            wrongIdentity,
            Bytes("second"),
            CancellationToken.None)).IsFailure);

        GrimoireOfflineTransitionJournalPublication wrongAnchor = first with
        {
            Anchor = first.Anchor with { Revision = 0, EnvelopeDigest = null },
        };

        Assert.True((await store.AdvanceAsync(
            _lock,
            wrongAnchor,
            Bytes("second"),
            CancellationToken.None)).IsFailure);

        Assert.Equal(before, File.ReadAllBytes(first.Location.JournalPath));

    }

    [Fact]
    public async Task Advance_propagates_an_anchor_read_failure_instead_of_a_revision_conflict()
    {

        GrimoireOfflineTransitionJournalStore setup = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(setup);

        PrefixThrowingCredentialStore anchorUnavailable = new(
            _credentials,
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccountPrefix);

        GrimoireOfflineTransitionJournalStore probing = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(anchorUnavailable));

        Result<GrimoireOfflineTransitionJournalPublication> result = await probing.AdvanceAsync(
            _lock,
            current,
            Bytes("second"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

    }

    [Fact]
    public async Task Advance_propagates_an_unavailable_key_during_payload_verification()
    {

        GrimoireOfflineTransitionJournalStore setup = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(setup);

        PrefixThrowingCredentialStore keyUnavailable = new(
            _credentials,
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccountPrefix);

        GrimoireOfflineTransitionJournalStore probing = new(
            keyUnavailable,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Result<GrimoireOfflineTransitionJournalPublication> result = await probing.AdvanceAsync(
            _lock,
            current,
            Bytes("second"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

    }

    [Fact]
    public async Task Advance_requires_external_installation_identity_to_still_match()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(store);

        byte[] before = File.ReadAllBytes(first.Location.JournalPath);

        string account = ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(
            first.Location.ProfileNamespace.AccountSuffix);

        Guid drifted = Guid.Parse("66666666-6666-4666-8666-666666666666");

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(
                ArcanumCredentialIdentity.Service,
                account,
                drifted.ToString("D").ToUpperInvariant()).Status);

        Result<GrimoireOfflineTransitionJournalPublication> result = await store.AdvanceAsync(
            _lock,
            first,
            Bytes("second"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);

        Assert.Equal(before, File.ReadAllBytes(first.Location.JournalPath));

        Assert.Equal(
            first.Anchor,
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(
                first.Location)));

    }

    [Fact]
    public async Task Advance_post_atomic_replace_failure_is_recovery_required_with_old_anchor()
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalFileStore postRenameFiles = new(
            failBeforeStep: step => step == "file:permissions-verified");

        async Task<Result> ReplaceThenMaskFailure(
            ArcanumMaintenanceLock heldInstallationLock,
            GrimoireOfflineTransitionJournalLocation location,
            ReadOnlyMemory<byte> bytes,
            FileHandleIdentity? expectedCurrentIdentity,
            CancellationToken cancellationToken)
        {

            Result replaced = await postRenameFiles.ReplaceDurablyAsync(
                heldInstallationLock,
                location,
                bytes,
                expectedCurrentIdentity,
                cancellationToken);

            return replaced.IsFailure
                ? new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "The injected lower layer hid its post-rename classification.")
                : replaced;

        }

        GrimoireOfflineTransitionJournalStore advancing = new(
            _credentials,
            postRenameFiles,
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials),
            afterStep: null,
            replaceDurably: ReplaceThenMaskFailure);

        Result<GrimoireOfflineTransitionJournalPublication> result = await advancing.AdvanceAsync(
            _lock,
            first,
            Bytes("second"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.Error.Code);

        Assert.Equal(
            first.Anchor,
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(
                first.Location)));

        GrimoireOfflineTransitionEnvelopeV1 oneAhead = Value(
            GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                File.ReadAllBytes(first.Location.JournalPath)));

        Assert.Equal(first.Envelope.Revision + 1, oneAhead.Revision);

        Assert.Equal(first.EnvelopeDigest, oneAhead.PreviousEnvelopeDigest);

    }

    [Fact]
    public async Task Failure_before_first_file_publication_compare_closes_only_the_exact_opening()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore(
            failBeforeStep: step => step == "file:atomic-replace");

        Result<GrimoireOfflineTransitionJournalPublication> result = await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        GrimoireOfflineTransitionJournalLocation location = Location();

        GrimoireOfflineTransitionAnchorV1 anchor = Assert.IsType<
            GrimoireOfflineTransitionAnchorV1>(
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, anchor.State);

        Assert.Equal(Operation, anchor.OperationId);

        Assert.Equal(0UL, anchor.Revision);

        Assert.Null(anchor.EnvelopeDigest);

        Assert.False(File.Exists(location.JournalPath));

    }

    [Fact]
    public async Task Failure_after_atomic_replace_preserves_active_authority_for_recovery()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore(
            failBeforeStep: step => step == "file:permissions-verified");

        Result<GrimoireOfflineTransitionJournalPublication> result = await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, result.Error.Code);

        GrimoireOfflineTransitionJournalLocation location = Location();

        GrimoireOfflineTransitionAnchorV1 anchor = Assert.IsType<
            GrimoireOfflineTransitionAnchorV1>(
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Active, anchor.State);

        Assert.Equal(0UL, anchor.Revision);

        Assert.True(File.Exists(location.JournalPath));

    }

    [Fact]
    public void Anchor_writes_are_read_compare_write_readback_under_the_borrowed_lock()
    {

        List<string> events = [];

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(_credentials, events.Add);

        GrimoireOfflineTransitionJournalLocation location = Location();

        Assert.True(anchors.WriteGenesisAndVerify(_lock, location, Installation).IsSuccess);

        GrimoireOfflineTransitionAnchorV1 genesis = Assert.IsType<
            GrimoireOfflineTransitionAnchorV1>(Value(anchors.Read(location)));

        GrimoireOfflineTransitionAnchorV1 opening = genesis with
        {
            SlotEpoch = 1,
            State = GrimoireOfflineTransitionAnchorState.Active,
            OperationId = Operation,
            Kind = GrimoireOfflineTransitionKind.CovenantReset,
            PayloadVersion = 1,
        };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            location,
            genesis,
            opening,
            GrimoireOfflineTransitionAnchorWriteStage.Opening).IsSuccess);

        Assert.Equal(
            (string[])
            [
                "anchor:genesis-written",
                "anchor:genesis-readback",
                "anchor:opening-written",
                "anchor:opening-readback",
            ],
            events);

        GrimoireOfflineTransitionAnchorV1 wrongExpected = genesis with { SlotEpoch = 7 };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            location,
            wrongExpected,
            genesis,
            GrimoireOfflineTransitionAnchorWriteStage.Closed).IsFailure);

        Assert.Equal(opening, Value(anchors.Read(location)));

    }

    [Fact]
    public async Task Recover_returns_no_active_only_for_proven_anchor_and_file_absence()
    {

        SeedIdentity(Installation);

        GrimoireOfflineTransitionJournalRecoveryState recovered = Value(await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None));

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal, recovered.Outcome);

        Assert.Null(recovered.Publication);

    }

    [Fact]
    public async Task Recover_refuses_an_absent_anchor_when_the_profile_journal_key_is_present()
    {

        GrimoireOfflineTransitionJournalLocation location = Location();

        GrimoireOfflineTransitionJournalKeyProvider keys = new(_credentials);

        using (Value(keys.CreateOrOpen(_lock, _guarded, location.ProfileNamespace)))
        {

        }

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered = await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.True(recovered.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recovered.Error.Code);

        Assert.Null(Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

    }

    [Fact]
    public async Task Recover_accepts_an_exact_anchor_file_match()
    {

        GrimoireOfflineTransitionJournalPublication published = await BeginAsync(ReadyStore());

        GrimoireOfflineTransitionJournalRecoveryState recovered = Value(await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None));

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated, recovered.Outcome);

        Assert.Equal(published.Anchor, recovered.Publication?.Anchor);

        Assert.Equal(published.FileMetadata.Identity, recovered.Publication?.FileMetadata.Identity);

    }

    [Fact]
    public async Task Recover_adopts_exactly_one_chained_file_revision_ahead_before_returning()
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalFileStore files = new(
            failBeforeStep: step => step == "file:permissions-verified");

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            files,
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.True((await interrupted.AdvanceAsync(
            _lock,
            current,
            Bytes("second"),
            CancellationToken.None)).IsFailure);

        GrimoireOfflineTransitionJournalFileStore observer = new();

        using (GrimoireOfflineTransitionJournalEvidence exchanged = Value(
                   await observer.InspectEvidenceAsync(current.Location, CancellationToken.None)))
        {

            Assert.NotNull(exchanged.Canonical);

            Assert.Null(exchanged.Working);

            Assert.NotNull(exchanged.Previous);

            GrimoireOfflineTransitionEnvelopeV1 canonical = Value(
                GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                    exchanged.Canonical!.Bytes.Span));

            GrimoireOfflineTransitionEnvelopeV1 predecessor = Value(
                GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                    exchanged.Previous!.Bytes.Span));

            Assert.Equal(current.Envelope.Revision + 1, canonical.Revision);

            Assert.Equal(current.EnvelopeDigest, canonical.PreviousEnvelopeDigest);

            Assert.Equal(current.Envelope, predecessor);

        }

        File.Move(current.Location.PreviousPath, current.Location.WorkingPath);

        using (GrimoireOfflineTransitionJournalEvidence working = Value(
                   await observer.InspectEvidenceAsync(current.Location, CancellationToken.None)))
        {

            Assert.NotNull(working.Canonical);

            Assert.NotNull(working.Working);

            Assert.Null(working.Previous);

            Assert.Null(working.Retiring);

        }

        int workingNormalized = 0;

        GrimoireOfflineTransitionJournalFileStore normalizingFiles = new(
            afterStep: step =>
            {

                if (step != "file:working-normalized")
                {

                    return;

                }

                workingNormalized++;

                using GrimoireOfflineTransitionJournalEvidence normalized = Value(new GrimoireOfflineTransitionJournalFileStore()
                    .InspectEvidenceAsync(current.Location, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());

                Assert.NotNull(normalized.Canonical);

                Assert.Null(normalized.Working);

                Assert.NotNull(normalized.Previous);

                Assert.Null(normalized.Retiring);

                GrimoireOfflineTransitionEnvelopeV1 canonical = Value(
                    GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                        normalized.Canonical!.Bytes.Span));

                GrimoireOfflineTransitionEnvelopeV1 predecessor = Value(
                    GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                        normalized.Previous!.Bytes.Span));

                Assert.Equal(current.Envelope.Revision + 1, canonical.Revision);

                Assert.Equal(current.EnvelopeDigest, canonical.PreviousEnvelopeDigest);

                Assert.Equal(current.Envelope, predecessor);

                Assert.Equal(current.FileMetadata.Identity, normalized.Previous.Metadata.Identity);

            });

        GrimoireOfflineTransitionJournalStore recovering = new(
            _credentials,
            normalizingFiles,
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        GrimoireOfflineTransitionJournalRecoveryState recovered = Value(await recovering.RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None));

        Assert.Equal(1, workingNormalized);

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated, recovered.Outcome);

        Assert.Equal(2UL, recovered.Publication?.Anchor.Revision);

    }

    [Theory]
    [InlineData("older")]
    [InlineData("skipped")]
    [InlineData("same-revision-resealed")]
    [InlineData("two-ahead")]
    public async Task Recover_rejects_older_skipped_same_revision_resealed_and_two_ahead_files(
        string mismatch)
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(store);

        GrimoireOfflineTransitionJournalPublication expected = current;

        GrimoireOfflineTransitionEnvelopeV1 candidate;

        switch (mismatch)
        {
            case "older":
                expected = Value(await store.AdvanceAsync(
                    _lock,
                    current,
                    Bytes("second"),
                    CancellationToken.None));

                candidate = current.Envelope;

                break;

            case "skipped":
                candidate = SealForTest(
                    current.Location,
                    current.Envelope,
                    revision: 2,
                    previousDigest: ZeroDigest(),
                    payload: Bytes("skipped"));

                break;

            case "same-revision-resealed":
                candidate = SealForTest(
                    current.Location,
                    current.Envelope,
                    revision: current.Envelope.Revision,
                    previousDigest: current.Envelope.PreviousEnvelopeDigest,
                    payload: current.PayloadBytes);

                Assert.NotEqual(
                    current.EnvelopeDigest,
                    Value(GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(candidate)));

                break;

            case "two-ahead":
                candidate = SealForTest(
                    current.Location,
                    current.Envelope,
                    revision: 3,
                    previousDigest: current.EnvelopeDigest,
                    payload: Bytes("two-ahead"));

                break;

            default:
                throw new Xunit.Sdk.XunitException(mismatch);
        }

        byte[] bytes = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(candidate));

        AssertAuthentic(current.Location, candidate);

        WriteOwnerOnly(current.Location.JournalPath, bytes);

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered = await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.True(recovered.IsFailure, mismatch);

        Assert.Equal(
            expected.Anchor,
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(current.Location)));

        Assert.Equal(bytes, File.ReadAllBytes(current.Location.JournalPath));

    }

    [Theory]
    [InlineData("profile")]
    [InlineData("installation")]
    [InlineData("epoch")]
    [InlineData("operation")]
    [InlineData("kind")]
    [InlineData("payload-version")]
    [InlineData("location")]
    public async Task Recover_rejects_cross_profile_installation_epoch_operation_kind_payload_version_and_location(
        string binding)
    {

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(ReadyStore());

        CovenantDigest profile = current.Envelope.ProfileNamespaceDigest;

        Guid installation = current.Envelope.InstallationId;

        ulong epoch = current.Envelope.SlotEpoch;

        Guid operation = current.Envelope.OperationId;

        GrimoireOfflineTransitionKind kind = current.Envelope.Kind;

        byte payloadVersion = current.Envelope.PayloadVersion;

        CovenantDigest location = current.Envelope.JournalLocationDigest;

        switch (binding)
        {
            case "profile":
                profile = Digest(17);

                break;

            case "installation":
                installation = Guid.Parse("33333333-3333-4333-8333-333333333333");

                break;

            case "epoch":
                epoch++;

                break;

            case "operation":
                operation = Guid.Parse("44444444-4444-4444-8444-444444444444");

                break;

            case "kind":
                kind = GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure;

                break;

            case "payload-version":
                payloadVersion++;

                break;

            case "location":
                location = Digest(18);

                break;

            default:
                throw new Xunit.Sdk.XunitException(binding);
        }

        GrimoireOfflineTransitionEnvelopeV1 candidate = SealForTest(
            current.Location,
            current.Envelope,
            profile,
            installation,
            epoch,
            operation,
            kind,
            payloadVersion,
            current.Envelope.Revision,
            current.Envelope.PreviousEnvelopeDigest,
            location,
            current.PayloadBytes);

        byte[] bytes = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(candidate));

        AssertAuthentic(current.Location, candidate);

        WriteOwnerOnly(current.Location.JournalPath, bytes);

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered = await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.True(recovered.IsFailure, binding);

        Assert.Equal(
            current.Anchor,
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(current.Location)));

        Assert.Equal(bytes, File.ReadAllBytes(current.Location.JournalPath));

    }

    [Fact]
    public async Task Recover_rejects_active_revision_zero_without_a_file()
    {

        _ = ReadyStore();

        GrimoireOfflineTransitionJournalLocation location = Location();

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(_credentials);

        Assert.True(anchors.WriteGenesisAndVerify(_lock, location, Installation).IsSuccess);

        GrimoireOfflineTransitionAnchorV1 genesis = Assert.IsType<
            GrimoireOfflineTransitionAnchorV1>(Value(anchors.Read(location)));

        GrimoireOfflineTransitionAnchorV1 opening = genesis with
        {
            SlotEpoch = 1,
            State = GrimoireOfflineTransitionAnchorState.Active,
            OperationId = Operation,
            Kind = GrimoireOfflineTransitionKind.CovenantReset,
            PayloadVersion = 1,
        };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            location,
            genesis,
            opening,
            GrimoireOfflineTransitionAnchorWriteStage.Opening).IsSuccess);

        Assert.True((await Store().RecoverAsync(_lock, _guarded, CancellationToken.None)).IsFailure);

    }

    [Theory]
    [InlineData("active", "key")]
    [InlineData("active", "identity")]
    [InlineData("closed", "key")]
    [InlineData("closed", "identity")]
    public async Task Recover_rejects_missing_key_or_identity_beside_active_or_closed_evidence(
        string state,
        string credential)
    {

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(ReadyStore());

        GrimoireOfflineTransitionAnchorV1 expectedAnchor = current.Anchor;

        if (state is "closed")
        {

            GrimoireOfflineTransitionAnchorV1 closed = current.Anchor with
            {
                State = GrimoireOfflineTransitionAnchorState.Closed,
            };

            Assert.True(new GrimoireOfflineTransitionJournalAnchorStore(_credentials)
                .CompareWriteAndVerify(
                    _lock,
                    current.Location,
                    current.Anchor,
                    closed,
                    GrimoireOfflineTransitionAnchorWriteStage.Closed).IsSuccess);

            expectedAnchor = closed;

        }

        string account = credential is "key"
            ? ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccount(
                current.Location.ProfileNamespace.AccountSuffix)
            : ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(
                current.Location.ProfileNamespace.AccountSuffix);

        Assert.Equal(OsCredentialStoreStatus.Ok, _credentials.Delete(
            ArcanumCredentialIdentity.Service,
            account).Status);

        byte[] canonical = File.ReadAllBytes(current.Location.JournalPath);

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered = await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.True(recovered.IsFailure, state + ":" + credential);

        Assert.Equal(
            expectedAnchor,
            Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(current.Location)));

        Assert.Equal(canonical, File.ReadAllBytes(current.Location.JournalPath));

    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("case-alias")]
    [InlineData("stale-temp")]
    [InlineData("unknown-residue")]
    [InlineData("multiple-evidence")]
    public async Task Recover_rejects_unanchored_file_case_alias_stale_temp_unknown_residue_and_multiple_evidence(
        string topology)
    {

        SeedIdentity(Installation);

        GrimoireOfflineTransitionJournalLocation location = Location();

        GrimoireOfflineTransitionEnvelopeV1 envelope = CreateUnanchoredEnvelope(location);

        byte[] bytes = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(envelope));

        AssertAuthentic(location, envelope);

        string fixedParent = Path.GetDirectoryName(location.JournalPath)
            ?? throw new Xunit.Sdk.XunitException("The fixed journal parent was unavailable.");

        string caseAliasLeaf = location.JournalLeaf.ToUpperInvariant();

        string evidencePath = topology switch
        {
            "canonical" => location.JournalPath,
            "case-alias" => Path.Combine(fixedParent, caseAliasLeaf),
            "stale-temp" => location.JournalPath + ".tmp.interrupted",
            "unknown-residue" => location.JournalPath + ".unexpected",
            "multiple-evidence" => location.JournalPath,
            _ => throw new Xunit.Sdk.XunitException(topology),
        };

        WriteOwnerOnly(evidencePath, bytes);

        if (topology is "case-alias")
        {

            Assert.False(string.Equals(
                location.JournalLeaf,
                caseAliasLeaf,
                StringComparison.Ordinal));

            Assert.Contains(
                caseAliasLeaf,
                Directory.EnumerateFileSystemEntries(fixedParent)
                    .Select(Path.GetFileName),
                StringComparer.Ordinal);

        }

        if (topology is "multiple-evidence")
        {

            WriteOwnerOnly(location.PreviousPath, bytes);

        }

        Assert.True((await Store().RecoverAsync(_lock, _guarded, CancellationToken.None)).IsFailure, topology);

        Assert.Null(Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

        Assert.True(File.Exists(evidencePath));

        if (topology is "multiple-evidence")
        {

            Assert.True(File.Exists(location.PreviousPath));

        }

    }

    [Theory]
    [InlineData("previous")]
    [InlineData("working")]
    public async Task Recover_converges_exchange_crashes_with_exact_working_or_previous_predecessor(
        string predecessorState)
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalStore failing = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(
                failBeforeStep: step => step == "file:previous-retiring"),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.True((await failing.AdvanceAsync(
            _lock,
            current,
            Bytes("second"),
            CancellationToken.None)).IsFailure);

        if (predecessorState is "working")
        {

            Assert.True(File.Exists(current.Location.PreviousPath));

            File.Move(current.Location.PreviousPath, current.Location.WorkingPath);

        }

        using (GrimoireOfflineTransitionJournalEvidence evidence = Value(
                   await new GrimoireOfflineTransitionJournalFileStore().InspectEvidenceAsync(
                       current.Location,
                       CancellationToken.None)))
        {

            Assert.NotNull(evidence.Canonical);

            Assert.Equal(predecessorState is "working", evidence.Working is not null);

            Assert.Equal(predecessorState is "previous", evidence.Previous is not null);

            Assert.Null(evidence.Retiring);

        }

        GrimoireOfflineTransitionJournalRecoveryState recovered = Value(await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None));

        Assert.Equal(
            GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated,
            recovered.Outcome);

        Assert.Equal(2UL, recovered.Publication?.Anchor.Revision);

        Assert.False(File.Exists(current.Location.WorkingPath));

        Assert.False(File.Exists(current.Location.PreviousPath));

        Assert.False(File.Exists(current.Location.RetiringPath));

    }

    [Fact]
    public async Task Recover_finishes_exact_predecessor_retirement_before_adopting_one_ahead()
    {

        await Recover_converges_exchange_crashes_with_exact_working_or_previous_predecessor("previous");

        GrimoireOfflineTransitionJournalLocation location = Location();

        Assert.False(File.Exists(location.PreviousPath));

        Assert.False(File.Exists(location.RetiringPath));

    }

    [Fact]
    public async Task Recover_propagates_an_unavailable_key_during_predecessor_authentication()
    {

        GrimoireOfflineTransitionJournalStore setup = ReadyStore();

        GrimoireOfflineTransitionJournalPublication first = await BeginAsync(setup);

        GrimoireOfflineTransitionJournalPublication second = Value(await setup.AdvanceAsync(
            _lock,
            first,
            Bytes("second"),
            CancellationToken.None));

        byte[] predecessorBytes = Value(
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(first.Envelope));

        WriteOwnerOnly(second.Location.PreviousPath, predecessorBytes);

        // The canonical file (second, revision 2) already matches the anchor exactly, so
        // RecoverAsync reads the key twice before reaching IsExactPredecessor's own Open call:
        // once for the early explicit key-presence check, once to authenticate the canonical
        // file. The third matching read is IsExactPredecessor's -- the one under test.
        CountedPrefixThrowingCredentialStore keyUnavailableOnThirdRead = new(
            _credentials,
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccountPrefix,
            throwOnOccurrence: 3);

        GrimoireOfflineTransitionJournalStore probing = new(
            keyUnavailableOnThirdRead,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered = await probing.RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.True(recovered.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, recovered.Error.Code);

    }

    [Fact]
    public async Task Recover_normalizes_post_exchange_working_predecessor_before_adopting_one_ahead()
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(
                failBeforeStep: step => step == "file:previous-retained"),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.True((await interrupted.AdvanceAsync(
            _lock,
            current,
            Bytes("second"),
            CancellationToken.None)).IsFailure);

        GrimoireOfflineTransitionJournalRecoveryState recovered = Value(await Store().RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None));

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated, recovered.Outcome);

        Assert.Equal(2UL, recovered.Publication?.Anchor.Revision);

        Assert.False(File.Exists(current.Location.WorkingPath));

        Assert.False(File.Exists(current.Location.PreviousPath));

        Assert.False(File.Exists(current.Location.RetiringPath));

    }

    [Fact]
    public async Task Recover_revalidates_canonical_identity_and_bytes_after_predecessor_cleanup()
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(
                failBeforeStep: step => step == "file:previous-retiring"),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.True((await interrupted.AdvanceAsync(
            _lock,
            current,
            Bytes("second"),
            CancellationToken.None)).IsFailure);

        int substitutions = 0;

        bool substitutionTopologyProved = false;

        FileHandleIdentity oneAheadIdentity;

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            current.Location.JournalPath,
            out FileHandleMetadata oneAheadMetadata));

        oneAheadIdentity = oneAheadMetadata.Identity;

        GrimoireOfflineTransitionJournalFileStore substituting = new(
            failBeforeStep: step =>
            {

                if (step != "file:retiring-moved")
                {

                    return false;

                }

                string preserved = Path.Combine(_guarded, "preserved-one-ahead-journal");

                Interlocked.Increment(ref substitutions);

                File.Move(current.Location.JournalPath, preserved);

                File.WriteAllBytes(current.Location.JournalPath, Bytes("substituted").ToArray());

                if (!OperatingSystem.IsWindows())
                {

                    File.SetUnixFileMode(
                        current.Location.JournalPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);

                }

                substitutionTopologyProved = File.Exists(current.Location.JournalPath)
                    && File.Exists(current.Location.PreviousPath)
                    && !File.Exists(current.Location.WorkingPath)
                    && !File.Exists(current.Location.RetiringPath);

                return false;

            });

        GrimoireOfflineTransitionJournalStore recovering = new(
            _credentials,
            substituting,
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered = await recovering.RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.Equal(1, substitutions);

        Assert.True(substitutionTopologyProved);

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            current.Location.JournalPath,
            out FileHandleMetadata substitutedMetadata));

        Assert.False(FileHandleIdentity.IdentitiesMatch(
            oneAheadIdentity,
            substitutedMetadata.Identity));

        Assert.True(recovered.IsFailure);

    }

    [Theory]
    [InlineData("begin")]
    [InlineData("recover")]
    public async Task Begin_and_recover_refuse_a_closed_genesis_while_the_key_still_exists(
        string entryPoint)
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(store);

        Assert.True((await store.RetireAsync(_lock, terminal, CancellationToken.None)).IsSuccess);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Delete(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccount(
                    terminal.Location.ProfileNamespace.AccountSuffix)).Status);

        GrimoireOfflineTransitionJournalLocation location = terminal.Location;

        Result outcome = entryPoint is "begin"
            ? await store.BeginAsync(
                _lock,
                _guarded,
                Installation,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                1,
                Bytes("reset"),
                CancellationToken.None)
            : await store.RecoverAsync(_lock, _guarded, CancellationToken.None);

        Assert.True(outcome.IsFailure, entryPoint);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, outcome.Error.Code);

        Assert.Null(Value(new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location)));

        Assert.False(File.Exists(location.JournalPath));

    }

    [Theory]
    [InlineData("begin")]
    [InlineData("recover")]
    [InlineData("retire")]
    public async Task Begin_recover_and_retire_propagate_an_unavailable_key_credential_store(
        string entryPoint)
    {

        GrimoireOfflineTransitionJournalStore setup = ReadyStore();

        GrimoireOfflineTransitionJournalPublication current = await BeginAsync(setup);

        if (entryPoint is "begin")
        {

            Assert.True((await setup.RetireAsync(_lock, current, CancellationToken.None)).IsSuccess);

        }

        PrefixThrowingCredentialStore keyUnavailable = new(
            _credentials,
            ArcanumCredentialIdentity.GrimoireTransitionJournalKeyAccountPrefix);

        GrimoireOfflineTransitionJournalStore probing = new(
            keyUnavailable,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Guid nextOperation = Guid.Parse("77777777-7777-4777-8777-777777777777");

        Result outcome = entryPoint switch
        {
            "begin" => await probing.BeginAsync(
                _lock,
                _guarded,
                Installation,
                nextOperation,
                GrimoireOfflineTransitionKind.CovenantReset,
                1,
                Bytes("next"),
                CancellationToken.None),
            "recover" => await probing.RecoverAsync(_lock, _guarded, CancellationToken.None),
            "retire" => await probing.RetireAsync(_lock, current, CancellationToken.None),
            _ => throw new Xunit.Sdk.XunitException(entryPoint),
        };

        Assert.True(outcome.IsFailure, entryPoint);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, outcome.Error.Code);

    }

    [Fact]
    public async Task Retire_writes_and_reads_closed_anchor_before_deleting_the_file()
    {

        List<string> events = [];

        GrimoireOfflineTransitionJournalStore store = ReadyStore(events);

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(store);

        Assert.True((await store.RetireAsync(_lock, terminal, CancellationToken.None)).IsSuccess);

        Assert.Contains("anchor:closed-written", events);

        Assert.Contains("anchor:closed-readback", events);

        Assert.False(File.Exists(terminal.Location.JournalPath));

    }

    [Fact]
    public async Task Retire_propagates_an_anchor_read_failure()
    {

        GrimoireOfflineTransitionJournalStore setup = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(setup);

        PrefixThrowingCredentialStore anchorUnavailable = new(
            _credentials,
            ArcanumCredentialIdentity.GrimoireTransitionJournalAnchorAccountPrefix);

        GrimoireOfflineTransitionJournalStore probing = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(anchorUnavailable));

        Result result = await probing.RetireAsync(_lock, terminal, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.Error.Code);

        byte[] canonical = Value(
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(terminal.Envelope));

        Assert.Equal(canonical, File.ReadAllBytes(terminal.Location.JournalPath));

    }

    [Fact]
    public async Task Recover_finishes_exact_file_cleanup_beneath_a_closed_anchor()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore(
            failBeforeStep: step => step == "file:retiring-unlinked");

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(store);

        Assert.True((await store.RetireAsync(_lock, terminal, CancellationToken.None)).IsFailure);

        Assert.Equal(
            GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
            Value(await Store().RecoverAsync(_lock, _guarded, CancellationToken.None)).Outcome);

    }

    [Theory]
    [InlineData("file:absence-parent-flushed")]
    [InlineData("file:absence-proved")]
    public async Task Recover_closed_absence_requires_durable_parent_flush_and_repeat_proof(
        string boundary)
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(
                failBeforeStep: step => step == boundary),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.True((await interrupted.RetireAsync(
            _lock,
            terminal,
            CancellationToken.None)).IsFailure);

        using (GrimoireOfflineTransitionJournalEvidence absent = Value(
                   await new GrimoireOfflineTransitionJournalFileStore().InspectEvidenceAsync(
                       terminal.Location,
                       CancellationToken.None)))
        {

            Assert.Null(absent.Canonical);

            Assert.Null(absent.Working);

            Assert.Null(absent.Previous);

            Assert.Null(absent.Retiring);

        }

        List<string> attempted = [];

        GrimoireOfflineTransitionJournalStore blocked = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(
                failBeforeStep: step =>
                {

                    attempted.Add(step);

                    return step == boundary;

                }),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Result<GrimoireOfflineTransitionJournalRecoveryState> blockedRecovery = await blocked.RecoverAsync(
            _lock,
            _guarded,
            CancellationToken.None);

        Assert.True(blockedRecovery.IsFailure, boundary);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, blockedRecovery.Error.Code);

        Assert.Contains(boundary, attempted);

        List<string> events = [];

        GrimoireOfflineTransitionJournalStore completing = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(events.Add),
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials));

        Assert.Equal(
            GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
            Value(await completing.RecoverAsync(
                _lock,
                _guarded,
                CancellationToken.None)).Outcome);

        Assert.Contains("file:absence-parent-flushed", events);

        Assert.Contains("file:absence-proved", events);

    }

    [Fact]
    public async Task Retire_is_idempotent_after_closed_anchor_file_delete_and_parent_fsync()
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(store);

        Assert.True((await store.RetireAsync(_lock, terminal, CancellationToken.None)).IsSuccess);

        Assert.True((await store.RetireAsync(_lock, terminal, CancellationToken.None)).IsSuccess);

    }

    [Theory]
    [InlineData("earlier")]
    [InlineData("different")]
    [InlineData("resealed")]
    public async Task Closed_anchor_refuses_an_earlier_different_or_resealed_file(string replay)
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication earlier = await BeginAsync(store);

        GrimoireOfflineTransitionJournalPublication terminal = Value(await store.AdvanceAsync(
            _lock,
            earlier,
            Bytes("terminal"),
            CancellationToken.None));

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(_credentials);

        GrimoireOfflineTransitionAnchorV1 closed = terminal.Anchor with
        {
            State = GrimoireOfflineTransitionAnchorState.Closed,
        };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            terminal.Location,
            terminal.Anchor,
            closed,
            GrimoireOfflineTransitionAnchorWriteStage.Closed).IsSuccess);

        GrimoireOfflineTransitionEnvelopeV1 candidate = replay switch
        {
            "earlier" => earlier.Envelope,
            "different" => SealForTest(
                terminal.Location,
                terminal.Envelope,
                terminal.Envelope.Revision,
                terminal.Envelope.PreviousEnvelopeDigest,
                Bytes("different")),
            "resealed" => SealForTest(
                terminal.Location,
                terminal.Envelope,
                terminal.Envelope.Revision,
                terminal.Envelope.PreviousEnvelopeDigest,
                terminal.PayloadBytes),
            _ => throw new Xunit.Sdk.XunitException(replay),
        };

        byte[] bytes = Value(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(candidate));

        AssertAuthentic(terminal.Location, candidate);

        WriteOwnerOnly(terminal.Location.JournalPath, bytes);

        Assert.True((await Store().RecoverAsync(_lock, _guarded, CancellationToken.None)).IsFailure);

        Assert.Equal(closed, Value(anchors.Read(terminal.Location)));

        Assert.Equal(bytes, File.ReadAllBytes(terminal.Location.JournalPath));

    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("working")]
    [InlineData("previous")]
    [InlineData("retiring")]
    [InlineData("temp")]
    public async Task Next_epoch_cannot_open_until_exact_canonical_working_previous_retiring_and_temp_absence_is_proved(
        string blocker)
    {

        GrimoireOfflineTransitionJournalStore store = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(store);

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(_credentials);

        GrimoireOfflineTransitionAnchorV1 closed = terminal.Anchor with
        {
            State = GrimoireOfflineTransitionAnchorState.Closed,
        };

        Assert.True(anchors.CompareWriteAndVerify(
            _lock,
            terminal.Location,
            terminal.Anchor,
            closed,
            GrimoireOfflineTransitionAnchorWriteStage.Closed).IsSuccess);

        byte[] bytes = File.ReadAllBytes(terminal.Location.JournalPath);

        string blockerPath = blocker switch
        {
            "canonical" => terminal.Location.JournalPath,
            "working" => terminal.Location.WorkingPath,
            "previous" => terminal.Location.PreviousPath,
            "retiring" => terminal.Location.RetiringPath,
            "temp" => terminal.Location.JournalPath + ".tmp.next-epoch",
            _ => throw new Xunit.Sdk.XunitException(blocker),
        };

        if (blocker is not "canonical")
        {

            File.Delete(terminal.Location.JournalPath);

            WriteOwnerOnly(blockerPath, bytes);

        }

        Assert.True(File.Exists(blockerPath));

        Assert.True((await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
            GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
            1,
            Bytes("next"),
            CancellationToken.None)).IsFailure);

        Assert.Equal(closed, Value(anchors.Read(terminal.Location)));

        Assert.True(File.Exists(blockerPath));

    }

    [Theory]
    [InlineData("anchor:opening-written", false, ExpectedCrashRecovery.NoActive)]
    [InlineData("anchor:opening-readback", false, ExpectedCrashRecovery.Manual)]
    [InlineData("file:temporary-created", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:temporary-written", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:temporary-flushed", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:atomic-replace", true, ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("file:previous-retained", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:permissions-verified", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:parent-flushed", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:secure-reread", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-retiring", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-retiring-verified", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-unlinked", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-zero-link-verified", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:previous-delete-parent-flushed", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("file:residue-absence-proved", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("anchor:advance-written", true, ExpectedCrashRecovery.AuthenticatedNext)]
    [InlineData("anchor:advance-readback", true, ExpectedCrashRecovery.AuthenticatedNext)]
    public async Task Recovery_crash_matrix_converges_every_publication_boundary(
        string boundary,
        bool advance,
        ExpectedCrashRecovery expected)
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalFileStore files = new(
            failBeforeStep: step => step == boundary);

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(
            _credentials,
            failBeforeStep: step => step == boundary);

        GrimoireOfflineTransitionJournalStore interrupted = new(_credentials, files, anchors);

        Result<GrimoireOfflineTransitionJournalPublication> interruptedResult;

        if (advance)
        {

            GrimoireOfflineTransitionJournalPublication current = await BeginAsync(initial);

            interruptedResult = await interrupted.AdvanceAsync(
                _lock,
                current,
                Bytes("second"),
                CancellationToken.None);

        }
        else
        {

            interruptedResult = await interrupted.BeginAsync(
                _lock,
                _guarded,
                Installation,
                Operation,
                GrimoireOfflineTransitionKind.CovenantReset,
                1,
                Bytes("first"),
                CancellationToken.None);

        }

        Assert.True(interruptedResult.IsFailure, boundary);

        await AssertExpectedCrashRecoveryAsync(
            await Store().RecoverAsync(_lock, _guarded, CancellationToken.None),
            boundary,
            expected);

    }

    [Theory]
    [InlineData("anchor:closed-written", ExpectedCrashRecovery.AuthenticatedPrior)]
    [InlineData("anchor:closed-readback", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-moved", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-verified", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-parent-flushed", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-unlinked", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:retiring-zero-link-verified", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:delete-parent-flushed", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:absence-parent-flushed", ExpectedCrashRecovery.NoActive)]
    [InlineData("file:absence-proved", ExpectedCrashRecovery.NoActive)]
    public async Task Recovery_crash_matrix_converges_every_retirement_boundary(
        string boundary,
        ExpectedCrashRecovery expected)
    {

        GrimoireOfflineTransitionJournalStore initial = ReadyStore();

        GrimoireOfflineTransitionJournalPublication terminal = await BeginAsync(initial);

        GrimoireOfflineTransitionJournalStore interrupted = new(
            _credentials,
            new GrimoireOfflineTransitionJournalFileStore(
                failBeforeStep: step => step == boundary),
            new GrimoireOfflineTransitionJournalAnchorStore(
                _credentials,
                failBeforeStep: step => step == boundary));

        Assert.True((await interrupted.RetireAsync(
            _lock,
            terminal,
            CancellationToken.None)).IsFailure, boundary);

        await AssertExpectedCrashRecoveryAsync(
            await Store().RecoverAsync(_lock, _guarded, CancellationToken.None),
            boundary,
            expected);

    }

    [Fact]
    public void Private_open_requires_an_explicit_trusted_installation_id_parameter()
    {

        MethodInfo open = typeof(GrimoireOfflineTransitionJournalStore).GetMethod(
            "Open",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Xunit.Sdk.XunitException(
                "GrimoireOfflineTransitionJournalStore no longer declares a private Open method.");

        ParameterInfo[] parameters = open.GetParameters();

        Assert.Equal(3, parameters.Length);

        Assert.Equal(typeof(Guid), parameters[1].ParameterType);

        Assert.Equal("expectedInstallationId", parameters[1].Name);

    }

    private GrimoireOfflineTransitionJournalStore ReadyStore(
        List<string>? events = null,
        Func<string, bool>? failBeforeStep = null)
    {

        SeedIdentity(Installation);

        Action<string>? record = events is null
            ? null
            : step =>
            {

                if (step != "file:residue-absence-proved")
                {

                    events.Add(step);

                }

            };

        GrimoireOfflineTransitionJournalFileStore files = new(
            record,
            failBeforeStep);

        GrimoireOfflineTransitionJournalAnchorStore anchors = new(
            _credentials,
            record,
            failBeforeStep);

        return new GrimoireOfflineTransitionJournalStore(
            _credentials,
            files,
            anchors,
            record);

    }

    private GrimoireOfflineTransitionJournalStore Store() =>
        new(_credentials);

    private async Task<GrimoireOfflineTransitionJournalPublication> BeginAsync(
        GrimoireOfflineTransitionJournalStore store) =>
        Value(await store.BeginAsync(
            _lock,
            _guarded,
            Installation,
            Operation,
            GrimoireOfflineTransitionKind.CovenantReset,
            1,
            Bytes("first"),
            CancellationToken.None));

    private void SeedIdentity(Guid installationId)
    {

        GrimoireOfflineTransitionJournalLocation location = Location();

        BackupRestoreJournalInstallationIdentityProvider identities = new(_credentials);

        Assert.Equal(
            installationId,
            Value(identities.SeedFromDatabase(
                _lock,
                _guarded,
                location.ProfileNamespace,
                installationId)));

    }

    private GrimoireOfflineTransitionJournalLocation Location() =>
        Value(new GrimoireOfflineTransitionJournalFileStore().ResolveLocation(_guarded));

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static CovenantDigest ZeroDigest() => new(new byte[32]);

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private GrimoireOfflineTransitionEnvelopeV1 CreateUnanchoredEnvelope(
        GrimoireOfflineTransitionJournalLocation location)
    {

        GrimoireOfflineTransitionJournalKeyProvider keys = new(_credentials);

        using GrimoireOfflineTransitionJournalKeyLease key = Value(keys.CreateOrOpen(
            _lock,
            _guarded,
            location.ProfileNamespace));

        return Value(GrimoireOfflineTransitionJournalAuthenticator.Seal(
            key,
            location.ProfileNamespace.Digest,
            Installation,
            slotEpoch: 1,
            operationId: Operation,
            kind: GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            revision: 1,
            previousEnvelopeDigest: ZeroDigest(),
            journalLocationDigest: location.JournalLocationDigest,
            payloadBytes: Bytes("unanchored").Span));

    }

    private GrimoireOfflineTransitionEnvelopeV1 SealForTest(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionEnvelopeV1 source,
        ulong revision,
        CovenantDigest previousDigest,
        ReadOnlyMemory<byte> payload) =>
        SealForTest(
            location,
            source,
            source.ProfileNamespaceDigest,
            source.InstallationId,
            source.SlotEpoch,
            source.OperationId,
            source.Kind,
            source.PayloadVersion,
            revision,
            previousDigest,
            source.JournalLocationDigest,
            payload);

    private GrimoireOfflineTransitionEnvelopeV1 SealForTest(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionEnvelopeV1 source,
        CovenantDigest profile,
        Guid installation,
        ulong epoch,
        Guid operation,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ulong revision,
        CovenantDigest previousDigest,
        CovenantDigest journalLocation,
        ReadOnlyMemory<byte> payload)
    {

        GrimoireOfflineTransitionJournalKeyProvider keys = new(_credentials);

        using GrimoireOfflineTransitionJournalKeyLease key = Value(keys.OpenExisting(
            location.ProfileNamespace));

        return Value(GrimoireOfflineTransitionJournalAuthenticator.Seal(
            key,
            profile,
            installation,
            epoch,
            operation,
            kind,
            payloadVersion,
            revision,
            previousDigest,
            journalLocation,
            payload.Span));

    }

    private void AssertAuthentic(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionEnvelopeV1 envelope)
    {

        GrimoireOfflineTransitionJournalKeyProvider keys = new(_credentials);

        using GrimoireOfflineTransitionJournalKeyLease key = Value(keys.OpenExisting(
            location.ProfileNamespace));

        Assert.True(GrimoireOfflineTransitionJournalAuthenticator.Open(
            key,
            envelope.ProfileNamespaceDigest,
            envelope.InstallationId,
            envelope.JournalLocationDigest,
            envelope).IsSuccess);

    }

    private static void WriteOwnerOnly(string path, ReadOnlySpan<byte> bytes)
    {

        File.WriteAllBytes(path, bytes);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        }

    }

    private async Task AssertExpectedCrashRecoveryAsync(
        Result<GrimoireOfflineTransitionJournalRecoveryState> recovered,
        string boundary,
        ExpectedCrashRecovery expected)
    {

        if (expected is ExpectedCrashRecovery.Manual)
        {

            Assert.True(recovered.IsFailure, boundary);

            Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, recovered.Error.Code);

            GrimoireOfflineTransitionAnchorV1 opening = Assert.IsType<
                GrimoireOfflineTransitionAnchorV1>(Value(new GrimoireOfflineTransitionJournalAnchorStore(
                    _credentials).Read(Location())));

            Assert.Equal(GrimoireOfflineTransitionAnchorState.Active, opening.State);

            Assert.Equal(0UL, opening.Revision);

            Assert.Null(opening.EnvelopeDigest);

            Assert.True(new GrimoireOfflineTransitionJournalFileStore().RequireNoEvidence(Location()).IsSuccess);

            return;

        }

        Assert.True(recovered.IsSuccess, boundary);

        if (expected is ExpectedCrashRecovery.NoActive)
        {

            Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal, recovered.Value.Outcome);

            Assert.Null(recovered.Value.Publication);

            GrimoireOfflineTransitionAnchorV1 closed = Assert.IsType<
                GrimoireOfflineTransitionAnchorV1>(Value(new GrimoireOfflineTransitionJournalAnchorStore(
                    _credentials).Read(Location())));

            Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, closed.State);

            Assert.True(new GrimoireOfflineTransitionJournalFileStore().RequireNoEvidence(Location()).IsSuccess);

            return;

        }

        Assert.Equal(GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated, recovered.Value.Outcome);

        Assert.NotNull(recovered.Value.Publication);

        Assert.Equal(
            expected is ExpectedCrashRecovery.AuthenticatedPrior ? 1UL : 2UL,
            recovered.Value.Publication!.Anchor.Revision);

        GrimoireOfflineTransitionAnchorV1 stored = Assert.IsType<
            GrimoireOfflineTransitionAnchorV1>(Value(new GrimoireOfflineTransitionJournalAnchorStore(
                _credentials).Read(Location())));

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Active, stored.State);

        Assert.Equal(recovered.Value.Publication.Anchor, stored);

        using GrimoireOfflineTransitionJournalEvidence evidence = Value(await new GrimoireOfflineTransitionJournalFileStore()
            .InspectEvidenceAsync(Location(), CancellationToken.None));

        Assert.NotNull(evidence.Canonical);

        Assert.Null(evidence.Working);

        Assert.Null(evidence.Previous);

        Assert.Null(evidence.Retiring);

        Assert.Equal(
            recovered.Value.Publication.FileMetadata.Identity,
            evidence.Canonical!.Metadata.Identity);

        Assert.Equal(
            recovered.Value.Publication.Envelope.Revision,
            Value(GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                evidence.Canonical.Bytes.Span)).Revision);

    }

    public enum ExpectedCrashRecovery : byte
    {

        Manual = 1,

        NoActive = 2,

        AuthenticatedPrior = 3,

        AuthenticatedNext = 4,

    }

    private static T Value<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException(result.Error.Message);

    /// <summary>
    /// Delegates every read to <paramref name="inner"/> except for the one account prefix under
    /// test, which throws to simulate a transient OS credential store outage for that secret alone.
    /// </summary>
    private sealed class PrefixThrowingCredentialStore(
        IOsCredentialStore inner,
        string throwingAccountPrefix) : IOsCredentialStore
    {

        public bool IsAvailable => inner.IsAvailable;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            account.StartsWith(throwingAccountPrefix, StringComparison.Ordinal)
                ? throw new IOException("The credential store is unavailable for this account.")
                : inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            inner.Delete(service, account);

    }

    /// <summary>
    /// Delegates every read to <paramref name="inner"/> until <see cref="Arm"/> is called, then
    /// throws for the one account prefix under test. Lets earlier, legitimate reads of the same
    /// account succeed before the one under test is made to fail.
    /// </summary>
    private sealed class ArmedPrefixThrowingCredentialStore(
        IOsCredentialStore inner,
        string throwingAccountPrefix) : IOsCredentialStore
    {

        private bool _armed;

        public bool IsAvailable => inner.IsAvailable;

        internal void Arm() => _armed = true;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            _armed && account.StartsWith(throwingAccountPrefix, StringComparison.Ordinal)
                ? throw new IOException("The credential store is unavailable for this account.")
                : inner.TryGet(service, account);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            inner.Delete(service, account);

    }

    /// <summary>
    /// Delegates every read to <paramref name="inner"/> except for one account prefix, which
    /// throws only on its <paramref name="throwOnOccurrence"/>-th matching read. Lets a fixed
    /// number of legitimate earlier reads of the same account succeed before the read under
    /// test is made to fail, when the exact occurrence to fail is known but no diagnostic step
    /// exists to arm on.
    /// </summary>
    private sealed class CountedPrefixThrowingCredentialStore(
        IOsCredentialStore inner,
        string throwingAccountPrefix,
        int throwOnOccurrence) : IOsCredentialStore
    {

        private int _matchingCalls;

        public bool IsAvailable => inner.IsAvailable;

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            if (account.StartsWith(throwingAccountPrefix, StringComparison.Ordinal))
            {

                _matchingCalls++;

                if (_matchingCalls == throwOnOccurrence)
                {

                    throw new IOException("The credential store is unavailable for this account.");

                }

            }

            return inner.TryGet(service, account);

        }

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            inner.Set(service, account, secret);

        public OsCredentialStoreResult Delete(string service, string account) =>
            inner.Delete(service, account);

    }

}
