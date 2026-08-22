using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Read-only classification of the same V2 and legacy restore evidence startup recovers.
/// </summary>
internal sealed class BackupRestoreActiveEvidenceInspector(
    string guardedRoot,
    IOsCredentialStore credentials)
{

    private readonly string _guardedRoot = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(guardedRoot));

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    internal async Task<Result<ActiveReplacementRestore?>> InspectAsync(
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string? parent = Path.GetDirectoryName(_guardedRoot);

        if (string.IsNullOrEmpty(parent))
        {

            return Failure<ActiveReplacementRestore?>(
                "The replacement-restore profile namespace has no retained parent.");

        }

        Result<NoFollowPathTopologyKind> parentTopology =
            NoFollowPathTopology.Classify(parent);

        if (parentTopology.IsFailure)
        {

            return Failure<ActiveReplacementRestore?>(
                "The replacement-restore retained parent could not be classified safely.");

        }

        if (parentTopology.Value is NoFollowPathTopologyKind.Absent)
        {

            return Result<ActiveReplacementRestore?>.Success(null);

        }

        if (parentTopology.Value is not NoFollowPathTopologyKind.Directory)
        {

            return Failure<ActiveReplacementRestore?>(
                "The replacement-restore retained parent is not an ordinary directory.");

        }

        Result<IReadOnlyList<string>> candidates = await CandidateRootsAsync(
            parent,
            cancellationToken).ConfigureAwait(false);

        if (candidates.IsFailure)
        {

            return Result<ActiveReplacementRestore?>.Failure(candidates.Error);

        }

        List<Guid> legacyOperations = [];

        foreach (string candidate in candidates.Value)
        {

            Result<bool> legacy = InspectJournalLeaf(
                candidate,
                BackupRestoreJournal.FileName);

            if (legacy.IsFailure)
            {

                return Result<ActiveReplacementRestore?>.Failure(legacy.Error);

            }

            if (legacy.Value)
            {

                if (BackupRestoreJournal.TryRead(candidate) is not { } legacyRecord)
                {

                    return Failure<ActiveReplacementRestore?>(
                        "A legacy replacement-restore journal is malformed or unreadable.");

                }

                Result validated = BackupRestoreJournal.ValidateForRecovery(
                    candidate,
                    _guardedRoot,
                    legacyRecord);

                if (validated.IsFailure)
                {

                    return Result<ActiveReplacementRestore?>.Failure(
                        validated.Error);

                }

                legacyOperations.Add(legacyRecord.OperationId);

            }

            Result<bool> authenticated = InspectJournalLeaf(
                candidate,
                BackupRestoreJournalAnchorStore.JournalFileName);

            if (authenticated.IsFailure)
            {

                return Result<ActiveReplacementRestore?>.Failure(authenticated.Error);

            }

        }

        if (legacyOperations.Count > 1)
        {

            return Failure<ActiveReplacementRestore?>(
                "More than one physical legacy replacement-restore journal is active.");

        }

        HashSet<Guid> activeOperations = [.. legacyOperations];

        Result<BackupRestoreProfileNamespace> profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guardedRoot);

        if (profile.IsFailure)
        {

            return Result<ActiveReplacementRestore?>.Failure(profile.Error);

        }

        BackupRestoreJournalKeyProvider keys = new(_credentials);

        BackupRestoreJournalInstallationIdentityProvider identities = new(_credentials);

        BackupRestoreJournalAnchorStore anchors = new(
            _credentials,
            keys,
            identities);

        Result<Guid?> v2Active = anchors.InspectActiveOperationId(
            profile.Value,
            candidates.Value);

        if (v2Active.IsFailure)
        {

            return Result<ActiveReplacementRestore?>.Failure(v2Active.Error);

        }

        if (v2Active.Value is { } authenticatedOperation)
        {

            activeOperations.Add(authenticatedOperation);

        }

        return activeOperations.Count switch
        {
            0 => Result<ActiveReplacementRestore?>.Success(null),

            1 => new ActiveReplacementRestore(activeOperations.Single()),

            _ => Failure<ActiveReplacementRestore?>(
                "More than one replacement-restore operation is active in this profile namespace."),
        };

    }

    private async Task<Result<IReadOnlyList<string>>> CandidateRootsAsync(
        string parent,
        CancellationToken cancellationToken)
    {

        HashSet<string> candidates = new(StringComparer.Ordinal);

        try
        {

            foreach (string candidate in Directory.EnumerateFileSystemEntries(
                         parent,
                         BackupRestoreJournal.StagingPrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (!BackupRestoreJournal.IsCanonicalStagingName(
                        Path.GetFileName(candidate)))
                {

                    continue;

                }

                Result<string> admitted = AdmitCandidate(candidate);

                if (admitted.IsFailure)
                {

                    return Result<IReadOnlyList<string>>.Failure(admitted.Error);

                }

                candidates.Add(admitted.Value);

            }

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return Failure<IReadOnlyList<string>>(
                "Replacement-restore staging roots could not be enumerated safely.");

        }

        Result<BackupRestoreStagingIndexRecord?> index =
            await BackupRestoreStagingIndex
                .InspectAsync(_guardedRoot, cancellationToken)
                .ConfigureAwait(false);

        if (index.IsFailure)
        {

            return Result<IReadOnlyList<string>>.Failure(index.Error);

        }

        foreach (string indexed in index.Value?.StagingRoots ?? [])
        {

            Result<string> admitted = AdmitCandidate(indexed);

            if (admitted.IsFailure)
            {

                return Result<IReadOnlyList<string>>.Failure(admitted.Error);

            }

            candidates.Add(admitted.Value);

        }

        return Result<IReadOnlyList<string>>.Success(
            [.. candidates.Order(StringComparer.Ordinal)]);

    }

    private static Result<string> AdmitCandidate(string candidate)
    {

        string full;

        try
        {

            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return Failure<string>(
                "A replacement-restore staging root is not a resolvable path.");

        }

        if (!BackupRestoreJournal.IsCanonicalStagingName(Path.GetFileName(full)))
        {

            return Failure<string>(
                "The restore staging index names a noncanonical root.");

        }

        Result<NoFollowPathTopologyKind> topology =
            NoFollowPathTopology.Classify(full);

        return topology.IsSuccess
            && topology.Value is NoFollowPathTopologyKind.Absent
                or NoFollowPathTopologyKind.Directory
            ? full
            : Failure<string>(
                "A replacement-restore staging root has unsafe topology.");

    }

    private static Result<bool> InspectJournalLeaf(
        string stagingRoot,
        string childName)
    {

        Result<NoFollowPathTopologyKind> root =
            NoFollowPathTopology.Classify(stagingRoot);

        if (root.IsFailure)
        {

            return Failure(
                "A replacement-restore staging root could not be classified safely.");

        }

        if (root.Value is NoFollowPathTopologyKind.Absent)
        {

            return false;

        }

        if (root.Value is not NoFollowPathTopologyKind.Directory)
        {

            return Failure(
                "A replacement-restore staging root is not an ordinary directory.");

        }

        string path = Path.Combine(stagingRoot, childName);

        Result<NoFollowPathTopologyKind> topology =
            NoFollowPathTopology.Classify(path);

        if (topology.IsFailure)
        {

            return Failure(
                "A replacement-restore journal could not be classified safely.");

        }

        if (topology.Value is NoFollowPathTopologyKind.Absent)
        {

            return false;

        }

        if (topology.Value is not NoFollowPathTopologyKind.RegularFile
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                path,
                out FileHandleMetadata metadata)
            || metadata.Kind is not FileSystemObjectKind.RegularFile
            || metadata.HardLinkCount != 1
            || !SecureFilePermissions.HasOwnerOnlyPosture(
                path,
                isDirectory: false))
        {

            return Failure(
                "A replacement-restore journal identity or owner-only permissions are unsafe.");

        }

        return true;

    }

    private static Result<bool> Failure(string message) =>
        Failure<bool>(message);

    private static Result<T> Failure<T>(string message) =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            message));

}
