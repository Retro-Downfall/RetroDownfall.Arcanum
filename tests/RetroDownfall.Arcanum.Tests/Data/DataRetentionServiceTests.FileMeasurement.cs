using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("audit", RetentionDataClass.AuditLogs)]
    [InlineData("guardrails", RetentionDataClass.GuardrailLogs)]
    public async Task Measurement_recovery_status_counts_only_the_declared_dated_log_grammar(string stem, RetentionDataClass dataClass)
    {
        RequireSqlCipher();

        await File.WriteAllBytesAsync(Path.Combine(_logsRoot, stem + "-20000101.jsonl"), [1, 2, 3]);

        await File.WriteAllBytesAsync(Path.Combine(_logsRoot, stem + "-snapshot.jsonl"), [4, 5, 6, 7]);

        DataRetentionStatus status = await CreateService().GetStatusAsync();

        DataRetentionStatusItem logs = Assert.Single(status.Items, item => item.DataClass == dataClass);

        Assert.Equal(1, logs.Files);

        Assert.Equal(3, logs.EstimatedBytes);

        Assert.True(File.Exists(Path.Combine(_logsRoot, stem + "-snapshot.jsonl")));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Measurement_recovery_observes_transient_files_without_throwing_from_status(bool uploaded)
    {
        RequireSqlCipher();

        string root = uploaded ? _filesRoot : _attachmentsRoot;

        for (int index = 0; index < 2000; index++) await File.WriteAllBytesAsync(Path.Combine(root, $"stable-{index}.bin"), [1]);

        DataRetentionService service = CreateService();

        RetentionMeasurementChurn churn = await RetentionMeasurementChurn.StartAsync(root);

        Exception? failure;

        try
        {
            failure = await Record.ExceptionAsync(async () =>
            {
                for (int index = 0; index < 100; index++)
                {
                    DataRetentionStatus status = await service.GetStatusAsync();

                    Assert.True(status.Files >= 2000);

                    Assert.True(status.EstimatedBytes >= 2000);
                }
            });
        }
        finally
        {
            await churn.DisposeAsync();
        }

        Assert.True(churn.Cycles > 0);

        Assert.Null(failure);
    }

    [SkippableFact]
    public async Task Measurement_recovery_log_planning_does_not_append_a_failed_metadata_sample()
    {
        RequireSqlCipher();

        for (int index = 0; index < 64; index++)
        {
            string path = Path.Combine(_logsRoot, $"audit-{20000000 + index:D8}.jsonl");

            await File.WriteAllBytesAsync(path, [1]);

            File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.AuditLogs = EnabledRule();

        DataRetentionService service = CreateService(settings);

        RetentionMeasurementChurn churn = await RetentionMeasurementChurn.StartAsync(_logsRoot, dated: true);

        Exception? failure;

        try
        {
            failure = await Record.ExceptionAsync(async () =>
            {
                for (int index = 0; index < 100; index++)
                {
                    DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.Prune));

                    Assert.Equal(plan.Files, plan.CandidateIds.LongLength);

                    Assert.True(plan.Files >= 64);
                }
            });
        }
        finally
        {
            await churn.DisposeAsync();
        }

        Assert.True(churn.Cycles > 0);

        Assert.Null(failure);
    }
}
