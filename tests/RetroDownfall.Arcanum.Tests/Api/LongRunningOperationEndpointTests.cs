using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LongRunningOperationEndpointTests
{
    private readonly ArcanumWebApplicationFactory _factory;

    public LongRunningOperationEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task ListAndShow_ReturnSafeSummariesWithoutCheckpointPayload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        _ = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            LongRunningOperationKinds.BlobEncryptionMigration,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Indexed 10 safe paths.",
            now));
        _ = await store.TryAcquireLeaseAsync(operation.Id, "api-test", now, now.AddMinutes(1));
        _ = await store.SaveCheckpointAsync(
            operation.Id,
            "api-test",
            0,
            1,
            [115, 101, 99, 114, 101, 116],
            null,
            "Indexed 10 safe paths.",
            now);

        HttpClient client = _factory.CreateAuthenticatedClient();
        HttpResponseMessage listResponse = await client.GetAsync("/api/operations");
        HttpResponseMessage showResponse = await client.GetAsync($"/api/operations/{operation.Id:D}");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, showResponse.StatusCode);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        string showJson = await showResponse.Content.ReadAsStringAsync();
        Assert.Contains("Indexed 10 safe paths.", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpointPayload", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("c2VjcmV0", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpointPayload", showJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("c2VjcmV0", showJson, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task CancelThenRetry_UsesCurrentRevisionAndReturnsUpdatedState()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        _ = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LongRunningOperation operation = await store.CreateAsync(new LongRunningOperationCreateRequest(
            LongRunningOperationKinds.BlobEncryptionMigration,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Retry API test.",
            now));
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            operation.Id,
            "api-test",
            now,
            now.AddMinutes(1));

        HttpClient client = _factory.CreateAuthenticatedClient();
        HttpResponseMessage cancel = await client.PostAsync(
            $"/api/operations/{operation.Id:D}/cancel",
            content: null);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        LongRunningOperation cancelling = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.Equal(LongRunningOperationState.Cancelling, cancelling.State);

        _ = await store.TryTransitionAsync(
            operation.Id,
            cancelling.Revision,
            "api-test",
            LongRunningOperationState.Abandoned,
            now.AddSeconds(1));
        HttpResponseMessage retry = await client.PostAsync(
            $"/api/operations/{operation.Id:D}/retry",
            content: null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        LongRunningOperation pending = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(operation.Id));
        Assert.Equal(LongRunningOperationState.Pending, pending.State);
        Assert.True(pending.Revision > leased.Operation.Revision);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, CovenantOfflineTransitionLaunchV4.CurrentVersion, LongRunningOperationState.ReconciliationRequired)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, DataRetentionFactoryTransitionLaunchV2.CurrentVersion, LongRunningOperationState.ReconciliationRequired)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Completed)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Failed)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Abandoned)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Completed)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Failed)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Abandoned)]
    public async Task Current_owner_bound_offline_transition_cancel_returns_conflict_without_mutating_row(
        string kind,
        int checkpointVersion,
        LongRunningOperationState state)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        HttpClient client = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation created = await store.CreateAsync(new LongRunningOperationCreateRequest(
            kind,
            kind == LongRunningOperationKinds.DataRetentionMutation
                ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                : LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Retain authenticated offline-transition recovery.",
            now));
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            created.Id,
            "offline-transition-owner",
            now,
            now.AddMinutes(1));
        byte[] checkpointPayload = [0x01, 0x02];
        string checkpointReference = "offline-transition:" + created.Id.ToString("N");

        Assert.True(leased.Acquired);
        Assert.True(await store.SaveCheckpointAsync(
            created.Id,
            "offline-transition-owner",
            expectedCheckpointVersion: 0,
            checkpointVersion,
            checkpointPayload,
            checkpointReference,
            created.PublicSummary,
            now.AddSeconds(1)));
        LongRunningOperation checkpointed = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));
        if (state != LongRunningOperationState.Running)
        {
            Assert.True(await store.TryTransitionAsync(
                created.Id,
                checkpointed.Revision,
                "offline-transition-owner",
                state,
                now.AddSeconds(2),
                ErrorCodes.Covenant.MaintenanceFailed));
        }
        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));

        HttpResponseMessage response = await client.PostAsync(
            $"/api/operations/{created.Id:D}/cancel",
            content: null);
        ApiResponse<LongRunningOperationDto>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(envelope);
        Assert.False(envelope.IsSuccess);
        Assert.Equal(ErrorCodes.Operation.StateConflict, envelope.Error?.Code);
        if (state is LongRunningOperationState.Completed or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned)
        {
            Assert.Contains("terminal", envelope.Error?.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("restart the host", envelope.Error?.Message, StringComparison.Ordinal);
        }
        else
        {
            AssertOwnerBoundGuidance(envelope.Error?.Message);
        }
        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));
        AssertStoredOperationEqual(before, after);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, CovenantOfflineTransitionLaunchV4.CurrentVersion, LongRunningOperationState.ReconciliationRequired)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, DataRetentionFactoryTransitionLaunchV2.CurrentVersion, LongRunningOperationState.ReconciliationRequired)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Completed)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Failed)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, LongRunningOperationState.Abandoned)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Running)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Completed)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Failed)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, LongRunningOperationState.Abandoned)]
    public async Task Current_owner_bound_offline_transition_retry_returns_conflict_without_mutating_row(
        string kind,
        int checkpointVersion,
        LongRunningOperationState state)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        HttpClient client = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        LongRunningOperation created = await store.CreateAsync(new LongRunningOperationCreateRequest(
            kind,
            kind == LongRunningOperationKinds.DataRetentionMutation
                ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                : LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Retain authenticated offline-transition recovery.",
            now));
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            created.Id,
            "offline-transition-owner",
            now,
            now.AddMinutes(1));
        byte[] checkpointPayload = [0x01, 0x02];
        string checkpointReference = "offline-transition:" + created.Id.ToString("N");

        Assert.True(leased.Acquired);
        Assert.True(await store.SaveCheckpointAsync(
            created.Id,
            "offline-transition-owner",
            expectedCheckpointVersion: 0,
            checkpointVersion,
            checkpointPayload,
            checkpointReference,
            created.PublicSummary,
            now.AddSeconds(1)));
        LongRunningOperation checkpointed = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));
        if (state != LongRunningOperationState.Running)
        {
            Assert.True(await store.TryTransitionAsync(
                created.Id,
                checkpointed.Revision,
                "offline-transition-owner",
                state,
                now.AddSeconds(2),
                ErrorCodes.Covenant.MaintenanceFailed));
        }
        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));

        HttpResponseMessage response = await client.PostAsync(
            $"/api/operations/{created.Id:D}/retry",
            content: null);
        ApiResponse<LongRunningOperationDto>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(envelope);
        Assert.False(envelope.IsSuccess);
        Assert.Equal(ErrorCodes.Operation.StateConflict, envelope.Error?.Code);
        if (state is LongRunningOperationState.Completed or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned)
        {
            Assert.Contains("terminal", envelope.Error?.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("restart the host", envelope.Error?.Message, StringComparison.Ordinal);
        }
        else
        {
            AssertOwnerBoundGuidance(envelope.Error?.Message);
        }
        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));
        AssertStoredOperationEqual(before, after);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 2)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 0)]
    public async Task Legacy_retention_checkpoint_retry_remains_available(
        string kind,
        int checkpointVersion)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        HttpClient client = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store =
            scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        DateTimeOffset now = new(2026, 9, 17, 13, 0, 0, TimeSpan.Zero);
        LongRunningOperation created = await store.CreateAsync(new LongRunningOperationCreateRequest(
            kind,
            kind == LongRunningOperationKinds.DataRetentionMutation
                ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                : LongRunningOperationRecoveryPolicy.RestartIdempotently,
            "Retry a legacy retention checkpoint.",
            now));
        LongRunningOperationLeaseResult leased = await store.TryAcquireLeaseAsync(
            created.Id,
            "legacy-retention-owner",
            now,
            now.AddMinutes(1));
        byte[]? checkpointPayload = checkpointVersion == 0 ? null : [0x03, 0x04];
        string? checkpointReference = checkpointVersion == 0
            ? null
            : "legacy-retention:" + created.Id.ToString("N");

        Assert.True(leased.Acquired);
        if (checkpointVersion > 0)
        {
            Assert.True(await store.SaveCheckpointAsync(
                created.Id,
                "legacy-retention-owner",
                expectedCheckpointVersion: 0,
                checkpointVersion,
                checkpointPayload,
                checkpointReference,
                created.PublicSummary,
                now.AddSeconds(1)));
        }

        LongRunningOperation checkpointed = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));
        Assert.True(await store.TryTransitionAsync(
            created.Id,
            checkpointed.Revision,
            "legacy-retention-owner",
            LongRunningOperationState.ReconciliationRequired,
            now.AddSeconds(2),
            ErrorCodes.Data.ReconciliationFailed));
        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));

        HttpResponseMessage response = await client.PostAsync(
            $"/api/operations/{created.Id:D}/retry",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await store.GetAsync(created.Id));
        Assert.Equal(LongRunningOperationState.Pending, after.State);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Null(after.TerminalErrorCode);
        Assert.Equal(before.CheckpointVersion, after.CheckpointVersion);
        Assert.Equal(before.CheckpointReference, after.CheckpointReference);
        Assert.Equal(before.CheckpointPayload, after.CheckpointPayload);
    }

    [SkippableFact]
    public async Task Reconcile_reports_owner_recovery_separately_without_replaying_or_mutating_rows()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        HttpClient client = _factory.CreateAuthenticatedClient();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ILongRunningOperationStore store = scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>();
        Assert.Same(store, scope.ServiceProvider.GetRequiredService<ILongRunningOperationOwnerRecoveryDiagnostics>());
        long baseline = await ReadOwnerRecoveryCountAsync(client);
        DateTimeOffset now = DateTimeOffset.UtcNow.AddHours(-1);
        List<LongRunningOperation> retained = [];

        foreach ((string kind, int version) in new[]
        {
            (LongRunningOperationKinds.DataRetentionMutation, 4),
            (LongRunningOperationKinds.DataRetentionFactoryReset, 2),
        })
        {
            LongRunningOperation created = await store.CreateAsync(new LongRunningOperationCreateRequest(
                kind, kind == LongRunningOperationKinds.DataRetentionMutation
                    ? LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                    : LongRunningOperationRecoveryPolicy.RestartIdempotently, "Owner recovery test.", now));
            Assert.True((await store.TryAcquireLeaseAsync(created.Id, "offline-owner", now, now.AddMinutes(1))).Acquired);
            Assert.True(await store.SaveCheckpointAsync(
                created.Id, "offline-owner", 0, version, [1, 2], "private-checkpoint", created.PublicSummary, now));
            LongRunningOperation checkpointed = (await store.GetAsync(created.Id))!;
            Assert.True(await store.TryTransitionAsync(
                created.Id, checkpointed.Revision, "offline-owner", LongRunningOperationState.ReconciliationRequired,
                now, ErrorCodes.Covenant.MaintenanceFailed));
            retained.Add((await store.GetAsync(created.Id))!);
        }

        Assert.Equal(baseline + 2, await ReadOwnerRecoveryCountAsync(client));
        foreach (LongRunningOperation before in retained)
        {
            AssertStoredOperationEqual(before, (await store.GetAsync(before.Id))!);
        }
    }

    [SkippableFact]
    public async Task Reconcile_does_not_report_zero_when_owner_recovery_diagnostics_fail()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddScoped<ILongRunningOperationOwnerRecoveryDiagnostics, UnavailableOwnerRecoveryDiagnostics>(),
        };
        using HttpClient client = factory.CreateAuthenticatedClient();
        using HttpResponseMessage response = await client.PostAsync("/api/operations/reconcile", content: null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("\"ownerRecoveryRequired\":0", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class UnavailableOwnerRecoveryDiagnostics : ILongRunningOperationOwnerRecoveryDiagnostics
    {
        public Task<long> CountOwnerRecoveryRequiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default) =>
            throw new IOException("Diagnostic count unavailable.");
    }

    private static async Task<long> ReadOwnerRecoveryCountAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("/api/operations/reconcile", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("data").TryGetProperty("ownerRecoveryRequired", out JsonElement count),
            "Manual reconciliation must report excluded owner-bound recovery separately from skipped operations.");
        Assert.DoesNotContain("private-checkpoint", json, StringComparison.Ordinal);
        return count.GetInt64();
    }

    [SkippableFact]
    public async Task OperationsRequireApiKey()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/operations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static void AssertOwnerBoundGuidance(string? message)
    {
        Assert.NotNull(message);
        Assert.Contains("authenticated offline transition", message, StringComparison.Ordinal);
        Assert.Contains("Resolve the recorded blocker", message, StringComparison.Ordinal);
        Assert.Contains("restart the host", message, StringComparison.Ordinal);
        Assert.Contains("same operation", message, StringComparison.Ordinal);
        Assert.Contains("arcanum operation show <id>", message, StringComparison.Ordinal);
        Assert.Contains("Do not start a second reset", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Only Failed, Abandoned", message, StringComparison.Ordinal);
    }

    private static void AssertStoredOperationEqual(
        LongRunningOperation expected,
        LongRunningOperation actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.RecoveryPolicy, actual.RecoveryPolicy);
        Assert.Equal(expected.RootOperationId, actual.RootOperationId);
        Assert.Equal(expected.ParentOperationId, actual.ParentOperationId);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.InferenceRunId, actual.InferenceRunId);
        Assert.Equal(expected.BudgetReservationId, actual.BudgetReservationId);
        Assert.Equal(expected.IdempotencyClaimId, actual.IdempotencyClaimId);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.StartedAt, actual.StartedAt);
        Assert.Equal(expected.HeartbeatAt, actual.HeartbeatAt);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
        Assert.Equal(expected.LeaseOwner, actual.LeaseOwner);
        Assert.Equal(expected.LeaseExpiresAt, actual.LeaseExpiresAt);
        Assert.Equal(expected.AttemptCount, actual.AttemptCount);
        Assert.Equal(expected.CheckpointVersion, actual.CheckpointVersion);
        Assert.Equal(expected.CheckpointPayload, actual.CheckpointPayload);
        Assert.Equal(expected.CheckpointReference, actual.CheckpointReference);
        Assert.Equal(expected.PublicSummary, actual.PublicSummary);
        Assert.Equal(expected.TerminalErrorCode, actual.TerminalErrorCode);
        Assert.Equal(expected.Revision, actual.Revision);
    }
}
