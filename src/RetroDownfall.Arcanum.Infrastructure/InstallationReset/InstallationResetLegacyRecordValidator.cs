using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// Requires an unsealed legacy V1 active record to name only what this installation would itself have
/// planned, before anything migrates it.
/// </summary>
/// <remarks>
/// Migrating a legacy file seals it, and every later arm trusts the roots and accounts a sealed record
/// names. So the check has to stand in front of every caller of the store's legacy migration, not in
/// front of one of them: the locked service migrates in its resume arm, and the host handoff
/// coordinator migrates the same file when the host receives a handoff the startup projection built
/// from that file's own accepted binding. A comparison of the record with a handoff built from the
/// record proves nothing about the record.
///
/// <para>Three independent checks, because the first alone proves nothing: an editor of the file can
/// recompute a binding id over whatever they wrote. The binding id has to match its own contents;
/// every selected root has to be one the current roots for that scope and the record's own workspace
/// binding would select, so a widened record cannot add a global root this installation does not own
/// or any workspace root but the <c>.arcanum</c> directory of the workspace it names; and no account
/// may be restore, reset or transition evidence or the host-tools taint marker, which an ordinary
/// reset retains by definition.</para>
///
/// <para>What this check cannot do is confirm the workspace binding itself, or the excluded roots the
/// record carries, against the registered Campaigns: after the point of no return the Grimoire that
/// registers them may already be gone, and this check runs on that path too. The workspace is bounded
/// where the record is used instead (<c>InstallationResetService.ValidateResumeAsync</c>): an All
/// resume requires the invocation directory to sit inside the record's workspace and outside the
/// excluded roots it names, and a Workspace resume, or an All resume before the point of no return
/// without a prepared online handoff, requires the Campaigns to resolve the same workspace.</para>
///
/// <para>The binding id is accepted in either of its two historical forms. Records written before the
/// injective preimage carry the joined-text form, and refusing them would refuse every record that
/// era produced; the weaker form is accepted here only, on a path that also checks the roots and
/// accounts directly.</para>
/// </remarks>
internal static class InstallationResetLegacyRecordValidator
{
    public static Result Validate(
        InstallationResetActiveRecord legacy,
        IInstallationResetStateRoots stateRoots)
    {
        ArgumentNullException.ThrowIfNull(legacy);

        ArgumentNullException.ThrowIfNull(stateRoots);

        InstallationResetAcceptedBinding binding = legacy.AcceptedBinding;

        bool bindingMatches =
            string.Equals(
                binding.BindingId,
                InstallationResetService.ComputeBindingId(legacy.Scope, binding),
                StringComparison.Ordinal)
            || string.Equals(
                binding.BindingId,
                InstallationResetService.ComputeDelimiterEraBindingId(legacy.Scope, binding),
                StringComparison.Ordinal);

        string[] permittedRoots = stateRoots.Resolve(legacy.Scope, legacy.Workspace);

        bool rootsPermitted = binding.SelectedRoots.All(
            root => permittedRoots.Contains(root, InstallationResetStateRoots.PathComparer));

        bool accountsOrdinary =
            InstallationResetCredentialCatalog
                .CollectOrdinaryAccounts(binding.CredentialAccounts)
                .Length == binding.CredentialAccounts.Length;

        return bindingMatches && rootsPermitted && accountsOrdinary
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "The legacy installation-reset record names state this installation would not have planned and requires manual recovery."));
    }
}
