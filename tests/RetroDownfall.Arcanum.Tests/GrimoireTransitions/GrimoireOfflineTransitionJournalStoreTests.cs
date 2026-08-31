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

    private static T Value<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new Xunit.Sdk.XunitException(result.Error.Message);

}
