using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Generated;

using RetroDownfall.Arcanum.Infrastructure.Security;


namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class InstallationResetExistingGrimoire(
    DataProtectionSecretStore secretStore,
    ArcanumSettings settings,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory)
    : IInstallationResetDataService,
      IInstallationResetWorkspaceResolver,
      IInstallationResetDatabaseIdentityReader,
      IInstallationResetHostProcessToolsDatabaseEvidenceReader
{

    private static readonly LongRunningOperationState[] RecoverableFactoryStates =
    [
        LongRunningOperationState.Running,
        LongRunningOperationState.ReconciliationRequired,
    ];

    private static readonly LongRunningOperationState[] RecoverableMutationStates =
    [
        LongRunningOperationState.Running,
        LongRunningOperationState.ReconciliationRequired,
    ];

    private readonly IOptionsMonitor<ArcanumSettings> _settings =
        new FixedArcanumSettingsMonitor(settings);

    public Task<Result<DataRetentionPlan>> PlanAsync(
        InstallationResetDataPlanRequest request,
        CancellationToken cancellationToken = default)
    {

        DataRetentionRequest? dataRequest = ToDataRequest(request);

        if (dataRequest is null)
        {

            return Task.FromResult(InvalidDataPlan());

        }

        return ExecuteAsync(
            writable: false,
            async (retention, _, _, token) =>
            {

                DataRetentionPlan plan = await retention.PlanAsync(
                    dataRequest,
                    token).ConfigureAwait(false);

                return Result<DataRetentionPlan>.Success(plan);

            },
            cancellationToken);

    }

    public Task<Result<DataRetentionApplyResult>> ApplyAsync(
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        return ExecuteAsync(
            writable: true,
            async (retention, operations, _, token) =>
            {

                if (request.Request.Operation is DataRetentionOperation.ResetWorkspace)
                {

                    Result<DataRetentionApplyResult?> recovered =
                        await RecoverWorkspaceResetAsync(
                            retention,
                            operations,
                            request,
                            token).ConfigureAwait(false);

                    if (recovered.IsFailure)
                    {

                        return Result<DataRetentionApplyResult>.Failure(
                            recovered.Error);

                    }

                    if (recovered.Value is { } completed)
                    {

                        return Result<DataRetentionApplyResult>.Success(completed);

                    }

                }

                if (request.Request.Operation is DataRetentionOperation.FactoryReset)
                {

                    Result<DataRetentionApplyResult?> recovered =
                        await RecoverFactoryResetAsync(
                            retention,
                            operations,
                            request,
                            token).ConfigureAwait(false);

                    if (recovered.IsFailure)
                    {

                        return Result<DataRetentionApplyResult>.Failure(
                            recovered.Error);

                    }

                    if (recovered.Value is { } completed)
                    {

                        return Result<DataRetentionApplyResult>.Success(completed);

                    }

                }

                return await retention.ApplyAsync(request, token)
                    .ConfigureAwait(false);

            },
            cancellationToken);

    }

    public Task<Result<InstallationResetWorkspaceResolution>> ResolveAsync(
        string invocationDirectory,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            writable: false,
            async (_, _, context, token) =>
            {

                Campaign[] campaigns = await context.Campaigns
                    .AsNoTracking()
                    .OrderBy(static campaign => campaign.Name)
                    .ToArrayAsync(token)
                    .ConfigureAwait(false);

                return InstallationResetWorkspaceResolver.Resolve(
                    invocationDirectory,
                    campaigns);

            },
            cancellationToken);

    public Task<Result<Guid>> ReadAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            writable: false,
            static async (_, _, context, token) =>
                await InstallationResetDatabaseIdentityReader
                    .ReadOpenConnectionAsync(
                        (SqliteConnection)context.Database.GetDbConnection(),
                        token)
                    .ConfigureAwait(false),
            cancellationToken);

    Task<Result<HostProcessToolsDatabaseMarkerEvidence>>
        IInstallationResetHostProcessToolsDatabaseEvidenceReader.ReadMarkerEvidenceAsync(
            CancellationToken cancellationToken) =>
        ExecuteAsync(
            writable: false,
            static async (_, _, context, token) =>
            {

                Result<HostProcessToolsDatabaseMarkerEvidence> evidence =
                    await HostToolsDatabaseMarkerProjectionReader
                        .ReadAsync(
                            (SqliteConnection)context.Database.GetDbConnection(),
                            token)
                        .ConfigureAwait(false);

                return evidence.IsSuccess
                    ? evidence
                    : HostProcessToolsEvidenceUnavailable();

            },
            cancellationToken);

    private async Task<Result<T>> ExecuteAsync<T>(
        bool writable,
        Func<
            DataRetentionService,
            LongRunningOperationStore,
            ArcanumDbContext,
            CancellationToken,
            Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string databasePath = ArcanumPaths.GrimoireDatabaseFile;

        if (!File.Exists(databasePath))
        {

            return Unavailable<T>();

        }

        try
        {

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(
                databasePath);

            SecretStoreReadResult secret = await secretStore
                .GetGrimoireEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            if (secret.Status is not SecretStoreReadStatus.Ok
                || string.IsNullOrEmpty(secret.Value))
            {

                return Unavailable<T>();

            }

            string passphrase = GrimoireKeyDerivation
                .DerivePassphraseFromEncryptionSecret(
                    secret.Value,
                    sidecar.GetSaltBytes());

            SqliteNativeRuntime.Instance.Initialize();

            GrimoireDbPassphraseSource passphraseSource = new();

            passphraseSource.SetPassphrase(passphrase);

            DbContextOptions<ArcanumDbContext> options =
                new DbContextOptionsBuilder<ArcanumDbContext>()
                    .UseSqlite(new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath,
                        Password = passphrase,
                        Pooling = false,
                        Mode = writable
                            ? SqliteOpenMode.ReadWrite
                            : SqliteOpenMode.ReadOnly,
                    }.ToString())
                    .UseModel(ArcanumDbContextModel.Instance)
                    .Options;

            await using ArcanumDbContext context = new(
                options,
                secretStore,
                passphraseSource);

            await context.Database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            SqliteConnection connection = (SqliteConnection)context.Database
                .GetDbConnection();

            if (writable)
            {

                await SqliteConnectionPragmas.ApplyAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }
            else
            {

                await ApplyReadOnlyPragmasAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);

            }

            LongRunningOperationStore operations = new(context);

            DataRetentionService retention = new(
                context,
                _settings,
                operations,
                timeProvider,
                loggerFactory.CreateLogger<DataRetentionService>());

            return await action(
                retention,
                operations,
                context,
                cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException
                or FormatException
                or SqliteException
                or CryptographicException)
        {

            return Unavailable<T>();

        }

    }

    private async Task<Result<DataRetentionApplyResult?>> RecoverFactoryResetAsync(
        DataRetentionService retention,
        LongRunningOperationStore operations,
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken)
    {

        string expectedSummary = FactoryResetSummary(
            request.ExpectedPlanId ?? string.Empty);

        List<LongRunningOperation> recoverable = [];

        foreach (LongRunningOperationState state in RecoverableFactoryStates)
        {

            IReadOnlyList<LongRunningOperation> rows = await operations.ListAsync(
                new LongRunningOperationQuery(
                    LongRunningOperationKinds.DataRetentionFactoryReset,
                    state,
                    Limit: 2),
                cancellationToken).ConfigureAwait(false);

            recoverable.AddRange(rows);

        }

        if (recoverable.Count == 0)
        {

            return await FindCompletedFactoryResetAsync(
                operations,
                request,
                expectedSummary,
                cancellationToken).ConfigureAwait(false);

        }

        if (recoverable.Count != 1
            || !MatchesFactoryResetOperation(
                recoverable[0],
                expectedSummary))
        {

            return RecoveryFailure();

        }

        LongRunningOperation operation = recoverable[0];

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (operation.State is LongRunningOperationState.Running)
        {

            bool released = await operations.TryTransitionAsync(
                operation.Id,
                operation.Revision,
                ownerId: null,
                LongRunningOperationState.ReconciliationRequired,
                now,
                ErrorCodes.Data.ReconciliationFailed,
                cancellationToken).ConfigureAwait(false);

            if (!released)
            {

                return RecoveryFailure();

            }

            operation = await operations.GetAsync(
                operation.Id,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The factory-reset operation disappeared during recovery.");

        }

        if (operation.TerminalErrorCode != ErrorCodes.Data.ReconciliationFailed)
        {

            return RecoveryFailure();

        }

        string ownerId = "installation-reset-recovery:"
            + Guid.NewGuid().ToString("N");

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5),
            cancellationToken).ConfigureAwait(false);

        if (!lease.Acquired)
        {

            return RecoveryFailure();

        }

        LongRunningOperationRecoveryResult outcome = await retention
            .RecoverFactoryResetAsync(
                lease.Operation,
                cancellationToken).ConfigureAwait(false);

        LongRunningOperation latest = await operations.GetAsync(
            operation.Id,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The factory-reset operation disappeared during recovery.");

        bool transitioned = await operations.TryTransitionAsync(
            latest.Id,
            latest.Revision,
            ownerId,
            outcome.State,
            timeProvider.GetUtcNow(),
            outcome.ErrorCode,
            cancellationToken).ConfigureAwait(false);

        if (!transitioned
            || outcome.State is not LongRunningOperationState.Completed)
        {

            return RecoveryFailure();

        }

        return Result<DataRetentionApplyResult?>.Success(
            new DataRetentionApplyResult(
                operation.Id,
                request.ExpectedPlanId ?? string.Empty,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                DerivedRecordsDeleted: 0,
                Reconciled: true,
                Blockers: [],
                Conflicts: []));

    }

    private static async Task<Result<DataRetentionApplyResult?>>
        FindCompletedFactoryResetAsync(
            LongRunningOperationStore operations,
            DataRetentionApplyRequest request,
            string expectedSummary,
            CancellationToken cancellationToken)
    {

        const int pageSize = 500;

        LongRunningOperation? match = null;

        for (int offset = 0; ; offset += pageSize)
        {

            IReadOnlyList<LongRunningOperation> rows = await operations.ListAsync(
                new LongRunningOperationQuery(
                    LongRunningOperationKinds.DataRetentionFactoryReset,
                    LongRunningOperationState.Completed,
                    Limit: pageSize,
                    Offset: offset),
                cancellationToken).ConfigureAwait(false);

            foreach (LongRunningOperation row in rows)
            {

                if (!MatchesFactoryResetOperation(row, expectedSummary))
                {

                    continue;

                }

                if (match is not null)
                {

                    return RecoveryFailure();

                }

                match = row;

            }

            if (rows.Count < pageSize)
            {

                break;

            }

        }

        if (match is null)
        {

            return Result<DataRetentionApplyResult?>.Success(null);

        }

        return Result<DataRetentionApplyResult?>.Success(
            new DataRetentionApplyResult(
                match.Id,
                request.ExpectedPlanId ?? string.Empty,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                DerivedRecordsDeleted: 0,
                Reconciled: true,
                Blockers: [],
                Conflicts: []));

    }

    private static bool MatchesFactoryResetOperation(
        LongRunningOperation operation,
        string expectedSummary) =>
        operation.RecoveryPolicy
            is LongRunningOperationRecoveryPolicy.RestartIdempotently
        && string.Equals(
            operation.PublicSummary,
            expectedSummary,
            StringComparison.Ordinal);

    private static string FactoryResetSummary(string planId) =>
        $"Applying FactoryReset data-retention plan {planId}.";

    private async Task<Result<DataRetentionApplyResult?>> RecoverWorkspaceResetAsync(
        DataRetentionService retention,
        LongRunningOperationStore operations,
        DataRetentionApplyRequest request,
        CancellationToken cancellationToken)
    {

        List<LongRunningOperation> recoverable = [];

        foreach (LongRunningOperationState state in RecoverableMutationStates)
        {

            IReadOnlyList<LongRunningOperation> rows = await operations.ListAsync(
                new LongRunningOperationQuery(
                    LongRunningOperationKinds.DataRetentionMutation,
                    state,
                    Limit: 2),
                cancellationToken).ConfigureAwait(false);

            recoverable.AddRange(rows);

        }

        if (recoverable.Count == 0)
        {

            return await FindCompletedWorkspaceResetAsync(
                operations,
                request,
                cancellationToken).ConfigureAwait(false);

        }

        if (recoverable.Count != 1)
        {

            return RecoveryFailure();

        }

        LongRunningOperation operation = recoverable[0];

        DataRetentionWorkspaceBinding? binding = request.Request.Workspace;

        if (binding is null
            || !DataRetentionService.MatchesWorkspaceResetMutation(
                operation,
                binding)
            || operation.State is LongRunningOperationState.ReconciliationRequired
                && operation.TerminalErrorCode != ErrorCodes.Data.ReconciliationFailed)
        {

            return RecoveryFailure();

        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (operation.State is LongRunningOperationState.Running)
        {

            bool released = await operations.TryTransitionAsync(
                operation.Id,
                operation.Revision,
                ownerId: null,
                LongRunningOperationState.ReconciliationRequired,
                now,
                ErrorCodes.Data.ReconciliationFailed,
                cancellationToken).ConfigureAwait(false);

            if (!released)
            {

                return RecoveryFailure();

            }

            operation = await operations.GetAsync(
                operation.Id,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The workspace-reset operation disappeared during recovery.");

        }

        if (operation.TerminalErrorCode != ErrorCodes.Data.ReconciliationFailed)
        {

            return RecoveryFailure();

        }

        string ownerId = "installation-reset-workspace-recovery:"
            + Guid.NewGuid().ToString("N");

        LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
            operation.Id,
            ownerId,
            now,
            now.AddMinutes(5),
            cancellationToken).ConfigureAwait(false);

        if (!lease.Acquired)
        {

            return RecoveryFailure();

        }

        LongRunningOperationRecoveryResult outcome = await retention
            .RecoverMutationAsync(
                lease.Operation,
                cancellationToken).ConfigureAwait(false);

        LongRunningOperation latest = await operations.GetAsync(
            operation.Id,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The workspace-reset operation disappeared during recovery.");

        bool transitioned = await operations.TryTransitionAsync(
            latest.Id,
            latest.Revision,
            ownerId,
            outcome.State,
            timeProvider.GetUtcNow(),
            outcome.ErrorCode,
            cancellationToken).ConfigureAwait(false);

        if (!transitioned)
        {

            return RecoveryFailure();

        }

        if (outcome.State is LongRunningOperationState.Failed)
        {

            return Result<DataRetentionApplyResult?>.Success(null);

        }

        if (outcome.State is not LongRunningOperationState.Completed)
        {

            return RecoveryFailure();

        }

        return Result<DataRetentionApplyResult?>.Success(
            new DataRetentionApplyResult(
                operation.Id,
                request.ExpectedPlanId ?? string.Empty,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                DerivedRecordsDeleted: 0,
                Reconciled: true,
                Blockers: [],
                Conflicts: []));

    }

    private static async Task<Result<DataRetentionApplyResult?>>
        FindCompletedWorkspaceResetAsync(
            LongRunningOperationStore operations,
            DataRetentionApplyRequest request,
            CancellationToken cancellationToken)
    {

        DataRetentionWorkspaceBinding? binding = request.Request.Workspace;

        if (binding is null)
        {

            return RecoveryFailure();

        }

        string expectedSummary = $"Applying ResetWorkspace data-retention plan "
            + $"{request.ExpectedPlanId ?? string.Empty}.";

        const int pageSize = 500;

        LongRunningOperation? match = null;

        for (int offset = 0; ; offset += pageSize)
        {

            IReadOnlyList<LongRunningOperation> rows = await operations.ListAsync(
                new LongRunningOperationQuery(
                    LongRunningOperationKinds.DataRetentionMutation,
                    LongRunningOperationState.Completed,
                    Limit: pageSize,
                    Offset: offset),
                cancellationToken).ConfigureAwait(false);

            foreach (LongRunningOperation row in rows)
            {

                if (!DataRetentionService.MatchesWorkspaceResetMutation(row, binding)
                    || !string.Equals(
                        row.PublicSummary,
                        expectedSummary,
                        StringComparison.Ordinal))
                {

                    continue;

                }

                if (match is not null)
                {

                    return RecoveryFailure();

                }

                match = row;

            }

            if (rows.Count < pageSize)
            {

                break;

            }

        }

        if (match is null)
        {

            return Result<DataRetentionApplyResult?>.Success(null);

        }

        return Result<DataRetentionApplyResult?>.Success(
            new DataRetentionApplyResult(
                match.Id,
                request.ExpectedPlanId ?? string.Empty,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                DerivedRecordsDeleted: 0,
                Reconciled: true,
                Blockers: [],
                Conflicts: []));

    }

    private static async Task ApplyReadOnlyPragmasAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";

        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

    }

    private static DataRetentionRequest? ToDataRequest(
        InstallationResetDataPlanRequest request) => request switch
        {
            { Scope: InstallationResetDataScope.Global, Workspace: null } =>
                new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            { Scope: InstallationResetDataScope.Workspace, Workspace: { } workspace } =>
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: workspace),
            _ => null,
        };

    private static Result<DataRetentionPlan> InvalidDataPlan() =>
        Result<DataRetentionPlan>.Failure(new Error(
            ErrorCodes.Data.InvalidRequest,
            "The reset data scope and workspace binding do not match."));

    private static Result<T> Unavailable<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.InventoryUnavailable,
            "The existing Grimoire could not be opened without creating or repairing state."));

    private static Result<HostProcessToolsDatabaseMarkerEvidence>
        HostProcessToolsEvidenceUnavailable() =>
        Result<HostProcessToolsDatabaseMarkerEvidence>.Failure(new Error(
            ErrorCodes.Data.ExternalRemediationRequired,
            "The host-process-tools database evidence is unavailable."));

    private static Result<DataRetentionApplyResult?> RecoveryFailure() =>
        Result<DataRetentionApplyResult?>.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The interrupted canonical data reset still requires recovery."));

    private sealed class FixedArcanumSettingsMonitor(ArcanumSettings value)
        : IOptionsMonitor<ArcanumSettings>
    {

        public ArcanumSettings CurrentValue => value;

        public ArcanumSettings Get(string? name) => value;

        public IDisposable OnChange(
            Action<ArcanumSettings, string?> listener) =>
            NoopDisposable.Instance;

    }

    private sealed class NoopDisposable : IDisposable
    {

        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }

    }

}
