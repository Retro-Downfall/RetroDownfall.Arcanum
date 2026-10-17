using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IStoppedHostGrimoireConnectionAuthority : IAsyncDisposable
{
}

internal interface IStoppedHostGrimoireConnectionLease : IAsyncDisposable
{
    SqliteConnection Connection { get; }
}

internal interface IStoppedHostGrimoireAuthorityIssuer
{
    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetPlanReadAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetWorkspaceResolutionAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetIdentityReadAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetHostToolsEvidenceReadAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostInstallationResetApplyAuthority();

    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostMarkerPairResetAuthority();

    /// <summary>
    /// The one-shot authority to lend the held installation lock to the transition-journal accessor
    /// for the length of a nested protected erasure. It authorizes no connection.
    /// </summary>
    Result<IStoppedHostGrimoireConnectionAuthority>
        IssueStoppedHostFactoryErasureLockLoan();
}

internal interface IStoppedHostGrimoireConnectionFactory
{
    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetPlanReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetWorkspaceResolutionAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetIdentityReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetHostToolsEvidenceReadAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostInstallationResetApplyAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);

    Task<Result<IStoppedHostGrimoireConnectionLease>>
        OpenStoppedHostMarkerPairResetAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken);
}

internal interface IInstallationResetStoppedHostDataService
{
    Task<Result<DataRetentionPlan>> PlanUnderStoppedHostAuthorityAsync(
        InstallationResetDataPlanRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetWorkspaceResolution>>
        ResolveWorkspaceUnderStoppedHostAuthorityAsync(
            string invocationDirectory,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken);

    Task<Result<Guid>> ReadIdentityUnderStoppedHostAuthorityAsync(
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

    Task<Result<HostProcessToolsDatabaseMarkerEvidence>>
        ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken);

    Task<Result<DataRetentionApplyResult>> ApplyUnderStoppedHostAuthorityAsync(
        DataRetentionApplyRequest request,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads where the nested erasure a reset record claimed stands, so a refusal that cannot have
    /// acted can be told from one that might have.
    /// </summary>
    /// <remarks>
    /// A default member, answering that the position cannot be proved, so an implementation that does
    /// not run a protected erasure keeps the conservative behavior: a record whose nested claim has not
    /// reported is retained unless this read proves nothing was launched.
    /// </remarks>
    Task<Result<StoppedHostNestedLaunchState>> ReadNestedLaunchStateUnderStoppedHostAuthorityAsync(
        Guid nestedOperationId,
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Result<StoppedHostNestedLaunchState>.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The nested erasure's position cannot be proved by this data service.")));

    /// <summary>
    /// Reads whether the Grimoire holds its authority row: true when it does, false when there is no
    /// database file or the file never finished its first install.
    /// </summary>
    /// <remarks>
    /// The same question, answered by the same rule, the Grimoire's own start asks before it decides a
    /// database is not yet born (<see cref="Hosting.GrimoireDatabaseBootstrapper"/>). A default member,
    /// answering that the position cannot be proved, so an implementation that does not read the
    /// database keeps the conservative behavior: the installation-identity repair refuses, because what
    /// it puts a damaged identity right to depends on this answer (a row present is seeded again from
    /// the row, <see cref="ReadIdentityUnderStoppedHostAuthorityAsync"/>; a row absent is put right from
    /// the closed anchors).
    /// </remarks>
    Task<Result<bool>> ReadAuthorityRowPresenceUnderStoppedHostAuthorityAsync(
        IStoppedHostGrimoireAuthorityIssuer issuer,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            Result<bool>.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The Grimoire's authority row cannot be read by this data service.")));
}
