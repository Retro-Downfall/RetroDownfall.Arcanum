using System.Text.Json;

using System.Text.Json.Serialization;

using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed record InstallationResetOnlineDataCompletion(
    Guid ServerOperationId,
    Guid RequestedOperationId,
    string DataPlanId,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    long DerivedRecordsDeleted);

internal sealed record InstallationResetActiveRecord(
    int Version,
    Guid OperationId,
    string PlanId,
    InstallationResetScope Scope,
    DataRetentionWorkspaceBinding? Workspace,
    InstallationResetAcceptedBinding AcceptedBinding,
    InstallationResetPhase Phase,
    bool PointOfNoReturn,
    long RowsDeleted,
    long FilesDeleted,
    long EstimatedBytesDeleted,
    InstallationResetCredentialResult[] CredentialResults,
    string? LastErrorCode,
    InstallationResetDataHandoff? DataHandoff = null,
    InstallationResetOnlineDataCompletion? OnlineDataCompletion = null,
    [property: JsonIgnore]
    FullInstallationResetRemediationClaimV1? FullInstallationResetRemediationClaim = null,
    [property: JsonIgnore]
    HostToolsMarkerPairResetCheckpointV1? HostToolsMarkerPairReset = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstallationResetActiveRecord))]
internal sealed partial class InstallationResetActiveLegacyJsonContext : JsonSerializerContext;

internal interface IInstallationResetActiveStore
{

    string GuardedRoot { get; }

    Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetActivePublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid installationId,
        InstallationResetActiveRecord record,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetActivePublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication current,
        InstallationResetActiveRecord next,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid installationId,
        InstallationResetActiveRecord expectedRecord,
        FileHandleIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    Task<Result> RetireAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task<Result> CompleteStartupCleanupAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

}

internal sealed class InstallationResetActiveStore : IInstallationResetActiveStore
{

    public const int CurrentVersion = 1;

    public const int MaxBytes = 64 * 1024;

    private readonly string _guardedRoot;

    private readonly InstallationResetActiveRecordKeyProvider? _keys;

    private readonly InstallationResetActiveAnchorStore? _anchors;

    private readonly BackupRestoreJournalInstallationIdentityProvider? _identities;

    private readonly InstallationResetActiveFilePersistence? _files;

    public InstallationResetActiveStore(string guardedRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        _guardedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(guardedRoot));

        string lockPath = ArcanumMaintenanceLock.LockPathFor(_guardedRoot);

        string parent = Path.GetDirectoryName(lockPath)!;

        string name = Path.GetFileNameWithoutExtension(lockPath);

        ActivePath = Path.Combine(parent, name + ".factory-reset.active.json");

    }

    internal InstallationResetActiveStore(
        string guardedRoot,
        IOsCredentialStore credentials,
        InstallationResetActiveFilePersistence? files = null)
        : this(guardedRoot)
    {

        ArgumentNullException.ThrowIfNull(credentials);

        _keys = new InstallationResetActiveRecordKeyProvider(credentials);

        _anchors = new InstallationResetActiveAnchorStore(credentials);

        _identities = new BackupRestoreJournalInstallationIdentityProvider(credentials);

        _files = files ?? new InstallationResetActiveFilePersistence();

    }

    public string ActivePath { get; }

    public string GuardedRoot => _guardedRoot;

    public async Task<Result<InstallationResetActivePublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid installationId,
        InstallationResetActiveRecord record,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(record);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        cancellationToken.ThrowIfCancellationRequested();

        Result dependencies = RequireAuthenticatedDependencies();

        if (dependencies.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(dependencies.Error);

        }

        Result<InstallationResetActivePayloadV2> payload = ToAuthenticatedPayload(record);

        if (installationId == Guid.Empty || payload.IsFailure)
        {

            return Integrity<InstallationResetActivePublication>();

        }

        Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
            resolved = ResolveEvidenceLocation();

        if (resolved.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(resolved.Error);

        }

        (BackupRestoreProfileNamespace profile, InstallationResetActiveLocation location) =
            resolved.Value;

        Result preflight = InstallationResetActiveRecordAuthenticator.PreflightEnvelope(
            location,
            installationId,
            revision: 1,
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            payload.Value);

        if (preflight.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(preflight.Error);

        }

        Result noFile = _files!.RequireNoEvidence(location);

        if (noFile.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(noFile.Error);

        }

        Result<InstallationResetActiveAnchorV1?> anchorRead = _anchors!.Read(profile);

        if (anchorRead.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(anchorRead.Error);

        }

        if (anchorRead.Value is not null)
        {

            return Conflict<InstallationResetActivePublication>();

        }

        Result<Guid> identity = _identities!.SeedFromDatabase(
            heldInstallationLock,
            _guardedRoot,
            profile,
            installationId);

        if (identity.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(identity.Error);

        }

        Result<InstallationResetActiveRecordKeyLease> key = _keys!.CreateOrOpen(
            heldInstallationLock,
            _guardedRoot,
            profile);

        if (key.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(key.Error);

        }

        InstallationResetActiveAnchorV1 opening = new(
            InstallationResetActiveRecordAuthenticator.AnchorVersion,
            InstallationResetActiveAnchorState.Active,
            profile.Digest,
            installationId,
            record.OperationId,
            Revision: 0,
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            location.Digest);

        Result anchored = _anchors.WriteOpeningAndVerify(
            heldInstallationLock,
            _guardedRoot,
            profile,
            opening);

        if (anchored.IsFailure)
        {

            key.Value.Dispose();

            return Result<InstallationResetActivePublication>.Failure(anchored.Error);

        }

        using (key.Value)
        {

            return await PublishAsync(
                    heldInstallationLock,
                    profile,
                    location,
                    opening,
                    key.Value,
                    payload.Value,
                    InstallationResetActiveRecordAuthenticator.ZeroDigest,
                    cancellationToken)
                .ConfigureAwait(false);

        }

    }

    public async Task<Result<InstallationResetActivePublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication current,
        InstallationResetActiveRecord next,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(current);

        ArgumentNullException.ThrowIfNull(next);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        cancellationToken.ThrowIfCancellationRequested();

        Result<InstallationResetActiveRecoveryState> recovered = await RecoverAsync(
            heldInstallationLock,
            cancellationToken).ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(recovered.Error);

        }

        if (recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } actual
            || !SamePublication(current, actual))
        {

            return Conflict<InstallationResetActivePublication>();

        }

        Result<InstallationResetActivePayloadV2> payload = ToAuthenticatedPayload(next);

        if (payload.IsFailure || !IsMonotonicTransition(actual.Payload, payload.Value))
        {

            return Conflict<InstallationResetActivePublication>();

        }

        if (actual.Anchor.Revision >= InstallationResetActiveRecordAuthenticator.MaxRevision)
        {

            return Conflict<InstallationResetActivePublication>();

        }

        Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
            resolved = ResolveEvidenceLocation();

        if (resolved.IsFailure || resolved.Value.Location != actual.Location)
        {

            return Integrity<InstallationResetActivePublication>();

        }

        BackupRestoreProfileNamespace profile = resolved.Value.Profile;

        Result<InstallationResetActiveRecordKeyLease> key = _keys!.OpenExisting(profile);

        if (key.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(key.Error);

        }

        using (key.Value)
        {

            return await PublishAsync(
                    heldInstallationLock,
                    profile,
                    actual.Location,
                    actual.Anchor,
                    key.Value,
                    payload.Value,
                    actual.EnvelopeDigest,
                    cancellationToken)
                .ConfigureAwait(false);

        }

    }

    public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        return RecoverCoreAsync(
            heldInstallationLock,
            mayAdvanceAnchor: true,
            cancellationToken);

    }

    public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
        CancellationToken cancellationToken = default) =>
        RecoverCoreAsync(
            heldInstallationLock: null,
            mayAdvanceAnchor: false,
            cancellationToken);

    public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid installationId,
        InstallationResetActiveRecord expectedRecord,
        FileHandleIdentity expectedIdentity,
        CancellationToken cancellationToken = default) =>
        MigrateLegacyV1CoreAsync(
            heldInstallationLock,
            installationId,
            expectedRecord,
            expectedIdentity,
            cancellationToken);

    private async Task<Result<InstallationResetActivePublication>> MigrateLegacyV1CoreAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid installationId,
        InstallationResetActiveRecord? expectedRecord,
        FileHandleIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        cancellationToken.ThrowIfCancellationRequested();

        Result dependencies = RequireAuthenticatedDependencies();

        if (dependencies.IsFailure || installationId == Guid.Empty)
        {

            return dependencies.IsFailure
                ? Result<InstallationResetActivePublication>.Failure(dependencies.Error)
                : Integrity<InstallationResetActivePublication>();

        }

        Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
            resolved = ResolveEvidenceLocation();

        if (resolved.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(resolved.Error);

        }

        (BackupRestoreProfileNamespace profile, InstallationResetActiveLocation location) =
            resolved.Value;

        Result<InstallationResetActiveFileRead?> fileRead = await _files!
            .ReadIfPresentAsync(location, cancellationToken).ConfigureAwait(false);

        if (fileRead.IsFailure || fileRead.Value is null)
        {

            return fileRead.IsFailure
                ? Result<InstallationResetActivePublication>.Failure(fileRead.Error)
                : EvidenceFailure<InstallationResetActivePublication>();

        }

        using InstallationResetActiveFileRead file = fileRead.Value;

        Result<InstallationResetActiveRecord> legacy = DecodeLegacyV1(file.Bytes.Span);

        if (legacy.IsFailure)
        {

            return EvidenceFailure<InstallationResetActivePublication>();

        }

        if (expectedRecord is not null
            && (expectedIdentity is null
                || file.Metadata.Identity != expectedIdentity.Value
                || !SameLegacyRecord(expectedRecord, legacy.Value)))
        {

            return EvidenceFailure<InstallationResetActivePublication>();

        }

        Result<InstallationResetActivePayloadV2> payload = ToAuthenticatedPayload(legacy.Value);

        if (payload.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(payload.Error);

        }

        Result preflight = InstallationResetActiveRecordAuthenticator.PreflightEnvelope(
            location,
            installationId,
            revision: 1,
            InstallationResetActiveRecordAuthenticator.ZeroDigest,
            payload.Value);

        if (preflight.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(preflight.Error);

        }

        Result<InstallationResetActiveAnchorV1?> anchorRead = _anchors!.Read(profile);

        if (anchorRead.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(anchorRead.Error);

        }

        InstallationResetActiveAnchorV1 opening;

        Result<InstallationResetActiveRecordKeyLease> key;

        if (anchorRead.Value is null)
        {

            Result<bool> keyPresent = _keys!.IsPresent(profile);

            if (keyPresent.IsFailure)
            {

                return Result<InstallationResetActivePublication>.Failure(keyPresent.Error);

            }

            if (keyPresent.Value)
            {

                return EvidenceFailure<InstallationResetActivePublication>();

            }

            Result<Guid> identity = _identities!.SeedFromDatabase(
                heldInstallationLock,
                _guardedRoot,
                profile,
                installationId);

            if (identity.IsFailure)
            {

                return Result<InstallationResetActivePublication>.Failure(identity.Error);

            }

            key = _keys.CreateOrOpen(
                heldInstallationLock,
                _guardedRoot,
                profile);

            if (key.IsFailure)
            {

                return Result<InstallationResetActivePublication>.Failure(key.Error);

            }

            opening = new InstallationResetActiveAnchorV1(
                InstallationResetActiveRecordAuthenticator.AnchorVersion,
                InstallationResetActiveAnchorState.Active,
                profile.Digest,
                installationId,
                legacy.Value.OperationId,
                Revision: 0,
                InstallationResetActiveRecordAuthenticator.ZeroDigest,
                location.Digest);

            Result stored = _anchors.WriteOpeningAndVerify(
                heldInstallationLock,
                _guardedRoot,
                profile,
                opening);

            if (stored.IsFailure)
            {

                key.Value.Dispose();

                return Result<InstallationResetActivePublication>.Failure(stored.Error);

            }

        }
        else
        {

            opening = anchorRead.Value;

            bool resumable = opening.State is InstallationResetActiveAnchorState.Active
                && opening.Version == InstallationResetActiveRecordAuthenticator.AnchorVersion
                && opening.ProfileNamespaceDigest == profile.Digest
                && opening.InstallationId == installationId
                && opening.OperationId == legacy.Value.OperationId
                && opening.Revision == 0
                && opening.EnvelopeDigest == InstallationResetActiveRecordAuthenticator.ZeroDigest
                && opening.ActiveLocationDigest == location.Digest;

            if (!resumable)
            {

                return EvidenceFailure<InstallationResetActivePublication>();

            }

            Result identity = _identities!.RequireMatchesDatabase(profile, installationId);

            if (identity.IsFailure)
            {

                return Result<InstallationResetActivePublication>.Failure(identity.Error);

            }

            key = _keys!.OpenExisting(profile);

            if (key.IsFailure)
            {

                return key.Error.Code == ErrorCodes.Covenant.NotFound
                    ? EvidenceFailure<InstallationResetActivePublication>()
                    : Result<InstallationResetActivePublication>.Failure(key.Error);

            }

        }

        using (key.Value)
        {

            return await PublishAsync(
                    heldInstallationLock,
                    profile,
                    location,
                    opening,
                    key.Value,
                    payload.Value,
                    InstallationResetActiveRecordAuthenticator.ZeroDigest,
                    cancellationToken)
                .ConfigureAwait(false);

        }

    }

    public async Task<Result> RetireAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        cancellationToken.ThrowIfCancellationRequested();

        Result dependencies = RequireAuthenticatedDependencies();

        if (dependencies.IsFailure)
        {

            return dependencies;

        }

        if (operationId == Guid.Empty)
        {

            return Integrity();

        }

        Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
            resolved = ResolveEvidenceLocation();

        if (resolved.IsFailure)
        {

            return resolved.Error;

        }

        (BackupRestoreProfileNamespace profile, InstallationResetActiveLocation location) =
            resolved.Value;

        Result<InstallationResetActiveAnchorV1?> anchorRead = _anchors!.Read(profile);

        if (anchorRead.IsFailure)
        {

            return anchorRead.Error;

        }

        if (anchorRead.Value is null)
        {

            return await RequireNoRetirementEvidenceAsync(
                    profile,
                    location,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        InstallationResetActiveAnchorV1 anchor = anchorRead.Value;

        if (anchor.State is InstallationResetActiveAnchorState.Closed)
        {

            if (anchor.OperationId != operationId)
            {

                return OperationConflict();

            }

            cancellationToken.ThrowIfCancellationRequested();

            using CancellationTokenSource checkpoint = CreateCheckpointToken();

            return await CompleteClosedSuffixAsync(
                    heldInstallationLock,
                    profile,
                    location,
                    anchor,
                    checkpoint.Token)
                .ConfigureAwait(false);

        }

        Result<InstallationResetActiveRecoveryState> recovered = await RecoverAsync(
            heldInstallationLock,
            cancellationToken).ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return recovered.Error;

        }

        if (recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } publication)
        {

            return EvidenceFailure();

        }

        if (publication.Envelope.OperationId != operationId)
        {

            return OperationConflict();

        }

        cancellationToken.ThrowIfCancellationRequested();

        InstallationResetActiveAnchorV1 closed = publication.Anchor with
        {
            State = InstallationResetActiveAnchorState.Closed,
        };

        Result stored = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            _guardedRoot,
            profile,
            publication.Anchor,
            closed);

        if (stored.IsFailure)
        {

            return stored;

        }

        using CancellationTokenSource cleanup = CreateCheckpointToken();

        return await CompleteClosedSuffixAsync(
                heldInstallationLock,
                profile,
                location,
                closed,
                cleanup.Token)
            .ConfigureAwait(false);

    }

    public async Task<Result> CompleteStartupCleanupAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(_guardedRoot);

        cancellationToken.ThrowIfCancellationRequested();

        Result dependencies = RequireAuthenticatedDependencies();

        if (dependencies.IsFailure)
        {

            return dependencies;

        }

        Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
            resolved = ResolveEvidenceLocation();

        if (resolved.IsFailure)
        {

            return resolved.Error;

        }

        (BackupRestoreProfileNamespace profile, InstallationResetActiveLocation location) =
            resolved.Value;

        Result<InstallationResetActiveAnchorV1?> anchorRead = _anchors!.Read(profile);

        if (anchorRead.IsFailure)
        {

            return anchorRead.Error;

        }

        if (anchorRead.Value is null)
        {

            return await CleanupKeyOnlySuffixAsync(
                    heldInstallationLock,
                    profile,
                    location,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        if (anchorRead.Value.State is not InstallationResetActiveAnchorState.Closed)
        {

            return EvidenceFailure();

        }

        cancellationToken.ThrowIfCancellationRequested();

        using CancellationTokenSource cleanup = CreateCheckpointToken();

        return await CompleteClosedSuffixAsync(
                heldInstallationLock,
                profile,
                location,
                anchorRead.Value,
                cleanup.Token)
            .ConfigureAwait(false);

    }

    private async Task<Result> RequireNoRetirementEvidenceAsync(
        BackupRestoreProfileNamespace profile,
        InstallationResetActiveLocation location,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveFileRead?> fileRead = await _files!
            .ReadIfPresentAsync(location, cancellationToken).ConfigureAwait(false);

        if (fileRead.IsFailure)
        {

            return fileRead.Error;

        }

        using InstallationResetActiveFileRead? file = fileRead.Value;

        if (file is not null)
        {

            return EvidenceFailure();

        }

        Result<bool> keyPresent = _keys!.IsPresent(profile);

        return keyPresent.IsFailure
            ? keyPresent.Error
            : keyPresent.Value
                ? EvidenceFailure()
                : Result.Success();

    }

    private async Task<Result> CleanupKeyOnlySuffixAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        BackupRestoreProfileNamespace profile,
        InstallationResetActiveLocation location,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetActiveFileRead?> fileRead = await _files!
            .ReadIfPresentAsync(location, cancellationToken).ConfigureAwait(false);

        if (fileRead.IsFailure)
        {

            return fileRead.Error;

        }

        using InstallationResetActiveFileRead? file = fileRead.Value;

        if (file is not null)
        {

            return EvidenceFailure();

        }

        Result<bool> keyPresent = _keys!.IsPresent(profile);

        if (keyPresent.IsFailure)
        {

            return keyPresent.Error;

        }

        cancellationToken.ThrowIfCancellationRequested();

        return keyPresent.Value
            ? _keys.RemoveAndVerifyAbsent(
                heldInstallationLock,
                _guardedRoot,
                profile)
            : Result.Success();

    }

    private async Task<Result> CompleteClosedSuffixAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        BackupRestoreProfileNamespace profile,
        InstallationResetActiveLocation location,
        InstallationResetActiveAnchorV1 closed,
        CancellationToken checkpointToken)
    {

        if (closed.State is not InstallationResetActiveAnchorState.Closed
            || closed.ProfileNamespaceDigest != profile.Digest
            || closed.ActiveLocationDigest != location.Digest)
        {

            return EvidenceFailure();

        }

        Result<BackupRestoreJournalIdentityProbe> identity = _identities!.Probe(profile);

        if (identity.IsFailure)
        {

            return identity.Error;

        }

        if (identity.Value.Presence is not BackupRestoreJournalIdentityPresence.Present
            || identity.Value.InstallationId != closed.InstallationId)
        {

            return EvidenceFailure();

        }

        Result<InstallationResetActiveFileRead?> fileRead;

        try
        {

            fileRead = await _files!.ReadIfPresentAsync(location, checkpointToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return CheckpointFailure();

        }

        if (fileRead.IsFailure)
        {

            return fileRead.Error;

        }

        using InstallationResetActiveFileRead? file = fileRead.Value;

        if (file is not null)
        {

            Result<InstallationResetActiveEnvelopeV2> envelope =
                InstallationResetActiveRecordAuthenticator.DecodeEnvelope(file.Bytes.Span);

            if (envelope.IsFailure
                || envelope.Value.ProfileNamespaceDigest != profile.Digest
                || envelope.Value.InstallationId != closed.InstallationId
                || envelope.Value.OperationId != closed.OperationId
                || envelope.Value.Revision != closed.Revision
                || envelope.Value.ActiveLocationDigest != location.Digest)
            {

                return EvidenceFailure();

            }

            Result<CovenantDigest> digest =
                InstallationResetActiveRecordAuthenticator.EnvelopeDigest(envelope.Value);

            if (digest.IsFailure || digest.Value != closed.EnvelopeDigest)
            {

                return EvidenceFailure();

            }

            Result<InstallationResetActivePayloadV2> opened = OpenEnvelope(
                profile,
                location,
                closed.InstallationId,
                envelope.Value);

            if (opened.IsFailure)
            {

                return opened.Error.Code == ErrorCodes.Covenant.NotFound
                    ? EvidenceFailure()
                    : opened.Error;

            }

            Result deleted = _files.DeleteDurably(
                heldInstallationLock,
                _guardedRoot,
                location,
                file.Metadata);

            if (deleted.IsFailure)
            {

                return deleted;

            }

        }
        else
        {

            Result absent = _files!.ProveAbsentDurably(
                heldInstallationLock,
                _guardedRoot,
                location);

            if (absent.IsFailure)
            {

                return absent;

            }

        }

        Result anchorRemoved = _anchors!.RemoveAndVerifyAbsent(
            heldInstallationLock,
            _guardedRoot,
            profile,
            closed);

        if (anchorRemoved.IsFailure)
        {

            return anchorRemoved;

        }

        return _keys!.RemoveAndVerifyAbsent(
            heldInstallationLock,
            _guardedRoot,
            profile);

    }

    private async Task<Result<InstallationResetActiveRecoveryState>> RecoverCoreAsync(
        ArcanumMaintenanceLock? heldInstallationLock,
        bool mayAdvanceAnchor,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Result dependencies = RequireAuthenticatedDependencies();

        if (dependencies.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(dependencies.Error);

        }

        Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
            resolved = ResolveEvidenceLocation();

        if (resolved.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(resolved.Error);

        }

        (BackupRestoreProfileNamespace profile, InstallationResetActiveLocation location) =
            resolved.Value;

        Result<InstallationResetActiveAnchorV1?> anchorRead = _anchors!.Read(profile);

        if (anchorRead.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(anchorRead.Error);

        }

        Result<InstallationResetActiveFileRead?> fileRead = await _files!
            .ReadIfPresentAsync(location, cancellationToken).ConfigureAwait(false);

        if (fileRead.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(fileRead.Error);

        }

        using InstallationResetActiveFileRead? file = fileRead.Value;

        if (anchorRead.Value is not { } anchor)
        {

            if (file is not null)
            {

                Result<InstallationResetActiveRecord> legacy = DecodeLegacyV1(file.Bytes.Span);

                if (legacy.IsFailure)
                {

                    return EvidenceFailure<InstallationResetActiveRecoveryState>();

                }

                Result<bool> legacyKeyPresent = _keys!.IsPresent(profile);

                if (legacyKeyPresent.IsFailure)
                {

                    return Result<InstallationResetActiveRecoveryState>.Failure(
                        legacyKeyPresent.Error);

                }

                return legacyKeyPresent.Value
                    ? EvidenceFailure<InstallationResetActiveRecoveryState>()
                    : Legacy(legacy.Value, file.Metadata.Identity);

            }

            Result<bool> keyPresent = _keys!.IsPresent(profile);

            if (keyPresent.IsFailure)
            {

                return Result<InstallationResetActiveRecoveryState>.Failure(keyPresent.Error);

            }

            return keyPresent.Value
                ? EvidenceFailure<InstallationResetActiveRecoveryState>()
                : new InstallationResetActiveRecoveryState(
                    InstallationResetActiveRecoveryOutcome.NoActiveRecord,
                    Publication: null,
                    LegacyRecord: null);

        }

        if (anchor.State is not InstallationResetActiveAnchorState.Active
            || anchor.ProfileNamespaceDigest != profile.Digest
            || anchor.ActiveLocationDigest != location.Digest)
        {

            return EvidenceFailure<InstallationResetActiveRecoveryState>();

        }

        if (file is null)
        {

            return EvidenceFailure<InstallationResetActiveRecoveryState>();

        }

        Result<BackupRestoreJournalIdentityProbe> identity = _identities!.Probe(profile);

        if (identity.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(identity.Error);

        }

        if (identity.Value.Presence is not BackupRestoreJournalIdentityPresence.Present
            || identity.Value.InstallationId != anchor.InstallationId)
        {

            return EvidenceFailure<InstallationResetActiveRecoveryState>();

        }

        Result<InstallationResetActiveEnvelopeV2> envelope =
            InstallationResetActiveRecordAuthenticator.DecodeEnvelope(file.Bytes.Span);

        if (envelope.IsFailure)
        {

            Result<InstallationResetActiveRecord> legacy = DecodeLegacyV1(file.Bytes.Span);

            bool resumableLegacy = legacy.IsSuccess
                && anchor.Version == InstallationResetActiveRecordAuthenticator.AnchorVersion
                && anchor.Revision == 0
                && anchor.EnvelopeDigest == InstallationResetActiveRecordAuthenticator.ZeroDigest
                && anchor.OperationId == legacy.Value.OperationId;

            if (!resumableLegacy)
            {

                return EvidenceFailure<InstallationResetActiveRecoveryState>();

            }

            Result<InstallationResetActiveRecordKeyLease> legacyKey = _keys!.OpenExisting(profile);

            if (legacyKey.IsFailure)
            {

                return legacyKey.Error.Code == ErrorCodes.Covenant.NotFound
                    ? EvidenceFailure<InstallationResetActiveRecoveryState>()
                    : Result<InstallationResetActiveRecoveryState>.Failure(legacyKey.Error);

            }

            legacyKey.Value.Dispose();

            return Legacy(legacy.Value, file.Metadata.Identity);

        }

        if (envelope.Value.ProfileNamespaceDigest != profile.Digest
            || envelope.Value.InstallationId != anchor.InstallationId
            || envelope.Value.OperationId != anchor.OperationId
            || envelope.Value.ActiveLocationDigest != location.Digest)
        {

            return EvidenceFailure<InstallationResetActiveRecoveryState>();

        }

        Result<InstallationResetActivePayloadV2> payload = OpenEnvelope(
            profile,
            location,
            anchor.InstallationId,
            envelope.Value);

        if (payload.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(payload.Error);

        }

        Result<CovenantDigest> digest =
            InstallationResetActiveRecordAuthenticator.EnvelopeDigest(envelope.Value);

        if (digest.IsFailure)
        {

            return Result<InstallationResetActiveRecoveryState>.Failure(digest.Error);

        }

        if (envelope.Value.Revision == anchor.Revision
            && digest.Value == anchor.EnvelopeDigest)
        {

            return Authenticated(
                location,
                envelope.Value,
                digest.Value,
                payload.Value,
                anchor);

        }

        bool oneAhead = anchor.Revision < InstallationResetActiveRecordAuthenticator.MaxRevision
            && envelope.Value.Revision == anchor.Revision + 1
            && envelope.Value.PreviousEnvelopeDigest == anchor.EnvelopeDigest;

        if (!oneAhead || !mayAdvanceAnchor || heldInstallationLock is null)
        {

            return EvidenceFailure<InstallationResetActiveRecoveryState>();

        }

        InstallationResetActiveAnchorV1 advanced = anchor with
        {
            Revision = envelope.Value.Revision,

            EnvelopeDigest = digest.Value,
        };

        Result stored = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            _guardedRoot,
            profile,
            anchor,
            advanced);

        return stored.IsFailure
            ? Result<InstallationResetActiveRecoveryState>.Failure(stored.Error)
            : Authenticated(
                location,
                envelope.Value,
                digest.Value,
                payload.Value,
                advanced);

    }

    private async Task<Result<InstallationResetActivePublication>> PublishAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        BackupRestoreProfileNamespace profile,
        InstallationResetActiveLocation location,
        InstallationResetActiveAnchorV1 anchor,
        InstallationResetActiveRecordKeyLease sealingKey,
        InstallationResetActivePayloadV2 payload,
        CovenantDigest previousEnvelopeDigest,
        CancellationToken cancellationToken)
    {

        if (anchor.Revision >= InstallationResetActiveRecordAuthenticator.MaxRevision)
        {

            return Conflict<InstallationResetActivePublication>();

        }

        ulong revision = anchor.Revision + 1;

        Result<InstallationResetActiveEnvelopeV2> sealed_ =
            InstallationResetActiveRecordAuthenticator.Seal(
                sealingKey,
                location,
                anchor.InstallationId,
                revision,
                previousEnvelopeDigest,
                payload);

        if (sealed_.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(sealed_.Error);

        }

        Result<CovenantDigest> digest =
            InstallationResetActiveRecordAuthenticator.EnvelopeDigest(sealed_.Value);

        Result<byte[]> encoded =
            InstallationResetActiveRecordAuthenticator.EncodeEnvelope(sealed_.Value);

        if (digest.IsFailure || encoded.IsFailure)
        {

            return digest.IsFailure
                ? Result<InstallationResetActivePublication>.Failure(digest.Error)
                : Result<InstallationResetActivePublication>.Failure(encoded.Error);

        }

        Result written = await _files!.ReplaceDurablyAsync(
            heldInstallationLock,
            _guardedRoot,
            location,
            encoded.Value,
            cancellationToken).ConfigureAwait(false);

        if (written.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(written.Error);

        }

        using CancellationTokenSource checkpoint = CreateCheckpointToken();

        Result<InstallationResetActiveFileRead?> reread;

        try
        {

            reread = await _files
                .ReadIfPresentAsync(location, checkpoint.Token).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            return Result<InstallationResetActivePublication>.Failure(
                CheckpointFailure().Error);

        }

        if (reread.IsFailure || reread.Value is null)
        {

            return reread.IsFailure
                ? Result<InstallationResetActivePublication>.Failure(reread.Error)
                : EvidenceFailure<InstallationResetActivePublication>();

        }

        using (reread.Value)
        {

            Result<InstallationResetActiveEnvelopeV2> landed =
                InstallationResetActiveRecordAuthenticator.DecodeEnvelope(reread.Value.Bytes.Span);

            if (landed.IsFailure || landed.Value != sealed_.Value)
            {

                return EvidenceFailure<InstallationResetActivePublication>();

            }

            Result<CovenantDigest> landedDigest =
                InstallationResetActiveRecordAuthenticator.EnvelopeDigest(landed.Value);

            if (landedDigest.IsFailure || landedDigest.Value != digest.Value)
            {

                return EvidenceFailure<InstallationResetActivePublication>();

            }

            Result<InstallationResetActivePayloadV2> opened = OpenEnvelope(
                profile,
                location,
                anchor.InstallationId,
                landed.Value);

            if (opened.IsFailure || !SamePayload(payload, opened.Value))
            {

                return opened.IsFailure
                    ? Result<InstallationResetActivePublication>.Failure(opened.Error)
                    : EvidenceFailure<InstallationResetActivePublication>();

            }

            InstallationResetActiveAnchorV1 advanced = anchor with
            {
                Revision = revision,

                EnvelopeDigest = digest.Value,
            };

            Result stored = _anchors!.CompareWriteAndVerify(
                heldInstallationLock,
                _guardedRoot,
                profile,
                anchor,
                advanced);

            return stored.IsFailure
                ? Result<InstallationResetActivePublication>.Failure(stored.Error)
                : new InstallationResetActivePublication(
                    location,
                    landed.Value,
                    digest.Value,
                    opened.Value,
                    advanced);

        }

    }

    private Result<InstallationResetActivePayloadV2> OpenEnvelope(
        BackupRestoreProfileNamespace profile,
        InstallationResetActiveLocation location,
        Guid installationId,
        InstallationResetActiveEnvelopeV2 envelope)
    {

        Result<InstallationResetActiveRecordKeyLease> key = _keys!.OpenExisting(profile);

        if (key.IsFailure)
        {

            return key.Error.Code == ErrorCodes.Covenant.NotFound
                ? EvidenceFailure<InstallationResetActivePayloadV2>()
                : Result<InstallationResetActivePayloadV2>.Failure(key.Error);

        }

        using (key.Value)
        {

            return InstallationResetActiveRecordAuthenticator.Open(
                key.Value,
                location,
                installationId,
                envelope);

        }

    }

    private Result<(BackupRestoreProfileNamespace Profile, InstallationResetActiveLocation Location)>
        ResolveEvidenceLocation()
    {

        Result<BackupRestoreProfileNamespace> profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guardedRoot);

        if (profile.IsFailure)
        {

            return Result<(
                BackupRestoreProfileNamespace Profile,
                InstallationResetActiveLocation Location)>.Failure(profile.Error);

        }

        Result<InstallationResetActiveLocation> location =
            InstallationResetActiveRecordAuthenticator.ResolveLocation(
                _guardedRoot,
                profile.Value);

        return location.IsFailure
            ? Result<(
                BackupRestoreProfileNamespace Profile,
                InstallationResetActiveLocation Location)>.Failure(location.Error)
            : (profile.Value, location.Value);

    }

    private Result RequireAuthenticatedDependencies() =>
        _keys is not null && _anchors is not null && _identities is not null && _files is not null
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.Unavailable,
                "The authenticated installation-reset active store is not composed.");

    private static Result<InstallationResetActivePayloadV2> ToAuthenticatedPayload(
        InstallationResetActiveRecord record)
    {

        try
        {

            if (record.Version is not CurrentVersion
                and not InstallationResetActiveRecordAuthenticator.EnvelopeVersion
                || !IsValid(record with { Version = CurrentVersion }))
            {

                return Integrity<InstallationResetActivePayloadV2>();

            }

            InstallationResetActivePayloadV2 payload =
                InstallationResetActivePayloadV2.FromRecord(record);

            Result valid = InstallationResetActiveRecordAuthenticator.ValidatePayload(payload);

            return valid.IsSuccess
                ? payload
                : Result<InstallationResetActivePayloadV2>.Failure(valid.Error);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NullReferenceException)
        {

            return Integrity<InstallationResetActivePayloadV2>();

        }

    }

    private static Result<InstallationResetActiveRecord> DecodeLegacyV1(
        ReadOnlySpan<byte> bytes)
    {

        if (bytes.IsEmpty || bytes.Length > MaxBytes)
        {

            return Integrity<InstallationResetActiveRecord>();

        }

        try
        {

            InstallationResetActiveRecord? record = JsonSerializer.Deserialize(
                bytes,
                InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord);

            if (record is null || !IsValid(record))
            {

                return Integrity<InstallationResetActiveRecord>();

            }

            byte[] currentCanonical = JsonSerializer.SerializeToUtf8Bytes(
                record,
                InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord);

            if (bytes.SequenceEqual(currentCanonical))
            {

                return record;

            }

            if (record.DataHandoff is not null || record.OnlineDataCompletion is not null)
            {

                return Integrity<InstallationResetActiveRecord>();

            }

            // The only older canonical spelling predates these two trailing nullable fields.
            ReadOnlySpan<byte> appendedFields =
                ",\"dataHandoff\":null,\"onlineDataCompletion\":null}"u8;

            int historicalPrefixLength = currentCanonical.Length - appendedFields.Length;

            bool historicalCanonical = historicalPrefixLength >= 0
                && currentCanonical.AsSpan().EndsWith(appendedFields)
                && bytes.Length == historicalPrefixLength + 1
                && bytes[..historicalPrefixLength]
                    .SequenceEqual(currentCanonical.AsSpan(0, historicalPrefixLength))
                && bytes[^1] == (byte)'}';

            return historicalCanonical
                ? record
                : Integrity<InstallationResetActiveRecord>();

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or ArgumentException
                or InvalidOperationException
                or NullReferenceException)
        {

            return Integrity<InstallationResetActiveRecord>();

        }

    }

    private static bool SamePublication(
        InstallationResetActivePublication expected,
        InstallationResetActivePublication actual) =>
        expected.Location == actual.Location
        && expected.Envelope == actual.Envelope
        && expected.EnvelopeDigest == actual.EnvelopeDigest
        && expected.Anchor == actual.Anchor
        && SamePayload(expected.Payload, actual.Payload);

    private static bool IsMonotonicTransition(
        InstallationResetActivePayloadV2 current,
        InstallationResetActivePayloadV2 next)
    {

        if (next.OperationId != current.OperationId
            || !string.Equals(next.PlanId, current.PlanId, StringComparison.Ordinal)
            || next.Scope != current.Scope
            || next.Workspace != current.Workspace
            || !SameBinding(current.AcceptedBinding, next.AcceptedBinding)
            || (int)next.Phase < (int)current.Phase
            || current.PointOfNoReturn && !next.PointOfNoReturn
            || next.RowsDeleted < current.RowsDeleted
            || next.FilesDeleted < current.FilesDeleted
            || next.EstimatedBytesDeleted < current.EstimatedBytesDeleted
            || next.DataHandoff != current.DataHandoff
            || current.OnlineDataCompletion is not null
                && next.OnlineDataCompletion != current.OnlineDataCompletion
            || !SameClaim(
                current.FullInstallationResetRemediationClaim,
                next.FullInstallationResetRemediationClaim)
            || !IsCheckpointTransition(
                current.HostToolsMarkerPairReset,
                next.HostToolsMarkerPairReset)
            || !CredentialsAreMonotonic(current.CredentialResults, next.CredentialResults))
        {

            return false;

        }

        return true;

    }

    private static bool SameBinding(
        InstallationResetActiveAcceptedBindingV2 left,
        InstallationResetActiveAcceptedBindingV2 right) =>
        string.Equals(left.BindingId, right.BindingId, StringComparison.Ordinal)
        && left.SelectedRoots.SequenceEqual(right.SelectedRoots, StringComparer.Ordinal)
        && left.ExcludedRoots.SequenceEqual(right.ExcludedRoots, StringComparer.Ordinal)
        && left.PreservedBackups.SequenceEqual(right.PreservedBackups)
        && left.CredentialAccounts.SequenceEqual(right.CredentialAccounts, StringComparer.Ordinal)
        && left.DataPlanIds.SequenceEqual(right.DataPlanIds, StringComparer.Ordinal);

    private static bool CredentialsAreMonotonic(
        System.Collections.Immutable.ImmutableArray<InstallationResetActiveCredentialResultV2> current,
        System.Collections.Immutable.ImmutableArray<InstallationResetActiveCredentialResultV2> next)
    {

        Dictionary<string, InstallationResetActiveCredentialResultV2> nextByAccount =
            new(StringComparer.Ordinal);

        foreach (InstallationResetActiveCredentialResultV2 result in next)
        {

            if (!nextByAccount.TryAdd(result.Account, result))
            {

                return false;

            }

        }

        foreach (InstallationResetActiveCredentialResultV2 prior in current)
        {

            if (!nextByAccount.TryGetValue(prior.Account, out var later))
            {

                return false;

            }

            bool priorSucceeded = prior.Status is InstallationResetItemStatus.Preserved
                or InstallationResetItemStatus.Deleted
                or InstallationResetItemStatus.Absent;

            bool laterFailed = later.Status is InstallationResetItemStatus.Pending
                or InstallationResetItemStatus.Unavailable
                or InstallationResetItemStatus.Failed;

            if (priorSucceeded && laterFailed)
            {

                return false;

            }

            if (prior.Status is InstallationResetItemStatus.Preserved
                && later.Status is not InstallationResetItemStatus.Preserved)
            {

                return false;

            }

        }

        return true;

    }

    private static bool SamePayload(
        InstallationResetActivePayloadV2 expected,
        InstallationResetActivePayloadV2 actual) =>
        expected.Version == actual.Version
        && expected.OperationId == actual.OperationId
        && string.Equals(expected.PlanId, actual.PlanId, StringComparison.Ordinal)
        && expected.Scope == actual.Scope
        && expected.Workspace == actual.Workspace
        && SameBinding(expected.AcceptedBinding, actual.AcceptedBinding)
        && expected.Phase == actual.Phase
        && expected.PointOfNoReturn == actual.PointOfNoReturn
        && expected.RowsDeleted == actual.RowsDeleted
        && expected.FilesDeleted == actual.FilesDeleted
        && expected.EstimatedBytesDeleted == actual.EstimatedBytesDeleted
        && expected.CredentialResults.SequenceEqual(actual.CredentialResults)
        && string.Equals(expected.LastErrorCode, actual.LastErrorCode, StringComparison.Ordinal)
        && expected.DataHandoff == actual.DataHandoff
        && expected.OnlineDataCompletion == actual.OnlineDataCompletion
        && SameCheckpoint(
            expected.HostToolsMarkerPairReset,
            actual.HostToolsMarkerPairReset)
        && SameClaim(
            expected.FullInstallationResetRemediationClaim,
            actual.FullInstallationResetRemediationClaim);

    private static bool IsCheckpointTransition(
        HostToolsMarkerPairResetCheckpointV1? current,
        HostToolsMarkerPairResetCheckpointV1? next)
    {

        if (current is null)
        {

            return next is null
                || next.Phase is HostToolsMarkerPairResetPhase.PairJournaled
                    && ReceiptIsNull(next);

        }

        if (next is null
            || !SameCheckpointEvidence(current, next)
            || (int)next.Phase < (int)current.Phase
            || (int)next.Phase > (int)current.Phase + 1)
        {

            return false;

        }

        if (next.Phase != current.Phase)
        {

            return SameReceipt(current, next);

        }

        if (current.Phase is not HostToolsMarkerPairResetPhase.PairAbsenceVerified)
        {

            return SameReceipt(current, next);

        }

        if (ReceiptIsNull(current))
        {

            return ReceiptIsNull(next) || ReceiptIsPrepared(next);

        }

        return ReceiptNeedsTerminalPublication(current)
            && ReceiptIsTerminal(next)
            && SameFixedReceipt(current, next);

    }

    private static bool SameCheckpoint(
        HostToolsMarkerPairResetCheckpointV1? left,
        HostToolsMarkerPairResetCheckpointV1? right) =>
        left is null && right is null
        || left is not null
            && right is not null
            && left.Phase == right.Phase
            && SameCheckpointEvidence(left, right)
            && SameReceipt(left, right);

    private static bool SameCheckpointEvidence(
        HostToolsMarkerPairResetCheckpointV1 left,
        HostToolsMarkerPairResetCheckpointV1 right) =>
        left.Version == right.Version
        && SameRestartProof(left.RestartProof, right.RestartProof)
        && SameCampaignInventory(left.CampaignInventory, right.CampaignInventory)
        && SameDigest(
            left.CampaignMarkerInventoryDigest,
            right.CampaignMarkerInventoryDigest)
        && SameDigest(left.OwnerEffectDigest, right.OwnerEffectDigest);

    private static bool SameRestartProof(
        FullInstallationResetRestartProofV1 left,
        FullInstallationResetRestartProofV1 right) =>
        left.Version == right.Version
        && SameSignedProjection(left.SignedAttestation, right.SignedAttestation)
        && left.AcceptedAtUtc == right.AcceptedAtUtc
        && SameDigest(left.SignedAttestationDigest, right.SignedAttestationDigest)
        && SameDatabaseEvidence(
            left.DatabaseMarkerEvidence,
            right.DatabaseMarkerEvidence)
        && SameOsEvidence(left.OsMarkerEvidence, right.OsMarkerEvidence)
        && SameDigest(left.PairEvidenceDigest, right.PairEvidenceDigest);

    private static bool SameSignedProjection(
        FullInstallationResetSignedAttestationProjectionV1 left,
        FullInstallationResetSignedAttestationProjectionV1 right) =>
        left.Version == right.Version
        && left.OperationId == right.OperationId
        && left.InstallationId == right.InstallationId
        && left.HostToolsTransitionId == right.HostToolsTransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && SameDigest(left.AuthorityFingerprint, right.AuthorityFingerprint)
        && SameDigest(left.DatabaseMarkerDigest, right.DatabaseMarkerDigest)
        && SameDigest(left.OsMarkerDigest, right.OsMarkerDigest)
        && SameDigest(left.RemediationActionDigest, right.RemediationActionDigest)
        && string.Equals(left.NonceBase64Url, right.NonceBase64Url, StringComparison.Ordinal)
        && string.Equals(left.Issuer, right.Issuer, StringComparison.Ordinal)
        && left.IssuedAtUtc == right.IssuedAtUtc
        && left.ExpiresAtUtc == right.ExpiresAtUtc
        && string.Equals(
            left.SignatureBase64Url,
            right.SignatureBase64Url,
            StringComparison.Ordinal);

    private static bool SameDatabaseEvidence(
        RetroDownfall.Arcanum.Core.Security.HostProcessToolsDatabaseMarkerEvidence left,
        RetroDownfall.Arcanum.Core.Security.HostProcessToolsDatabaseMarkerEvidence right) =>
        string.Equals(
            left.InstallationIdentity,
            right.InstallationIdentity,
            StringComparison.Ordinal)
        && left.State == right.State
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && SameOptionalDigest(left.TaintFingerprint, right.TaintFingerprint)
        && SameOptionalDigest(left.TaintIdentityDigest, right.TaintIdentityDigest)
        && SameDigest(left.DatabaseMarkerDigest, right.DatabaseMarkerDigest);

    private static bool SameOsEvidence(
        RetroDownfall.Arcanum.Core.Security.HostProcessToolsOsMarkerEvidence left,
        RetroDownfall.Arcanum.Core.Security.HostProcessToolsOsMarkerEvidence right) =>
        string.Equals(
            left.InstallationIdentity,
            right.InstallationIdentity,
            StringComparison.Ordinal)
        && left.TransitionId == right.TransitionId
        && left.TaintMasterKeyVersion == right.TaintMasterKeyVersion
        && SameDigest(left.TaintFingerprint, right.TaintFingerprint)
        && SameDigest(left.MarkerBytesDigest, right.MarkerBytesDigest)
        && SameDigest(left.DurableIdentityDigest, right.DurableIdentityDigest)
        && SameDigest(left.TaintIdentityDigest, right.TaintIdentityDigest);

    private static bool SameCampaignInventory(
        ImmutableArray<CampaignMarkerInventoryEntryV1> left,
        ImmutableArray<CampaignMarkerInventoryEntryV1> right)
    {

        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
        {

            return left.IsDefault && right.IsDefault;

        }

        for (int index = 0; index < left.Length; index++)
        {

            CampaignMarkerInventoryEntryV1 leftEntry = left[index];

            CampaignMarkerInventoryEntryV1 rightEntry = right[index];

            if (leftEntry.CampaignId != rightEntry.CampaignId
                || leftEntry.PriorPathRevision != rightEntry.PriorPathRevision
                || !SameDigest(leftEntry.MarkerDigest, rightEntry.MarkerDigest)
                || !SameDigest(
                    leftEntry.IndexedPhysicalIdentityDigest,
                    rightEntry.IndexedPhysicalIdentityDigest)
                || !SameDigest(
                    leftEntry.CanonicalDisplayPathDigest,
                    rightEntry.CanonicalDisplayPathDigest)
                || !SameDigest(
                    leftEntry.SameHandleOwnershipEvidenceDigest,
                    rightEntry.SameHandleOwnershipEvidenceDigest))
            {

                return false;

            }

        }

        return true;

    }

    private static bool SameReceipt(
        HostToolsMarkerPairResetCheckpointV1 left,
        HostToolsMarkerPairResetCheckpointV1 right) =>
        left.MarkerIntentCount == right.MarkerIntentCount
        && SameIntentIds(left.OrderedMarkerIntentIds, right.OrderedMarkerIntentIds)
        && SameOptionalDigest(
            left.MarkerIntentVectorDigest,
            right.MarkerIntentVectorDigest)
        && left.DeletedCount == right.DeletedCount
        && left.OrphanCount == right.OrphanCount;

    private static bool ReceiptIsNull(HostToolsMarkerPairResetCheckpointV1 checkpoint) =>
        checkpoint.MarkerIntentCount is null
        && checkpoint.OrderedMarkerIntentIds is null
        && checkpoint.MarkerIntentVectorDigest is null
        && checkpoint.DeletedCount is null
        && checkpoint.OrphanCount is null;

    private static bool ReceiptIsPrepared(HostToolsMarkerPairResetCheckpointV1 checkpoint) =>
        checkpoint.MarkerIntentCount is not null
        && checkpoint.OrderedMarkerIntentIds is not null
        && checkpoint.MarkerIntentVectorDigest is not null
        && checkpoint.DeletedCount is 0
        && checkpoint.OrphanCount is 0;

    private static bool ReceiptNeedsTerminalPublication(
        HostToolsMarkerPairResetCheckpointV1 checkpoint) =>
        ReceiptIsPrepared(checkpoint)
        && checkpoint.MarkerIntentCount is > 0;

    private static bool ReceiptIsTerminal(
        HostToolsMarkerPairResetCheckpointV1 checkpoint)
    {

        if (checkpoint.MarkerIntentCount is not { } count
            || checkpoint.OrderedMarkerIntentIds is null
            || checkpoint.MarkerIntentVectorDigest is null
            || checkpoint.DeletedCount is not { } deleted
            || checkpoint.OrphanCount is not { } orphan)
        {

            return false;

        }

        return count == 0
            ? deleted == 0 && orphan == 0
            : (deleted != 0 || orphan != 0)
                && deleted <= count
                && orphan == count - deleted;

    }

    private static bool SameFixedReceipt(
        HostToolsMarkerPairResetCheckpointV1 left,
        HostToolsMarkerPairResetCheckpointV1 right) =>
        left.MarkerIntentCount == right.MarkerIntentCount
        && SameIntentIds(left.OrderedMarkerIntentIds, right.OrderedMarkerIntentIds)
        && SameOptionalDigest(
            left.MarkerIntentVectorDigest,
            right.MarkerIntentVectorDigest);

    private static bool SameIntentIds(
        ImmutableArray<Guid>? left,
        ImmutableArray<Guid>? right)
    {

        if (left is null || right is null)
        {

            return left is null && right is null;

        }

        if (left.Value.IsDefault
            || right.Value.IsDefault
            || left.Value.Length != right.Value.Length)
        {

            return left.Value.IsDefault && right.Value.IsDefault;

        }

        return left.Value.AsSpan().SequenceEqual(right.Value.AsSpan());

    }

    private static bool SameClaim(
        FullInstallationResetRemediationClaimV1? left,
        FullInstallationResetRemediationClaimV1? right) =>
        left is null && right is null
        || left is not null
            && right is not null
            && left.Version == right.Version
            && left.OperationId == right.OperationId
            && left.InstallationId == right.InstallationId
            && SameDigest(left.AttestationDigest, right.AttestationDigest)
            && SameDigest(left.NonceDigest, right.NonceDigest)
            && SameDigest(left.IssuerDigest, right.IssuerDigest)
            && left.AcceptedAtUtc == right.AcceptedAtUtc;

    private static bool SameOptionalDigest(
        CovenantDigest? left,
        CovenantDigest? right) =>
        left is null && right is null
        || left is { } leftDigest
            && right is { } rightDigest
            && SameDigest(leftDigest, rightDigest);

    private static bool SameDigest(CovenantDigest left, CovenantDigest right) =>
        left.Bytes.AsSpan().SequenceEqual(right.Bytes);

    private static bool SameLegacyRecord(
        InstallationResetActiveRecord expected,
        InstallationResetActiveRecord actual)
    {

        try
        {

            byte[] expectedBytes = JsonSerializer.SerializeToUtf8Bytes(
                expected,
                InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord);

            byte[] actualBytes = JsonSerializer.SerializeToUtf8Bytes(
                actual,
                InstallationResetActiveLegacyJsonContext.Default.InstallationResetActiveRecord);

            return expectedBytes.AsSpan().SequenceEqual(actualBytes);

        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {

            return false;

        }

    }

    private static Result<InstallationResetActiveRecoveryState> Authenticated(
        InstallationResetActiveLocation location,
        InstallationResetActiveEnvelopeV2 envelope,
        CovenantDigest digest,
        InstallationResetActivePayloadV2 payload,
        InstallationResetActiveAnchorV1 anchor) =>
        new InstallationResetActiveRecoveryState(
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
            new InstallationResetActivePublication(location, envelope, digest, payload, anchor),
            LegacyRecord: null);

    private static Result<InstallationResetActiveRecoveryState> Legacy(
        InstallationResetActiveRecord record,
        FileHandleIdentity identity) =>
        new InstallationResetActiveRecoveryState(
            InstallationResetActiveRecoveryOutcome.LegacyV1,
            Publication: null,
            record,
            identity);

    private static Result<T> Integrity<T>() =>
        new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The installation-reset active evidence did not authenticate.");

    private static Result<T> Conflict<T>() =>
        new Error(
            ErrorCodes.Covenant.RevisionConflict,
            "The installation-reset active transition does not match the current publication.");

    private static Result Integrity() =>
        new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "The installation-reset active evidence did not authenticate.");

    private static Result EvidenceFailure() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The installation-reset active evidence requires authenticated recovery.");

    private static Result<T> EvidenceFailure<T>() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The installation-reset active evidence requires authenticated recovery.");

    private static Result OperationConflict() =>
        new Error(
            ErrorCodes.Data.ResetInProgress,
            "A different installation reset owns the authenticated active evidence.");

    private static Result CheckpointFailure() =>
        new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The installation-reset retirement checkpoint requires recovery.");

    private static CancellationTokenSource CreateCheckpointToken() =>
        new(TimeSpan.FromSeconds(5));


    private static Result Failure(
        string message,
        string code = ErrorCodes.Data.ControlPathUnavailable) =>
        Result.Failure(new Error(code, message));

    private static Result<T> Failure<T>(string message) =>
        Result<T>.Failure(new Error(ErrorCodes.Data.ControlPathUnavailable, message));

    private static bool IsValid(InstallationResetActiveRecord record)
    {

        if (record.Version != CurrentVersion
            || record.OperationId == Guid.Empty
            || string.IsNullOrWhiteSpace(record.PlanId)
            || !Enum.IsDefined(record.Scope)
            || !Enum.IsDefined(record.Phase)
            || record.RowsDeleted < 0
            || record.FilesDeleted < 0
            || record.EstimatedBytesDeleted < 0
            || record.AcceptedBinding is not { } binding
            || string.IsNullOrWhiteSpace(binding.BindingId)
            || binding.SelectedRoots is null
            || binding.ExcludedRoots is null
            || binding.PreservedBackups is null
            || binding.CredentialAccounts is null
            || binding.DataPlanIds is null
            || record.CredentialResults is null)
        {

            return false;

        }

        bool workspaceRequired = record.Scope is InstallationResetScope.Workspace
            or InstallationResetScope.All;

        if (workspaceRequired != (record.Workspace is not null)
            || record.Workspace is { } workspace
                && (workspace.CampaignId == Guid.Empty
                    || string.IsNullOrWhiteSpace(workspace.WorkspaceRoot))
            || binding.SelectedRoots.Any(string.IsNullOrWhiteSpace)
            || binding.ExcludedRoots.Any(string.IsNullOrWhiteSpace)
            || binding.CredentialAccounts.Any(string.IsNullOrWhiteSpace)
            || binding.DataPlanIds.Any(string.IsNullOrWhiteSpace))
        {

            return false;

        }

        foreach (InstallationResetPreservedBackup backup in binding.PreservedBackups)
        {

            if (backup is null
                || string.IsNullOrWhiteSpace(backup.CanonicalPath)
                || backup.Identity is null
                || string.IsNullOrWhiteSpace(backup.Identity.Value)
                || backup.Identity.Length < 0
                || backup.Identity.HardLinkCount == 0)
            {

                return false;

            }

        }

        foreach (InstallationResetCredentialResult result in record.CredentialResults)
        {

            if (result is null
                || string.IsNullOrWhiteSpace(result.Account)
                || !Enum.IsDefined(result.Status))
            {

                return false;

            }

        }

        if (record.DataHandoff is null)
        {

            return record.OnlineDataCompletion is null;

        }

        if (record.DataHandoff is not InstallationResetDataHandoff.HostFactoryErasure
            || record.Scope is not (InstallationResetScope.Global or InstallationResetScope.All)
            || binding.DataPlanIds.Length != 1
            || string.IsNullOrWhiteSpace(binding.DataPlanIds[0]))
        {

            return false;

        }

        if (record.OnlineDataCompletion is not { } completion)
        {

            return record.Phase is InstallationResetPhase.Prepared;

        }

        return completion.ServerOperationId != Guid.Empty
            && completion.RequestedOperationId == record.OperationId
            && completion.ServerOperationId != completion.RequestedOperationId
            && string.Equals(
                completion.DataPlanId,
                binding.DataPlanIds[0],
                StringComparison.Ordinal)
            && completion.RowsDeleted >= 0
            && completion.FilesDeleted >= 0
            && completion.EstimatedBytesDeleted >= 0
            && completion.DerivedRecordsDeleted >= 0;

    }

}
