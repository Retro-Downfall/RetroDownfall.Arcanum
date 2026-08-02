using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task FactoryReset_WhenManagedLogPublicationWins_DeletesTheCountedAppend(
        bool guardrail)
    {

        RequireSqlCipher();

        CoordinatedManagedLogMutationGate gate = new();

        ArcanumSettings settings = CreateManagedLogSettings();

        Task publication = PublishManagedLogAsync(
            guardrail,
            settings,
            gate);

        await gate.FirstReleaseRequested.WaitAsync(TimeSpan.FromSeconds(10));

        string pattern = guardrail
            ? "guardrails-????????.jsonl"
            : "audit-????????.jsonl";

        string publishedPath = Assert.Single(
            Directory.EnumerateFiles(_logsRoot, pattern));

        DataRetentionService service = CreateServiceWithManagedLogGate(
            settings,
            gate);

        DataRetentionRequest request = new(
            DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        DataRetentionPlanItem logItem = Assert.Single(
            plan.Items,
            item => item.DataClass == (guardrail
                ? RetentionDataClass.GuardrailLogs
                : RetentionDataClass.AuditLogs));

        Assert.Equal(1, logItem.Files);

        Task<Result<DataRetentionApplyResult>> reset = service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        await gate.SecondAttempted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reset.IsCompleted);

        gate.AllowFirstRelease();

        await publication;

        Result<DataRetentionApplyResult> result = await reset;

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(1, result.Value.FilesDeleted);

        Assert.False(File.Exists(publishedPath));

    }

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task FactoryReset_WhenResetWins_WaitingManagedLogPublishesAfterReset(
        bool guardrail)
    {

        RequireSqlCipher();

        CoordinatedManagedLogMutationGate gate = new();

        ArcanumSettings settings = CreateManagedLogSettings();

        DataRetentionService service = CreateServiceWithManagedLogGate(
            settings,
            gate);

        DataRetentionRequest request = new(
            DataRetentionOperation.FactoryReset);

        DataRetentionPlan plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Task<Result<DataRetentionApplyResult>> reset = service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        await gate.FirstReleaseRequested.WaitAsync(TimeSpan.FromSeconds(10));

        Task publication = PublishManagedLogAsync(
            guardrail,
            settings,
            gate);

        await gate.SecondAttempted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(publication.IsCompleted);

        Assert.Empty(Directory.EnumerateFiles(_logsRoot, "*.jsonl"));

        gate.AllowFirstRelease();

        Result<DataRetentionApplyResult> result = await reset;

        await publication;

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.Reconciled);

        Assert.Equal(0, result.Value.FilesDeleted);

        string pattern = guardrail
            ? "guardrails-????????.jsonl"
            : "audit-????????.jsonl";

        string publishedPath = Assert.Single(
            Directory.EnumerateFiles(_logsRoot, pattern));

        Assert.NotEmpty(await File.ReadAllTextAsync(publishedPath));

        LongRunningOperation marker = Assert.Single(
            await new LongRunningOperationStore(_db!).ListAsync(
                new LongRunningOperationQuery(
                    Kind: LongRunningOperationKinds.DataRetentionFactoryReset,
                    Limit: 10)));

        Assert.Equal(LongRunningOperationState.Completed, marker.State);

    }

    private DataRetentionService CreateServiceWithManagedLogGate(
        ArcanumSettings settings,
        IManagedLogMutationGate gate)
    {

        LongRunningOperationStore operations = new(_db!);

        return new DataRetentionService(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            operations,
            TimeProvider.System,
            NullLogger<DataRetentionService>.Instance,
            _attachmentsRoot,
            _filesRoot,
            _logsRoot,
            policyStore: null,
            attachmentStore: null,
            daemonExecutions: null,
            daemonMutationGate: null,
            managedLogMutationGate: gate);

    }

    private Task PublishManagedLogAsync(
        bool guardrail,
        ArcanumSettings settings,
        IManagedLogMutationGate gate)
    {

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        if (guardrail)
        {

            GuardrailAuditLogger auditLogger = new(
                options,
                NullLogger<GuardrailAuditLogger>.Instance,
                Path.Combine(_logsRoot, "guardrails.jsonl"),
                gate);

            GuardrailAuditRecord record = new(
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                SessionId: null,
                Stage: "Input",
                ViolationType: "test",
                MatchedTextRedacted: "***",
                Model: "test-model");

            return auditLogger.LogAsync(
                record,
                CancellationToken.None);

        }

        InferenceAuditLogger inferenceLogger = new(
            options,
            NullLogger<InferenceAuditLogger>.Instance,
            Path.Combine(_logsRoot, "audit.jsonl"),
            gate);

        InferenceAuditRecord inferenceRecord = new(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: null,
            RequestType: "test",
            Model: "test-model",
            Provider: "test-provider",
            PromptTokens: 1,
            CompletionTokens: 1,
            TotalTokens: 2,
            LatencyMs: 1,
            ToolCalls: 0,
            ToolNames: [],
            ToolArgumentsJson: null,
            FinishReason: "stop",
            ClientIp: null,
            SpellName: null,
            CampaignId: null);

        return inferenceLogger.LogAsync(
            inferenceRecord,
            CancellationToken.None);

    }

    private static ArcanumSettings CreateManagedLogSettings() =>
        new()
        {

            Features = new FeatureSettings
            {

                Guardrails = true,

            },

            Host = new HostSettings
            {

                AuditLog = new HostAuditPolicySettings
                {

                    Enabled = true,

                },

            },

            Security = new SecuritySettings
            {

                Guardrails = new GuardrailsPolicySettings
                {

                    AuditLog = new GuardrailsAuditPolicySettings
                    {

                        Enabled = true,

                    },

                },

            },

            Retention = new RetentionSettings
            {

                AutomaticSweepsEnabled = false,

            },

        };

    private sealed class CoordinatedManagedLogMutationGate :
        IManagedLogMutationGate,
        IDisposable
    {

        private readonly SemaphoreSlim _gate = new(1, 1);

        private readonly TaskCompletionSource _firstReleaseRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowFirstRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _secondAttempted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _attempts;

        public Task FirstReleaseRequested =>
            _firstReleaseRequested.Task;

        public Task SecondAttempted =>
            _secondAttempted.Task;

        public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
            CancellationToken cancellationToken = default)
        {

            int attempt = Interlocked.Increment(ref _attempts);

            if (attempt == 2)
            {

                _secondAttempted.TrySetResult();

            }

            await _gate.WaitAsync(cancellationToken);

            return new Lease(
                this,
                attempt);

        }

        public void AllowFirstRelease() =>
            _allowFirstRelease.TrySetResult();

        public void Dispose() =>
            _gate.Dispose();

        private async ValueTask ReleaseAsync(int attempt)
        {

            if (attempt == 1)
            {

                _firstReleaseRequested.TrySetResult();

                await _allowFirstRelease.Task;

            }

            _gate.Release();

        }

        private sealed class Lease(
            CoordinatedManagedLogMutationGate owner,
            int attempt) : IAsyncDisposable
        {

            private int _disposed;

            public async ValueTask DisposeAsync()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                await owner.ReleaseAsync(attempt);

            }

        }

    }

}
