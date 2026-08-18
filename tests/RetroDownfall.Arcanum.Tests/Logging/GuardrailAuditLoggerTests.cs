using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Logging;

/// <summary>
/// <see cref="GuardrailAuditLogger"/> — persisted guardrails audit log (§8.x). Each test uses its
/// own temp directory (cleaned up in <see cref="Dispose"/>) so tests never share on-disk state.
/// </summary>
public sealed class GuardrailAuditLoggerTests : IDisposable
{

    private readonly string _tempDirectory;

    public GuardrailAuditLoggerTests()
    {

        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcanum-guardrails-audit-tests-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempDirectory);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_tempDirectory, recursive: true);

        }
        catch (IOException)
        {

            // Best-effort cleanup; harmless if a file handle briefly lingers on some platforms.
        }

    }

    [Fact]
    public async Task LogAsync_WhenDisabled_WritesNothing()
    {

        GuardrailAuditLogger logger = CreateLogger(enabled: false);

        await logger.LogAsync(MakeRecord("pii-email"), CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_tempDirectory));

    }

    [Fact]
    public async Task QueryAsync_WhenDisabled_ReturnsEmpty()
    {

        GuardrailAuditLogger logger = CreateLogger(enabled: false);

        IReadOnlyList<GuardrailAuditRecord> results = await logger.QueryAsync(null, null, null, null, null, 100, CancellationToken.None);

        Assert.Empty(results);

    }

    [Fact]
    public async Task LogAsync_ThenQueryAsync_RoundTripsRecord()
    {

        GuardrailAuditLogger logger = CreateLogger(enabled: true);

        GuardrailAuditRecord record = MakeRecord("pii-email", stage: "Input", sessionId: "sess-1", model: "mistral:latest");

        await logger.LogAsync(record, CancellationToken.None);

        IReadOnlyList<GuardrailAuditRecord> results = await logger.QueryAsync(null, null, null, null, null, 100, CancellationToken.None);

        GuardrailAuditRecord found = Assert.Single(results);

        Assert.Equal("Input", found.Stage);

        Assert.Equal("pii-email", found.ViolationType);

        Assert.Equal("***@***.***", found.MatchedTextRedacted);

        Assert.Equal("sess-1", found.SessionId);

        Assert.Equal("mistral:latest", found.Model);

    }

    [Fact]

    public async Task QueryAsync_WithoutFrom_ReturnsRecordsOlderThanFormerLookbackCeiling()
    {

        GuardrailAuditLogger logger = CreateLogger(enabled: true);

        DateTimeOffset oldTimestamp = DateTimeOffset.UtcNow.AddDays(-500);

        GuardrailAuditRecord record = MakeRecord("old", sessionId: "old-session") with
        {

            Timestamp = oldTimestamp.ToString("O"),

        };

        string oldFile = Path.Combine(_tempDirectory, $"guardrails-{oldTimestamp:yyyyMMdd}.jsonl");

        string json = JsonSerializer.Serialize(record, AuditJsonContext.Default.GuardrailAuditRecord);

        await File.WriteAllTextAsync(oldFile, json + "\n");

        IReadOnlyList<GuardrailAuditRecord> results =
            await logger.QueryAsync(null, null, null, null, null, 100, CancellationToken.None);

        GuardrailAuditRecord found = Assert.Single(results);

        Assert.Equal("old-session", found.SessionId);

    }

    [Fact]
    public async Task QueryAsync_FiltersByStageAndViolationType()
    {

        GuardrailAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("pii-email", stage: "Input"), CancellationToken.None);

        await logger.LogAsync(MakeRecord("toxicity", stage: "Output"), CancellationToken.None);

        IReadOnlyList<GuardrailAuditRecord> inputOnly = await logger.QueryAsync(null, null, "Input", null, null, 100, CancellationToken.None);

        Assert.Single(inputOnly);

        Assert.Equal("Input", inputOnly[0].Stage);

        IReadOnlyList<GuardrailAuditRecord> toxicityOnly = await logger.QueryAsync(null, null, null, "toxicity", null, 100, CancellationToken.None);

        Assert.Single(toxicityOnly);

        Assert.Equal("toxicity", toxicityOnly[0].ViolationType);

    }

    [Fact]
    public async Task QueryPageAsync_Cursor_preserves_snapshot_when_new_records_are_appended()
    {

        GuardrailAuditLogger logger = CreateLogger(enabled: true);

        GuardrailAuditRecord oldest = MakeRecord("oldest", sessionId: "oldest");

        await logger.LogAsync(oldest, CancellationToken.None);

        await logger.LogAsync(oldest with { SessionId = "middle" }, CancellationToken.None);

        await logger.LogAsync(oldest with { SessionId = "newest" }, CancellationToken.None);

        Result<AuditQueryPage<GuardrailAuditRecord>> first = await logger.QueryPageAsync(
            null,
            null,
            null,
            null,
            null,
            2,
            cursor: null,
            CancellationToken.None);

        Assert.True(first.IsSuccess);

        Assert.Equal(["newest", "middle"], first.Value.Records.Select(static record => record.SessionId));

        string cursor = Assert.IsType<string>(first.Value.NextCursor);

        await logger.LogAsync(oldest with { SessionId = "appended-after-snapshot" }, CancellationToken.None);

        Result<AuditQueryPage<GuardrailAuditRecord>> second = await logger.QueryPageAsync(
            null,
            null,
            null,
            null,
            null,
            2,
            cursor,
            CancellationToken.None);

        Assert.True(second.IsSuccess);

        GuardrailAuditRecord result = Assert.Single(second.Value.Records);

        Assert.Equal("oldest", result.SessionId);

        Assert.Null(second.Value.NextCursor);

    }

    [Fact]
    public async Task LogAsync_WhenUnifiedAutomaticSweepIsEnabled_DoesNotDeleteOldFiles()
    {

        GuardrailAuditLogger logger = CreateLogger(
            enabled: true,
            automaticSweepsEnabled: true,
            unifiedRetentionEnabled: true,
            unifiedRetentionDays: 7);

        string oldDate = DateTime.UtcNow.AddDays(-45).ToString("yyyyMMdd");

        string oldFile = Path.Combine(
            _tempDirectory,
            $"guardrails-{oldDate}.jsonl");

        await File.WriteAllTextAsync(oldFile, "{}\n");

        await logger.LogAsync(MakeRecord("pii-email"), CancellationToken.None);

        Assert.True(File.Exists(oldFile));

    }

    /// <summary>
    /// A dated file the reader cannot open is dropped from the result and the page still comes back
    /// <c>Success</c>, so <c>GET /api/guardrails/audit</c> answers "no violations that day" with full
    /// confidence. The only diagnostic was <c>LogDebug</c>, and the Serilog pipeline floor is a
    /// hardcoded <c>MinimumLevel.Information()</c> — no sink, no rolling file, no ring buffer, nothing
    /// in <c>arcanum logs</c>. Silent incompleteness on a security audit surface needs a signal.
    /// </summary>
    [Fact]
    public async Task QueryPageAsync_WhenADatedFileCannotBeRead_ReportsItAboveTheLogFloor()
    {

        TestCapturingLogger<GuardrailAuditLogger> diagnostics = new();

        GuardrailAuditLogger logger = CreateLogger(enabled: true, diagnostics: diagnostics);

        await logger.LogAsync(MakeRecord("today"), CancellationToken.None);

        string yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyyMMdd");

        string yesterdayFile = Path.Combine(_tempDirectory, $"guardrails-{yesterday}.jsonl");

        await File.WriteAllTextAsync(
            yesterdayFile,
            JsonSerializer.Serialize(
                MakeRecord("yesterday"),
                AuditJsonContext.Default.GuardrailAuditRecord) + "\n");

        // FileShare.None takes an exclusive advisory lock, so the reader's OpenRead (FileShare.ReadWrite
        // | FileShare.Delete) fails exactly the way a mode/ACL change or a disk error makes it fail.
        await using (FileStream _ = new(yesterdayFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {

            Result<AuditQueryPage<GuardrailAuditRecord>> page = await logger.QueryPageAsync(
                null,
                null,
                null,
                null,
                null,
                100,
                cursor: null,
                CancellationToken.None);

            Assert.True(page.IsSuccess);

            Assert.Equal(["today"], page.Value.Records.Select(static record => record.ViolationType));

        }

        TestLogEntry warning = Assert.Single(
            diagnostics.Entries,
            static entry => entry.Level >= LogLevel.Warning);

        Assert.Contains(yesterdayFile, warning.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// <see cref="AuditLogPageReader"/> is the sole owner of dated-file discovery — QueryPageAsync
    /// delegates to it wholesale. A private copy on the logger is unreachable code that parses file
    /// stamps by different rules, so a maintainer editing the copy changes nothing at runtime.
    /// </summary>
    [Fact]
    public void GuardrailAuditLogger_DeclaresNoDatedLogFileDiscoveryOfItsOwn()
    {

        string[] declared = typeof(GuardrailAuditLogger)
            .GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Where(static name => name is "EnumerateDatedLogFiles" or "ParseDatedLogFile")
            .ToArray();

        Assert.Empty(declared);

    }

    private GuardrailAuditLogger CreateLogger(
        bool enabled,
        bool automaticSweepsEnabled = true,
        bool unifiedRetentionEnabled = true,
        int unifiedRetentionDays = 7,
        ILogger<GuardrailAuditLogger>? diagnostics = null)
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Guardrails = true },
            Security = new SecuritySettings
            {
                Guardrails = new GuardrailsPolicySettings
                {
                    AuditLog = new GuardrailsAuditPolicySettings
                    {
                        Enabled = enabled,
                    },
                },
            },
            Retention = new RetentionSettings
            {
                AutomaticSweepsEnabled = automaticSweepsEnabled,
                GuardrailLogs = new RetentionRuleSettings
                {
                    Enabled = unifiedRetentionEnabled,
                    Days = unifiedRetentionDays,
                },
            },
        };

        return new GuardrailAuditLogger(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            diagnostics ?? NullLogger<GuardrailAuditLogger>.Instance,
            Path.Combine(_tempDirectory, "guardrails.jsonl"));

    }

    private static GuardrailAuditRecord MakeRecord(
        string violationType,
        string stage = "Input",
        string? sessionId = null,
        string? model = "test-model") =>
        new(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: sessionId,
            Stage: stage,
            ViolationType: violationType,
            MatchedTextRedacted: "***@***.***",
            Model: model);

}
