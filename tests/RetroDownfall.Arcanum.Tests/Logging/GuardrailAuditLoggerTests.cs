using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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

    private GuardrailAuditLogger CreateLogger(bool enabled, int maxSizeMb = 100, int retentionDays = 7)
    {

        ArcanumSettings settings = new()
        {
            Guardrails = new GuardrailsSettings
            {
                Enabled = true,
                AuditLog = new GuardrailsAuditLogSettings
                {
                    Enabled = enabled,
                    FilePath = Path.Combine(_tempDirectory, "guardrails.jsonl"),
                    MaxSizeMb = maxSizeMb,
                    RetentionDays = retentionDays,
                },
            },
        };

        return new GuardrailAuditLogger(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<GuardrailAuditLogger>.Instance);

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
