using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Secrets.Security;

using Serilog.Core;

using Serilog.Events;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// What a restore writes to the log, read through Serilog's one process-wide logger.
/// </summary>
/// <remarks>
/// Swapping that logger changes it for every test running beside these, so they sit in the
/// serialized seam collection rather than in <see cref="BackupRestoreServiceTests"/>, and borrow its
/// fixture through an instance of it.
/// </remarks>
[Collection(ProcessGlobalSeamCollectionName.Value)]
public sealed class BackupRestoreServiceLoggingTests : IDisposable
{
    private readonly BackupRestoreServiceTests _restores = new();

    public void Dispose() => _restores.Dispose();

    /// <summary>
    /// A cancelled restore whose rollback could not reinstate a secret logs that, naming the secret,
    /// because the cancellation itself carries no result an operator would read.
    /// </summary>
    [Fact]
    public async Task A_cancelled_rollback_that_cannot_reinstate_a_secret_logs_a_warning_naming_it()
    {
        BackupRestoreServiceTests.Fixture fixture = await _restores.CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("logged-cancelled-rollback.arcbackup");

        using CancellationTokenSource cancellation = new();

        BackupRestoreServiceOptions options = new()
        {
            BeforePhaseForTests = phase =>
            {
                if (phase == BackupRestorePhase.Reconcile)
                {
                    cancellation.Cancel();

                    cancellation.Token.ThrowIfCancellationRequested();
                }
            },
        };

        BackupRestoreServiceTests.RecordingSecretStore store = new()
        {
            GrimoireSecret = "the prior machine secret",
            FailingGrimoireSecretWrite = 2,
        };

        IReadOnlyList<LogEvent> events = await CaptureAsync(
            () => Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _restores.Restore(store, options).RestoreAsync(
                    new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                    BackupRestoreServiceTests.Passphrase.AsMemory(),
                    cancellation.Token)));

        LogEvent warning = Assert.Single(
            events,
            static logged => logged.Level == LogEventLevel.Warning
                && logged.MessageTemplate.Text.Contains("could not be rolled back cleanly", StringComparison.Ordinal));

        Assert.Contains(
            "the Grimoire encryption secret (IOException)",
            warning.RenderMessage(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled replacement restore whose client blocker cannot be retired says so in the log. The
    /// blocker staying is safe — the next start retires it — but it used to stay without a word.
    /// </summary>
    [Fact]
    public async Task A_cancelled_restore_whose_client_blocker_cannot_be_retired_logs_why()
    {
        BackupRestoreServiceTests.Fixture fixture = await _restores.CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("logged-unretired-blocker.arcbackup");

        ClientMutationBlockerStore blocker = new(_restores.Installation);

        InstallationMaintenanceCoordination coordination = new(
            _restores.Installation,
            blocker,
            new UnreadableAfterAcquisitionResetEvidenceProbe(),
            new BackupRestoreClientMutationEvidenceProbe(
                _restores.Installation,
                new InMemoryOsCredentialStore()));

        using CancellationTokenSource cancellation = new();

        BackupRestoreServiceOptions options = new()
        {
            BeforeStagedEntryComposeForTests = _ => cancellation.Cancel(),
        };

        IReadOnlyList<LogEvent> events = await CaptureAsync(
            () => Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _restores.Restore(
                        new BackupRestoreServiceTests.RecordingSecretStore(),
                        options,
                        coordination: coordination)
                    .RestoreAsync(
                        new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                        BackupRestoreServiceTests.Passphrase.AsMemory(),
                        cancellation.Token)));

        LogEvent warning = Assert.Single(
            events,
            static logged => logged.Level == LogEventLevel.Warning
                && logged.MessageTemplate.Text.Contains("client-mutation blocker", StringComparison.Ordinal));

        Assert.Contains("test.reset_evidence_unreadable", warning.RenderMessage(), StringComparison.Ordinal);

        Assert.NotNull((await blocker.InspectAsync()).Value);
    }

    private static async Task<IReadOnlyList<LogEvent>> CaptureAsync(Func<Task> act)
    {
        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            await act();
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        return sink.Events;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
            {
                _events.Add(logEvent);
            }
        }
    }

    /// <summary>
    /// Answers clear for the acquisition, then cannot be read: what makes a blocker retirement refuse.
    /// </summary>
    private sealed class UnreadableAfterAcquisitionResetEvidenceProbe :
        IClientMutationResetEvidenceProbe
    {
        private int _inspections;

        public Task<Result<ActiveInstallationReset?>> InspectAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(
                ++_inspections == 1
                    ? Result<ActiveInstallationReset?>.Success(null)
                    : Result<ActiveInstallationReset?>.Failure(new Error(
                        "test.reset_evidence_unreadable",
                        "The reset evidence could not be read.")));
    }
}
