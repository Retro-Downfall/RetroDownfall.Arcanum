using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal sealed record GrimoireOfflineTransitionJournalPublication(
    GrimoireOfflineTransitionJournalLocation Location,
    GrimoireOfflineTransitionEnvelopeV1 Envelope,
    CovenantDigest EnvelopeDigest,
    byte[] PayloadBytes,
    GrimoireOfflineTransitionAnchorV1 Anchor,
    FileHandleMetadata FileMetadata);

internal enum GrimoireOfflineTransitionJournalRecoveryOutcome : byte
{

    NoActiveJournal = 1,

    Authenticated = 2,

}

internal sealed record GrimoireOfflineTransitionJournalRecoveryState(
    GrimoireOfflineTransitionJournalRecoveryOutcome Outcome,
    GrimoireOfflineTransitionJournalPublication? Publication,
    Guid? OperationId = null);

internal delegate Task<Result> GrimoireOfflineTransitionJournalReplaceDurably(
    ArcanumMaintenanceLock heldInstallationLock,
    GrimoireOfflineTransitionJournalLocation location,
    ReadOnlyMemory<byte> bytes,
    FileHandleIdentity? expectedCurrentIdentity,
    CancellationToken cancellationToken);

internal delegate Result<ReadOnlyMemory<byte>> GrimoireOfflineTransitionJournalPayloadFactory(
    ulong slotEpoch);

internal interface IGrimoireOfflineTransitionJournalStore
{

    Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid installationId,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlyMemory<byte> payloadBytes,
        CancellationToken cancellationToken);

    Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginBoundAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid installationId,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        GrimoireOfflineTransitionJournalPayloadFactory payloadFactory,
        CancellationToken cancellationToken);

    Task<Result<GrimoireOfflineTransitionJournalPublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication current,
        ReadOnlyMemory<byte> payloadBytes,
        CancellationToken cancellationToken);

    Task<Result<GrimoireOfflineTransitionJournalRecoveryState>> RecoverAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        CancellationToken cancellationToken);

    Task<Result> RetireAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication terminal,
        CancellationToken cancellationToken);

}

internal sealed class GrimoireOfflineTransitionJournalStore : IGrimoireOfflineTransitionJournalStore
{

    private static readonly CovenantDigest ZeroDigest = new(new byte[32]);

    private readonly GrimoireOfflineTransitionJournalKeyProvider _keys;

    private readonly BackupRestoreJournalInstallationIdentityProvider _identities;

    private readonly GrimoireOfflineTransitionJournalFileStore _files;

    private readonly GrimoireOfflineTransitionJournalAnchorStore _anchors;

    private readonly Action<string>? _afterStep;

    private readonly GrimoireOfflineTransitionJournalReplaceDurably _replaceDurably;

    internal GrimoireOfflineTransitionJournalStore(IOsCredentialStore credentials)
        : this(
            credentials,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(credentials),
            afterStep: null)
    {

    }

    public async Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginBoundAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid installationId,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        GrimoireOfflineTransitionJournalPayloadFactory payloadFactory,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(payloadFactory);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<GrimoireOfflineTransitionJournalLocation> resolved =
            _files.ResolveLocation(guardedDirectory);

        if (resolved.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(resolved.Error);

        }

        Result<GrimoireOfflineTransitionAnchorV1?> read = _anchors.Read(resolved.Value);

        if (read.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(read.Error);

        }

        ulong slotEpoch;

        try
        {

            slotEpoch = read.Value is { State: GrimoireOfflineTransitionAnchorState.Active } active
                ? active.SlotEpoch
                : checked((read.Value?.SlotEpoch ?? 0) + 1);

        }
        catch (OverflowException)
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<ReadOnlyMemory<byte>> payload = payloadFactory(slotEpoch);

        if (payload.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(payload.Error);

        }

        return await BeginAsync(
                heldInstallationLock,
                guardedDirectory,
                installationId,
                operationId,
                kind,
                payloadVersion,
                payload.Value,
                cancellationToken)
            .ConfigureAwait(false);

    }

    internal GrimoireOfflineTransitionJournalStore(
        IOsCredentialStore credentials,
        GrimoireOfflineTransitionJournalFileStore files,
        GrimoireOfflineTransitionJournalAnchorStore anchors,
        Action<string>? afterStep = null,
        GrimoireOfflineTransitionJournalReplaceDurably? replaceDurably = null)
    {

        ArgumentNullException.ThrowIfNull(credentials);

        _keys = new GrimoireOfflineTransitionJournalKeyProvider(credentials);

        _identities = new BackupRestoreJournalInstallationIdentityProvider(credentials);

        _files = files ?? throw new ArgumentNullException(nameof(files));

        _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));

        _afterStep = afterStep;

        _replaceDurably = replaceDurably ?? files.ReplaceDurablyAsync;

    }

    public async Task<Result<GrimoireOfflineTransitionJournalPublication>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid installationId,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlyMemory<byte> payloadBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<GrimoireOfflineTransitionJournalLocation> resolved =
            _files.ResolveLocation(guardedDirectory);

        if (resolved.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(resolved.Error);

        }

        GrimoireOfflineTransitionJournalLocation location = resolved.Value;

        if (!ValidRequest(installationId, operationId, kind, payloadVersion, payloadBytes))
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        Result identity = _identities.RequireMatchesDatabase(
            location.ProfileNamespace,
            installationId);

        if (identity.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(identity.Error);

        }

        Result<GrimoireOfflineTransitionAnchorV1?> anchorResult = _anchors.Read(location);

        if (anchorResult.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(anchorResult.Error);

        }

        Result<GrimoireOfflineTransitionJournalEvidence> evidenceResult =
            await _files.InspectEvidenceAsync(location, cancellationToken).ConfigureAwait(false);

        if (evidenceResult.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        using GrimoireOfflineTransitionJournalEvidence evidence = evidenceResult.Value;

        GrimoireOfflineTransitionAnchorV1? existing = anchorResult.Value;

        bool keyPresenceProved = false;

        if (existing is null)
        {

            if (!AllAbsent(evidence))
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

            }

            Result<bool> keyPresentBeforeGenesis = _keys.IsPresent(location.ProfileNamespace);

            if (keyPresentBeforeGenesis.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalPublication>.Failure(
                    keyPresentBeforeGenesis.Error);

            }

            if (keyPresentBeforeGenesis.Value)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

            }

            Result<GrimoireOfflineTransitionJournalKeyLease> created = _keys.CreateOrOpen(
                heldInstallationLock,
                location.GuardedDirectory,
                location.ProfileNamespace);

            if (created.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalPublication>.Failure(created.Error);

            }

            using (created.Value)
            {

                Emit("key:read-or-created");

            }

            keyPresenceProved = true;

            Result genesis = _anchors.WriteGenesisAndVerify(
                heldInstallationLock,
                location,
                installationId);

            if (genesis.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalPublication>.Failure(genesis.Error);

            }

            Result<GrimoireOfflineTransitionAnchorV1?> reread = _anchors.Read(location);

            if (reread.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalPublication>.Failure(reread.Error);

            }

            if (reread.Value is null)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

            }

            existing = reread.Value;

        }

        if (existing.InstallationId != installationId)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        if (existing.State is GrimoireOfflineTransitionAnchorState.Active)
        {

            return await ResumeExactCurrentAsync(
                    location,
                    existing,
                    operationId,
                    kind,
                    payloadVersion,
                    payloadBytes,
                    evidence)
                .ConfigureAwait(false);

        }

        if (existing.OperationId == operationId)
        {

            return LifecycleConflict<GrimoireOfflineTransitionJournalPublication>(
                "This transition operation was already closed and cannot be reopened.");

        }

        if (!AllAbsent(evidence))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        if (!keyPresenceProved)
        {

            Result<GrimoireOfflineTransitionJournalKeyLease> present =
                _keys.OpenExisting(location.ProfileNamespace);

            if (present.IsFailure)
            {

                return KeyFailure<GrimoireOfflineTransitionJournalPublication>(present.Error);

            }

            using (present.Value)
            {

                Emit("key:read-or-created");

            }

        }

        ulong nextEpoch;

        try
        {

            nextEpoch = checked(existing.SlotEpoch + 1);

        }
        catch (OverflowException)
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        if (nextEpoch > GrimoireOfflineTransitionJournalAuthenticator.MaxSlotEpoch)
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionAnchorV1 opening = existing with
        {
            SlotEpoch = nextEpoch,
            State = GrimoireOfflineTransitionAnchorState.Active,
            OperationId = operationId,
            Kind = kind,
            PayloadVersion = payloadVersion,
            Revision = 0,
            EnvelopeDigest = null,
        };

        Result opened = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            location,
            existing,
            opening,
            GrimoireOfflineTransitionAnchorWriteStage.Opening);

        if (opened.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(opened.Error);

        }

        Result<GrimoireOfflineTransitionEnvelopeV1> sealedResult = Seal(
            location,
            installationId,
            nextEpoch,
            operationId,
            kind,
            payloadVersion,
            revision: 1,
            ZeroDigest,
            payloadBytes);

        if (sealedResult.IsFailure)
        {

            return CloseUnpublishedOpening(
                heldInstallationLock,
                location,
                opening,
                sealedResult.Error);

        }

        GrimoireOfflineTransitionEnvelopeV1 envelope = sealedResult.Value;

        Result<CovenantDigest> digest =
            GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(envelope);

        Result<byte[]> encoded =
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(envelope);

        if (digest.IsFailure || encoded.IsFailure)
        {

            return CloseUnpublishedOpening(
                heldInstallationLock,
                location,
                opening,
                digest.IsFailure ? digest.Error : encoded.Error);

        }

        Result replaced = await _replaceDurably(
                heldInstallationLock,
                location,
                encoded.Value,
                expectedCurrentIdentity: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (replaced.IsFailure)
        {

            return _files.RequireNoEvidence(location).IsSuccess
                ? CloseUnpublishedOpening(
                    heldInstallationLock,
                    location,
                    opening,
                    replaced.Error)
                : RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<GrimoireOfflineTransitionJournalPublication> published =
            await AuthenticatePublishedAsync(
                    location,
                    envelope,
                    digest.Value,
                    payloadBytes,
                    opening,
                    cancellationToken)
                .ConfigureAwait(false);

        if (published.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionAnchorV1 advanced = opening with
        {
            Revision = 1,
            EnvelopeDigest = digest.Value,
        };

        Result unchanged = _anchors.RequireMatches(location, opening);

        if (unchanged.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        Result anchored = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            location,
            opening,
            advanced,
            GrimoireOfflineTransitionAnchorWriteStage.Advance);

        if (anchored.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        return published.Value with { Anchor = advanced };

    }

    public async Task<Result<GrimoireOfflineTransitionJournalPublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication current,
        ReadOnlyMemory<byte> payloadBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(current);

        heldInstallationLock.AssertHeldFor(current.Location.GuardedDirectory);

        if (payloadBytes.IsEmpty
            || payloadBytes.Length > GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes
            || current.PayloadBytes is null)
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<GrimoireOfflineTransitionJournalLocation> resolved =
            _files.ResolveLocation(current.Location.GuardedDirectory);

        if (resolved.IsFailure || resolved.Value != current.Location
            || !CurrentBindingsMatch(current))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        Result identity = _identities.RequireMatchesDatabase(
            resolved.Value.ProfileNamespace,
            current.Envelope.InstallationId);

        if (identity.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(identity.Error);

        }

        Result<GrimoireOfflineTransitionAnchorV1?> anchorResult = _anchors.Read(current.Location);

        if (anchorResult.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(anchorResult.Error);

        }

        if (anchorResult.Value != current.Anchor
            || current.Anchor.State is not GrimoireOfflineTransitionAnchorState.Active)
        {

            return RevisionConflict<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<GrimoireOfflineTransitionJournalEvidence> evidenceResult =
            await _files.InspectEvidenceAsync(current.Location, cancellationToken).ConfigureAwait(false);

        if (evidenceResult.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        using (GrimoireOfflineTransitionJournalEvidence evidence = evidenceResult.Value)
        {

            if (evidence.Canonical is null
                || evidence.Working is not null
                || evidence.Previous is not null
                || evidence.Retiring is not null
                || !FileHandleIdentity.IdentitiesMatch(
                    current.FileMetadata.Identity,
                    evidence.Canonical.Metadata.Identity)
                || !evidence.Canonical.Bytes.Span.SequenceEqual(
                    ValueOrEmpty(GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(
                        current.Envelope))))
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

            }

            Result<GrimoireOfflineTransitionEnvelopeV1> decoded =
                GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(
                    evidence.Canonical.Bytes.Span);

            Result<CovenantDigest> digest = decoded.IsSuccess
                ? GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(decoded.Value)
                : Result<CovenantDigest>.Failure(decoded.Error);

            if (decoded.IsFailure || decoded.Value != current.Envelope
                || digest.IsFailure || digest.Value != current.EnvelopeDigest
                || current.Anchor.EnvelopeDigest != current.EnvelopeDigest)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

            }

        }

        Result<byte[]> currentPayload = Open(
            current.Location,
            current.Envelope.InstallationId,
            current.Envelope);

        if (currentPayload.IsFailure
            || !currentPayload.Value.AsSpan().SequenceEqual(current.PayloadBytes))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        ulong nextRevision;

        try
        {

            nextRevision = checked(current.Envelope.Revision + 1);

        }
        catch (OverflowException)
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        if (nextRevision > GrimoireOfflineTransitionJournalAuthenticator.MaxRevision)
        {

            return Invalid<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<GrimoireOfflineTransitionEnvelopeV1> sealedResult = Seal(
            current.Location,
            current.Envelope.InstallationId,
            current.Envelope.SlotEpoch,
            current.Envelope.OperationId,
            current.Envelope.Kind,
            current.Envelope.PayloadVersion,
            nextRevision,
            current.EnvelopeDigest,
            payloadBytes);

        if (sealedResult.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(sealedResult.Error);

        }

        GrimoireOfflineTransitionEnvelopeV1 envelope = sealedResult.Value;

        Result<CovenantDigest> nextDigest =
            GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(envelope);

        Result<byte[]> encoded =
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(envelope);

        if (nextDigest.IsFailure || encoded.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(
                nextDigest.IsFailure ? nextDigest.Error : encoded.Error);

        }

        Result replaced = await _replaceDurably(
                heldInstallationLock,
                current.Location,
                encoded.Value,
                current.FileMetadata.Identity,
                cancellationToken)
            .ConfigureAwait(false);

        if (replaced.IsFailure)
        {

            return await ClassifyFailedAdvanceReplacementAsync(
                    current,
                    replaced.Error)
                .ConfigureAwait(false);

        }

        Result<GrimoireOfflineTransitionJournalPublication> published =
            await AuthenticatePublishedAsync(
                    current.Location,
                    envelope,
                    nextDigest.Value,
                    payloadBytes,
                    current.Anchor,
                    cancellationToken)
                .ConfigureAwait(false);

        if (published.IsFailure || _anchors.RequireMatches(
                current.Location,
                current.Anchor).IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionAnchorV1 advanced = current.Anchor with
        {
            Revision = nextRevision,
            EnvelopeDigest = nextDigest.Value,
        };

        Result anchored = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            current.Location,
            current.Anchor,
            advanced,
            GrimoireOfflineTransitionAnchorWriteStage.Advance);

        return anchored.IsFailure
            ? RecoveryRequired<GrimoireOfflineTransitionJournalPublication>()
            : published.Value with { Anchor = advanced };

    }

    public async Task<Result<GrimoireOfflineTransitionJournalRecoveryState>> RecoverAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<GrimoireOfflineTransitionJournalLocation> resolved =
            _files.ResolveLocation(guardedDirectory);

        if (resolved.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalRecoveryState>.Failure(resolved.Error);

        }

        GrimoireOfflineTransitionJournalLocation location = resolved.Value;

        Result<GrimoireOfflineTransitionAnchorV1?> anchorResult = _anchors.Read(location);

        if (anchorResult.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalRecoveryState>.Failure(anchorResult.Error);

        }

        Result<GrimoireOfflineTransitionJournalEvidence> inspected =
            await _files.InspectEvidenceAsync(location, cancellationToken).ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

        if (anchorResult.Value is null)
        {

            if (!AllAbsent(evidence))
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result<bool> keyPresentWithoutAnchor = _keys.IsPresent(location.ProfileNamespace);

            if (keyPresentWithoutAnchor.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalRecoveryState>.Failure(
                    keyPresentWithoutAnchor.Error);

            }

            return keyPresentWithoutAnchor.Value
                ? RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>()
                : new GrimoireOfflineTransitionJournalRecoveryState(
                    GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
                    Publication: null);

        }

        GrimoireOfflineTransitionAnchorV1 anchor = anchorResult.Value;

        Result identity = _identities.RequireMatchesDatabase(
            location.ProfileNamespace,
            anchor.InstallationId);

        if (identity.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalRecoveryState>.Failure(identity.Error);

        }

        Result<GrimoireOfflineTransitionJournalKeyLease> key = _keys.OpenExisting(
            location.ProfileNamespace);

        if (key.IsFailure)
        {

            return KeyFailure<GrimoireOfflineTransitionJournalRecoveryState>(key.Error);

        }

        using (key.Value)
        {

        }

        if (anchor.State is GrimoireOfflineTransitionAnchorState.Closed)
        {

            return await RecoverClosedAsync(
                    heldInstallationLock,
                    location,
                    anchor,
                    evidence,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        if (anchor.Revision == 0 && evidence.Canonical is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        if (evidence.Canonical is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        if (evidence.Working is not null)
        {

            Result<GrimoireOfflineTransitionJournalPublication> current =
                AuthenticateEvidence(location, evidence.Canonical, anchor);

            Result<GrimoireOfflineTransitionJournalPublication> next =
                AuthenticateOneAhead(location, evidence.Working, anchor);

            if (current.IsSuccess && next.IsSuccess
                && evidence.Previous is null && evidence.Retiring is null)
            {

                Result resumed = await _files.ResumeWorkingPublicationAsync(
                        heldInstallationLock,
                        location,
                        current.Value.FileMetadata,
                        evidence.Canonical.Bytes,
                        next.Value.FileMetadata,
                        evidence.Working.Bytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                return resumed.IsSuccess
                    ? await RecoverAsync(heldInstallationLock, guardedDirectory, cancellationToken)
                        .ConfigureAwait(false)
                    : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result<GrimoireOfflineTransitionJournalPublication> workingOneAhead =
                AuthenticateOneAhead(location, evidence.Canonical, anchor);

            Result<GrimoireOfflineTransitionJournalPublication> predecessor =
                AuthenticateEvidence(location, evidence.Working, anchor);

            if (workingOneAhead.IsFailure || predecessor.IsFailure
                || evidence.Previous is not null || evidence.Retiring is not null)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result normalized = await _files.NormalizeWorkingPredecessorAsync(
                    heldInstallationLock,
                    location,
                    workingOneAhead.Value.FileMetadata,
                    evidence.Canonical.Bytes,
                    predecessor.Value.FileMetadata,
                    evidence.Working.Bytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (normalized.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result retired = await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    location,
                    GrimoireOfflineTransitionJournalRetirementSource.Previous,
                    predecessor.Value.FileMetadata,
                    evidence.Working.Bytes,
                    requireCanonicalAfter: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (retired.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result<GrimoireOfflineTransitionJournalPublication> revalidated =
                await ReauthenticateCanonicalAsync(location, workingOneAhead.Value, cancellationToken)
                    .ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result workingAnchorAdvanced = _anchors.CompareWriteAndVerify(
                heldInstallationLock,
                location,
                anchor,
                workingOneAhead.Value.Anchor,
                GrimoireOfflineTransitionAnchorWriteStage.Advance);

            return workingAnchorAdvanced.IsSuccess
                ? Authenticated(revalidated.Value)
                : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        Result<GrimoireOfflineTransitionJournalPublication> canonical =
            AuthenticateEvidence(location, evidence.Canonical, anchor);

        if (canonical.IsSuccess && evidence.Previous is null && evidence.Retiring is null)
        {

            return Authenticated(canonical.Value);

        }

        if (canonical.IsSuccess && (evidence.Previous is not null || evidence.Retiring is not null))
        {

            GrimoireOfflineTransitionJournalFileRead predecessor = evidence.Previous ?? evidence.Retiring!;

            GrimoireOfflineTransitionJournalRetirementSource source = evidence.Previous is not null
                ? GrimoireOfflineTransitionJournalRetirementSource.Previous
                : GrimoireOfflineTransitionJournalRetirementSource.Retiring;

            if (!IsExactPredecessor(location, predecessor, canonical.Value))
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result completed = await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    location,
                    source,
                    predecessor.Metadata,
                    predecessor.Bytes,
                    requireCanonicalAfter: true,
                    cancellationToken)
                .ConfigureAwait(false);

            Result<GrimoireOfflineTransitionJournalPublication> revalidated = completed.IsSuccess
                ? await ReauthenticateCanonicalAsync(location, canonical.Value, cancellationToken)
                    .ConfigureAwait(false)
                : RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

            return revalidated.IsSuccess
                ? Authenticated(revalidated.Value)
                : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        Result<GrimoireOfflineTransitionJournalPublication> oneAhead =
            AuthenticateOneAhead(location, evidence.Canonical, anchor);

        if (oneAhead.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        if (evidence.Previous is not null || evidence.Retiring is not null)
        {

            GrimoireOfflineTransitionJournalFileRead predecessor = evidence.Previous ?? evidence.Retiring!;

            GrimoireOfflineTransitionJournalRetirementSource source = evidence.Previous is not null
                ? GrimoireOfflineTransitionJournalRetirementSource.Previous
                : GrimoireOfflineTransitionJournalRetirementSource.Retiring;

            Result<GrimoireOfflineTransitionJournalPublication> anchoredPredecessor =
                AuthenticateEvidence(location, predecessor, anchor);

            if (anchoredPredecessor.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result completed = await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    location,
                    source,
                    predecessor.Metadata,
                    predecessor.Bytes,
                    requireCanonicalAfter: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (completed.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result<GrimoireOfflineTransitionJournalPublication> revalidated =
                await ReauthenticateCanonicalAsync(location, oneAhead.Value, cancellationToken)
                    .ConfigureAwait(false);

            if (revalidated.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            oneAhead = revalidated;

        }

        GrimoireOfflineTransitionAnchorV1 advanced = oneAhead.Value.Anchor;

        Result written = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            location,
            anchor,
            advanced,
            GrimoireOfflineTransitionAnchorWriteStage.Advance);

        return written.IsSuccess
            ? Authenticated(oneAhead.Value)
            : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

    }

    public async Task<Result> RetireAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication terminal,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(terminal);

        heldInstallationLock.AssertHeldFor(terminal.Location.GuardedDirectory);

        if (!CurrentBindingsMatch(terminal))
        {

            return RecoveryRequired();

        }

        Result identity = _identities.RequireMatchesDatabase(
            terminal.Location.ProfileNamespace,
            terminal.Envelope.InstallationId);

        if (identity.IsFailure)
        {

            return Result.Failure(identity.Error);

        }

        Result<GrimoireOfflineTransitionJournalKeyLease> key = _keys.OpenExisting(
            terminal.Location.ProfileNamespace);

        if (key.IsFailure)
        {

            return KeyFailure(key.Error);

        }

        using (key.Value)
        {

        }

        Result<GrimoireOfflineTransitionAnchorV1?> currentResult = _anchors.Read(
            terminal.Location);

        if (currentResult.IsFailure)
        {

            return Result.Failure(currentResult.Error);

        }

        if (currentResult.Value is null)
        {

            return RecoveryRequired();

        }

        GrimoireOfflineTransitionAnchorV1 closed = terminal.Anchor with
        {
            State = GrimoireOfflineTransitionAnchorState.Closed,
        };

        GrimoireOfflineTransitionAnchorV1 current = currentResult.Value;

        if (current != terminal.Anchor && current != closed)
        {

            return RecoveryRequired();

        }

        Result<GrimoireOfflineTransitionJournalEvidence> inspected =
            await _files.InspectEvidenceAsync(terminal.Location, cancellationToken).ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return RecoveryRequired();

        }

        using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

        if (current.State is GrimoireOfflineTransitionAnchorState.Active)
        {

            if (evidence.Canonical is null
                || evidence.Working is not null
                || evidence.Previous is not null
                || evidence.Retiring is not null
                || !PublicationMatches(terminal, evidence.Canonical))
            {

                return RecoveryRequired();

            }

            Result closedWritten = _anchors.CompareWriteAndVerify(
                heldInstallationLock,
                terminal.Location,
                current,
                closed,
                GrimoireOfflineTransitionAnchorWriteStage.Closed);

            if (closedWritten.IsFailure)
            {

                return RecoveryRequired();

            }

        }

        if (evidence.Canonical is not null
            && evidence.Working is null
            && evidence.Previous is null
            && evidence.Retiring is null
            && PublicationMatches(terminal with { Anchor = closed }, evidence.Canonical))
        {

            return await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    terminal.Location,
                    GrimoireOfflineTransitionJournalRetirementSource.Canonical,
                    evidence.Canonical.Metadata,
                    evidence.Canonical.Bytes,
                    requireCanonicalAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        if (evidence.Canonical is null
            && evidence.Working is null
            && evidence.Previous is null
            && evidence.Retiring is not null
            && PublicationMatches(terminal with { Anchor = closed }, evidence.Retiring))
        {

            return await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    terminal.Location,
                    GrimoireOfflineTransitionJournalRetirementSource.Retiring,
                    evidence.Retiring.Metadata,
                    evidence.Retiring.Bytes,
                    requireCanonicalAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);

        }

        return AllAbsent(evidence)
            ? _files.ProveAbsentDurably(heldInstallationLock, terminal.Location)
            : RecoveryRequired();

    }

    private async Task<Result<GrimoireOfflineTransitionJournalRecoveryState>> RecoverClosedAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 anchor,
        GrimoireOfflineTransitionJournalEvidence evidence,
        CancellationToken cancellationToken)
    {

        if (AllAbsent(evidence))
        {

            Result absent = _files.ProveAbsentDurably(heldInstallationLock, location);

            return absent.IsSuccess
                ? new GrimoireOfflineTransitionJournalRecoveryState(
                    GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
                    Publication: null,
                    anchor.OperationId)
                : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        if (evidence.Canonical is not null
            && evidence.Working is null
            && evidence.Previous is null
            && evidence.Retiring is null)
        {

            Result<GrimoireOfflineTransitionJournalPublication> canonical =
                AuthenticateEvidence(location, evidence.Canonical, anchor);

            if (canonical.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result completed = await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    location,
                    GrimoireOfflineTransitionJournalRetirementSource.Canonical,
                    evidence.Canonical.Metadata,
                    evidence.Canonical.Bytes,
                    requireCanonicalAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return completed.IsSuccess
                ? new GrimoireOfflineTransitionJournalRecoveryState(
                    GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
                    Publication: null,
                    anchor.OperationId)
                : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        if (evidence.Canonical is null
            && evidence.Working is null
            && evidence.Previous is null
            && evidence.Retiring is not null)
        {

            Result<GrimoireOfflineTransitionJournalPublication> retiring =
                AuthenticateEvidence(location, evidence.Retiring, anchor);

            if (retiring.IsFailure)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

            }

            Result completed = await _files.CompleteRetirementAsync(
                    heldInstallationLock,
                    location,
                    GrimoireOfflineTransitionJournalRetirementSource.Retiring,
                    evidence.Retiring.Metadata,
                    evidence.Retiring.Bytes,
                    requireCanonicalAfter: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return completed.IsSuccess
                ? new GrimoireOfflineTransitionJournalRecoveryState(
                    GrimoireOfflineTransitionJournalRecoveryOutcome.NoActiveJournal,
                    Publication: null,
                    anchor.OperationId)
                : RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

        }

        return RecoveryRequired<GrimoireOfflineTransitionJournalRecoveryState>();

    }

    private Result<GrimoireOfflineTransitionJournalPublication> AuthenticateEvidence(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionJournalFileRead file,
        GrimoireOfflineTransitionAnchorV1 anchor)
    {

        Result<GrimoireOfflineTransitionEnvelopeV1> decoded =
            GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(file.Bytes.Span);

        Result<CovenantDigest> digest = decoded.IsSuccess
            ? GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(decoded.Value)
            : Result<CovenantDigest>.Failure(decoded.Error);

        if (decoded.IsFailure
            || digest.IsFailure
            || !EnvelopeMatchesAnchor(location, decoded.Value, digest.Value, anchor))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<byte[]> payload = Open(location, anchor.InstallationId, decoded.Value);

        return payload.IsSuccess
            ? new GrimoireOfflineTransitionJournalPublication(
                location,
                decoded.Value,
                digest.Value,
                payload.Value,
                anchor,
                file.Metadata)
            : RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

    }

    private async Task<Result<GrimoireOfflineTransitionJournalPublication>> ReauthenticateCanonicalAsync(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionJournalPublication expected,
        CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalFileRead?> reread = await _files.ReadIfPresentAsync(
                location,
                cancellationToken)
            .ConfigureAwait(false);

        if (reread.IsFailure || reread.Value is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        using GrimoireOfflineTransitionJournalFileRead canonical = reread.Value;

        Result<GrimoireOfflineTransitionJournalPublication> authenticated =
            AuthenticateEvidence(location, canonical, expected.Anchor);

        return authenticated.IsSuccess
            && FileHandleIdentity.IdentitiesMatch(
                expected.FileMetadata.Identity,
                authenticated.Value.FileMetadata.Identity)
            && authenticated.Value.Envelope == expected.Envelope
            && authenticated.Value.EnvelopeDigest == expected.EnvelopeDigest
            && authenticated.Value.PayloadBytes.AsSpan().SequenceEqual(expected.PayloadBytes)
            ? authenticated
            : RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

    }

    private Result<GrimoireOfflineTransitionJournalPublication> AuthenticateOneAhead(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionJournalFileRead file,
        GrimoireOfflineTransitionAnchorV1 anchor)
    {

        Result<GrimoireOfflineTransitionEnvelopeV1> decoded =
            GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(file.Bytes.Span);

        Result<CovenantDigest> digest = decoded.IsSuccess
            ? GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(decoded.Value)
            : Result<CovenantDigest>.Failure(decoded.Error);

        if (decoded.IsFailure || digest.IsFailure || anchor.Revision == GrimoireOfflineTransitionJournalAuthenticator.MaxRevision)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        CovenantDigest previous = anchor.Revision == 0
            ? ZeroDigest
            : anchor.EnvelopeDigest ?? ZeroDigest;

        if (anchor.Revision > 0 && anchor.EnvelopeDigest is null
            || decoded.Value.Revision != anchor.Revision + 1
            || decoded.Value.PreviousEnvelopeDigest != previous
            || decoded.Value.ProfileNamespaceDigest != anchor.ProfileNamespaceDigest
            || decoded.Value.InstallationId != anchor.InstallationId
            || decoded.Value.SlotEpoch != anchor.SlotEpoch
            || decoded.Value.OperationId != anchor.OperationId
            || decoded.Value.Kind != anchor.Kind
            || decoded.Value.PayloadVersion != anchor.PayloadVersion
            || decoded.Value.JournalLocationDigest != anchor.JournalLocationDigest)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionAnchorV1 advanced = anchor with
        {
            Revision = decoded.Value.Revision,
            EnvelopeDigest = digest.Value,
        };

        return AuthenticateEvidence(location, file, advanced);

    }

    private bool IsExactPredecessor(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionJournalFileRead file,
        GrimoireOfflineTransitionJournalPublication current)
    {

        if (current.Envelope.Revision <= 1)
        {

            return false;

        }

        Result<GrimoireOfflineTransitionEnvelopeV1> decoded =
            GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(file.Bytes.Span);

        Result<CovenantDigest> digest = decoded.IsSuccess
            ? GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(decoded.Value)
            : Result<CovenantDigest>.Failure(decoded.Error);

        if (decoded.IsFailure
            || digest.IsFailure
            || digest.Value != current.Envelope.PreviousEnvelopeDigest
            || decoded.Value.Revision != current.Envelope.Revision - 1
            || decoded.Value.ProfileNamespaceDigest != current.Envelope.ProfileNamespaceDigest
            || decoded.Value.InstallationId != current.Envelope.InstallationId
            || decoded.Value.SlotEpoch != current.Envelope.SlotEpoch
            || decoded.Value.OperationId != current.Envelope.OperationId
            || decoded.Value.Kind != current.Envelope.Kind
            || decoded.Value.PayloadVersion != current.Envelope.PayloadVersion
            || decoded.Value.JournalLocationDigest != current.Envelope.JournalLocationDigest)
        {

            return false;

        }

        Result<byte[]> opened = Open(location, current.Envelope.InstallationId, decoded.Value);

        return opened.IsSuccess;

    }

    private bool PublicationMatches(
        GrimoireOfflineTransitionJournalPublication terminal,
        GrimoireOfflineTransitionJournalFileRead file)
    {

        Result<GrimoireOfflineTransitionJournalPublication> authenticated =
            AuthenticateEvidence(terminal.Location, file, terminal.Anchor);

        return authenticated.IsSuccess
            && authenticated.Value.Envelope == terminal.Envelope
            && authenticated.Value.EnvelopeDigest == terminal.EnvelopeDigest
            && authenticated.Value.PayloadBytes.AsSpan().SequenceEqual(terminal.PayloadBytes)
            && FileHandleIdentity.IdentitiesMatch(
                authenticated.Value.FileMetadata.Identity,
                terminal.FileMetadata.Identity);

    }

    private static bool EnvelopeMatchesAnchor(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionEnvelopeV1 envelope,
        CovenantDigest digest,
        GrimoireOfflineTransitionAnchorV1 anchor) =>
        anchor.Revision > 0
        && anchor.EnvelopeDigest == digest
        && envelope.ProfileNamespaceDigest == location.ProfileNamespace.Digest
        && envelope.ProfileNamespaceDigest == anchor.ProfileNamespaceDigest
        && envelope.InstallationId == anchor.InstallationId
        && envelope.SlotEpoch == anchor.SlotEpoch
        && envelope.OperationId == anchor.OperationId
        && envelope.Kind == anchor.Kind
        && envelope.PayloadVersion == anchor.PayloadVersion
        && envelope.Revision == anchor.Revision
        && envelope.JournalLocationDigest == location.JournalLocationDigest
        && envelope.JournalLocationDigest == anchor.JournalLocationDigest;

    private static Result<GrimoireOfflineTransitionJournalRecoveryState> Authenticated(
        GrimoireOfflineTransitionJournalPublication publication) =>
        new GrimoireOfflineTransitionJournalRecoveryState(
            GrimoireOfflineTransitionJournalRecoveryOutcome.Authenticated,
            publication,
            publication.Envelope.OperationId);

    private async Task<Result<GrimoireOfflineTransitionJournalPublication>> ResumeExactCurrentAsync(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 anchor,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlyMemory<byte> payloadBytes,
        GrimoireOfflineTransitionJournalEvidence evidence)
    {

        if (anchor.OperationId != operationId)
        {

            return LifecycleConflict<GrimoireOfflineTransitionJournalPublication>(
                "Another transition operation already owns this profile's active journal.");

        }

        if (anchor.Kind != kind || anchor.PayloadVersion != payloadVersion)
        {

            return LifecycleConflict<GrimoireOfflineTransitionJournalPublication>(
                "The active transition operation has different fixed bindings.");

        }

        if (evidence.Canonical is null
            || evidence.Working is not null
            || evidence.Previous is not null
            || evidence.Retiring is not null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionJournalFileRead canonical = evidence.Canonical;

        Result<GrimoireOfflineTransitionEnvelopeV1> decoded =
            GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(canonical.Bytes.Span);

        if (decoded.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionEnvelopeV1 envelope = decoded.Value;

        Result<CovenantDigest> digest =
            GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(envelope);

        if (digest.IsFailure
            || anchor.Revision == 0
            || anchor.EnvelopeDigest is not CovenantDigest anchoredDigest
            || digest.Value != anchoredDigest
            || envelope.ProfileNamespaceDigest != anchor.ProfileNamespaceDigest
            || envelope.InstallationId != anchor.InstallationId
            || envelope.SlotEpoch != anchor.SlotEpoch
            || envelope.OperationId != operationId
            || envelope.Kind != kind
            || envelope.PayloadVersion != payloadVersion
            || envelope.Revision != anchor.Revision
            || envelope.JournalLocationDigest != anchor.JournalLocationDigest)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<byte[]> opened = Open(location, anchor.InstallationId, envelope);

        if (opened.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        if (!opened.Value.AsSpan().SequenceEqual(payloadBytes.Span))
        {

            return LifecycleConflict<GrimoireOfflineTransitionJournalPublication>(
                "An idempotent transition begin must repeat the exact payload bytes.");

        }

        await Task.CompletedTask.ConfigureAwait(false);

        return new GrimoireOfflineTransitionJournalPublication(
            location,
            envelope,
            digest.Value,
            opened.Value,
            anchor,
            canonical.Metadata);

    }

    private async Task<Result<GrimoireOfflineTransitionJournalPublication>> AuthenticatePublishedAsync(
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionEnvelopeV1 expectedEnvelope,
        CovenantDigest expectedDigest,
        ReadOnlyMemory<byte> expectedPayload,
        GrimoireOfflineTransitionAnchorV1 anchor,
        CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalFileRead?> rereadResult =
            await _files.ReadIfPresentAsync(location, cancellationToken).ConfigureAwait(false);

        if (rereadResult.IsFailure || rereadResult.Value is null)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        using GrimoireOfflineTransitionJournalFileRead reread = rereadResult.Value;

        Result<GrimoireOfflineTransitionEnvelopeV1> decoded =
            GrimoireOfflineTransitionJournalAuthenticator.DecodeEnvelope(reread.Bytes.Span);

        Result<CovenantDigest> digest = decoded.IsSuccess
            ? GrimoireOfflineTransitionJournalAuthenticator.EnvelopeDigest(decoded.Value)
            : Result<CovenantDigest>.Failure(decoded.Error);

        if (decoded.IsFailure || decoded.Value != expectedEnvelope
            || digest.IsFailure || digest.Value != expectedDigest)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        Result<byte[]> payload = Open(location, anchor.InstallationId, decoded.Value);

        if (payload.IsFailure || !payload.Value.AsSpan().SequenceEqual(expectedPayload.Span))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        return new GrimoireOfflineTransitionJournalPublication(
            location,
            decoded.Value,
            digest.Value,
            payload.Value,
            anchor,
            reread.Metadata);

    }

    private Result<GrimoireOfflineTransitionEnvelopeV1> Seal(
        GrimoireOfflineTransitionJournalLocation location,
        Guid installationId,
        ulong slotEpoch,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ulong revision,
        CovenantDigest previousDigest,
        ReadOnlyMemory<byte> payloadBytes)
    {

        Result<GrimoireOfflineTransitionJournalKeyLease> lease =
            _keys.OpenExisting(location.ProfileNamespace);

        if (lease.IsFailure)
        {

            return Result<GrimoireOfflineTransitionEnvelopeV1>.Failure(lease.Error);

        }

        using GrimoireOfflineTransitionJournalKeyLease sealing = lease.Value;

        return GrimoireOfflineTransitionJournalAuthenticator.Seal(
            sealing,
            location.ProfileNamespace.Digest,
            installationId,
            slotEpoch,
            operationId,
            kind,
            payloadVersion,
            revision,
            previousDigest,
            location.JournalLocationDigest,
            payloadBytes.Span);

    }

    private Result<byte[]> Open(
        GrimoireOfflineTransitionJournalLocation location,
        Guid expectedInstallationId,
        GrimoireOfflineTransitionEnvelopeV1 envelope)
    {

        Result<GrimoireOfflineTransitionJournalKeyLease> lease =
            _keys.OpenExisting(location.ProfileNamespace);

        if (lease.IsFailure)
        {

            return Result<byte[]>.Failure(lease.Error);

        }

        using GrimoireOfflineTransitionJournalKeyLease opening = lease.Value;

        return GrimoireOfflineTransitionJournalAuthenticator.Open(
            opening,
            location.ProfileNamespace.Digest,
            expectedInstallationId,
            location.JournalLocationDigest,
            envelope);

    }

    private Result<GrimoireOfflineTransitionJournalPublication> CloseUnpublishedOpening(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionAnchorV1 opening,
        Error original)
    {

        if (_files.RequireNoEvidence(location).IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        GrimoireOfflineTransitionAnchorV1 closed = opening with
        {
            State = GrimoireOfflineTransitionAnchorState.Closed,
        };

        Result tombstoned = _anchors.CompareWriteAndVerify(
            heldInstallationLock,
            location,
            opening,
            closed,
            GrimoireOfflineTransitionAnchorWriteStage.Closed);

        return tombstoned.IsSuccess
            ? Result<GrimoireOfflineTransitionJournalPublication>.Failure(original)
            : RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

    }

    private async Task<Result<GrimoireOfflineTransitionJournalPublication>>
        ClassifyFailedAdvanceReplacementAsync(
            GrimoireOfflineTransitionJournalPublication current,
            Error original)
    {

        Result<GrimoireOfflineTransitionJournalEvidence> inspected =
            await _files.InspectEvidenceAsync(
                    current.Location,
                    CancellationToken.None)
                .ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

        }

        using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

        Result<byte[]> currentBytes =
            GrimoireOfflineTransitionJournalAuthenticator.EncodeEnvelope(current.Envelope);

        bool exactOldPublicationRemains = currentBytes.IsSuccess
            && evidence.Canonical is { } canonical
            && evidence.Working is null
            && evidence.Previous is null
            && evidence.Retiring is null
            && FileHandleIdentity.IdentitiesMatch(
                current.FileMetadata.Identity,
                canonical.Metadata.Identity)
            && canonical.Bytes.Span.SequenceEqual(currentBytes.Value);

        return exactOldPublicationRemains
            ? Result<GrimoireOfflineTransitionJournalPublication>.Failure(original)
            : RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

    }

    private static bool CurrentBindingsMatch(
        GrimoireOfflineTransitionJournalPublication current) =>
        current.Anchor.ProfileNamespaceDigest == current.Location.ProfileNamespace.Digest
        && current.Anchor.InstallationId == current.Envelope.InstallationId
        && current.Anchor.SlotEpoch == current.Envelope.SlotEpoch
        && current.Anchor.OperationId == current.Envelope.OperationId
        && current.Anchor.Kind == current.Envelope.Kind
        && current.Anchor.PayloadVersion == current.Envelope.PayloadVersion
        && current.Anchor.Revision == current.Envelope.Revision
        && current.Anchor.EnvelopeDigest == current.EnvelopeDigest
        && current.Anchor.JournalLocationDigest == current.Location.JournalLocationDigest
        && current.Envelope.ProfileNamespaceDigest == current.Location.ProfileNamespace.Digest
        && current.Envelope.JournalLocationDigest == current.Location.JournalLocationDigest;

    private static bool ValidRequest(
        Guid installationId,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ReadOnlyMemory<byte> payloadBytes) =>
        installationId != Guid.Empty
        && operationId != Guid.Empty
        && Enum.IsDefined(kind)
        && payloadVersion != 0
        && !payloadBytes.IsEmpty
        && payloadBytes.Length <=
            GrimoireOfflineTransitionJournalAuthenticator.MaxHandlerPayloadBytes;

    private static bool AllAbsent(GrimoireOfflineTransitionJournalEvidence evidence) =>
        evidence.Canonical is null
        && evidence.Working is null
        && evidence.Previous is null
        && evidence.Retiring is null;

    private static ReadOnlySpan<byte> ValueOrEmpty(Result<byte[]> result) =>
        result.IsSuccess ? result.Value : ReadOnlySpan<byte>.Empty;

    private void Emit(string step) => _afterStep?.Invoke(step);

    private static Result<T> Invalid<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.IntegrityFailure,
        "The transition journal request is invalid."));

    private static Result<T> RevisionConflict<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.RevisionConflict,
        "The transition journal anchor or file revision changed before its checked write."));

    private static Result<T> LifecycleConflict<T>(string message) =>
        Result<T>.Failure(new Error(ErrorCodes.Covenant.LifecycleConflict, message));

    private static Result<T> RecoveryRequired<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The transition journal has durable evidence that requires exact recovery."));

    private static Result RecoveryRequired() => new Error(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The transition journal has durable evidence that requires exact recovery.");

    private static Result<T> KeyFailure<T>(Error error) =>
        error.Code == ErrorCodes.Covenant.Unavailable
            ? Result<T>.Failure(error)
            : RecoveryRequired<T>();

    private static Result KeyFailure(Error error) =>
        error.Code == ErrorCodes.Covenant.Unavailable
            ? Result.Failure(error)
            : RecoveryRequired();

}
