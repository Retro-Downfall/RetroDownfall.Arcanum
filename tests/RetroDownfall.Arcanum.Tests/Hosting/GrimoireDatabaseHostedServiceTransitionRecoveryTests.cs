using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// What the host does with an active journal, now that something in the product can finish one.
/// </summary>
/// <remarks>
/// #249 shipped the refusal, which was the right half to ship first: an installation whose erasure
/// crashed must not be bootstrapped over. What it left was an installation that could not start at
/// all. The resumption runs on the same borrowed lock, before the bootstrap, and only a durable
/// verdict lets the start continue.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class GrimoireDatabaseHostedServiceTransitionRecoveryTests : IAsyncLifetime
{

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task An_active_journal_is_resumed_and_the_start_carries_on()
    {

        string root = _workspace.CreateSubdir("host-resume");

        RecordingTransitionRecovery recovery = new(
            Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Success(
                GrimoireOfflineTransitionStartupRecoveryOutcome.Resumed));

        // The bootstrap that follows is not composed in this container, so the start fails on the
        // Grimoire rather than on the transition. What this proves is which of the two it reached:
        // the recovery ran, and the refusal that used to stand in front of the bootstrap is gone.
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => Host(root, recovery).StartAsync(CancellationToken.None));

        Assert.True(recovery.Called);

        Assert.DoesNotContain(
            "An offline Grimoire transition is active",
            failure.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_transition_that_could_not_be_finished_refuses_the_start()
    {

        string root = _workspace.CreateSubdir("host-refuse");

        RecordingTransitionRecovery recovery = new(
            Result<GrimoireOfflineTransitionStartupRecoveryOutcome>.Failure(
                new Error(ErrorCodes.Covenant.ManualRecoveryRequired, "parked")));

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Host(root, recovery).StartAsync(CancellationToken.None));

        Assert.True(recovery.Called);

        Assert.Contains(
            "An offline Grimoire transition is active",
            failure.Message,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A composition with no resuming pass keeps the refusal it had before there was one.
    /// </summary>
    /// <remarks>
    /// This is the claim the optional-dependency inventory records for this parameter, made checkable:
    /// a null recoverer resumes nothing and admits nothing.
    /// </remarks>
    [Fact]
    public async Task A_host_without_the_resuming_pass_still_refuses()
    {

        string root = _workspace.CreateSubdir("host-uncomposed");

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Host(root, transitionRecovery: null).StartAsync(CancellationToken.None));

        Assert.Contains(
            "An offline Grimoire transition is active",
            failure.Message,
            StringComparison.Ordinal);

    }

    private static GrimoireDatabaseHostedService Host(
        string root,
        IGrimoireOfflineTransitionStartupRecovery? transitionRecovery)
    {

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();

        ServiceProvider provider = services.BuildServiceProvider();

        return new GrimoireDatabaseHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestApiKeySecretStore(GrimoireFixture.TestApiKey),
            new GrimoireDbPassphraseSource(),
            root,
            new InstallationResetMaintenanceLockAccessor(),
            new ActiveJournalStartupRecovery(),
            apiAdmission: null,
            startupCoordination: null,
            transitionRecovery);

    }

    /// <summary>A pair resolution that always reports one standalone transition in flight.</summary>
    private sealed class ActiveJournalStartupRecovery : IInstallationResetStartupRecovery
    {

        public Task<Result<InstallationResetStartupRecoveryState>> RecoverBeforeBootstrapAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<InstallationResetStartupRecoveryState>.Success(
                    new InstallationResetStartupRecoveryState(
                        ActiveReset: null,
                        ExpectedInstallationId: null,
                        IsLegacyV1: false,
                        InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition)));

    }

    private sealed class RecordingTransitionRecovery(
        Result<GrimoireOfflineTransitionStartupRecoveryOutcome> answer)
        : IGrimoireOfflineTransitionStartupRecovery
    {

        internal bool Called { get; private set; }

        public Task<Result<GrimoireOfflineTransitionStartupRecoveryOutcome>> RecoverBeforeBootstrapAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            string guardedDirectory,
            string databasePath,
            InstallationResetNestedTransitionEvidenceOutcome? evidence,
            GrimoireOfflineTransitionRecoveryEvidence? journal,
            CancellationToken cancellationToken)
        {

            Called = true;

            heldInstallationLock.AssertHeldFor(guardedDirectory);

            return Task.FromResult(answer);

        }

    }

}
