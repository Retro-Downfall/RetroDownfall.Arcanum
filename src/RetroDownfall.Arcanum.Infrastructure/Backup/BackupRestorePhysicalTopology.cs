using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// Which side of the one irreversible rename a converged installation sits on.
/// </summary>
/// <remarks>
/// Two states, because only one boundary matters to everything downstream: whether the directory the
/// database will be opened from is the tree that was already there or the tree the archive produced. A
/// pre-swap restore has changed nothing durable and may be rolled back; a post-swap one has replaced
/// the installation and can only be carried forward or left closed.
/// </remarks>
internal enum BackupRestoreTopologyState : byte
{

    PreSwap = 1,

    PostSwap = 2,

}

/// <summary>
/// The rename topology one interrupted restore left, proved from durable identities rather than paths.
/// </summary>
/// <remarks>
/// This is the whole of what physical recovery is allowed to do: reopen the recorded parents, verify
/// their identities, open only the recorded children without following links, compare each node against
/// the identity the journal committed to, and — inside the displacement window only — perform the one
/// rename that converges the tree plus the parent flush that publishes it.
///
/// <para>Identity is what makes this safe, and it is also what makes it possible. A rename preserves a
/// directory's volume and file identifiers, so the tree that now sits at the rollback path <em>is</em>
/// the live root the journal recorded, provably, without trusting either name. A directory that merely
/// occupies a recorded path is never adopted (§10.19.8).</para>
/// </remarks>
internal static class BackupRestorePhysicalTopology
{

    /// <summary>
    /// Classifies the observed tree against the journal, converging it when effects are permitted.
    /// </summary>
    /// <param name="payload">The authenticated journal payload this restore published.</param>
    /// <param name="guardedDirectory">The installation root this process is bootstrapping.</param>
    /// <param name="allowEffects">
    /// Whether the caller is the pre-database phase, which owns the one rename and parent flush. The
    /// authority phase passes <see langword="false"/>: it runs after convergence, so a tree still
    /// inside the displacement window is evidence that convergence did not happen, not work to redo.
    /// </param>
    internal static Result<BackupRestoreTopologyState> Reconcile(
        BackupRestoreJournalPayloadV2 payload,
        string guardedDirectory,
        bool allowEffects)
    {

        ArgumentNullException.ThrowIfNull(payload);

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        Result<string> livePath = ChildPath(payload.LiveRoot);

        if (livePath.IsFailure)
        {

            return Result<BackupRestoreTopologyState>.Failure(livePath.Error);

        }

        if (!SamePath(livePath.Value, guardedDirectory))
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal describes a different installation root than the one being "
                + "started.");

        }

        Result<NodeObservation> live = Observe(payload.LiveRoot);

        Result<NodeObservation> staged = Observe(payload.StagedRoot);

        Result<NodeObservation> displaced = Observe(payload.DisplacedRoot);

        if (live.IsFailure || staged.IsFailure || displaced.IsFailure)
        {

            return Result<BackupRestoreTopologyState>.Failure(
                live.IsFailure ? live.Error : staged.IsFailure ? staged.Error : displaced.Error);

        }

        if (payload.Phase != BackupRestorePhase.Commit)
        {

            // Outside the displacement window the journal's own recorded shape is the only admissible
            // one, and there is nothing to converge: a phase before commit has not moved anything, and
            // a phase after it advanced only once the parent flush had already published the swap.
            if (!Matches(live.Value, payload.LiveRoot)
                || !Matches(staged.Value, payload.StagedRoot)
                || !Matches(displaced.Value, payload.DisplacedRoot))
            {

                return Ambiguous();

            }

            return payload.Phase > BackupRestorePhase.Commit
                ? Durable(BackupRestoreTopologyState.PostSwap, payload, allowEffects)
                : Result<BackupRestoreTopologyState>.Success(BackupRestoreTopologyState.PreSwap);

        }

        return ReconcileDisplacementWindow(payload, live.Value, staged.Value, displaced.Value, allowEffects);

    }

    /// <summary>
    /// The four states a process killed inside the commit window can leave behind.
    /// </summary>
    /// <remarks>
    /// The journal is published once, before the first displacement, so the identities it carries are
    /// the pre-swap ones: the live root that is about to be displaced and the staged root that is about
    /// to take its place. Every state below is those two identities observed somewhere else.
    /// </remarks>
    private static Result<BackupRestoreTopologyState> ReconcileDisplacementWindow(
        BackupRestoreJournalPayloadV2 payload,
        NodeObservation live,
        NodeObservation staged,
        NodeObservation displaced,
        bool allowEffects)
    {

        bool replacing = payload.ConflictMode is BackupRestoreConflictMode.ReplaceInstallation;

        if (payload.StagedRoot.Presence is not BackupRestoreNodePresence.Present
            || payload.StagedRoot.NodePhysicalIdentityDigest is not { } stagedIdentity
            || payload.DisplacedRoot.Presence is not BackupRestoreNodePresence.Absent
            || (replacing
                ? payload.LiveRoot.Presence is not BackupRestoreNodePresence.Present
                : payload.LiveRoot.Presence is not BackupRestoreNodePresence.Absent))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal froze a displacement window whose shape no commit can produce.");

        }

        CovenantDigest? liveIdentity = payload.LiveRoot.NodePhysicalIdentityDigest;

        // Before the first displacement: nothing has moved, and nothing here may move it. The staged
        // generation has not been proven healthy, so converging forward would publish a tree nobody
        // verified.
        if (Matches(live, payload.LiveRoot) && !displaced.Present && Is(staged, stagedIdentity))
        {

            return BackupRestoreTopologyState.PreSwap;

        }

        // Between the two renames: the live root is the tree now sitting at the rollback path, proved
        // by the identity a rename preserved. Moving it back is the whole of the convergence.
        if (replacing
            && !live.Present
            && liveIdentity is { } displacedLive
            && Is(displaced, displacedLive)
            && Is(staged, stagedIdentity))
        {

            // The authority phase runs after convergence, so a tree still inside the window is evidence
            // that convergence did not happen rather than work to redo.
            if (!allowEffects)
            {

                return Ambiguous();

            }

            Result moved = Rename(payload.DisplacedRoot, payload.LiveRoot);

            return moved.IsFailure
                ? Result<BackupRestoreTopologyState>.Failure(moved.Error)
                : Durable(BackupRestoreTopologyState.PreSwap, payload, allowEffects);

        }

        // After the staged root took the live name, with or without the parent flush that publishes
        // it. The two are indistinguishable from here, so the flush is simply redone.
        if (Is(live, stagedIdentity)
            && !staged.Present
            && (replacing
                ? liveIdentity is { } rolledBack && Is(displaced, rolledBack)
                : !displaced.Present))
        {

            return Durable(BackupRestoreTopologyState.PostSwap, payload, allowEffects);

        }

        return Ambiguous();

    }

    private static Result<BackupRestoreTopologyState> Durable(
        BackupRestoreTopologyState state,
        BackupRestoreJournalPayloadV2 payload,
        bool allowEffects)
    {

        if (!allowEffects)
        {

            return state;

        }

        Result flushed = FlushDirectory(payload.LiveRoot.CanonicalParentPath);

        return flushed.IsFailure
            ? Result<BackupRestoreTopologyState>.Failure(flushed.Error)
            : state;

    }

    private static Result<BackupRestoreTopologyState> Ambiguous() =>
        new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "The installation tree does not match any state this restore's journal can have reached.");

    /// <summary>
    /// Renames one journaled node onto another, both named by evidence already proved.
    /// </summary>
    /// <remarks>
    /// A rename changes two directories: the entry leaves one parent and appears in another. This
    /// flushes the parent it left, and the caller flushes the parent it arrived in — a rollback that
    /// published the new entry but not the removal would leave a restart able to see the same tree
    /// under both names.
    /// </remarks>
    private static Result Rename(
        BackupRestoreDurableNodeIdentityV1 source,
        BackupRestoreDurableNodeIdentityV1 destination)
    {

        Result<string> from = ChildPath(source);

        Result<string> to = ChildPath(destination);

        if (from.IsFailure || to.IsFailure)
        {

            return from.IsFailure ? from.Error : to.Error;

        }

        try
        {

            Directory.Move(from.Value, to.Value);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "An interrupted restore's installation root could not be moved back into place.");

        }

        return FlushDirectory(source.CanonicalParentPath);

    }

    /// <summary>
    /// Flushes a directory entry so a rename cannot outrun the entry that publishes it.
    /// </summary>
    private static Result FlushDirectory(string directory)
    {

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                directory,
                out SafeFileHandle handle,
                out _))
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "An installation root's parent directory could not be opened to prove its durability.");

        }

        using (handle)
        {

            return BackupRestoreJournalNativeMethods.TryFlushDirectory(handle)
                ? Result.Success()
                : new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "An installation root's directory entry could not be flushed.");

        }

    }

    /// <summary>
    /// Opens one journaled node through its recorded parent and reports what is actually there.
    /// </summary>
    /// <remarks>
    /// The parent is verified before the child is even named. A recorded path that became a symlink
    /// between publication and recovery therefore redirects nothing, because the identity behind it no
    /// longer matches and the node is refused before any rename or deletion.
    /// </remarks>
    private static Result<NodeObservation> Observe(BackupRestoreDurableNodeIdentityV1 node)
    {

        if (node is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal node cannot be observed without its recorded identity.");

        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                node.CanonicalParentPath,
                out FileHandleMetadata parent)
            || parent.Kind is not FileSystemObjectKind.Directory)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "A restore journal's recorded parent directory could not be opened without following "
                + "links.");

        }

        if (BackupRestoreJournalAuthenticator.PhysicalIdentity(
                parent.Identity.VolumeId,
                parent.Identity.FileId)
            != node.ParentPhysicalIdentityDigest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "A restore journal's recorded parent directory is not the one this restore wrote "
                + "through.");

        }

        Result<string> child = ChildPath(node);

        if (child.IsFailure)
        {

            return Result<NodeObservation>.Failure(child.Error);

        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                child.Value,
                out FileHandleMetadata metadata))
        {

            return ProveAbsent(node);

        }

        FileSystemObjectKind expected = node.NodeKind is BackupRestoreNodeKind.Directory
            ? FileSystemObjectKind.Directory
            : FileSystemObjectKind.RegularFile;

        return metadata.Kind == expected
            ? new NodeObservation(
                true,
                BackupRestoreJournalAuthenticator.PhysicalIdentity(
                    metadata.Identity.VolumeId,
                    metadata.Identity.FileId))
            : new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "A restore journal's recorded node is no longer the kind of object it was written as.");

    }

    /// <summary>
    /// Absence, or a typed refusal when absence cannot be proved.
    /// </summary>
    /// <remarks>
    /// A failed metadata query is not an absent node: it answers the same way for a missing entry and
    /// for one that cannot be read. The parent has already been opened and its identity verified, so
    /// enumerating it is what turns "I could not look" into either a proven absence or a blocker.
    /// </remarks>
    private static Result<NodeObservation> ProveAbsent(BackupRestoreDurableNodeIdentityV1 node)
    {

        try
        {

            foreach (string entry in Directory.EnumerateFileSystemEntries(node.CanonicalParentPath))
            {

                if (string.Equals(Path.GetFileName(entry), node.ChildLeaf, StringComparison.Ordinal))
                {

                    return new Error(
                        ErrorCodes.Covenant.ManualRecoveryRequired,
                        "A restore journal's recorded node exists but could not be identified.");

                }

            }

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A restore journal's recorded parent directory could not be read, so its child is "
                + "neither present nor proven absent.");

        }

        return new NodeObservation(false, default);

    }

    private static bool Matches(NodeObservation observation, BackupRestoreDurableNodeIdentityV1 node) =>
        node.Presence is BackupRestoreNodePresence.Present
            ? node.NodePhysicalIdentityDigest is { } identity && Is(observation, identity)
            : !observation.Present;

    private static bool Is(NodeObservation observation, CovenantDigest identity) =>
        observation.Present && observation.Identity == identity;

    private static Result<string> ChildPath(BackupRestoreDurableNodeIdentityV1 node)
    {

        try
        {

            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(node.CanonicalParentPath, node.ChildLeaf)));

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal names a node whose location is not a resolvable path.");

        }

    }

    private static bool SamePath(string left, string right)
    {

        try
        {

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return false;

        }

    }

    /// <summary>What one recorded node actually is on disk right now.</summary>
    private readonly record struct NodeObservation(bool Present, CovenantDigest Identity);

}
