using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("WorkspacePathPolicy")]
public sealed class InstallationResetStartupRecoveryTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_opening_does_not_block_host_startup(bool cancelled)
    {
        // R-002 startup twin: a reset whose first publication failed or was cancelled in a live
        // process must not leave the host unable to start. Before the rollback, startup recovery
        // and startup cleanup both refused the Active revision-zero anchor with no file.
        string guardedRoot = _workspace.CreateSubdir(
            cancelled ? "startup-interrupted-opening-cancelled" : "startup-interrupted-opening");

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        using CancellationTokenSource cancellation = new();

        InMemoryOsCredentialStore credentials = new();

        InstallationResetActiveStore interrupted = new(
            guardedRoot,
            credentials,
            new InstallationResetActiveFilePersistence(
                step =>
                {
                    if (cancelled
                        && string.Equals(step, "file:temporary-flushed", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                },
                failBeforeStep: step => !cancelled
                    && string.Equals(step, "file:atomic-replace", StringComparison.Ordinal)));

        Guid installationId = Guid.Parse("4a111111-2222-4333-8444-555555555555");

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.BeginAsync(
                heldLock,
                installationId,
                CreateRecord(),
                cancellation.Token));
        }
        else
        {
            Assert.True((await interrupted.BeginAsync(
                heldLock,
                installationId,
                CreateRecord(),
                CancellationToken.None)).IsFailure);
        }

        Assert.False(File.Exists(interrupted.ActivePath));

        InstallationResetStartupRecovery recovery = new(
            guardedRoot,
            new InstallationResetActiveStore(guardedRoot, credentials),
            new GrimoireOfflineTransitionLifecycleStore(
                new GrimoireOfflineTransitionJournalStore(new InMemoryOsCredentialStore()),
                GrimoireOfflineTransitionHandlerRegistry.Production));

        Result<InstallationResetStartupRecoveryState> recovered =
            await recovery.RecoverBeforeBootstrapAsync(heldLock, CancellationToken.None);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Null(recovered.Value.ActiveReset);

        Assert.False(recovered.Value.IsLegacyV1);
    }

    /// <summary>
    /// A transition-journal location that cannot be resolved, here because its parent directory is no
    /// longer owner-only, fails this pair and yields no transition evidence. The pair runs first on the
    /// same guarded root under the same held lock, so the terminal-suffix finisher's own resolution of
    /// that location, which answers the same condition with <c>Covenant.Unavailable</c>, is never the
    /// first read to meet a condition that persists.
    /// </summary>
    [SkippableFact]
    public async Task A_journal_location_that_cannot_be_resolved_fails_the_pair_before_any_evidence()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "This parent-posture check is macOS-specific.");

        // Dead once Skip.IfNot above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        string container = _workspace.CreateSubdir("startup-unresolvable-journal-parent");

        File.SetUnixFileMode(container, ownerOnly);

        string guardedRoot = Path.Combine(container, "arcanum");

        Directory.CreateDirectory(guardedRoot);

        using ArcanumMaintenanceLock heldLock = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guardedRoot));

        InMemoryOsCredentialStore credentials = new();

        GrimoireOfflineTransitionLifecycleStore transitions = new(
            new GrimoireOfflineTransitionJournalStore(new InMemoryOsCredentialStore()),
            GrimoireOfflineTransitionHandlerRegistry.Production);

        InstallationResetStartupRecovery recovery = new(
            guardedRoot,
            new InstallationResetActiveStore(guardedRoot, credentials),
            transitions);

        Result<InstallationResetStartupRecoveryState> healthy =
            await recovery.RecoverBeforeBootstrapAsync(heldLock, CancellationToken.None);

        Assert.True(healthy.IsSuccess, healthy.IsFailure ? healthy.Error.Message : null);

        File.SetUnixFileMode(container, ownerOnly | UnixFileMode.GroupRead);

        try
        {
            Result<InstallationResetStartupRecoveryState> recovered =
                await recovery.RecoverBeforeBootstrapAsync(heldLock, CancellationToken.None);

            Assert.True(recovered.IsFailure);

            // The pair forwards the journal's own answer unchanged, which is the answer the read the
            // finisher makes first, on the same root under the same lock, gives the same condition.
            Assert.Equal(ErrorCodes.Covenant.Unavailable, recovered.Error.Code);

            Result<GrimoireOfflineTransitionTypedRecoveryState> finisherView = await transitions
                .RecoverAsync(heldLock, guardedRoot, CancellationToken.None);

            Assert.True(finisherView.IsFailure);

            Assert.Equal(recovered.Error.Code, finisherView.Error.Code);

            Assert.Equal(recovered.Error.Message, finisherView.Error.Message);
        }
        finally
        {
            File.SetUnixFileMode(container, ownerOnly);
        }
    }

    private static InstallationResetActiveRecord CreateRecord() =>
        new(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            "composite-plan",
            InstallationResetScope.Global,
            Workspace: null,
            new InstallationResetAcceptedBinding(
                "binding",
                ["/selected"],
                ["/excluded"],
                [],
                ["master-api-key"],
                ["data-plan"]),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null);
}
