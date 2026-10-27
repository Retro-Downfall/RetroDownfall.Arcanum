using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("audit", "audit-log:")]
    [InlineData("guardrails", "guardrail-log:")]
    public async Task Log_inventory_prune_selects_only_checkpoint_compatible_names(string stem, string prefix)
    {
        RequireSqlCipher();

        string valid = stem + "-20000101.jsonl";

        string[] invalid = [stem + "-.jsonl", stem + "-1.jsonl", stem + "-old.jsonl", stem + "-snapshot.jsonl", stem + "-１２３４５６７８.jsonl", stem + "-200001010.jsonl"];

        foreach (string name in invalid.Append(valid))
        {
            string path = Path.Combine(_logsRoot, name);

            await File.WriteAllTextAsync(path, "log");

            File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        ArcanumSettings settings = CreatePruneSettings();

        settings.Retention.AuditLogs = EnabledRule();

        settings.Retention.GuardrailLogs = EnabledRule();

        DataRetentionService service = CreateService(settings);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        DataRetentionPlan plan = await service.PlanAsync(request);

        Assert.Equal(prefix + valid, Assert.Single(plan.CandidateIds));

        Assert.Equal(1, plan.Files);

        Assert.Equal(3, plan.EstimatedBytes);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(new(request, plan.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(1, applied.Value.FilesDeleted);

        Assert.Empty(applied.Value.Conflicts);

        Assert.False(File.Exists(Path.Combine(_logsRoot, valid)));

        Assert.All(invalid, name => Assert.True(File.Exists(Path.Combine(_logsRoot, name))));

        Assert.Empty((await service.PlanAsync(request)).CandidateIds);
    }

    [SkippableTheory]
    [InlineData("audit", "cleanup", null)]
    [InlineData("guardrails", "cleanup", null)]
    [InlineData("audit", "retention", null)]
    [InlineData("guardrails", "retention", null)]
    [InlineData("audit", "cleanup", "retention")]
    [InlineData("guardrails", "cleanup", "retention")]
    [InlineData("audit", "retention", "cleanup")]
    [InlineData("guardrails", "retention", "cleanup")]
    [InlineData("audit", "retention", "retention")]
    [InlineData("guardrails", "retention", "retention")]
    public async Task Log_inventory_factory_recovers_every_owned_quarantine_family(string stem, string outer, string? inner)
    {
        RequireSqlCipher();

        string root = Directory.CreateDirectory(Path.Combine(_logsRoot, ".arcanum-" + outer + "-" + Guid.NewGuid().ToString("N") + "-probe")).FullName;

        string directory = inner is null ? root : Directory.CreateDirectory(Path.Combine(root, ".arcanum-" + inner + "-" + Guid.NewGuid().ToString("N") + "-probe")).FullName;

        string quarantined = Path.Combine(directory, stem + "-20000101.jsonl");

        string top = Path.Combine(_logsRoot, stem + "-20000102.jsonl");

        string unrelatedDirectory = Directory.CreateDirectory(Path.Combine(_logsRoot, ".arcanum-unrelated-probe")).FullName;

        string unrelated = Path.Combine(unrelatedDirectory, stem + "-20000103.jsonl");

        await File.WriteAllTextAsync(quarantined, "ab");

        await File.WriteAllTextAsync(top, "cde");

        await File.WriteAllTextAsync(unrelated, "keep");

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        RetentionDataClass expected = stem == "audit" ? RetentionDataClass.AuditLogs : RetentionDataClass.GuardrailLogs;

        DataRetentionPlanItem logs = Assert.Single(plan.Items, item => item.DataClass == expected);

        Assert.Equal(2, logs.Files);

        Assert.Equal(5, logs.EstimatedBytes);

        (LongRunningOperationReconciliationSummary recovery, _) = await ReconcileFactoryResetV0Async(service, "log-quarantine-family");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.False(File.Exists(top));

        Assert.False(File.Exists(quarantined));

        Assert.False(Directory.Exists(root));

        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [SkippableTheory]
    [InlineData("cleanup")]
    [InlineData("retention")]
    public async Task Log_inventory_factory_refuses_quarantine_directory_links_and_preserves_outside_bytes(string family)
    {
        RequireSqlCipher();

        Skip.If(OperatingSystem.IsWindows(), "Symbolic-link creation requires Windows privileges.");

        string outside = Directory.CreateDirectory(Path.Combine(Directory.GetParent(_logsRoot)!.FullName, "outside-log-" + Guid.NewGuid().ToString("N"))).FullName;

        string sentinel = Path.Combine(outside, "audit-20000101.jsonl");

        await File.WriteAllTextAsync(sentinel, "outside");

        string link = Path.Combine(_logsRoot, ".arcanum-" + family + "-" + Guid.NewGuid().ToString("N") + "-probe");

        Directory.CreateSymbolicLink(link, outside);

        try
        {
            IOException refusal = await Assert.ThrowsAnyAsync<IOException>(() => CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset)));

            Assert.Contains("Data.ManagedTreeRefused", refusal.Message, StringComparison.Ordinal);

            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [SkippableTheory]
    [InlineData("audit", null)]
    [InlineData("guardrails", null)]
    [InlineData("audit", "cleanup")]
    [InlineData("guardrails", "cleanup")]
    [InlineData("audit", "retention")]
    [InlineData("guardrails", "retention")]
    public async Task Log_inventory_factory_preserves_non_date_names(string stem, string? quarantine)
    {
        RequireSqlCipher();

        string directory = quarantine is null ? _logsRoot : Directory.CreateDirectory(Path.Combine(_logsRoot, ".arcanum-" + quarantine + "-" + Guid.NewGuid().ToString("N") + "-probe")).FullName;

        string valid = Path.Combine(directory, stem + "-20000101.jsonl");

        string unrelated = Path.Combine(directory, stem + "-snapshot.jsonl");

        await File.WriteAllTextAsync(valid, "log");

        await File.WriteAllTextAsync(unrelated, "keep");

        DataRetentionService service = CreateService();

        DataRetentionPlan plan = await service.PlanAsync(new(DataRetentionOperation.FactoryReset));

        RetentionDataClass expected = stem == "audit" ? RetentionDataClass.AuditLogs : RetentionDataClass.GuardrailLogs;

        DataRetentionPlanItem logs = Assert.Single(plan.Items, item => item.DataClass == expected);

        Assert.Equal(1, logs.Files);

        Assert.Equal(3, logs.EstimatedBytes);

        (LongRunningOperationReconciliationSummary recovery, _) = await ReconcileFactoryResetV0Async(service, "log-name-ownership");

        Assert.Equal(1, recovery.Completed);

        Assert.Equal(0, recovery.RequiresAttention);

        Assert.False(File.Exists(valid));

        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }
}
