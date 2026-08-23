using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// Proves the core tier is the exact shape this binary declares before a full installation reset
/// journals a single Campaign cleanup child.
/// </summary>
/// <remarks>
/// A read-only adapter over <see cref="GrimoireSchemaManifestInspector"/> and nothing else. It runs
/// no DDL, no DML, no mutating <c>PRAGMA</c>, no <c>grimoire_feature_schemas</c> update, and no table
/// rebuild — it is a gate, not an installer.
///
/// <para>That restraint is the point. There is no numbered migration path in this repository, so an
/// installation whose core tier predates the kind-four cleanup objects cannot be brought forward
/// in place. Refusing leaves that installation exactly as it was, with both host-tools markers
/// still present and both still recoverable by hand. Repairing it here would instead journal
/// children into a schema nobody agreed to, immediately before the one operation that deletes the
/// evidence needed to undo the decision.</para>
///
/// <para>Every rejection collapses to one content-free failure. Naming the object that was missing
/// or drifted would turn a refusal into an oracle over the installed catalog, readable by anyone who
/// can trigger a reset attempt.</para>
/// </remarks>
internal sealed class FullInstallationResetCampaignSchemaReadiness
    : IFullInstallationResetCampaignSchemaReadiness
{

    private readonly GrimoireSchemaManifestInspector _inspector;

    internal FullInstallationResetCampaignSchemaReadiness(
        GrimoireSchemaManifestInspector inspector) =>
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

    /// <summary>
    /// Inspects the caller's already-open core connection and succeeds only for the complete exact
    /// current manifest.
    /// </summary>
    /// <remarks>
    /// Deliberately transaction-free. The coordinator holds one non-pooled core connection for the
    /// whole operation and opens its own immediate transaction later; a snapshot begun here would
    /// either be committed by a gate that has no business committing anything, or left open across
    /// the effects that follow.
    /// </remarks>
    public async Task<Result> RequireExactAsync(
        SqliteConnection liveCoreConnection,
        CancellationToken cancellationToken)
    {

        try
        {

            if (liveCoreConnection is null
                || liveCoreConnection.State != ConnectionState.Open)
            {

                return Unready();

            }

            GrimoireSchemaInspectionResult inspection = await _inspector.InspectAsync(
                liveCoreConnection,
                transaction: null,
                GrimoireSchemaManifests.Core,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Valid *and* fingerprinted. An inspection that reported success without producing the
            // installed-catalog fingerprint never completed its frame, and reading that as readiness
            // would admit exactly the uncertainty this gate exists to refuse.
            return inspection.IsValid
                && !string.IsNullOrEmpty(inspection.InstalledCatalogFingerprint)
                    ? Result.Success()
                    : Unready();

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or ObjectDisposedException
                or IOException
                or ArgumentException
                or NotSupportedException
                or OverflowException)
        {

            return Unready();

        }

    }

    private static Result Unready() =>
        Result.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The installed schema does not match this installation's declared shape. Restore a "
                + "known-good backup or reinstall before attempting a full installation reset."));

}
