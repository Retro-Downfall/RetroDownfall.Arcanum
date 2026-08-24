using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

using FullInstallationResetManagedFileErasureAuthority =
    RetroDownfall.Arcanum.Infrastructure.InstallationReset
        .FullInstallationResetManagedFileReconciler.FullInstallationResetManagedFileErasureAuthority;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The two entry points a stopped-host full installation reset reaches this kernel through.
/// </summary>
/// <remarks>
/// They exist because a full reset has neither of the two things the ordinary entry point requires: a
/// pooled connection source, which resolves through the database context that pre-readiness code has
/// no business opening, and a live <c>CovenantArtifactErasureAuthority</c>, which is a lease minted
/// from an operator context that a stopped host cannot honestly reissue.
///
/// <para>What they substitute is narrower, not wider. The connection is the caller's own already
/// initialized core connection; the authorization is the authenticated reset journal, reasserted
/// before every transaction and every filesystem effect through the shared revalidation port. Owner
/// coverage is unconditional here for the one reason that makes it safe: the operation is removing the
/// entire installation, so there is no narrower scope for a file to fall outside of.</para>
///
/// <para>Neither overload adds a deletion algorithm. Both reach exactly the same
/// <see cref="ManagedFileErasureStateMachine"/> that the live path does, in the same order, under the
/// same schema guards, so there is one opener, one verifier, one compare-delete, and one completion
/// sequence in the product.</para>
/// </remarks>
internal sealed partial class CovenantManagedFileErasureKernel
{

    /// <summary>
    /// The scope a stopped-host reset performs its work-item writes under.
    /// </summary>
    /// <remarks>
    /// The insert guard and the adoption-to-erasure edge both refuse the managed-file intent scope and
    /// require exactly one erasure scope, so this is not a free choice. It is the same scope the
    /// pre-readiness local-erasure recovery already uses, which keeps one meaning for "a restart is
    /// finishing work the database already authorized".
    /// </remarks>
    private const CovenantSqliteAuthorizationKind FullInstallationResetAuthorization =
        CovenantSqliteAuthorizationKind.SensitivityRetentionPurge;

    /// <summary>
    /// Creates or reuses the one work item for an adopted managed source, then drives it to terminal.
    /// </summary>
    /// <remarks>
    /// The reuse is what makes a crash between a syscall and its compare-and-swap safe: an attempt that
    /// finds the durable work item already there resumes that exact row rather than issuing a second
    /// effect, and the request's work-item identity is the caller's own reread of the active row.
    /// </remarks>
    internal async Task<Result<CovenantArtifactErasureProgress>> ReconcileSourceForFullInstallationResetAsync(
        SqliteConnection connection,
        CovenantManagedFileErasureRequest request,
        FullInstallationResetManagedFileErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(authority);

        Result current = await authority.AssertCurrentAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(0, 0, 0, CovenantErasureBlocker.AuthorityStale));

        }

        Result<LocalErasureWorkItemRow> prepared = await PrepareAsync(
            connection,
            request,
            // A full installation reset covers every owner scope there is, because every owner is
            // about to stop existing. This is the one caller for which coverage is not a narrowing.
            static _ => true,
            FullInstallationResetAuthorization,
            cancellationToken).ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(1, 0, 1, MapBlocker(prepared.Error)));

        }

        return await _stateMachine
            .ResolveAsync(
                connection,
                prepared.Value,
                FullInstallationResetAuthorization,
                authority,
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Drives one work item that already exists to terminal, without re-deriving its authorization.
    /// </summary>
    /// <remarks>
    /// The row itself is the durable authorization — its insert guard already proved the producer, the
    /// revision, the artifact, and the live label — so nothing here rereads the producer. Re-deriving
    /// authority from a producer that has since moved would retarget work the database already
    /// authorized against a different file.
    /// </remarks>
    internal async Task<Result<CovenantArtifactErasureProgress>> ResumeWorkItemForFullInstallationResetAsync(
        SqliteConnection connection,
        LocalErasureWorkItemRow item,
        FullInstallationResetManagedFileErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(item);

        ArgumentNullException.ThrowIfNull(authority);

        Result current = await authority.AssertCurrentAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(0, 0, 0, CovenantErasureBlocker.AuthorityStale));

        }

        return await _stateMachine
            .ResolveAsync(
                connection,
                item,
                FullInstallationResetAuthorization,
                authority,
                cancellationToken)
            .ConfigureAwait(false);

    }

}
