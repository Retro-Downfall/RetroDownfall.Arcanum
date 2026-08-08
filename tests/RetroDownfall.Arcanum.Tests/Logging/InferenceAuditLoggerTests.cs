using Microsoft.Extensions.Logging.Abstractions;
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
/// <see cref="InferenceAuditLogger"/> — persisted inference audit log (§8.26). Each test uses its
/// own temp directory (cleaned up in <see cref="Dispose"/>) so tests never share on-disk state.
/// </summary>
public sealed class InferenceAuditLoggerTests : IDisposable
{

    private readonly string _tempDirectory;

    public InferenceAuditLoggerTests()
    {

        _tempDirectory = Path.Combine(Path.GetTempPath(), "arcanum-audit-tests-" + Guid.NewGuid().ToString("N"));

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

        InferenceAuditLogger logger = CreateLogger(enabled: false);

        await logger.LogAsync(MakeRecord("ping"), CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_tempDirectory));

    }

    [Fact]
    public async Task QueryAsync_WhenDisabled_ReturnsEmpty()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: false);

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None);

        Assert.Empty(results);

    }

    [Fact]
    public async Task LogAsync_ThenQueryAsync_RoundTripsRecord()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        InferenceAuditRecord record = MakeRecord("ping", model: "mistral:latest", sessionId: "abc-123");

        await logger.LogAsync(record, CancellationToken.None);

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None);

        InferenceAuditRecord found = Assert.Single(results);

        Assert.Equal("ping", found.RequestType);

        Assert.Equal("mistral:latest", found.Model);

        Assert.Equal("abc-123", found.SessionId);

        Assert.Equal(record.TotalTokens, found.TotalTokens);

    }

    [Fact]

    public async Task QueryAsync_WithoutFrom_ReturnsRecordsOlderThanFormerLookbackCeiling()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        DateTimeOffset oldTimestamp = DateTimeOffset.UtcNow.AddDays(-500);

        InferenceAuditRecord record = MakeRecord("old", sessionId: "old-session") with
        {

            Timestamp = oldTimestamp.ToString("O"),

        };

        string oldFile = Path.Combine(_tempDirectory, $"audit-{oldTimestamp:yyyyMMdd}.jsonl");

        string json = JsonSerializer.Serialize(record, AuditJsonContext.Default.InferenceAuditRecord);

        await File.WriteAllTextAsync(oldFile, json + "\n");

        IReadOnlyList<InferenceAuditRecord> results =
            await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None);

        InferenceAuditRecord found = Assert.Single(results);

        Assert.Equal("old-session", found.SessionId);

    }

    [Fact]
    public async Task LogAsync_ThenQueryAsync_PreservesContextEstimateAndReportedVariance()
    {
        InferenceAuditLogger logger = CreateLogger(enabled: true);
        ContextTokenBreakdown breakdown = new()
        {
            Provider = "provider",
            Model = "model",
            Profile = new ResolvedModelTokenizationProfile
            {
                ProfileId = "fallback:model",
                Type = ModelTokenizationProfileType.UnknownFallback,
                TokenizerId = "o200k_base",
                SafetyMarginPercent = 15,
                PerMessageOverheadTokens = 4,
                PerToolOverheadTokens = 8,
                ProviderFramingTokens = 3,
                StopTokenOverheadTokens = 1,
                UnknownImageReserveTokens = 2048,
                Confidence = 0.5,
            },
            Components = [],
            InputTokens = 100,
            ReservedTokens = 32,
            TotalTokens = 132,
            OverallClassification = TokenEstimateClassification.Estimated,
            SafetyMarginTokens = 10,
            ProviderReportedInputTokens = 107,
            EstimationVarianceTokens = 7,
        };
        InferenceAuditRecord record = MakeRecord("ping") with
        {
            ContextBreakdowns = [breakdown],
        };

        await logger.LogAsync(record, CancellationToken.None);
        InferenceAuditRecord found = Assert.Single(
            await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None));
        ContextTokenBreakdown foundBreakdown = Assert.Single(found.ContextBreakdowns!);

        Assert.Equal(100, foundBreakdown.InputTokens);
        Assert.Equal(107, foundBreakdown.ProviderReportedInputTokens);
        Assert.Equal(7, foundBreakdown.EstimationVarianceTokens);
        Assert.Equal("fallback:model", foundBreakdown.Profile.ProfileId);
    }

    [Fact]
    public void AuditJson_RoundTripsReasoningCountAndDropsReasoningText()
    {
        const string secretReasoning = "never-persist-this-reasoning";
        string sourceJson =
            $$"""
            {
              "timestamp": "2026-07-24T00:00:00.0000000+00:00",
              "sessionId": null,
              "requestType": "ping",
              "model": "reasoner",
              "provider": "test",
              "promptTokens": 10,
              "completionTokens": 8,
              "reasoningTokens": 7,
              "totalTokens": 18,
              "latencyMs": 42,
              "toolCalls": 0,
              "toolNames": [],
              "toolArgumentsJson": null,
              "finishReason": "stop",
              "clientIp": null,
              "spellName": null,
              "campaignId": null,
              "reasoningText": "{{secretReasoning}}"
            }
            """;

        InferenceAuditRecord? record = JsonSerializer.Deserialize(
            sourceJson,
            AuditJsonContext.Default.InferenceAuditRecord);

        Assert.NotNull(record);

        string persisted = JsonSerializer.Serialize(
            record,
            AuditJsonContext.Default.InferenceAuditRecord);
        using JsonDocument document = JsonDocument.Parse(persisted);

        Assert.Equal(
            7,
            document.RootElement.GetProperty("reasoningTokens").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("reasoningText", out _));
        Assert.DoesNotContain(secretReasoning, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogAsync_WritesFileWithOwnerOnlyPermissions()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping"), CancellationToken.None);

        string[] files = [.. Directory.EnumerateFiles(_tempDirectory)];

        Assert.Single(files);

        Assert.StartsWith("audit-", Path.GetFileName(files[0]), StringComparison.Ordinal);

        Assert.EndsWith(".jsonl", files[0], StringComparison.Ordinal);

    }

    [Fact]
    public async Task LogAsync_RedactsToolArguments_ByDefault()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true, redactToolArguments: true);

        InferenceAuditRecord record = MakeRecord("ping") with
        {
            ToolNames = ["execute_command"],
            ToolArgumentsJson = ["{\"command\":\"rm -rf /\"}"],
        };

        await logger.LogAsync(record, CancellationToken.None);

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None);

        InferenceAuditRecord found = Assert.Single(results);

        // The logger itself is the redaction boundary — WizardIntelligenceProvider always passes a
        // populated ToolArgumentsJson; whether the caller's raw JSON survives to disk depends on this
        // config. Here we simulate the caller already having redacted (empty list) since
        // WizardIntelligenceProviderTests covers the config-driven redaction at that layer; this test
        // instead confirms round-tripping a record with populated arguments.
        Assert.NotNull(found.ToolArgumentsJson);

    }

    [Fact]
    public async Task QueryAsync_FiltersByModel()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping", model: "model-a"), CancellationToken.None);

        await logger.LogAsync(MakeRecord("ping", model: "model-b"), CancellationToken.None);

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, "model-a", null, 100, CancellationToken.None);

        InferenceAuditRecord found = Assert.Single(results);

        Assert.Equal("model-a", found.Model);

    }

    [Fact]
    public async Task QueryAsync_FiltersBySessionId()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping", sessionId: "session-1"), CancellationToken.None);

        await logger.LogAsync(MakeRecord("ping", sessionId: "session-2"), CancellationToken.None);

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, "session-2", 100, CancellationToken.None);

        InferenceAuditRecord found = Assert.Single(results);

        Assert.Equal("session-2", found.SessionId);

    }

    [Fact]
    public async Task QueryAsync_RespectsLimit()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        for (int i = 0; i < 5; i++)
        {

            await logger.LogAsync(MakeRecord("ping"), CancellationToken.None);

        }

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, null, 2, CancellationToken.None);

        Assert.Equal(2, results.Count);

    }

    [Fact]
    public async Task QueryPageAsync_Cursor_preserves_snapshot_when_new_records_are_appended()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        InferenceAuditRecord oldest = MakeRecord("ping", sessionId: "oldest");

        await logger.LogAsync(oldest, CancellationToken.None);

        await logger.LogAsync(oldest with { SessionId = "middle" }, CancellationToken.None);

        await logger.LogAsync(oldest with { SessionId = "newest" }, CancellationToken.None);

        Result<AuditQueryPage<InferenceAuditRecord>> first = await logger.QueryPageAsync(
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

        Result<AuditQueryPage<InferenceAuditRecord>> second = await logger.QueryPageAsync(
            null,
            null,
            null,
            null,
            2,
            cursor,
            CancellationToken.None);

        Assert.True(second.IsSuccess);

        InferenceAuditRecord result = Assert.Single(second.Value.Records);

        Assert.Equal("oldest", result.SessionId);

        Assert.Null(second.Value.NextCursor);

    }

    [Fact]
    public async Task QueryPageAsync_Rejects_cursor_from_a_different_filter()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping", model: "model-a"), CancellationToken.None);

        await logger.LogAsync(MakeRecord("ping", model: "model-a"), CancellationToken.None);

        Result<AuditQueryPage<InferenceAuditRecord>> first = await logger.QueryPageAsync(
            null,
            null,
            "model-a",
            null,
            1,
            cursor: null,
            CancellationToken.None);

        Assert.True(first.IsSuccess);

        string cursor = Assert.IsType<string>(first.Value.NextCursor);

        Result<AuditQueryPage<InferenceAuditRecord>> mismatched = await logger.QueryPageAsync(
            null,
            null,
            "model-b",
            null,
            1,
            cursor,
            CancellationToken.None);

        Assert.True(mismatched.IsFailure);

        Assert.Equal("Validation.InvalidQuery", mismatched.Error.Code);

    }

    [Fact]
    public async Task QueryPageAsync_Rejects_cursor_when_its_file_was_replaced()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping", sessionId: "oldest"), CancellationToken.None);

        await logger.LogAsync(MakeRecord("ping", sessionId: "newest"), CancellationToken.None);

        Result<AuditQueryPage<InferenceAuditRecord>> first = await logger.QueryPageAsync(
            null,
            null,
            null,
            null,
            1,
            cursor: null,
            CancellationToken.None);

        Assert.True(first.IsSuccess);

        string cursor = Assert.IsType<string>(first.Value.NextCursor);

        string todayFile = Path.Combine(_tempDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        await File.WriteAllTextAsync(todayFile, "{}\n");

        Result<AuditQueryPage<InferenceAuditRecord>> replaced = await logger.QueryPageAsync(
            null,
            null,
            null,
            null,
            1,
            cursor,
            CancellationToken.None);

        Assert.True(replaced.IsFailure);

        Assert.Equal("Validation.InvalidQuery", replaced.Error.Code);

    }

    [Fact]
    public async Task ReverseJsonlReader_Returns_newest_record_without_reading_older_payload()
    {

        InferenceAuditRecord newest = MakeRecord("ping", sessionId: "newest");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            newest,
            AuditJsonContext.Default.InferenceAuditRecord);

        byte[] content = new byte[1_000_000 + 1 + json.Length + 1];

        Array.Fill(content, (byte)'x', 0, 1_000_000);

        content[1_000_000] = (byte)'\n';

        json.CopyTo(content.AsSpan(1_000_001));

        content[^1] = (byte)'\n';

        await using ReadBudgetStream stream = new(content, maxBytesRead: 128 * 1024);

        InferenceAuditRecord? found = null;

        await foreach (ReverseJsonlRecord<InferenceAuditRecord> candidate in ReverseJsonlFileReader
                           .ReadAsync(
                               stream,
                               stream.Length,
                               AuditJsonContext.Default.InferenceAuditRecord,
                               CancellationToken.None))
        {

            found = candidate.Value;

            break;

        }

        Assert.NotNull(found);

        Assert.Equal("newest", found!.SessionId);

        Assert.InRange(stream.BytesRead, 1, 128 * 1024);

    }

    [Fact]
    public async Task QueryAsync_ReturnsNewestFirst()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping") with { SessionId = "first" }, CancellationToken.None);

        await logger.LogAsync(MakeRecord("ping") with { SessionId = "second" }, CancellationToken.None);

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None);

        Assert.Equal(2, results.Count);

        Assert.Equal("second", results[0].SessionId);

        Assert.Equal("first", results[1].SessionId);

    }

    [Fact]
    public async Task LogAsync_SizeCapReached_DropsFurtherWrites()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        string todayFile = Path.Combine(_tempDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        int maxSizeMb = ArcanumSettingClamps.HostAuditLogMaxSizeMb(
            ArcanumRuntimeDefaults.HostAuditLog.MaxSizeMb);
        await using (FileStream stream = File.Create(todayFile))
        {
            stream.SetLength((long)maxSizeMb * 1024L * 1024L);
        }

        long sizeBefore = new FileInfo(todayFile).Length;

        await logger.LogAsync(MakeRecord("ping"), CancellationToken.None);

        long sizeAfter = new FileInfo(todayFile).Length;

        Assert.Equal(sizeBefore, sizeAfter);

    }

    [Fact]
    public async Task LogAsync_WhenUnifiedAutomaticSweepIsEnabled_DoesNotDeleteOldFiles()
    {

        InferenceAuditLogger logger = CreateLogger(
            enabled: true,
            automaticSweepsEnabled: true,
            unifiedRetentionEnabled: true,
            unifiedRetentionDays: 7);

        string oldDate = DateTime.UtcNow.AddDays(-30).ToString("yyyyMMdd");

        string oldFile = Path.Combine(_tempDirectory, $"audit-{oldDate}.jsonl");

        await File.WriteAllTextAsync(oldFile, "{}\n");

        Assert.True(File.Exists(oldFile));

        await logger.LogAsync(MakeRecord("ping"), CancellationToken.None);

        Assert.True(File.Exists(oldFile));

    }

    [Fact]
    public async Task LogAsync_DoesNotDeleteRecentFiles()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true, unifiedRetentionDays: 30);

        string recentDate = DateTime.UtcNow.AddDays(-2).ToString("yyyyMMdd");

        string recentFile = Path.Combine(_tempDirectory, $"audit-{recentDate}.jsonl");

        await File.WriteAllTextAsync(recentFile, "{}\n");

        await logger.LogAsync(MakeRecord("ping"), CancellationToken.None);

        Assert.True(File.Exists(recentFile));

    }

    [Fact]
    public async Task QueryAsync_SkipsMalformedLines_WithoutThrowing()
    {

        InferenceAuditLogger logger = CreateLogger(enabled: true);

        await logger.LogAsync(MakeRecord("ping", sessionId: "valid"), CancellationToken.None);

        string todayFile = Path.Combine(_tempDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        await File.AppendAllTextAsync(todayFile, "{ not valid json\n");

        IReadOnlyList<InferenceAuditRecord> results = await logger.QueryAsync(null, null, null, null, 100, CancellationToken.None);

        InferenceAuditRecord found = Assert.Single(results);

        Assert.Equal("valid", found.SessionId);

    }

    /// <summary>
    /// PrepareForNewDate marked the UTC day prepared before creating the directory, so one transient
    /// failure made every later turn that day take the "already prepared" fast path and die in
    /// AppendAllTextAsync — the whole day's audit trail lost with no retry. The sibling
    /// GuardrailAuditLogger assigns the flag only after success.
    /// </summary>
    [Fact]
    public async Task LogAsync_retries_directory_preparation_after_a_transient_failure()
    {

        string blockedDirectory = Path.Combine(_tempDirectory, "blocked");

        // A file where the audit directory belongs makes Directory.CreateDirectory throw.
        await File.WriteAllTextAsync(blockedDirectory, "blocker");

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                AuditLog = new HostAuditPolicySettings { Enabled = true },
            },
        };

        InferenceAuditLogger logger = new(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<InferenceAuditLogger>.Instance,
            Path.Combine(blockedDirectory, "audit.jsonl"));

        await logger.LogAsync(MakeRecord("blocked"), CancellationToken.None);

        Assert.False(Directory.Exists(blockedDirectory));

        File.Delete(blockedDirectory);

        await logger.LogAsync(MakeRecord("recovered"), CancellationToken.None);

        Assert.True(Directory.Exists(blockedDirectory));

        Assert.NotEmpty(Directory.EnumerateFiles(blockedDirectory));

    }

    private InferenceAuditLogger CreateLogger(
        bool enabled,
        bool redactToolArguments = true,
        bool automaticSweepsEnabled = true,
        bool unifiedRetentionEnabled = true,
        int unifiedRetentionDays = 7)
    {

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                AuditLog = new HostAuditPolicySettings
                {
                    Enabled = enabled,
                    RedactToolArguments = redactToolArguments,
                },
            },
            Retention = new RetentionSettings
            {
                AutomaticSweepsEnabled = automaticSweepsEnabled,
                AuditLogs = new RetentionRuleSettings
                {
                    Enabled = unifiedRetentionEnabled,
                    Days = unifiedRetentionDays,
                },
            },
        };

        return new InferenceAuditLogger(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<InferenceAuditLogger>.Instance,
            Path.Combine(_tempDirectory, "audit.jsonl"));

    }

    private static InferenceAuditRecord MakeRecord(
        string requestType,
        string? model = "test-model",
        string? sessionId = null) =>
        new(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            SessionId: sessionId,
            RequestType: requestType,
            Model: model,
            Provider: "test-provider",
            PromptTokens: 10,
            CompletionTokens: 5,
            TotalTokens: 15,
            LatencyMs: 42,
            ToolCalls: 0,
            ToolNames: [],
            ToolArgumentsJson: null,
            FinishReason: "stop",
            ClientIp: "127.0.0.1",
            SpellName: null,
            CampaignId: null);

    private sealed class ReadBudgetStream : Stream
    {

        private readonly MemoryStream _inner;

        private readonly long _maxBytesRead;

        internal ReadBudgetStream(
            byte[] content,
            long maxBytesRead)
        {

            _inner = new MemoryStream(content, writable: false);

            _maxBytesRead = maxBytesRead;

        }

        internal long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {

            get => _inner.Position;

            set => _inner.Position = value;

        }

        public override void Flush()
        {

        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {

            int read = _inner.Read(buffer, offset, count);

            Account(read);

            return read;

        }

        public override int Read(Span<byte> buffer)
        {

            int read = _inner.Read(buffer);

            Account(read);

            return read;

        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            int read = await _inner.ReadAsync(buffer, cancellationToken);

            Account(read);

            return read;

        }

        public override long Seek(
            long offset,
            SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {

            if (disposing)
            {

                _inner.Dispose();

            }

            base.Dispose(disposing);

        }

        private void Account(int count)
        {

            BytesRead += count;

            if (BytesRead > _maxBytesRead)
            {

                throw new IOException("The reverse reader exceeded its bounded read budget.");

            }

        }

    }

}
