using System.Text.Json;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed partial class InstallationResetStoppedHostCovenantTests
{
    [SkippableTheory]
    [InlineData(false, LongRunningOperationKinds.DataRetentionMutation, 4)]
    [InlineData(true, LongRunningOperationKinds.DataRetentionMutation, 4)]
    [InlineData(false, LongRunningOperationKinds.DataRetentionFactoryReset, 2)]
    [InlineData(true, LongRunningOperationKinds.DataRetentionFactoryReset, 2)]
    public async Task Stopped_global_reset_preserves_owner_context_without_generic_cancel_or_retry(
        bool covenantFeature, string kind, int version)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature);

        Guid id = Guid.NewGuid();

        _ = await StoppedHostResetTestSupport.ExecuteAsync(
            """
            INSERT INTO LongRunningOperations
                (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary, AttemptCount, CheckpointVersion,
                 CheckpointPayload, TerminalErrorCode, Revision)
            VALUES ($id, $kind, 7, 1, $now, 'private-stored-content-summary', 1, $version,
                    X'010203', 'Data.ReconciliationFailed', 0)
            """,
            ("$id", id.ToString("N")),
            ("$kind", kind),
            ("$now", Now),
            ("$version", version));

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        Dictionary<string, string> credentials = StoppedHostResetTestSupport.CredentialDigests(_credentials);

        var dryRun = await RunAsync(covenantFeature, GlobalArguments(apply: false));

        Assert.Equal((int)CliExitCode.Success, dryRun.ExitCode);

        using JsonDocument document = JsonDocument.Parse(dryRun.Output);

        JsonElement blocker = Assert.Single(document.RootElement.GetProperty("blockers").EnumerateArray().ToArray(),
            row => row.GetProperty("resourceId").GetString() == id.ToString("D"));

        string message = blocker.GetProperty("message").GetString()!;

        Assert.Contains("If the host is stopped", message, StringComparison.Ordinal);

        int start = message.IndexOf("start it with 'arcanum serve'", StringComparison.Ordinal);

        Assert.True(start >= 0 && message.IndexOf("arcanum operation show", StringComparison.Ordinal) > start, message);

        Assert.Contains("If startup cannot serve", message, StringComparison.Ordinal);

        Assert.Contains("authenticated offline transition", message, StringComparison.Ordinal);

        Assert.Contains("does not repair", message, StringComparison.Ordinal);

        Assert.Contains(id.ToString("D"), message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation cancel", message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation retry", message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-stored-content", message, StringComparison.Ordinal);

        if (!covenantFeature && kind == LongRunningOperationKinds.DataRetentionMutation)
        {
            string[] humanArguments = ["--plain", .. GlobalArguments(apply: false).Where(argument => argument != "--json")];

            var human = await RunAsync(covenantFeature, humanArguments);

            Assert.Equal((int)CliExitCode.Success, human.ExitCode);

            Assert.Contains("Data.ActiveOperation", human.Output, StringComparison.Ordinal);

            Assert.Contains(id.ToString("D"), human.Output, StringComparison.Ordinal);

            Assert.Contains("authenticated offline transition", human.Output, StringComparison.Ordinal);

            Assert.DoesNotContain("operation cancel", human.Output, StringComparison.Ordinal);

            Assert.DoesNotContain("operation retry", human.Output, StringComparison.Ordinal);
        }

        var apply = await RunAsync(covenantFeature, GlobalArguments(apply: true));

        Assert.Equal((int)CliExitCode.GenericError, apply.ExitCode);

        Assert.Equal(0, _mutations.Calls);

        AssertUnchanged(before);

        Assert.Equal(credentials, StoppedHostResetTestSupport.CredentialDigests(_credentials));

        AssertNoActiveResetOrJournal();
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 0, 0, null)]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "LongRest.TransportUnknown")]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "long_rest.authorized_request_required")]
    [InlineData("private-stored-content-unsupported", 99, 7, null)]
    public async Task Stopped_global_reset_orders_host_start_before_queued_parked_or_unsupported_inspection(
        string kind, int version, int state, string? terminalCode)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InstallPriorGrimoireAsync(covenantFeature: false);

        Guid id = Guid.NewGuid();

        _ = await StoppedHostResetTestSupport.ExecuteAsync(
            """
            INSERT INTO LongRunningOperations
                (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary, AttemptCount,
                 CheckpointVersion, TerminalErrorCode, Revision)
            VALUES ($id, $kind, $state, 1, $now, 'private-stored-content', 0, $version, $error, 0)
            """,
            ("$id", id.ToString("N")), ("$kind", kind), ("$state", state), ("$now", Now),
            ("$version", version), ("$error", (object?)terminalCode ?? DBNull.Value));

        await StoppedHostResetTestSupport.UseWriteAheadLoggingAsync(_fixture);

        Dictionary<string, string> before = CaptureStableFiles();

        var result = await RunAsync(false, GlobalArguments(apply: false));

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        string message = Assert.Single(document.RootElement.GetProperty("blockers").EnumerateArray().ToArray(),
            row => row.GetProperty("resourceId").GetString() == id.ToString("D")).GetProperty("message").GetString()!;

        Assert.Contains("If the host is stopped", message, StringComparison.Ordinal);

        int start = message.IndexOf("start it with 'arcanum serve'", StringComparison.Ordinal);

        Assert.True(start >= 0 && message.IndexOf("arcanum operation show", StringComparison.Ordinal) > start, message);

        int cancel = message.IndexOf("arcanum operation cancel", StringComparison.Ordinal);

        Assert.True(cancel < 0 || cancel > start, message);

        Assert.Contains("If startup cannot serve", message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-stored-content", message, StringComparison.Ordinal);

        AssertUnchanged(before);

        Assert.Equal(0, _mutations.Calls);

        AssertNoActiveResetOrJournal();
    }
}
