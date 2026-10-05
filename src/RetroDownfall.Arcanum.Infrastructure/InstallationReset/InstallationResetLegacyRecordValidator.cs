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
/// every selected root has to be one the current roots for that scope and workspace would select, so a
/// widened record cannot point the sweep at a directory this installation never owned; and no account
/// may be restore, reset or transition evidence or the host-tools taint marker, which an ordinary
/// reset retains by definition.</para>
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
