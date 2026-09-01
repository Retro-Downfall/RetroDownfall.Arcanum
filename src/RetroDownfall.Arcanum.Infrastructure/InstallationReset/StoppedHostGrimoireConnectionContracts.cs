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

}
