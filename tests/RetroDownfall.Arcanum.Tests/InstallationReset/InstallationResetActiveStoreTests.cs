using System.Text.Json;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("WorkspacePathPolicy")]
public sealed class InstallationResetActiveStoreTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public async Task DisposeAsync()
    {

        SecureFileReader.AfterOpenForTests = null;

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task New_v2_publication_writes_revision_zero_anchor_before_revision_one_envelope()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-begin");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveFilePersistence files = new(events.Add);

        InstallationResetActiveStore store = new(guardedRoot, credentials, files);

        Guid installationId = Guid.Parse("11111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        };

        Result<InstallationResetActivePublication> result = await store.BeginAsync(
            heldLock,
            installationId,
            record,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1UL, result.Value.Envelope.Revision);

        Assert.Equal(InstallationResetActiveRecordAuthenticator.ZeroDigest,
            result.Value.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(1UL, result.Value.Anchor.Revision);

        Assert.Equal(result.Value.EnvelopeDigest, result.Value.Anchor.EnvelopeDigest);

        AssertOrdered(
            events,
            "key:readback",
            "anchor:set:Active:0",
            "anchor:readback:Active:0",
            "file:temporary-flushed",
            "file:atomic-replace",
            "file:parent-flushed");

    }

    [Fact]
    public async Task New_v2_publication_rereads_authenticates_and_then_verifies_the_anchor()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-reread");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        Result<InstallationResetActivePublication> result = await store.BeginAsync(
            heldLock,
            Guid.Parse("21111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        AssertOrdered(
            events,
            "file:parent-flushed",
            "file:secure-reread",
            "key:open-existing",
            "anchor:compare-read:Active:0",
            "anchor:set:Active:1",
            "anchor:readback:Active:1");

        Result<InstallationResetActiveRecoveryState> inspected = await store.InspectAsync(
            CancellationToken.None);

        Assert.True(inspected.IsSuccess, inspected.Error.Message);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            inspected.Value.Outcome);

        Assert.Equal(result.Value.EnvelopeDigest, inspected.Value.Publication!.EnvelopeDigest);

    }

    [Fact]
    public async Task Publication_cancellation_after_atomic_replace_finishes_the_bounded_checkpoint()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-commit-cancellation");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        using CancellationTokenSource cancellation = new();

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(step =>
            {

                if (string.Equals(step, "file:atomic-replace", StringComparison.Ordinal))
                {

                    cancellation.Cancel();

                }

            }));

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("2a111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);

        Assert.Equal(1UL, publication.Anchor.Revision);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            Value(await store.InspectAsync(CancellationToken.None)).Outcome);

    }

    [Fact]
    public async Task Publication_cancellation_before_atomic_replace_preserves_the_opening_anchor()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-precommit-cancellation");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        using CancellationTokenSource cancellation = new();

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(step =>
            {

                if (string.Equals(step, "file:temporary-flushed", StringComparison.Ordinal))
                {

                    cancellation.Cancel();

                }

            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginAsync(
            heldLock,
            Guid.Parse("2b111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            cancellation.Token));

        Assert.False(File.Exists(store.ActivePath));

        InstallationResetActiveAnchorV1 opening = CredentialAnchor(credentials);

        Assert.Equal(InstallationResetActiveAnchorState.Active, opening.State);

        Assert.Equal(0UL, opening.Revision);

        Assert.Equal(
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            opening.EnvelopeDigest);

        Assert.True((await store.RecoverAsync(
            heldLock,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Advance_chains_exactly_one_revision_and_rejects_regression_skip_overflow_or_changed_binding()
    {

        string guardedRoot = _workspace.CreateSubdir("arcanum-v2-advance");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence());

        InstallationResetActiveRecord record = CreateRecord(InstallationResetPhase.Prepared) with
        {
            CredentialResults =
            [
                new InstallationResetCredentialResult(
                    "master-api-key",
                    InstallationResetItemStatus.Pending),
            ],
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        InstallationResetActivePublication begun = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("31111111-2222-4333-8444-555555555555"),
            record,
            CancellationToken.None));

        InstallationResetActiveRecord next = record with
        {
            Version = 2,
            Phase = InstallationResetPhase.DataResetComplete,
            PointOfNoReturn = true,
            RowsDeleted = 5,
            FilesDeleted = 3,
            EstimatedBytesDeleted = 11,
            CredentialResults =
            [
                new InstallationResetCredentialResult(
                    "master-api-key",
                    InstallationResetItemStatus.Deleted),
            ],
            OnlineDataCompletion = new InstallationResetOnlineDataCompletion(
                Guid.Parse("3a111111-2222-4333-8444-555555555555"),
                record.OperationId,
                "data-plan",
                RowsDeleted: 5,
                FilesDeleted: 3,
                EstimatedBytesDeleted: 11,
                DerivedRecordsDeleted: 2),
        };

        InstallationResetActivePublication advanced = Value(await store.AdvanceAsync(
            heldLock,
            begun,
            next,
            CancellationToken.None));

        Assert.Equal(2UL, advanced.Envelope.Revision);

        Assert.Equal(begun.EnvelopeDigest, advanced.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(advanced.EnvelopeDigest, advanced.Anchor.EnvelopeDigest);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { RowsDeleted = 4 },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { Phase = InstallationResetPhase.Prepared },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { PointOfNoReturn = false },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with { OnlineDataCompletion = null },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with
            {
                CredentialResults =
                [
                    new InstallationResetCredentialResult(
                        "master-api-key",
                        InstallationResetItemStatus.Pending),
                ],
            },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced,
            next with
            {
                AcceptedBinding = next.AcceptedBinding with { BindingId = "substituted" },
            },
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced with
            {
                Envelope = advanced.Envelope with { Revision = 4 },
            },
            next,
            CancellationToken.None)).IsFailure);

        Assert.True((await store.AdvanceAsync(
            heldLock,
            advanced with
            {
                Envelope = advanced.Envelope with
                {
                    Revision = InstallationResetActiveRecordAuthenticator.MaxRevision,
                },
                Anchor = advanced.Anchor with
                {
                    Revision = InstallationResetActiveRecordAuthenticator.MaxRevision,
                },
            },
            next,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveRecoveryState recovered = Value(
            await store.RecoverAsync(heldLock, CancellationToken.None));

        Assert.Equal(2UL, recovered.Publication!.Envelope.Revision);

        Assert.Equal(5, recovered.Publication.Payload.RowsDeleted);

    }

    [Theory]
    [InlineData("removed")]
    [InlineData("version")]
    [InlineData("operation")]
    [InlineData("installation")]
    [InlineData("attestation-digest")]
    [InlineData("nonce-digest")]
    [InlineData("issuer-digest")]
    [InlineData("accepted-at")]
    public async Task Advance_cannot_remove_or_substitute_an_authenticated_full_claim(
        string mutation)
    {

        string guardedRoot = _workspace.CreateSubdir(
            "arcanum-v2-claim-" + mutation);

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        Guid installationId = Guid.Parse(
            "32111111-2222-4333-8444-555555555555");

        Guid operationId = Guid.Parse(
            "33111111-2222-4333-8444-555555555555");

        FullInstallationResetRemediationClaimV1 claim = new(
            Version: 1,
            operationId,
            installationId,
            ClaimDigest(0x10),
            ClaimDigest(0x20),
            ClaimDigest(0x30),
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = operationId,
            Scope = InstallationResetScope.All,
            Workspace = new DataRetentionWorkspaceBinding(
                Guid.Parse("34111111-2222-4333-8444-555555555555"),
                "/selected/workspace"),
            LastErrorCode = ErrorCodes.Data.RecoveryRequired,
            FullInstallationResetRemediationClaim = claim,
        };

        InstallationResetActivePublication begun = Value(await store.BeginAsync(
            heldLock,
            installationId,
            record,
            CancellationToken.None));

        FullInstallationResetRemediationClaimV1? changed = mutation switch
        {
            "removed" => null,
            "version" => claim with { Version = 2 },
            "operation" => claim with { OperationId = Guid.NewGuid() },
            "installation" => claim with { InstallationId = Guid.NewGuid() },
            "attestation-digest" => claim with
            {
                AttestationDigest = ClaimDigest(0x40),
            },
            "nonce-digest" => claim with { NonceDigest = ClaimDigest(0x50) },
            "issuer-digest" => claim with { IssuerDigest = ClaimDigest(0x60) },
            "accepted-at" => claim with
            {
                AcceptedAtUtc = claim.AcceptedAtUtc.AddSeconds(1),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Result<InstallationResetActivePublication> advanced = await store.AdvanceAsync(
            heldLock,
            begun,
            record with { FullInstallationResetRemediationClaim = changed },
            CancellationToken.None);

        Assert.True(advanced.IsFailure);

        InstallationResetActiveRecoveryState recovered = Value(
            await store.RecoverAsync(heldLock, CancellationToken.None));

        Assert.Equal(1UL, recovered.Publication!.Envelope.Revision);

        Assert.Equal(
            claim,
            recovered.Publication.Payload.FullInstallationResetRemediationClaim);

    }

    [Fact]
    public async Task Recovery_accepts_only_an_exact_anchor_envelope_pair_or_one_authenticated_envelope_ahead()
    {

        using AuthenticatedFixture fixture = await BeginAuthenticatedAsync("recovery-exact-ahead");

        InstallationResetActiveRecoveryState exact = Value(await fixture.Store.RecoverAsync(
            fixture.Lock,
            CancellationToken.None));

        Assert.Equal(InstallationResetActiveRecoveryOutcome.AuthenticatedV2, exact.Outcome);

        Assert.Equal(fixture.Publication.EnvelopeDigest, exact.Publication!.EnvelopeDigest);

        InstallationResetActiveRecord next = fixture.Record with
        {
            Version = 2,
            Phase = InstallationResetPhase.DataResetComplete,
            PointOfNoReturn = true,
            RowsDeleted = 7,
        };

        InstallationResetActiveEnvelopeV2 ahead = SealEnvelope(
            fixture,
            revision: 2,
            fixture.Publication.EnvelopeDigest,
            InstallationResetActivePayloadV2.FromRecord(next));

        WriteEnvelope(fixture.Store.ActivePath, ahead);

        Result<InstallationResetActiveRecoveryState> readOnly = await fixture.Store.InspectAsync(
            CancellationToken.None);

        Assert.True(readOnly.IsFailure);

        Assert.Equal(1UL, CredentialAnchor(fixture.Credentials).Revision);

        InstallationResetActiveRecoveryState recovered = Value(await fixture.Store.RecoverAsync(
            fixture.Lock,
            CancellationToken.None));

        Assert.Equal(2UL, recovered.Publication!.Anchor.Revision);

        Assert.Equal(
            Value(InstallationResetActiveRecordAuthenticator.EnvelopeDigest(ahead)),
            recovered.Publication.Anchor.EnvelopeDigest);

        InstallationResetActiveRecoveryState readback = Value(await fixture.Store.RecoverAsync(
            fixture.Lock,
            CancellationToken.None));

        Assert.Equal(recovered.Publication.Anchor, readback.Publication!.Anchor);

    }

    [Fact]
    public async Task Recovery_rejects_rollback_skipped_revision_cross_profile_cross_operation_and_location_substitution()
    {

        using (AuthenticatedFixture rollback = await BeginAuthenticatedAsync("recovery-rollback"))
        {

            InstallationResetActivePublication second = Value(await rollback.Store.AdvanceAsync(
                rollback.Lock,
                rollback.Publication,
                rollback.Record with
                {
                    Version = 2,
                    Phase = InstallationResetPhase.DataResetComplete,
                    PointOfNoReturn = true,
                },
                CancellationToken.None));

            Assert.Equal(2UL, second.Anchor.Revision);

            WriteEnvelope(rollback.Store.ActivePath, rollback.Publication.Envelope);

            Assert.True((await rollback.Store.RecoverAsync(
                rollback.Lock,
                CancellationToken.None)).IsFailure);

        }

        using (AuthenticatedFixture skipped = await BeginAuthenticatedAsync("recovery-skipped"))
        {

            InstallationResetActiveEnvelopeV2 jump = SealEnvelope(
                skipped,
                revision: 3,
                skipped.Publication.EnvelopeDigest,
                skipped.Publication.Payload);

            WriteEnvelope(skipped.Store.ActivePath, jump);

            Assert.True((await skipped.Store.RecoverAsync(
                skipped.Lock,
                CancellationToken.None)).IsFailure);

        }

        using (AuthenticatedFixture operation = await BeginAuthenticatedAsync("recovery-operation"))
        {

            InstallationResetActivePayloadV2 substituted =
                InstallationResetActivePayloadV2.FromRecord(operation.Record with
                {
                    OperationId = Guid.Parse("99999999-8888-4777-8666-555555555555"),
                });

            InstallationResetActiveEnvelopeV2 crossOperation = SealEnvelope(
                operation,
                revision: 2,
                operation.Publication.EnvelopeDigest,
                substituted);

            WriteEnvelope(operation.Store.ActivePath, crossOperation);

            Assert.True((await operation.Store.RecoverAsync(
                operation.Lock,
                CancellationToken.None)).IsFailure);

        }

        using (AuthenticatedFixture location = await BeginAuthenticatedAsync("recovery-location"))
        {

            BackupRestoreProfileNamespace profile = Value(
                BackupRestoreJournalAuthenticator.ResolveProfileNamespace(location.GuardedRoot));

            string account = ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profile.AccountSuffix);

            InstallationResetActiveAnchorV1 substituted = location.Publication.Anchor with
            {
                ActiveLocationDigest = Digest(0x91),
            };

            location.Credentials.Values[account] = Value(
                InstallationResetActiveRecordAuthenticator.EncodeAnchor(substituted));

            Assert.True((await location.Store.RecoverAsync(
                location.Lock,
                CancellationToken.None)).IsFailure);

        }

        using AuthenticatedFixture source = await BeginAuthenticatedAsync("recovery-profile-source");

        string targetRoot = _workspace.CreateSubdir("recovery-profile-target");

        using ArcanumMaintenanceLock targetLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(targetRoot));

        BackupRestoreProfileNamespace sourceProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(source.GuardedRoot));

        BackupRestoreProfileNamespace targetProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(targetRoot));

        CopyCredential(
            source.Credentials,
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(sourceProfile.AccountSuffix),
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(targetProfile.AccountSuffix));

        CopyCredential(
            source.Credentials,
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(sourceProfile.AccountSuffix),
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(targetProfile.AccountSuffix));

        CopyCredential(
            source.Credentials,
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(sourceProfile.AccountSuffix),
            ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(targetProfile.AccountSuffix));

        InstallationResetActiveStore target = new(targetRoot, source.Credentials);

        File.Copy(source.Store.ActivePath, target.ActivePath);

        Assert.True((await target.RecoverAsync(targetLock, CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Recovery_treats_file_key_anchor_partial_combinations_and_lookalikes_as_blocking_evidence()
    {

        using AuthenticatedFixture source = await BeginAuthenticatedAsync("recovery-partial-source");

        string anchorOnlyRoot = _workspace.CreateSubdir("recovery-anchor-only");

        using ArcanumMaintenanceLock anchorOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(anchorOnlyRoot));

        BackupRestoreProfileNamespace sourceProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(source.GuardedRoot));

        BackupRestoreProfileNamespace anchorOnlyProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(anchorOnlyRoot));

        RecordingCredentialStore anchorOnlyCredentials = new([]);

        anchorOnlyCredentials.Values[
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                anchorOnlyProfile.AccountSuffix)] = Value(
                    InstallationResetActiveRecordAuthenticator.EncodeAnchor(
                        source.Publication.Anchor with
                        {
                            ProfileNamespaceDigest = anchorOnlyProfile.Digest,
                            ActiveLocationDigest = Value(
                                InstallationResetActiveRecordAuthenticator.ResolveLocation(
                                    anchorOnlyRoot,
                                    anchorOnlyProfile)).Digest,
                        }));

        InstallationResetActiveStore anchorOnly = new(anchorOnlyRoot, anchorOnlyCredentials);

        Assert.True((await anchorOnly.RecoverAsync(
            anchorOnlyLock,
            CancellationToken.None)).IsFailure);

        string fileOnlyRoot = _workspace.CreateSubdir("recovery-file-only");

        using ArcanumMaintenanceLock fileOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(fileOnlyRoot));

        InstallationResetActiveStore fileOnly = new(fileOnlyRoot, new RecordingCredentialStore([]));

        File.Copy(source.Store.ActivePath, fileOnly.ActivePath);

        Assert.True((await fileOnly.RecoverAsync(
            fileOnlyLock,
            CancellationToken.None)).IsFailure);

        string keyOnlyRoot = _workspace.CreateSubdir("recovery-key-only");

        using ArcanumMaintenanceLock keyOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(keyOnlyRoot));

        RecordingCredentialStore keyOnlyCredentials = new([]);

        BackupRestoreProfileNamespace keyOnlyProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(keyOnlyRoot));

        Value(new InstallationResetActiveRecordKeyProvider(keyOnlyCredentials).CreateOrOpen(
            keyOnlyLock,
            keyOnlyRoot,
            keyOnlyProfile)).Dispose();

        InstallationResetActiveStore keyOnly = new(keyOnlyRoot, keyOnlyCredentials);

        Assert.True((await keyOnly.RecoverAsync(
            keyOnlyLock,
            CancellationToken.None)).IsFailure);

        string lookalikeRoot = _workspace.CreateSubdir("recovery-lookalike");

        using ArcanumMaintenanceLock lookalikeLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(lookalikeRoot));

        InstallationResetActiveStore lookalike = new(
            lookalikeRoot,
            new RecordingCredentialStore([]));

        await File.WriteAllTextAsync(lookalike.ActivePath + ".tmp", "ambiguous");

        Assert.True((await lookalike.RecoverAsync(
            lookalikeLock,
            CancellationToken.None)).IsFailure);

        string symlinkRoot = _workspace.CreateSubdir("recovery-symlink");

        using ArcanumMaintenanceLock symlinkLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(symlinkRoot));

        InstallationResetActiveStore symlink = new(
            symlinkRoot,
            new RecordingCredentialStore([]));

        string outside = _workspace.WriteFile("recovery-outside.json", "{}");

        File.CreateSymbolicLink(symlink.ActivePath, outside);

        Assert.True((await symlink.RecoverAsync(
            symlinkLock,
            CancellationToken.None)).IsFailure);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variant_evidence_blocks_read_only_inspection_and_locked_recovery(
        bool temporary)
    {

        string guardedRoot = _workspace.CreateSubdir(
            temporary
                ? "case-variant-temporary-recovery"
                : "case-variant-canonical-recovery");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InstallationResetActiveStore store = new(
            guardedRoot,
            new RecordingCredentialStore([]));

        string variant = CaseVariantEvidencePath(store.ActivePath, temporary);

        await File.WriteAllTextAsync(variant, "ambiguous");

        Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

        Assert.True((await store.RecoverAsync(
            heldLock,
            CancellationToken.None)).IsFailure);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variant_evidence_refuses_begin_without_creating_credentials(
        bool temporary)
    {

        string guardedRoot = _workspace.CreateSubdir(
            temporary
                ? "case-variant-temporary-begin"
                : "case-variant-canonical-begin");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        string variant = CaseVariantEvidencePath(store.ActivePath, temporary);

        await File.WriteAllTextAsync(variant, "ambiguous");

        Result<InstallationResetActivePublication> begun = await store.BeginAsync(
            heldLock,
            Guid.Parse("b1111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            CancellationToken.None);

        Assert.True(begun.IsFailure);

        Assert.Equal(0, credentials.SetCount);

        Assert.Equal("ambiguous", await File.ReadAllTextAsync(variant));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Case_variant_evidence_blocks_retirement_absence_proof(
        bool temporary)
    {

        string guardedRoot = _workspace.CreateSubdir(
            temporary
                ? "case-variant-temporary-retirement"
                : "case-variant-canonical-retirement");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Completed);

        _ = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("c1111111-2222-4333-8444-555555555555"),
            record,
            CancellationToken.None));

        credentials.FailDelete = account => account.StartsWith(
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
            StringComparison.Ordinal);

        Assert.True((await store.RetireAsync(
            heldLock,
            record.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.False(File.Exists(store.ActivePath));

        credentials.FailDelete = null;

        string variant = CaseVariantEvidencePath(store.ActivePath, temporary);

        bool injected = false;

        InstallationResetActiveStore resumed = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(step =>
            {

                if (!injected
                    && string.Equals(
                        step,
                        "file:absence-parent-flushed",
                        StringComparison.Ordinal))
                {

                    File.WriteAllText(variant, "ambiguous");

                    injected = true;

                }

            }));

        Assert.True((await resumed.RetireAsync(
            heldLock,
            record.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.True(injected);

        Assert.Equal(
            InstallationResetActiveAnchorState.Closed,
            CredentialAnchor(credentials).State);

        Assert.True(File.Exists(variant));

    }

    [Fact]
    public async Task File_mutation_primitives_reject_a_wrong_root_lock_before_any_side_effect()
    {

        string guardedRoot = _workspace.CreateSubdir("file-mutation-wrong-root-target");

        string otherRoot = _workspace.CreateSubdir("file-mutation-wrong-root-lock");

        using ArcanumMaintenanceLock wrongLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(otherRoot));

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        InstallationResetActiveLocation location = Value(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                guardedRoot,
                profile));

        await File.WriteAllTextAsync(location.ActivePath, "owned");

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            location.ActivePath,
            out FileHandleMetadata metadata));

        List<string> events = [];

        InstallationResetActiveFilePersistence files = new(events.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(() => files.ReplaceDurablyAsync(
            wrongLock,
            guardedRoot,
            location,
            new byte[] { 1, 2, 3 },
            CancellationToken.None));

        Assert.Throws<InvalidOperationException>(() => files.DeleteDurably(
            wrongLock,
            guardedRoot,
            location,
            metadata));

        Assert.Throws<InvalidOperationException>(() => files.ProveAbsentDurably(
            wrongLock,
            guardedRoot,
            location));

        Assert.Empty(events);

        Assert.Equal("owned", await File.ReadAllTextAsync(location.ActivePath));

        AssertNoTemporaryEvidence(location);

    }

    [Fact]
    public async Task File_mutation_primitives_reject_a_disposed_lock_before_any_side_effect()
    {

        string guardedRoot = _workspace.CreateSubdir("file-mutation-disposed-lock");

        ArcanumMaintenanceLock disposedLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        disposedLock.Dispose();

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        InstallationResetActiveLocation location = Value(
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                guardedRoot,
                profile));

        await File.WriteAllTextAsync(location.ActivePath, "owned");

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
            location.ActivePath,
            out FileHandleMetadata metadata));

        List<string> events = [];

        InstallationResetActiveFilePersistence files = new(events.Add);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.ReplaceDurablyAsync(
            disposedLock,
            guardedRoot,
            location,
            new byte[] { 1, 2, 3 },
            CancellationToken.None));

        Assert.Throws<ObjectDisposedException>(() => files.DeleteDurably(
            disposedLock,
            guardedRoot,
            location,
            metadata));

        Assert.Throws<ObjectDisposedException>(() => files.ProveAbsentDurably(
            disposedLock,
            guardedRoot,
            location));

        Assert.Empty(events);

        Assert.Equal("owned", await File.ReadAllTextAsync(location.ActivePath));

        AssertNoTemporaryEvidence(location);

    }

    [Fact]
    public async Task Recovery_never_creates_or_repairs_missing_authentication_material()
    {

        using AuthenticatedFixture missingKey = await BeginAuthenticatedAsync("recovery-no-repair");

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(missingKey.GuardedRoot));

        string keyAccount = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        _ = missingKey.Credentials.Values.Remove(keyAccount);

        int writesBefore = missingKey.Credentials.SetCount;

        Assert.True((await missingKey.Store.RecoverAsync(
            missingKey.Lock,
            CancellationToken.None)).IsFailure);

        Assert.Equal(writesBefore, missingKey.Credentials.SetCount);

        Assert.False(missingKey.Credentials.Values.ContainsKey(keyAccount));

        missingKey.Credentials.Values[keyAccount] = "not-canonical";

        Assert.True((await missingKey.Store.RecoverAsync(
            missingKey.Lock,
            CancellationToken.None)).IsFailure);

        Assert.Equal("not-canonical", missingKey.Credentials.Values[keyAccount]);

        Assert.Equal(writesBefore, missingKey.Credentials.SetCount);

        missingKey.Credentials.IsAvailable = false;

        Assert.True((await missingKey.Store.RecoverAsync(
            missingKey.Lock,
            CancellationToken.None)).IsFailure);

        Assert.Equal(writesBefore, missingKey.Credentials.SetCount);

    }

    [Fact]
    public async Task V1_ordinary_record_migrates_to_authenticated_v2_before_the_next_effect()
    {

        string guardedRoot = _workspace.CreateSubdir("legacy-migration");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        InstallationResetActiveRecord legacy = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = Guid.Parse("41111111-2222-4333-8444-555555555555"),
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        Assert.True((await store.WriteLegacyV1ForTestsAsync(legacy, CancellationToken.None)).IsSuccess);

        InstallationResetActiveRecoveryState inspection = Value(await store.InspectAsync(
            CancellationToken.None));

        Assert.Equal(InstallationResetActiveRecoveryOutcome.LegacyV1, inspection.Outcome);

        Assert.Equivalent(legacy, inspection.LegacyRecord, strict: true);

        Guid installationId = Guid.Parse("51111111-2222-4333-8444-555555555555");

        events.Clear();

        InstallationResetActivePublication migrated = Value(await store.MigrateLegacyV1ForTestsAsync(
            heldLock,
            installationId,
            CancellationToken.None));

        Assert.Equal(1UL, migrated.Envelope.Revision);

        Assert.Equal(
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            migrated.Envelope.PreviousEnvelopeDigest);

        Assert.Equal(legacy.OperationId, migrated.Payload.OperationId);

        Assert.Equal(legacy.AcceptedBinding.BindingId, migrated.Payload.AcceptedBinding.BindingId);

        Assert.Equal(legacy.DataHandoff, migrated.Payload.DataHandoff);

        AssertOrdered(
            events,
            "key:readback",
            "anchor:set:Active:0",
            "anchor:readback:Active:0",
            "file:temporary-flushed",
            "file:atomic-replace",
            "file:parent-flushed",
            "file:secure-reread",
            "anchor:set:Active:1");

        InstallationResetActiveRecoveryState recovered = Value(await store.RecoverAsync(
            heldLock,
            CancellationToken.None));

        Assert.Equal(InstallationResetActiveRecoveryOutcome.AuthenticatedV2, recovered.Outcome);

        Assert.Equal(migrated.EnvelopeDigest, recovered.Publication!.EnvelopeDigest);

    }

    [Fact]
    public async Task V1_record_with_full_reset_authority_or_nonnull_reserved_slot_is_refused()
    {

        foreach (string forbiddenMember in (string[])
                 [
                     "\"fullResetAuthority\":true",
                     "\"hostToolsMarkerPairReset\":{\"revision\":1}",
                 ])
        {

            string guardedRoot = _workspace.CreateSubdir(
                "legacy-forbidden-" + Guid.NewGuid().ToString("N"));

            using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(guardedRoot));

            RecordingCredentialStore credentials = new([]);

            InstallationResetActiveStore store = new(guardedRoot, credentials);

            const string prefix =
                "{\"version\":1,\"operationId\":\"61111111-2222-4333-8444-555555555555\","
                + "\"planId\":\"composite-plan\",\"scope\":\"Global\",\"workspace\":null,"
                + "\"acceptedBinding\":{\"bindingId\":\"binding\",\"selectedRoots\":[],"
                + "\"excludedRoots\":[],\"preservedBackups\":[],\"credentialAccounts\":[],"
                + "\"dataPlanIds\":[]},\"phase\":\"Prepared\",\"pointOfNoReturn\":false,"
                + "\"rowsDeleted\":0,\"filesDeleted\":0,\"estimatedBytesDeleted\":0,"
                + "\"credentialResults\":[],\"lastErrorCode\":null,\"dataHandoff\":null,"
                + "\"onlineDataCompletion\":null,";

            await File.WriteAllTextAsync(store.ActivePath, prefix + forbiddenMember + "}");

            Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

            Assert.True((await store.MigrateLegacyV1ForTestsAsync(
                heldLock,
                Guid.NewGuid(),
                CancellationToken.None)).IsFailure);

            Assert.Equal(0, credentials.SetCount);

        }

    }

    [Fact]
    public async Task V1_revision_zero_anchor_crash_resumes_only_the_same_ordinary_operation()
    {

        string guardedRoot = _workspace.CreateSubdir("legacy-revision-zero-resume");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore failingStore = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(
                failBeforeStep: step => string.Equals(
                    step,
                    "file:temporary-flushed",
                    StringComparison.Ordinal)));

        Guid operationId = Guid.Parse("6a111111-2222-4333-8444-555555555555");

        InstallationResetActiveRecord legacy = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = operationId,
        };

        Assert.True((await failingStore.WriteLegacyV1ForTestsAsync(
            legacy,
            CancellationToken.None)).IsSuccess);

        string exactLegacy = await File.ReadAllTextAsync(failingStore.ActivePath);

        Guid installationId = Guid.Parse("6b111111-2222-4333-8444-555555555555");

        Assert.True((await failingStore.MigrateLegacyV1ForTestsAsync(
            heldLock,
            installationId,
            CancellationToken.None)).IsFailure);

        InstallationResetActiveStore resumedStore = new(guardedRoot, credentials);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.LegacyV1,
            Value(await resumedStore.InspectAsync(CancellationToken.None)).Outcome);

        await File.WriteAllTextAsync(
            resumedStore.ActivePath,
            exactLegacy.Replace(
                operationId.ToString("D"),
                Guid.Parse("6c111111-2222-4333-8444-555555555555").ToString("D"),
                StringComparison.Ordinal));

        Assert.True((await resumedStore.InspectAsync(CancellationToken.None)).IsFailure);

        Assert.True((await resumedStore.MigrateLegacyV1ForTestsAsync(
            heldLock,
            installationId,
            CancellationToken.None)).IsFailure);

        await File.WriteAllTextAsync(resumedStore.ActivePath, exactLegacy);

        InstallationResetActivePublication migrated = Value(
            await resumedStore.MigrateLegacyV1ForTestsAsync(
                heldLock,
                installationId,
                CancellationToken.None));

        Assert.Equal(operationId, migrated.Envelope.OperationId);

        Assert.Equal(1UL, migrated.Anchor.Revision);

    }

    [Fact]
    public async Task V1_semantically_invalid_binding_is_not_a_migration_candidate()
    {

        string guardedRoot = _workspace.CreateSubdir("legacy-invalid-binding");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        InstallationResetActiveRecord invalid = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            AcceptedBinding = CreateRecord(InstallationResetPhase.Prepared)
                .AcceptedBinding with
            {
                SelectedRoots = [" "],
            },
        };

        await File.WriteAllBytesAsync(
            store.ActivePath,
            JsonSerializer.SerializeToUtf8Bytes(
                invalid,
                InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord));

        Assert.True((await store.InspectAsync(CancellationToken.None)).IsFailure);

        Assert.True((await store.MigrateLegacyV1ForTestsAsync(
            heldLock,
            Guid.NewGuid(),
            CancellationToken.None)).IsFailure);

        Assert.Equal(0, credentials.SetCount);

    }

    [Fact]
    public async Task Closed_anchor_retirement_deletes_file_then_anchor_then_key_idempotently()
    {

        string guardedRoot = _workspace.CreateSubdir("closed-retirement");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        List<string> events = [];

        RecordingCredentialStore credentials = new(events);

        InstallationResetActiveStore store = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(events.Add));

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Completed);

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            Guid.Parse("71111111-2222-4333-8444-555555555555"),
            record,
            CancellationToken.None));

        events.Clear();

        Result retired = await store.RetireAsync(
            heldLock,
            record.OperationId,
            CancellationToken.None);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        AssertOrdered(
            events,
            "anchor:set:Closed:1",
            "anchor:readback:Closed:1",
            "file:secure-reread",
            "file:delete",
            "file:delete-parent-flushed",
            "file:absence-proved",
            "anchor:delete",
            "anchor:absence-readback",
            "key:delete",
            "key:absence-readback");

        Assert.False(File.Exists(store.ActivePath));

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccount(
                profile.AccountSuffix),
            credentials.Values.Keys);

        Assert.DoesNotContain(
            ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
                profile.AccountSuffix),
            credentials.Values.Keys);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.NoActiveRecord,
            Value(await store.InspectAsync(CancellationToken.None)).Outcome);

        Assert.True((await store.RetireAsync(
            heldLock,
            publication.Envelope.OperationId,
            CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Exact_operation_retirement_cannot_claim_key_only_evidence_but_startup_cleanup_can()
    {

        string guardedRoot = _workspace.CreateSubdir("key-only-retirement-authority");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot));

        using (Value(new InstallationResetActiveRecordKeyProvider(credentials).CreateOrOpen(
                   heldLock,
                   guardedRoot,
                   profile)))
        {
        }

        string keyAccount = ArcanumCredentialIdentity.InstallationResetActiveKeyAccount(
            profile.AccountSuffix);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        Result retired = await store.RetireAsync(
            heldLock,
            Guid.Parse("d1111111-2222-4333-8444-555555555555"),
            CancellationToken.None);

        Assert.True(retired.IsFailure);

        Assert.True(credentials.Values.ContainsKey(keyAccount));

        Assert.True((await store.CompleteStartupCleanupAsync(
            heldLock,
            CancellationToken.None)).IsSuccess);

        Assert.False(credentials.Values.ContainsKey(keyAccount));

    }

    [Fact]
    public async Task Startup_cleanup_removes_only_closed_or_orphaned_key_evidence_and_never_active_evidence()
    {

        string closedRoot = _workspace.CreateSubdir("closed-cleanup");

        using ArcanumMaintenanceLock closedLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(closedRoot));

        List<string> closedEvents = [];

        RecordingCredentialStore closedCredentials = new(closedEvents);

        InstallationResetActiveStore closedStore = new(
            closedRoot,
            closedCredentials,
            new InstallationResetActiveFilePersistence(closedEvents.Add));

        InstallationResetActiveRecord closedRecord = CreateRecord(
            InstallationResetPhase.Completed);

        _ = Value(await closedStore.BeginAsync(
            closedLock,
            Guid.Parse("81111111-2222-4333-8444-555555555555"),
            closedRecord,
            CancellationToken.None));

        closedCredentials.FailDelete = account => account.StartsWith(
            ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
            StringComparison.Ordinal);

        Assert.True((await closedStore.RetireAsync(
            closedLock,
            closedRecord.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.False(File.Exists(closedStore.ActivePath));

        Assert.Equal(
            InstallationResetActiveAnchorState.Closed,
            CredentialAnchor(closedCredentials).State);

        closedCredentials.FailDelete = null;

        Assert.True((await closedStore.CompleteStartupCleanupAsync(
            closedLock,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(
            InstallationResetActiveRecoveryOutcome.NoActiveRecord,
            Value(await closedStore.InspectAsync(CancellationToken.None)).Outcome);

        string filePresentRoot = _workspace.CreateSubdir("closed-file-cleanup");

        using ArcanumMaintenanceLock filePresentLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(filePresentRoot));

        RecordingCredentialStore filePresentCredentials = new([]);

        InstallationResetActiveFilePersistence failingDelete = new(
            failBeforeStep: step => string.Equals(
                step,
                "file:delete",
                StringComparison.Ordinal));

        InstallationResetActiveStore filePresentStore = new(
            filePresentRoot,
            filePresentCredentials,
            failingDelete);

        InstallationResetActiveRecord filePresentRecord = CreateRecord(
            InstallationResetPhase.Completed);

        _ = Value(await filePresentStore.BeginAsync(
            filePresentLock,
            Guid.Parse("91111111-2222-4333-8444-555555555555"),
            filePresentRecord,
            CancellationToken.None));

        Assert.True((await filePresentStore.RetireAsync(
            filePresentLock,
            filePresentRecord.OperationId,
            CancellationToken.None)).IsFailure);

        Assert.True(File.Exists(filePresentStore.ActivePath));

        InstallationResetActiveStore resumedClosedStore = new(
            filePresentRoot,
            filePresentCredentials);

        Assert.True((await resumedClosedStore.CompleteStartupCleanupAsync(
            filePresentLock,
            CancellationToken.None)).IsSuccess);

        Assert.False(File.Exists(filePresentStore.ActivePath));

        string keyOnlyRoot = _workspace.CreateSubdir("key-only-cleanup");

        using ArcanumMaintenanceLock keyOnlyLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(keyOnlyRoot));

        RecordingCredentialStore keyOnlyCredentials = new([]);

        BackupRestoreProfileNamespace keyOnlyProfile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(keyOnlyRoot));

        using (Value(new InstallationResetActiveRecordKeyProvider(keyOnlyCredentials).CreateOrOpen(
                   keyOnlyLock,
                   keyOnlyRoot,
                   keyOnlyProfile)))
        {
        }

        InstallationResetActiveStore keyOnlyStore = new(keyOnlyRoot, keyOnlyCredentials);

        Assert.True((await keyOnlyStore.CompleteStartupCleanupAsync(
            keyOnlyLock,
            CancellationToken.None)).IsSuccess);

        Assert.Empty(keyOnlyCredentials.Values);

        string activeRoot = _workspace.CreateSubdir("active-missing-file");

        using ArcanumMaintenanceLock activeLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(activeRoot));

        RecordingCredentialStore activeCredentials = new([]);

        InstallationResetActiveStore activeStore = new(
            activeRoot,
            activeCredentials,
            new InstallationResetActiveFilePersistence(
                failBeforeStep: step => string.Equals(
                    step,
                    "file:temporary-flushed",
                    StringComparison.Ordinal)));

        Assert.True((await activeStore.BeginAsync(
            activeLock,
            Guid.Parse("a1111111-2222-4333-8444-555555555555"),
            CreateRecord(InstallationResetPhase.Prepared),
            CancellationToken.None)).IsFailure);

        int activeCredentialCount = activeCredentials.Values.Count;

        Assert.True((await activeStore.CompleteStartupCleanupAsync(
            activeLock,
            CancellationToken.None)).IsFailure);

        Assert.Equal(activeCredentialCount, activeCredentials.Values.Count);

    }


    private static InstallationResetActiveRecord CreateRecord(
        InstallationResetPhase phase)
    {

        InstallationResetAcceptedBinding binding = new(
            "binding",
            ["/selected"],
            ["/excluded"],
            [],
            ["master-api-key"],
            ["data-plan"]);

        return new InstallationResetActiveRecord(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.Global,
            Workspace: null,
            binding,
            phase,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null);

    }

    private static CovenantDigest ClaimDigest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    private static void AssertOrdered(List<string> events, params string[] expected)
    {

        int prior = -1;

        foreach (string value in expected)
        {

            int current = events.FindIndex(
                prior + 1,
                candidate => string.Equals(candidate, value, StringComparison.Ordinal));

            Assert.True(
                current > prior,
                $"Expected '{value}' after event index {prior}. Actual: {string.Join(", ", events)}");

            prior = current;

        }

    }

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

    private async Task<AuthenticatedFixture> BeginAuthenticatedAsync(string name)
    {

        string guardedRoot = _workspace.CreateSubdir(name);

        ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        RecordingCredentialStore credentials = new([]);

        InstallationResetActiveStore store = new(guardedRoot, credentials);

        InstallationResetActiveRecord record = CreateRecord(
            InstallationResetPhase.Prepared) with
        {
            OperationId = Guid.NewGuid(),
        };

        InstallationResetActivePublication publication = Value(await store.BeginAsync(
            heldLock,
            Guid.NewGuid(),
            record,
            CancellationToken.None));

        return new AuthenticatedFixture(
            guardedRoot,
            heldLock,
            credentials,
            store,
            record,
            publication);

    }

    private static InstallationResetActiveEnvelopeV2 SealEnvelope(
        AuthenticatedFixture fixture,
        ulong revision,
        CovenantDigest previousDigest,
        InstallationResetActivePayloadV2 payload)
    {

        BackupRestoreProfileNamespace profile = Value(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(fixture.GuardedRoot));

        using InstallationResetActiveRecordKeyLease key = Value(
            new InstallationResetActiveRecordKeyProvider(fixture.Credentials)
                .OpenExisting(profile));

        return Value(InstallationResetActiveRecordAuthenticator.Seal(
            key,
            fixture.Publication.Location,
            fixture.Publication.Anchor.InstallationId,
            revision,
            previousDigest,
            payload));

    }

    private static void WriteEnvelope(
        string path,
        InstallationResetActiveEnvelopeV2 envelope) =>
        File.WriteAllBytes(
            path,
            Value(InstallationResetActiveRecordAuthenticator.EncodeEnvelope(envelope)));

    private static CovenantDigest Digest(byte first) =>
        new(Enumerable.Range(first, 32).Select(static value => (byte)value).ToArray());

    private static string CaseVariantPath(string path)
    {

        string leaf = Path.GetFileName(path);

        int letterIndex = leaf.Index().First(pair => char.IsLetter(pair.Item)).Index;

        char variant = char.IsLower(leaf[letterIndex])
            ? char.ToUpperInvariant(leaf[letterIndex])
            : char.ToLowerInvariant(leaf[letterIndex]);

        string variantLeaf = leaf[..letterIndex]
            + variant
            + leaf[(letterIndex + 1)..];

        return Path.Combine(
            Path.GetDirectoryName(path)!,
            variantLeaf);

    }

    private static string CaseVariantEvidencePath(string activePath, bool temporary) =>
        CaseVariantPath(activePath)
        + (temporary ? ".TMP.interrupted" : string.Empty);

    private static void AssertNoTemporaryEvidence(
        InstallationResetActiveLocation location) =>
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                Path.GetDirectoryName(location.ActivePath)!),
            entry => Path.GetFileName(entry).StartsWith(
                location.ActiveLeaf + ".tmp",
                StringComparison.OrdinalIgnoreCase));

    private static void CopyCredential(
        RecordingCredentialStore credentials,
        string source,
        string destination) =>
        credentials.Values[destination] = credentials.Values[source];

    private static InstallationResetActiveAnchorV1 CredentialAnchor(
        RecordingCredentialStore credentials)
    {

        KeyValuePair<string, string> stored = Assert.Single(
            credentials.Values,
            pair => pair.Key.StartsWith(
                ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                StringComparison.Ordinal));

        return Value(InstallationResetActiveRecordAuthenticator.DecodeAnchor(
            stored.Value));

    }

    private sealed record AuthenticatedFixture(
        string GuardedRoot,
        ArcanumMaintenanceLock Lock,
        RecordingCredentialStore Credentials,
        InstallationResetActiveStore Store,
        InstallationResetActiveRecord Record,
        InstallationResetActivePublication Publication) : IDisposable
    {

        public void Dispose() => Lock.Dispose();

    }

    private sealed class RecordingCredentialStore(List<string> events) : IOsCredentialStore
    {

        public bool IsAvailable { get; set; } = true;

        public Func<string, bool>? FailDelete { get; set; }

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        private readonly HashSet<string> _pendingReadbacks = new(StringComparer.Ordinal);

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            if (account.StartsWith(
                    ArcanumCredentialIdentity.InstallationResetActiveKeyAccountPrefix,
                    StringComparison.Ordinal))
            {

                if (Values.ContainsKey(account))
                {

                    events.Add(_pendingReadbacks.Remove(account)
                        ? "key:readback"
                        : "key:open-existing");

                }
                else
                {

                    events.Add(_pendingDeletions.Remove(account)
                        ? "key:absence-readback"
                        : "key:probe");

                }

            }
            else if (account.StartsWith(
                         ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                         StringComparison.Ordinal))
            {

                if (Values.TryGetValue(account, out string? encoded))
                {

                    InstallationResetActiveAnchorV1 anchor = Value(
                        InstallationResetActiveRecordAuthenticator.DecodeAnchor(encoded));

                    events.Add(_pendingReadbacks.Remove(account)
                        ? $"anchor:readback:{anchor.State}:{anchor.Revision}"
                        : $"anchor:compare-read:{anchor.State}:{anchor.Revision}");

                }
                else
                {

                    events.Add(_pendingDeletions.Remove(account)
                        ? "anchor:absence-readback"
                        : "anchor:probe");

                }

            }

            return Values.TryGetValue(account, out string? value)
                ? OsCredentialStoreResult.Ok(value)
                : OsCredentialStoreResult.NotFound();

        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            Values[account] = secret;

            SetCount++;

            _pendingReadbacks.Add(account);

            if (account.StartsWith(
                    ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                    StringComparison.Ordinal))
            {

                InstallationResetActiveAnchorV1 anchor = Value(
                    InstallationResetActiveRecordAuthenticator.DecodeAnchor(secret));

                events.Add($"anchor:set:{anchor.State}:{anchor.Revision}");

            }

            return OsCredentialStoreResult.Ok(secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            Assert.Equal(ArcanumCredentialIdentity.Service, service);

            if (!IsAvailable)
            {

                return OsCredentialStoreResult.Unavailable("unavailable");

            }

            if (FailDelete?.Invoke(account) is true)
            {

                return OsCredentialStoreResult.Failed("injected");

            }

            if (account.StartsWith(
                    ArcanumCredentialIdentity.InstallationResetActiveAnchorAccountPrefix,
                    StringComparison.Ordinal))
            {

                events.Add("anchor:delete");

            }
            else if (account.StartsWith(
                         ArcanumCredentialIdentity.InstallationResetActiveKeyAccountPrefix,
                         StringComparison.Ordinal))
            {

                events.Add("key:delete");

            }

            _ = Values.Remove(account);

            _pendingDeletions.Add(account);

            return OsCredentialStoreResult.Ok(string.Empty);

        }

        public int SetCount { get; private set; }

        private readonly HashSet<string> _pendingDeletions = new(StringComparer.Ordinal);

    }

}
