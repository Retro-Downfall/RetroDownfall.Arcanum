using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal sealed record GrimoireOfflineTransitionJournalLocation(
    BackupRestoreProfileNamespace ProfileNamespace,
    string GuardedDirectory,
    string MaintenanceLockPath,
    string JournalPath,
    string JournalLeaf,
    string WorkingPath,
    string WorkingLeaf,
    string PreviousPath,
    string PreviousLeaf,
    string RetiringPath,
    string RetiringLeaf,
    CovenantDigest GuardedParentPhysicalIdentityDigest,
    CovenantDigest JournalLocationDigest);

internal sealed record GrimoireOfflineTransitionJournalEvidence(
    GrimoireOfflineTransitionJournalFileRead? Canonical,
    GrimoireOfflineTransitionJournalFileRead? Working,
    GrimoireOfflineTransitionJournalFileRead? Previous,
    GrimoireOfflineTransitionJournalFileRead? Retiring) : IDisposable
{

    private int _disposed;

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        Canonical?.Dispose();

        Working?.Dispose();

        Previous?.Dispose();

        Retiring?.Dispose();

    }

}

internal enum GrimoireOfflineTransitionJournalRetirementSource : byte
{

    Canonical = 1,

    Previous = 2,

    Retiring = 3,

}

internal sealed class GrimoireOfflineTransitionJournalFileRead : IDisposable
{

    private SecureFileReadResult? _read;

    internal GrimoireOfflineTransitionJournalFileRead(SecureFileReadResult read)
    {

        _read = read;

        Metadata = read.Metadata;

    }

    internal ReadOnlyMemory<byte> Bytes =>
        _read?.Bytes ?? ReadOnlyMemory<byte>.Empty;

    internal FileHandleMetadata Metadata { get; }

    public void Dispose()
    {

        SecureFileReadResult? read = Interlocked.Exchange(ref _read, null);

        read?.Dispose();

    }

}

/// <summary>
/// Owns the fixed sibling slot used to publish authenticated offline Grimoire transition evidence.
/// </summary>
internal sealed class GrimoireOfflineTransitionJournalFileStore
{

    private static readonly StringComparer Ordinal = StringComparer.Ordinal;

    private readonly Action<string>? _afterStep;

    private readonly Func<string, bool>? _failBeforeStep;

    private readonly Action? _beforeAtomicReplace;

    private readonly Func<
        GrimoireOfflineTransitionJournalLocation,
        Result<IGrimoireOfflineTransitionJournalFilePrimitives>> _openPrimitives;

    internal GrimoireOfflineTransitionJournalFileStore(
        Action<string>? afterStep = null,
        Func<string, bool>? failBeforeStep = null,
        Action? beforeAtomicReplace = null)
        : this(
            afterStep,
            failBeforeStep,
            beforeAtomicReplace,
            OpenProductionPrimitives)
    {

    }

    internal GrimoireOfflineTransitionJournalFileStore(
        Action<string>? afterStep,
        Func<string, bool>? failBeforeStep,
        Action? beforeAtomicReplace,
        Func<
            GrimoireOfflineTransitionJournalLocation,
            Result<IGrimoireOfflineTransitionJournalFilePrimitives>> openPrimitives)
    {

        _afterStep = afterStep;

        _failBeforeStep = failBeforeStep;

        _beforeAtomicReplace = beforeAtomicReplace;

        _openPrimitives = openPrimitives;

    }

    internal Result<GrimoireOfflineTransitionJournalLocation> ResolveLocation(
        string guardedDirectory)
    {

        if (string.IsNullOrWhiteSpace(guardedDirectory))
        {

            return Unavailable<GrimoireOfflineTransitionJournalLocation>();

        }

        try
        {

            string guarded = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(guardedDirectory));

            string lockPath = ArcanumMaintenanceLock.LockPathFor(guarded);

            string journalPath = lockPath + ".grimoire-transition.active.json";

            string? parent = Path.GetDirectoryName(lockPath);

            string leaf = Path.GetFileName(journalPath);

            string workingPath = journalPath + ".publish";

            string previousPath = journalPath + ".previous";

            string retiringPath = journalPath + ".retiring";

            if (string.IsNullOrEmpty(parent) || !ValidLeaf(leaf)
                || !string.Equals(
                    Path.GetDirectoryName(journalPath),
                    parent,
                    PathComparison()))
            {

                return Unavailable<GrimoireOfflineTransitionJournalLocation>();

            }

            Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
                GrimoireOfflineTransitionJournalFilePrimitives.Open(parent);

            if (opened.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalLocation>.Failure(opened.Error);

            }

            using GrimoireOfflineTransitionJournalFilePrimitives parentCapability = opened.Value;

            FileHandleMetadata parentMetadata = parentCapability.ParentMetadata;

            CovenantDigest parentDigest = BackupRestoreJournalAuthenticator.PhysicalIdentity(
                parentMetadata.Identity.VolumeId,
                parentMetadata.Identity.FileId);

            Result<BackupRestoreProfileNamespace> profile =
                BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guarded);

            if (profile.IsFailure || profile.Value.ParentPhysicalIdentityDigest != parentDigest)
            {

                return Unavailable<GrimoireOfflineTransitionJournalLocation>();

            }

            Result<CovenantDigest> locationDigest =
                GrimoireOfflineTransitionJournalAuthenticator.JournalLocation(
                    profile.Value.Digest,
                    parentDigest,
                    leaf);

            if (locationDigest.IsFailure)
            {

                return Result<GrimoireOfflineTransitionJournalLocation>.Failure(locationDigest.Error);

            }

            return new GrimoireOfflineTransitionJournalLocation(
                profile.Value,
                guarded,
                lockPath,
                journalPath,
                leaf,
                workingPath,
                Path.GetFileName(workingPath),
                previousPath,
                Path.GetFileName(previousPath),
                retiringPath,
                Path.GetFileName(retiringPath),
                parentDigest,
                locationDigest.Value);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException)
        {

            return Unavailable<GrimoireOfflineTransitionJournalLocation>();

        }

    }

    internal Result RequireNoEvidence(
        GrimoireOfflineTransitionJournalLocation location)
    {

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        Result<GrimoireOfflineTransitionJournalEvidence> inspected = InspectEvidenceAsync(
                committed.Value,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (inspected.IsFailure)
        {

            return Result.Failure(inspected.Error);

        }

        using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

        return AllAbsent(evidence)
            ? Result.Success()
            : RecoveryRequired();

    }

    internal async Task<Result<GrimoireOfflineTransitionJournalEvidence>> InspectEvidenceAsync(
        GrimoireOfflineTransitionJournalLocation location,
        CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalEvidence>.Failure(committed.Error);

        }

        location = committed.Value;

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalEvidence>.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        return await InspectEvidenceAsync(
                primitives,
                location,
                cancellationToken)
            .ConfigureAwait(false);

    }

    internal async Task<Result<GrimoireOfflineTransitionJournalFileRead?>> ReadIfPresentAsync(
        GrimoireOfflineTransitionJournalLocation location,
        CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalFileRead?>.Failure(committed.Error);

        }

        location = committed.Value;

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalFileRead?>.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
            primitives.EnumerateExactChildren(Leaves(location));

        if (enumerated.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalFileRead?>.Failure(enumerated.Error);

        }

        using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

        Result residue = ValidateNames(location, children.Names);

        if (residue.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalFileRead?>.Failure(residue.Error);

        }

        if (!children.ExactChildren.TryGetValue(
                location.JournalLeaf,
                out GrimoireOfflineTransitionJournalOpenedFile? child))
        {

            return Result<GrimoireOfflineTransitionJournalFileRead?>.Success(null);

        }

        Result<GrimoireOfflineTransitionJournalFileRead> read = await ReadAsync(
                primitives,
                child,
                location.JournalLeaf,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalFileRead?>.Failure(read.Error);

        }

        try
        {

            Before("file:secure-reread");

        }
        catch (InjectedStepFailureException)
        {

            read.Value.Dispose();

            return RecoveryRequired<GrimoireOfflineTransitionJournalFileRead?>();

        }

        Emit("file:secure-reread");

        return Result<GrimoireOfflineTransitionJournalFileRead?>.Success(read.Value);

    }

    internal async Task<Result> ReplaceDurablyAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        ReadOnlyMemory<byte> bytes,
        FileHandleIdentity? expectedCurrentIdentity,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        location = committed.Value;

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        if (bytes.IsEmpty
            || bytes.Length > GrimoireOfflineTransitionJournalAuthenticator.MaxJournalFileBytes)
        {

            return Unavailable();

        }

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        GrimoireOfflineTransitionJournalOpenedFile? created = null;

        bool published = false;

        try
        {

            cancellationToken.ThrowIfCancellationRequested();

            Result<GrimoireOfflineTransitionJournalEvidence> initialResult =
                await InspectEvidenceAsync(
                        primitives,
                        location,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (initialResult.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalEvidence initial = initialResult.Value;

            if (initial.Working is not null || initial.Previous is not null || initial.Retiring is not null
                || expectedCurrentIdentity is null && initial.Canonical is not null
                || expectedCurrentIdentity is FileHandleIdentity expected
                    && (initial.Canonical is null
                        || !FileHandleIdentity.IdentitiesMatch(
                            expected,
                            initial.Canonical.Metadata.Identity)))
            {

                return RecoveryRequired();

            }

            Before("file:temporary-created");

            Result<GrimoireOfflineTransitionJournalOpenedFile> createdResult =
                primitives.CreateWorkingExclusive(location.WorkingLeaf);

            if (createdResult.IsFailure)
            {

                return Result.Failure(createdResult.Error);

            }

            created = createdResult.Value;

            Emit("file:temporary-created");

            cancellationToken.ThrowIfCancellationRequested();

            Before("file:temporary-written");

            FileStream stream = created.GetStream(FileAccess.ReadWrite);

            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            Emit("file:temporary-written");

            cancellationToken.ThrowIfCancellationRequested();

            Before("file:temporary-flushed");

            if (primitives.FlushWorking(created).IsFailure)
            {

                return CleanupBeforePublication(primitives, location, created, RecoveryRequired().Error);

            }

            Emit("file:temporary-flushed");

            Result<GrimoireOfflineTransitionJournalEvidence> finalValidationResult =
                await InspectEvidenceAsync(
                        primitives,
                        location,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (finalValidationResult.IsFailure)
            {

                return CleanupBeforePublication(primitives, location, created, finalValidationResult.Error);

            }

            using (GrimoireOfflineTransitionJournalEvidence finalValidation =
                   finalValidationResult.Value)
            {

                bool currentMatches = expectedCurrentIdentity is null
                    ? finalValidation.Canonical is null
                    : finalValidation.Canonical is { } canonical
                        && FileHandleIdentity.IdentitiesMatch(
                            expectedCurrentIdentity.Value,
                            canonical.Metadata.Identity);

                if (!currentMatches
                    || finalValidation.Working is null
                    || !FileHandleIdentity.IdentitiesMatch(
                        created.Metadata.Identity,
                        finalValidation.Working.Metadata.Identity)
                    || finalValidation.Previous is not null
                    || finalValidation.Retiring is not null)
                {

                    return CleanupBeforePublication(
                        primitives,
                        location,
                        created,
                        RecoveryRequired().Error);

                }

            }

            _beforeAtomicReplace?.Invoke();

            Before("file:atomic-replace");

            if (expectedCurrentIdentity is null)
            {

                Result first = primitives.PublishFirstNoReplace(
                    location.JournalLeaf,
                    location.WorkingLeaf);

                if (first.IsFailure)
                {

                    return CleanupBeforePublication(
                        primitives,
                        location,
                        created,
                        RecoveryRequired().Error);

                }

            }
            else
            {

                Result<GrimoireOfflineTransitionExchangeResult> exchanged =
                    primitives.ExchangeRetainingPrevious(
                        location.JournalLeaf,
                        location.WorkingLeaf,
                        location.PreviousLeaf);

                if (exchanged.IsFailure)
                {

                    return CleanupBeforePublication(
                        primitives,
                        location,
                        created,
                        RecoveryRequired().Error);

                }

                published = true;

                Emit("file:atomic-replace");

                if (exchanged.Value.Retention
                    is GrimoireOfflineTransitionPreviousRetention.Working)
                {

                    Result moved = primitives.MoveNoReplace(
                        location.WorkingLeaf,
                        location.PreviousLeaf);

                    if (moved.IsFailure)
                    {

                        return RecoveryRequired();

                    }

                }

                Before("file:previous-retained");

                Emit("file:previous-retained");

            }

            if (!published)
            {

                published = true;

                Emit("file:atomic-replace");

            }

            Result<GrimoireOfflineTransitionJournalEvidence> landedResult =
                await InspectEvidenceAsync(
                        primitives,
                        location,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            if (landedResult.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalEvidence landed = landedResult.Value;

            if (landed.Canonical is null
                || !FileHandleIdentity.IdentitiesMatch(
                    created.Metadata.Identity,
                    landed.Canonical.Metadata.Identity)
                || landed.Working is not null
                || landed.Retiring is not null
                || expectedCurrentIdentity is null && landed.Previous is not null
                || expectedCurrentIdentity is FileHandleIdentity priorIdentity
                    && (landed.Previous is null
                        || !FileHandleIdentity.IdentitiesMatch(
                            priorIdentity,
                            landed.Previous.Metadata.Identity)))
            {

                return RecoveryRequired();

            }

            Before("file:permissions-verified");

            Result permissions = primitives.ApplyOwnerOnlyAndVerify(
                created,
                location.JournalLeaf);

            if (permissions.IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:permissions-verified");

            Before("file:parent-flushed");

            if (primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:parent-flushed");

            Result<GrimoireOfflineTransitionJournalFileRead> rereadResult =
                await ReadCanonicalRelativeAsync(
                        primitives,
                        location,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            if (rereadResult.IsFailure)
            {

                return RecoveryRequired();

            }

            using (GrimoireOfflineTransitionJournalFileRead reread = rereadResult.Value)
            {

                if (!FileHandleIdentity.IdentitiesMatch(
                        created.Metadata.Identity,
                        reread.Metadata.Identity)
                    || !reread.Bytes.Span.SequenceEqual(bytes.Span))
                {

                    return RecoveryRequired();

                }

            }

            if (initial.Canonical is not null)
            {

                Result retired = await RetireAsync(
                        primitives,
                        location,
                        location.PreviousLeaf,
                        initial.Canonical.Metadata,
                        initial.Canonical.Bytes,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (retired.IsFailure)
                {

                    return RecoveryRequired();

                }

            }

            if (!await ProveResidueAbsentAsync(
                    primitives,
                    location,
                    requireCanonical: true,
                    CancellationToken.None).ConfigureAwait(false))
            {

                return RecoveryRequired();

            }

            Before("file:residue-absence-proved");

            Emit("file:residue-absence-proved");

            return Result.Success();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException
                or InjectedStepFailureException)
        {

            if (published)
            {

                return RecoveryRequired();

            }

            return created is null
                ? Unavailable()
                : CleanupBeforePublication(
                    primitives,
                    location,
                    created,
                    Unavailable().Error);

        }
        finally
        {

            created?.Dispose();

        }

    }

    internal Result DeleteDurably(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        FileHandleMetadata expected,
        ReadOnlyMemory<byte> expectedBytes)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        location = committed.Value;

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        try
        {

            Result<GrimoireOfflineTransitionJournalEvidence> inspected = InspectEvidenceAsync(
                    primitives,
                    location,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (inspected.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

            if (evidence.Canonical is null
                || evidence.Working is not null
                || evidence.Previous is not null
                || evidence.Retiring is not null
                || !FileHandleIdentity.IdentitiesMatch(
                    expected.Identity,
                    evidence.Canonical.Metadata.Identity)
                || !evidence.Canonical.Bytes.Span.SequenceEqual(expectedBytes.Span))
            {

                return RecoveryRequired();

            }

            Result retired = RetireAsync(
                    primitives,
                    location,
                    location.JournalLeaf,
                    expected,
                    expectedBytes,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (retired.IsFailure)
            {

                return RecoveryRequired();

            }

            if (!ProveAllAbsentAsync(
                    primitives,
                    location,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                || !ProveAllAbsentAsync(
                    primitives,
                    location,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            {

                return RecoveryRequired();

            }

            Before("file:residue-absence-proved");

            Emit("file:residue-absence-proved");

            return Result.Success();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InjectedStepFailureException)
        {

            return RecoveryRequired();

        }

    }

    internal Result ProveAbsentDurably(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        location = committed.Value;

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        try
        {

            if (!ProveAllAbsentAsync(
                    primitives,
                    location,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            {

                return RecoveryRequired();

            }

            Before("file:absence-parent-flushed");

            if (primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:absence-parent-flushed");

            if (!ProveAllAbsentAsync(
                    primitives,
                    location,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            {

                return RecoveryRequired();

            }

            Before("file:absence-proved");

            Emit("file:absence-proved");

            return Result.Success();

        }
        catch (InjectedStepFailureException)
        {

            return RecoveryRequired();

        }

    }

    internal async Task<Result> NormalizeWorkingPredecessorAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        FileHandleMetadata expectedCanonical,
        ReadOnlyMemory<byte> expectedCanonicalBytes,
        FileHandleMetadata expectedWorking,
        ReadOnlyMemory<byte> expectedWorkingBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        location = committed.Value;

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        try
        {

            Result<GrimoireOfflineTransitionJournalEvidence> inspected = await InspectEvidenceAsync(
                    primitives,
                    location,
                    cancellationToken)
                .ConfigureAwait(false);

            if (inspected.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

            if (evidence.Canonical is null
                || evidence.Working is null
                || evidence.Previous is not null
                || evidence.Retiring is not null
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedCanonical.Identity,
                    evidence.Canonical.Metadata.Identity)
                || !evidence.Canonical.Bytes.Span.SequenceEqual(expectedCanonicalBytes.Span)
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedWorking.Identity,
                    evidence.Working.Metadata.Identity)
                || !evidence.Working.Bytes.Span.SequenceEqual(expectedWorkingBytes.Span))
            {

                return RecoveryRequired();

            }

            if (primitives.MoveNoReplace(location.WorkingLeaf, location.PreviousLeaf).IsFailure)
            {

                return RecoveryRequired();

            }

            Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
                primitives.EnumerateExactChildren(Leaves(location));

            if (enumerated.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

            if (!children.ExactChildren.TryGetValue(
                    location.JournalLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? canonical)
                || !children.ExactChildren.TryGetValue(
                    location.PreviousLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? previous)
                || children.ExactChildren.ContainsKey(location.WorkingLeaf)
                || children.ExactChildren.ContainsKey(location.RetiringLeaf))
            {

                return RecoveryRequired();

            }

            Result<GrimoireOfflineTransitionJournalFileRead> rereadCanonical = await ReadAsync(
                    primitives,
                    canonical,
                    location.JournalLeaf,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rereadCanonical.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalFileRead verifiedCanonical = rereadCanonical.Value;

            Result<GrimoireOfflineTransitionJournalFileRead> rereadPrevious = await ReadAsync(
                    primitives,
                    previous,
                    location.PreviousLeaf,
                    cancellationToken)
                .ConfigureAwait(false);

            if (rereadPrevious.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalFileRead verifiedPrevious = rereadPrevious.Value;

            if (!FileHandleIdentity.IdentitiesMatch(
                    expectedCanonical.Identity,
                    verifiedCanonical.Metadata.Identity)
                || !verifiedCanonical.Bytes.Span.SequenceEqual(expectedCanonicalBytes.Span)
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedWorking.Identity,
                    verifiedPrevious.Metadata.Identity)
                || !verifiedPrevious.Bytes.Span.SequenceEqual(expectedWorkingBytes.Span)
                || primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:working-normalized");

            return Result.Success();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException
                or InjectedStepFailureException)
        {

            return RecoveryRequired();

        }

    }

    internal async Task<Result> CompleteRetirementAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionJournalRetirementSource source,
        FileHandleMetadata expected,
        ReadOnlyMemory<byte> expectedBytes,
        bool requireCanonicalAfter,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        location = committed.Value;

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        try
        {

            Result<GrimoireOfflineTransitionJournalEvidence> inspected = await InspectEvidenceAsync(
                    primitives,
                    location,
                    cancellationToken)
                .ConfigureAwait(false);

            if (inspected.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

            GrimoireOfflineTransitionJournalFileRead? selected = source switch
            {
                GrimoireOfflineTransitionJournalRetirementSource.Canonical => evidence.Canonical,
                GrimoireOfflineTransitionJournalRetirementSource.Previous => evidence.Previous,
                GrimoireOfflineTransitionJournalRetirementSource.Retiring => evidence.Retiring,
                _ => null,
            };

            bool expectedCanonicalBeforeMove = source
                is GrimoireOfflineTransitionJournalRetirementSource.Canonical
                || requireCanonicalAfter;

            bool shape = selected is not null
                && evidence.Working is null
                && (source is GrimoireOfflineTransitionJournalRetirementSource.Canonical
                    ? evidence.Previous is null && evidence.Retiring is null
                    : evidence.Previous is null || source is GrimoireOfflineTransitionJournalRetirementSource.Previous)
                && (expectedCanonicalBeforeMove
                    ? evidence.Canonical is not null
                    : evidence.Canonical is null);

            if (!shape || selected is null)
            {

                return RecoveryRequired();

            }

            if (!FileHandleIdentity.IdentitiesMatch(expected.Identity, selected.Metadata.Identity)
                || !selected.Bytes.Span.SequenceEqual(expectedBytes.Span))
            {

                return RecoveryRequired();

            }

            if (source is not GrimoireOfflineTransitionJournalRetirementSource.Retiring)
            {

                string sourceLeaf = source is GrimoireOfflineTransitionJournalRetirementSource.Canonical
                    ? location.JournalLeaf
                    : location.PreviousLeaf;

                Before("file:retiring-moved");

                if (primitives.MoveNoReplace(sourceLeaf, location.RetiringLeaf).IsFailure)
                {

                    return RecoveryRequired();

                }

                Emit("file:retiring-moved");

            }

            Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
                primitives.EnumerateExactChildren(Leaves(location));

            if (enumerated.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

            if (!children.ExactChildren.TryGetValue(
                    location.RetiringLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? retiring)
                || (requireCanonicalAfter != children.ExactChildren.ContainsKey(location.JournalLeaf))
                || children.ExactChildren.ContainsKey(location.WorkingLeaf)
                || children.ExactChildren.ContainsKey(location.PreviousLeaf))
            {

                return RecoveryRequired();

            }

            Result<GrimoireOfflineTransitionJournalFileRead> reread = await ReadAsync(
                    primitives,
                    retiring,
                    location.RetiringLeaf,
                    cancellationToken)
                .ConfigureAwait(false);

            if (reread.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalFileRead verified = reread.Value;

            if (!FileHandleIdentity.IdentitiesMatch(expected.Identity, verified.Metadata.Identity)
                || !verified.Bytes.Span.SequenceEqual(expectedBytes.Span))
            {

                return RecoveryRequired();

            }

            Before("file:retiring-verified");

            Emit("file:retiring-verified");

            Before("file:retiring-parent-flushed");

            if (primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:retiring-parent-flushed");

            Before("file:retiring-unlinked");

            if (primitives.CompareUnlink(retiring, location.RetiringLeaf).IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:retiring-unlinked");

            Before("file:retiring-zero-link-verified");

            Emit("file:retiring-zero-link-verified");

            Before("file:delete-parent-flushed");

            if (primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:delete-parent-flushed");

            if (!await ProveResidueAbsentAsync(
                    primitives,
                    location,
                    requireCanonicalAfter,
                    cancellationToken).ConfigureAwait(false))
            {

                return RecoveryRequired();

            }

            Before("file:absence-parent-flushed");

            if (primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:absence-parent-flushed");

            if (!await ProveResidueAbsentAsync(
                    primitives,
                    location,
                    requireCanonicalAfter,
                    cancellationToken).ConfigureAwait(false))
            {

                return RecoveryRequired();

            }

            Before("file:absence-proved");

            Emit("file:absence-proved");

            return Result.Success();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException
                or InjectedStepFailureException)
        {

            return RecoveryRequired();

        }

    }

    internal async Task<Result> ResumeWorkingPublicationAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        GrimoireOfflineTransitionJournalLocation location,
        FileHandleMetadata expectedCurrent,
        ReadOnlyMemory<byte> expectedCurrentBytes,
        FileHandleMetadata expectedNext,
        ReadOnlyMemory<byte> expectedNextBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(location);

        Result<GrimoireOfflineTransitionJournalLocation> committed =
            ValidateLocationCommitment(location);

        if (committed.IsFailure)
        {

            return Result.Failure(committed.Error);

        }

        location = committed.Value;

        heldInstallationLock.AssertHeldFor(location.GuardedDirectory);

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened = Open(location);

        if (opened.IsFailure)
        {

            return Result.Failure(opened.Error);

        }

        using IGrimoireOfflineTransitionJournalFilePrimitives primitives = opened.Value;

        try
        {

            Result<GrimoireOfflineTransitionJournalEvidence> inspected = await InspectEvidenceAsync(
                    primitives,
                    location,
                    cancellationToken)
                .ConfigureAwait(false);

            if (inspected.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

            if (evidence.Canonical is null
                || evidence.Working is null
                || evidence.Previous is not null
                || evidence.Retiring is not null
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedCurrent.Identity,
                    evidence.Canonical.Metadata.Identity)
                || !evidence.Canonical.Bytes.Span.SequenceEqual(expectedCurrentBytes.Span)
                || !FileHandleIdentity.IdentitiesMatch(
                    expectedNext.Identity,
                    evidence.Working.Metadata.Identity)
                || !evidence.Working.Bytes.Span.SequenceEqual(expectedNextBytes.Span))
            {

                return RecoveryRequired();

            }

            Result<GrimoireOfflineTransitionExchangeResult> exchanged =
                primitives.ExchangeRetainingPrevious(
                    location.JournalLeaf,
                    location.WorkingLeaf,
                    location.PreviousLeaf);

            if (exchanged.IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:atomic-replace");

            if (exchanged.Value.Retention is GrimoireOfflineTransitionPreviousRetention.Working
                && primitives.MoveNoReplace(location.WorkingLeaf, location.PreviousLeaf).IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:previous-retained");

            Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
                primitives.EnumerateExactChildren(Leaves(location));

            if (enumerated.IsFailure)
            {

                return RecoveryRequired();

            }

            using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

            if (!children.ExactChildren.TryGetValue(
                    location.JournalLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? canonical)
                || !children.ExactChildren.TryGetValue(
                    location.PreviousLeaf,
                    out GrimoireOfflineTransitionJournalOpenedFile? previous)
                || children.ExactChildren.ContainsKey(location.WorkingLeaf)
                || !FileHandleIdentity.IdentitiesMatch(expectedNext.Identity, canonical.Metadata.Identity)
                || !FileHandleIdentity.IdentitiesMatch(expectedCurrent.Identity, previous.Metadata.Identity))
            {

                return RecoveryRequired();

            }

            Before("file:permissions-verified");

            if (primitives.ApplyOwnerOnlyAndVerify(canonical, location.JournalLeaf).IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:permissions-verified");

            Before("file:parent-flushed");

            if (primitives.FlushParent().IsFailure)
            {

                return RecoveryRequired();

            }

            Emit("file:parent-flushed");

            return Result.Success();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException
                or InjectedStepFailureException)
        {

            return RecoveryRequired();

        }

    }

    private async Task<Result<GrimoireOfflineTransitionJournalEvidence>> InspectEvidenceAsync(
        IGrimoireOfflineTransitionJournalFilePrimitives primitives,
        GrimoireOfflineTransitionJournalLocation location,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
            primitives.EnumerateExactChildren(Leaves(location));

        if (enumerated.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalEvidence>.Failure(enumerated.Error);

        }

        using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

        Result names = ValidateNames(location, children.Names);

        if (names.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalEvidence>.Failure(names.Error);

        }

        GrimoireOfflineTransitionJournalFileRead? canonical = null;

        GrimoireOfflineTransitionJournalFileRead? working = null;

        GrimoireOfflineTransitionJournalFileRead? previous = null;

        GrimoireOfflineTransitionJournalFileRead? retiring = null;

        try
        {

            foreach ((string leaf, GrimoireOfflineTransitionJournalOpenedFile child) in
                     children.ExactChildren)
            {

                Result<GrimoireOfflineTransitionJournalFileRead> read = await ReadAsync(
                        primitives,
                        child,
                        leaf,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (read.IsFailure)
                {

                    return Result<GrimoireOfflineTransitionJournalEvidence>.Failure(read.Error);

                }

                if (Ordinal.Equals(leaf, location.JournalLeaf))
                {

                    canonical = read.Value;

                }
                else if (Ordinal.Equals(leaf, location.WorkingLeaf))
                {

                    working = read.Value;

                }
                else if (Ordinal.Equals(leaf, location.PreviousLeaf))
                {

                    previous = read.Value;

                }
                else if (Ordinal.Equals(leaf, location.RetiringLeaf))
                {

                    retiring = read.Value;

                }

            }

            GrimoireOfflineTransitionJournalEvidence evidence = new(
                canonical,
                working,
                previous,
                retiring);

            canonical = null;

            working = null;

            previous = null;

            retiring = null;

            return evidence;

        }
        finally
        {

            canonical?.Dispose();

            working?.Dispose();

            previous?.Dispose();

            retiring?.Dispose();

        }

    }

    private async Task<Result> RetireAsync(
        IGrimoireOfflineTransitionJournalFilePrimitives primitives,
        GrimoireOfflineTransitionJournalLocation location,
        string sourceLeaf,
        FileHandleMetadata expected,
        ReadOnlyMemory<byte> expectedBytes,
        CancellationToken cancellationToken)
    {

        Before("file:previous-retiring");

        Result moved = primitives.MoveNoReplace(sourceLeaf, location.RetiringLeaf);

        if (moved.IsFailure)
        {

            return RecoveryRequired();

        }

        Emit("file:previous-retiring");

        Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
            primitives.EnumerateExactChildren(Leaves(location));

        if (enumerated.IsFailure)
        {

            return RecoveryRequired();

        }

        using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

        if (!children.ExactChildren.TryGetValue(
                location.RetiringLeaf,
                out GrimoireOfflineTransitionJournalOpenedFile? retiring)
            || children.ExactChildren.ContainsKey(sourceLeaf))
        {

            return RecoveryRequired();

        }

        Result<GrimoireOfflineTransitionJournalFileRead> readResult = await ReadAsync(
                primitives,
                retiring,
                location.RetiringLeaf,
                cancellationToken)
            .ConfigureAwait(false);

        if (readResult.IsFailure)
        {

            return RecoveryRequired();

        }

        using GrimoireOfflineTransitionJournalFileRead read = readResult.Value;

        if (!FileHandleIdentity.IdentitiesMatch(expected.Identity, read.Metadata.Identity)
            || !read.Bytes.Span.SequenceEqual(expectedBytes.Span))
        {

            return RecoveryRequired();

        }

        Before("file:previous-retiring-verified");

        Emit("file:previous-retiring-verified");

        if (primitives.FlushParent().IsFailure)
        {

            return RecoveryRequired();

        }

        Before("file:previous-unlinked");

        Result unlinked = primitives.CompareUnlink(retiring, location.RetiringLeaf);

        if (unlinked.IsFailure)
        {

            return RecoveryRequired();

        }

        Emit("file:previous-unlinked");

        Before("file:previous-zero-link-verified");

        Emit("file:previous-zero-link-verified");

        Before("file:previous-delete-parent-flushed");

        if (primitives.FlushParent().IsFailure)
        {

            return RecoveryRequired();

        }

        Emit("file:previous-delete-parent-flushed");

        return Result.Success();

    }

    private static async Task<Result<GrimoireOfflineTransitionJournalFileRead>>
        ReadCanonicalRelativeAsync(
            IGrimoireOfflineTransitionJournalFilePrimitives primitives,
            GrimoireOfflineTransitionJournalLocation location,
            CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalChildEnumeration> enumerated =
            primitives.EnumerateExactChildren(Leaves(location));

        if (enumerated.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalFileRead>.Failure(enumerated.Error);

        }

        using GrimoireOfflineTransitionJournalChildEnumeration children = enumerated.Value;

        if (!children.ExactChildren.TryGetValue(
                location.JournalLeaf,
                out GrimoireOfflineTransitionJournalOpenedFile? child))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalFileRead>();

        }

        return await ReadAsync(
                primitives,
                child,
                location.JournalLeaf,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private static async Task<Result<GrimoireOfflineTransitionJournalFileRead>> ReadAsync(
        IGrimoireOfflineTransitionJournalFilePrimitives primitives,
        GrimoireOfflineTransitionJournalOpenedFile child,
        string relativeLeaf,
        CancellationToken cancellationToken)
    {

        if (child.Metadata.Kind is not FileSystemObjectKind.RegularFile
            || child.Metadata.HardLinkCount != 1
            || !GrimoireOfflineTransitionJournalFilePrimitives
                .VerifyOwnerControlledOpenedFileHandle(child))
        {

            return RecoveryRequired<GrimoireOfflineTransitionJournalFileRead>();

        }

        SecureFileReadResult read = await SecureFileReader.ReadBytesAsync(
                child.GetStream(FileAccess.Read),
                GrimoireOfflineTransitionJournalAuthenticator.MaxJournalFileBytes,
                cancellationToken,
                requireOwnerControlled: false)
            .ConfigureAwait(false);

        if (read.Status is not SecureFileReadStatus.Success
            || !FileHandleIdentity.IdentitiesMatch(
                child.Metadata.Identity,
                read.Metadata.Identity)
            || !GrimoireOfflineTransitionJournalFilePrimitives
                .VerifyOwnerControlledOpenedFileHandle(child))
        {

            read.Dispose();

            return RecoveryRequired<GrimoireOfflineTransitionJournalFileRead>();

        }

        Result<GrimoireOfflineTransitionJournalChildEnumeration> namedResult =
            primitives.EnumerateExactChildren([relativeLeaf]);

        if (namedResult.IsFailure)
        {

            read.Dispose();

            return RecoveryRequired<GrimoireOfflineTransitionJournalFileRead>();

        }

        using GrimoireOfflineTransitionJournalChildEnumeration named = namedResult.Value;

        if (!named.ExactChildren.TryGetValue(
                relativeLeaf,
                out GrimoireOfflineTransitionJournalOpenedFile? reopened)
            || !FileHandleIdentity.IdentitiesMatch(
                child.Metadata.Identity,
                reopened.Metadata.Identity)
            || !FileHandleIdentity.IdentitiesMatch(
                read.Metadata.Identity,
                reopened.Metadata.Identity)
            || !GrimoireOfflineTransitionJournalFilePrimitives
                .VerifyOwnerControlledOpenedFileHandle(reopened))
        {

            read.Dispose();

            return RecoveryRequired<GrimoireOfflineTransitionJournalFileRead>();

        }

        return new GrimoireOfflineTransitionJournalFileRead(read);

    }

    private async Task<bool> ProveResidueAbsentAsync(
        IGrimoireOfflineTransitionJournalFilePrimitives primitives,
        GrimoireOfflineTransitionJournalLocation location,
        bool requireCanonical,
        CancellationToken cancellationToken)
    {

        Result<GrimoireOfflineTransitionJournalEvidence> inspected = await InspectEvidenceAsync(
                primitives,
                location,
                cancellationToken)
            .ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return false;

        }

        using GrimoireOfflineTransitionJournalEvidence evidence = inspected.Value;

        return (requireCanonical ? evidence.Canonical is not null : evidence.Canonical is null)
            && evidence.Working is null
            && evidence.Previous is null
            && evidence.Retiring is null;

    }

    private async Task<bool> ProveAllAbsentAsync(
        IGrimoireOfflineTransitionJournalFilePrimitives primitives,
        GrimoireOfflineTransitionJournalLocation location,
        CancellationToken cancellationToken) =>
        await ProveResidueAbsentAsync(
                primitives,
                location,
                requireCanonical: false,
                cancellationToken)
            .ConfigureAwait(false);

    private Result CleanupBeforePublication(
        IGrimoireOfflineTransitionJournalFilePrimitives primitives,
        GrimoireOfflineTransitionJournalLocation location,
        GrimoireOfflineTransitionJournalOpenedFile working,
        Error original)
    {

        Result removed = primitives.CompareUnlink(working, location.WorkingLeaf);

        if (removed.IsFailure || primitives.FlushParent().IsFailure)
        {

            return RecoveryRequired();

        }

        return Result.Failure(original);

    }

    private Result<GrimoireOfflineTransitionJournalLocation> ValidateLocationCommitment(
        GrimoireOfflineTransitionJournalLocation location)
    {

        if (location is null || string.IsNullOrWhiteSpace(location.GuardedDirectory))
        {

            return Unavailable<GrimoireOfflineTransitionJournalLocation>();

        }

        Result<GrimoireOfflineTransitionJournalLocation> reconstructed =
            ResolveLocation(location.GuardedDirectory);

        if (reconstructed.IsFailure)
        {

            return Result<GrimoireOfflineTransitionJournalLocation>.Failure(
                reconstructed.Error);

        }

        GrimoireOfflineTransitionJournalLocation expected = reconstructed.Value;

        bool matches =
            location.ProfileNamespace is not null
            && location.ProfileNamespace.Digest == expected.ProfileNamespace.Digest
            && location.ProfileNamespace.ParentPhysicalIdentityDigest
                == expected.ProfileNamespace.ParentPhysicalIdentityDigest
            && string.Equals(
                location.ProfileNamespace.ChildLeaf,
                expected.ProfileNamespace.ChildLeaf,
                StringComparison.Ordinal)
            && string.Equals(
                location.GuardedDirectory,
                expected.GuardedDirectory,
                StringComparison.Ordinal)
            && string.Equals(
                location.MaintenanceLockPath,
                expected.MaintenanceLockPath,
                StringComparison.Ordinal)
            && string.Equals(
                location.JournalPath,
                expected.JournalPath,
                StringComparison.Ordinal)
            && string.Equals(
                location.JournalLeaf,
                expected.JournalLeaf,
                StringComparison.Ordinal)
            && string.Equals(
                location.WorkingPath,
                expected.WorkingPath,
                StringComparison.Ordinal)
            && string.Equals(
                location.WorkingLeaf,
                expected.WorkingLeaf,
                StringComparison.Ordinal)
            && string.Equals(
                location.PreviousPath,
                expected.PreviousPath,
                StringComparison.Ordinal)
            && string.Equals(
                location.PreviousLeaf,
                expected.PreviousLeaf,
                StringComparison.Ordinal)
            && string.Equals(
                location.RetiringPath,
                expected.RetiringPath,
                StringComparison.Ordinal)
            && string.Equals(
                location.RetiringLeaf,
                expected.RetiringLeaf,
                StringComparison.Ordinal)
            && location.GuardedParentPhysicalIdentityDigest
                == expected.GuardedParentPhysicalIdentityDigest
            && location.JournalLocationDigest == expected.JournalLocationDigest;

        return matches
            ? expected
            : RecoveryRequired<GrimoireOfflineTransitionJournalLocation>();

    }

    private Result<IGrimoireOfflineTransitionJournalFilePrimitives> Open(
        GrimoireOfflineTransitionJournalLocation location)
    {

        if (location is null
            || !location.GuardedParentPhysicalIdentityDigest.IsValid
            || !location.JournalLocationDigest.IsValid)
        {

            return Unavailable<IGrimoireOfflineTransitionJournalFilePrimitives>();

        }

        Result<IGrimoireOfflineTransitionJournalFilePrimitives> opened =
            _openPrimitives(location);

        if (opened.IsFailure)
        {

            return opened;

        }

        CovenantDigest current = BackupRestoreJournalAuthenticator.PhysicalIdentity(
            opened.Value.ParentMetadata.Identity.VolumeId,
            opened.Value.ParentMetadata.Identity.FileId);

        if (current != location.GuardedParentPhysicalIdentityDigest)
        {

            opened.Value.Dispose();

            return RecoveryRequired<IGrimoireOfflineTransitionJournalFilePrimitives>();

        }

        return opened;

    }

    private static Result<IGrimoireOfflineTransitionJournalFilePrimitives> OpenProductionPrimitives(
        GrimoireOfflineTransitionJournalLocation location)
    {

        string? parent = Path.GetDirectoryName(location.JournalPath);

        if (string.IsNullOrEmpty(parent))
        {

            return Unavailable<IGrimoireOfflineTransitionJournalFilePrimitives>();

        }

        Result<GrimoireOfflineTransitionJournalFilePrimitives> opened =
            GrimoireOfflineTransitionJournalFilePrimitives.Open(
                parent,
                location.GuardedParentPhysicalIdentityDigest);

        return opened.IsSuccess
            ? Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Success(opened.Value)
            : Result<IGrimoireOfflineTransitionJournalFilePrimitives>.Failure(opened.Error);

    }

    private static Result ValidateNames(
        GrimoireOfflineTransitionJournalLocation location,
        IReadOnlyList<string> names)
    {

        string[] exact = Leaves(location);

        foreach (string name in names)
        {

            if (exact.Contains(name, StringComparer.Ordinal))
            {

                continue;

            }

            bool aliasesCandidate = exact.Contains(name, StringComparer.OrdinalIgnoreCase);

            bool legacy = name.StartsWith(
                location.JournalLeaf + ".tmp.",
                StringComparison.OrdinalIgnoreCase);

            bool journalPrefixed = name.StartsWith(
                location.JournalLeaf,
                StringComparison.OrdinalIgnoreCase);

            if (aliasesCandidate || legacy || journalPrefixed)
            {

                return RecoveryRequired();

            }

        }

        return Result.Success();

    }

    private static string[] Leaves(GrimoireOfflineTransitionJournalLocation location) =>
    [
        location.JournalLeaf,
        location.WorkingLeaf,
        location.PreviousLeaf,
        location.RetiringLeaf,
    ];

    private static bool AllAbsent(GrimoireOfflineTransitionJournalEvidence evidence) =>
        evidence.Canonical is null
        && evidence.Working is null
        && evidence.Previous is null
        && evidence.Retiring is null;

    private void Before(string step)
    {

        if (_failBeforeStep?.Invoke(step) is true)
        {

            throw new InjectedStepFailureException();

        }

    }

    private void Emit(string step) => _afterStep?.Invoke(step);

    private static bool ValidLeaf(string? leaf) =>
        GrimoireOfflineTransitionLeafName.IsValid(leaf);

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static Result Unavailable() => new Error(
        ErrorCodes.Covenant.Unavailable,
        "The transition journal filesystem capability is unavailable.");

    private static Result<T> Unavailable<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.Unavailable,
        "The transition journal filesystem capability is unavailable."));

    private static Result RecoveryRequired() => new Error(
        ErrorCodes.Data.RecoveryRequired,
        "Transition journal filesystem evidence requires recovery.");

    private static Result<T> RecoveryRequired<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Data.RecoveryRequired,
        "Transition journal filesystem evidence requires recovery."));

    private sealed class InjectedStepFailureException : Exception;

}
