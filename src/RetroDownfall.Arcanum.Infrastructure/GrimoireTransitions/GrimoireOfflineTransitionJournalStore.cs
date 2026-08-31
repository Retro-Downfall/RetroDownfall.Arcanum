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

    Task<Result<GrimoireOfflineTransitionJournalPublication>> AdvanceAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalPublication current,
        ReadOnlyMemory<byte> payloadBytes,
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

    internal GrimoireOfflineTransitionJournalStore(IOsCredentialStore credentials)
        : this(
            credentials,
            new GrimoireOfflineTransitionJournalFileStore(),
            new GrimoireOfflineTransitionJournalAnchorStore(credentials),
            afterStep: null)
    {

    }

    internal GrimoireOfflineTransitionJournalStore(
        IOsCredentialStore credentials,
        GrimoireOfflineTransitionJournalFileStore files,
        GrimoireOfflineTransitionJournalAnchorStore anchors,
        Action<string>? afterStep = null)
    {

        ArgumentNullException.ThrowIfNull(credentials);

        _keys = new GrimoireOfflineTransitionJournalKeyProvider(credentials);

        _identities = new BackupRestoreJournalInstallationIdentityProvider(credentials);

        _files = files ?? throw new ArgumentNullException(nameof(files));

        _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));

        _afterStep = afterStep;

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

            if (reread.IsFailure || reread.Value is null)
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

                return RecoveryRequired<GrimoireOfflineTransitionJournalPublication>();

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

        Result replaced = await _files.ReplaceDurablyAsync(
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

        Result<GrimoireOfflineTransitionAnchorV1?> anchorResult = _anchors.Read(current.Location);

        if (anchorResult.IsFailure || anchorResult.Value != current.Anchor
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

        Result replaced = await _files.ReplaceDurablyAsync(
                heldInstallationLock,
                current.Location,
                encoded.Value,
                current.FileMetadata.Identity,
                cancellationToken)
            .ConfigureAwait(false);

        if (replaced.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalPublication>.Failure(replaced.Error);

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

        Result<byte[]> opened = Open(location, envelope);

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

        Result<byte[]> payload = Open(location, decoded.Value);

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
            envelope.InstallationId,
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

}
