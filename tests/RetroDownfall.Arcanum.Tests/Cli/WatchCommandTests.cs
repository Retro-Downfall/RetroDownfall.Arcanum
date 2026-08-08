using System.Net;

using System.Globalization;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class WatchCommandTests
{

    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid ApprenticeId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]

    public void Watch_help_exposes_every_live_source_and_free_form_filters()
    {

        CliTestResult result = RunCommand(new ScriptedHandler(), ["watch", "--help"]);

        Assert.Equal(0, result.ExitCode);

        foreach (string source in new[] { "session", "apprentice", "logs", "mcp", "daemons", "health" })
        {

            Assert.Contains(source, result.Output, StringComparison.OrdinalIgnoreCase);

        }

        CliTestResult logs = RunCommand(new ScriptedHandler(), ["watch", "logs", "--help"]);

        Assert.Contains("--level", logs.Output, StringComparison.Ordinal);

        Assert.Contains("--category", logs.Output, StringComparison.Ordinal);

        Assert.Contains("--search", logs.Output, StringComparison.Ordinal);

        Assert.Contains("--event-type", logs.Output, StringComparison.Ordinal);

        Assert.Contains("--tool", logs.Output, StringComparison.Ordinal);

        Assert.Contains("--reconnect", logs.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Watch_json_parse_failure_keeps_event_stdout_empty()
    {

        ScriptedHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "health", "--interval", "not-a-number"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.True(string.IsNullOrWhiteSpace(result.Output));

        Assert.Contains("invalid", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(handler.Requests);

    }

    public static TheoryData<string[], string, string, string> SseSources => new()
    {

        {

            ["watch", "session", SessionId.ToString("D")],

            $"/api/sessions/{SessionId:D}/stream",

            "{\"id\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\",\"role\":\"assistant\",\"content\":\"answer\",\"toolName\":null,\"createdAt\":\"2026-08-01T12:00:00Z\"}",

            "id"

        },

        {

            ["watch", "apprentice", ApprenticeId.ToString("D")],

            $"/api/apprentices/{ApprenticeId:D}/chronicle",

            "{\"type\":\"stepCompleted\",\"apprenticeId\":\"22222222-2222-2222-2222-222222222222\",\"timestamp\":\"2026-08-01T12:00:00Z\",\"description\":\"done\"}",

            "type"

        },

        {

            ["watch", "logs"],

            "/api/events/logs",

            "{\"sequence\":7,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"warning\",\"category\":\"Api\",\"message\":\"slow\",\"properties\":{}}",

            "sequence"

        },

        {

            ["watch", "mcp"],

            "/api/events/mcp",

            "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"serverName\":\"forge\",\"state\":\"running\",\"message\":null,\"tools\":[\"apply_patch\"]}",

            "serverName"

        },

        {

            ["watch", "daemons"],

            "/api/events/daemon",

            "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"runId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\",\"jobName\":\"scribe\",\"targetSpell\":\"index\",\"eventType\":\"started\"}",

            "jobName"

        },

    };

    [Theory]

    [MemberData(nameof(SseSources))]

    public void Watch_sse_sources_emit_only_raw_ndjson_events(
        string[] args,
        string expectedPath,
        string payload,
        string expectedProperty)
    {

        ScriptedHandler handler = new(
            _ => Sse($": keep-alive\n\ndata: {payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(handler, ["--json", .. args]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("keep-alive", result.Error, StringComparison.OrdinalIgnoreCase);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);

        Assert.True(handler.SawApiKey);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.True(document.RootElement.TryGetProperty(expectedProperty, out _));

        Assert.DoesNotContain("keep-alive", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("[DONE]", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Watch_logs_passes_server_filters_and_applies_free_form_event_filter()
    {

        const string Debug = "{\"sequence\":1,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"debug\",\"category\":\"Api\",\"message\":\"needle\",\"properties\":{}}";

        const string Warning = "{\"sequence\":2,\"timestamp\":\"2026-08-01T12:00:01Z\",\"level\":\"warning\",\"category\":\"Api\",\"message\":\"needle\",\"properties\":{}}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Debug}\n\ndata: {Warning}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            [

                "--json",

                "watch",

                "logs",

                "--level",

                "information",

                "--category",

                "Api",

                "--search",

                "needle",

                "--event-type",

                "WaRnInG",

            ]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Contains("level=information", request.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("category=Api", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("search=needle", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(2, document.RootElement.GetProperty("sequence").GetInt64());

    }

    [Fact]

    public void Watch_mcp_tool_filter_is_case_insensitive_and_does_not_hide_other_matching_tools()
    {

        const string First = "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"serverName\":\"one\",\"state\":\"running\",\"tools\":[\"read_file\"]}";

        const string Second = "{\"timestamp\":\"2026-08-01T12:00:01Z\",\"serverName\":\"two\",\"state\":\"running\",\"tools\":[\"Apply_Patch\",\"write_file\"]}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {First}\n\ndata: {Second}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "mcp", "--tool", "apply_patch"]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal("two", document.RootElement.GetProperty("serverName").GetString());

    }

    [Fact]

    public void Watch_logs_tool_filter_reads_serilog_tool_name_properties()
    {

        const string Payload = "{\"sequence\":16,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"information\",\"category\":\"Tools\",\"message\":\"invoked\",\"properties\":{\"ToolName\":\"\\\"Apply_Patch\\\"\"}}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "logs", "--tool", "apply_patch"]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(16, document.RootElement.GetProperty("sequence").GetInt64());

    }

    [Fact]

    public void Watch_blank_filter_values_do_not_hide_events()
    {

        const string Payload = "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"serverName\":\"forge\",\"state\":\"running\",\"tools\":[]}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            [

                "--json",

                "watch",

                "mcp",

                "--event-type",

                "   ",

                "--tool",

                string.Empty,

            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Single(OutputLines(result.Output));

    }

    [Fact]

    public void Watch_parser_joins_multiline_data_and_stops_only_on_done()
    {

        ScriptedHandler handler = new(
            _ => Sse(
                "data: {\"sequence\":9,\"timestamp\":\"2026-08-01T12:00:00Z\",\n"
                + "data: \"level\":\"information\",\"category\":\"Api\",\"message\":\"joined\",\"properties\":{}}\n\n"
                + "data: [DONE]\n\n"));

        CliTestResult result = RunCommand(handler, ["--json", "watch", "logs"]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal("joined", document.RootElement.GetProperty("message").GetString());

    }

    [Fact]

    public void Watch_logs_emits_a_domain_event_that_has_a_connected_property()
    {

        const string Payload = "{\"connected\":true,\"sequence\":13,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"information\",\"category\":\"Api\",\"message\":\"client connected\",\"properties\":{}}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "logs"]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(13, document.RootElement.GetProperty("sequence").GetInt64());

    }

    [Fact]

    public void Watch_session_treats_a_non_string_type_as_domain_data()
    {

        const string Payload = "{\"id\":\"dddddddd-dddd-dddd-dddd-dddddddddddd\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\",\"type\":7,\"role\":\"assistant\",\"content\":\"numeric domain type\",\"createdAt\":\"2026-08-01T12:00:00Z\"}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "session", SessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(7, document.RootElement.GetProperty("type").GetInt32());

    }

    [Fact]

    public void Watch_skips_a_malformed_event_without_losing_later_valid_events()
    {

        const string Valid = "{\"sequence\":10,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"information\",\"category\":\"Api\",\"message\":\"valid\",\"properties\":{}}";

        ScriptedHandler handler = new(
            _ => Sse(
                $"data: not-json\n\ndata: {Valid}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "logs"]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(10, document.RootElement.GetProperty("sequence").GetInt64());

        Assert.Contains("Api.InvalidResponse", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Watch_skips_a_non_object_json_frame_without_losing_later_events()
    {

        const string Valid = "{\"sequence\":17,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"information\",\"category\":\"Api\",\"message\":\"valid\",\"properties\":{}}";

        ScriptedHandler handler = new(
            _ => Sse(
                $"data: 42\n\ndata: {Valid}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "logs"]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(17, document.RootElement.GetProperty("sequence").GetInt64());

        Assert.Contains("Api.InvalidResponse", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Watch_unexpected_eof_is_diagnostic_and_fails_without_reconnect()
    {

        const string Payload = "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"serverName\":\"forge\",\"state\":\"running\",\"tools\":[]}";

        ScriptedHandler handler = new(_ => Sse($"data: {Payload}\n\n"));

        CliTestResult result = RunCommand(handler, ["--json", "watch", "mcp"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Single(OutputLines(result.Output));

        Assert.Contains("disconnect", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Watch_http_error_leaves_json_stdout_empty_and_reports_stderr_only()
    {

        ScriptedHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {

                Content = new StringContent(
                    "{\"isSuccess\":false,\"error\":{\"code\":\"Api.RateLimited\",\"message\":\"Try later.\"}}"),

            });

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "mcp"]);

        Assert.Equal(1, result.ExitCode);

        Assert.True(string.IsNullOrWhiteSpace(result.Output));

        Assert.Contains("Api.RateLimited", result.Error, StringComparison.Ordinal);

        Assert.Contains("Try later", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Watch_reconnect_warns_about_possible_gap_and_resumes_session_cursor_without_claiming_replay()
    {

        Guid entryId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        string payload = "{\"id\":\"cccccccc-cccc-cccc-cccc-cccccccccccc\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\",\"role\":\"assistant\",\"content\":\"answer\",\"createdAt\":\"2026-08-01T12:00:00Z\"}";

        Queue<HttpResponseMessage> responses = new(
            [Sse($"data: {payload}\n\n"), Sse("data: [DONE]\n\n")]);

        ScriptedHandler handler = new(_ => responses.Dequeue());

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "session", SessionId.ToString("D"), "--reconnect"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Contains(
            $"since={entryId:D}",
            handler.Requests[1].RequestUri!.Query,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("gap", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("may", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("replayed", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Watch_reconnect_does_not_retry_a_permanent_http_error()
    {

        ScriptedHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {

                Content = new StringContent(
                    "{\"isSuccess\":false,\"error\":{\"code\":\"Validation.InvalidQuery\",\"message\":\"Invalid filter.\"}}"),

            });

        ServiceCollection services = CreateServices(handler);

        await using ServiceProvider provider = services.BuildServiceProvider();

        WatchCommands commands = provider.GetRequiredService<WatchCommands>();

        using CancellationTokenSource cancellation = new(
            TimeSpan.FromMilliseconds(200));

        int exitCode = await commands.Logs(
            null,
            null,
            null,
            new WatchCommandOptions(true, [], []),
            cancellation.Token);

        Assert.Equal(1, exitCode);

        Assert.Single(handler.Requests);

    }

    [Fact]

    public void Watch_reconnect_keeps_increasing_backoff_when_each_connection_flaps_after_data()
    {

        const string Payload = "{\"timestamp\":\"2026-08-01T12:00:00Z\",\"serverName\":\"forge\",\"state\":\"running\",\"tools\":[]}";

        Queue<HttpResponseMessage> responses = new(
            [

                Sse($"data: {Payload}\n\n"),

                Sse($"data: {Payload}\n\n"),

                Sse($"data: {Payload}\n\n"),

                Sse("data: [DONE]\n\n"),

            ]);

        ScriptedHandler handler = new(_ => responses.Dequeue());

        ImmediateRecordingTimeProvider timeProvider = new();

        ServiceCollection services = CreateServices(handler);

        services.RemoveAll<TimeProvider>();

        services.AddSingleton<TimeProvider>(timeProvider);

        CliTestResult result = CliTestHarness.Run(
            services,
            ["--json", "watch", "mcp", "--reconnect"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
            timeProvider.Delays);

    }

    /// <summary>
    /// <c>apprentice chronicle</c> was a second spelling of this stream and is gone; the canonical
    /// entry must still hand back the server's own event objects untouched.
    /// </summary>
    [Fact]
    public void Watch_apprentice_preserves_raw_ndjson()
    {

        const string Payload = "{\"type\":\"toolCall\",\"apprenticeId\":\"22222222-2222-2222-2222-222222222222\",\"timestamp\":\"2026-08-01T12:00:00Z\",\"toolCall\":{\"name\":\"apply_patch\"}}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "apprentice", ApprenticeId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal("toolCall", document.RootElement.GetProperty("type").GetString());

        Assert.True(document.RootElement.TryGetProperty("toolCall", out _));

    }

    [Theory]

    [InlineData(1, 1)]

    [InlineData(2, 2)]

    [InlineData(5, 16)]

    [InlineData(6, 30)]

    [InlineData(100, 30)]

    public void Reconnect_backoff_is_exponential_with_only_the_delay_capped(
        int attempt,
        int expectedSeconds)
    {

        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            WatchCommands.ReconnectDelay(attempt));

    }

    [Fact]

    public void Watch_health_emits_an_unhealthy_503_snapshot_before_a_later_transport_failure()
    {

        HealthReportDto report = new(
            HealthStatus.Unhealthy,
            [new HealthComponentDto("Grimoire", HealthStatus.Unhealthy, "Unavailable")]);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ApiResponse<HealthReportDto>.FromResult(
                Result<HealthReportDto>.Success(report)),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        Queue<HttpResponseMessage> responses = new(
            [

                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {

                    Content = new ByteArrayContent(json),

                },

                new HttpResponseMessage(HttpStatusCode.BadGateway),

            ]);

        ScriptedHandler handler = new(_ => responses.Dequeue());

        CliTestResult result = RunCommand(
            handler,
            ["--json", "watch", "health", "--interval", "1"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.True(document.RootElement.TryGetProperty("timestamp", out _));

        Assert.True(document.RootElement.TryGetProperty("status", out _));

        Assert.Single(document.RootElement.GetProperty("components").EnumerateArray());

    }

    [Fact]

    public void Watch_health_rejects_only_a_non_positive_poll_interval()
    {

        ScriptedHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["watch", "health", "--interval", "0"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("positive", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Watch_health_reconnect_does_not_retry_missing_authentication()
    {

        ScriptedHandler handler = new();

        ServiceCollection services = CreateServices(handler);

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore(null));

        await using ServiceProvider provider = services.BuildServiceProvider();

        WatchCommands commands = provider.GetRequiredService<WatchCommands>();

        using CancellationTokenSource cancellation = new(
            TimeSpan.FromMilliseconds(200));

        int exitCode = await commands.Health(
            1,
            new WatchCommandOptions(true, [], []),
            cancellation.Token);

        Assert.Equal(1, exitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public void Watch_health_reconnect_retries_a_structured_transient_status()
    {

        Error transientError = new(
            ErrorCodes.RateLimit.TooManyRequests,
            "Try the health observation again.");

        HealthReportDto report = new(
            HealthStatus.Healthy,
            [new HealthComponentDto("API", HealthStatus.Healthy, "Ready")]);

        byte[] transientJson = JsonSerializer.SerializeToUtf8Bytes(
            ApiResponse<HealthReportDto>.FromResult(
                Result<HealthReportDto>.Failure(transientError)),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        byte[] successJson = JsonSerializer.SerializeToUtf8Bytes(
            ApiResponse<HealthReportDto>.FromResult(
                Result<HealthReportDto>.Success(report)),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        Queue<HttpResponseMessage> responses = new(
            [

                new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {

                    Content = new ByteArrayContent(transientJson),

                },

                new HttpResponseMessage(HttpStatusCode.OK)
                {

                    Content = new ByteArrayContent(successJson),

                },

                new HttpResponseMessage(HttpStatusCode.BadRequest),

            ]);

        ScriptedHandler handler = new(_ => responses.Dequeue());

        ImmediateRecordingTimeProvider timeProvider = new(
            DateTimeOffset.Parse(
                "2026-08-01T12:00:00Z",
                CultureInfo.InvariantCulture));

        ServiceCollection services = CreateServices(handler);

        services.RemoveAll<TimeProvider>();

        services.AddSingleton<TimeProvider>(timeProvider);

        CliTestResult result = CliTestHarness.Run(
            services,
            ["--json", "watch", "health", "--interval", "1", "--reconnect"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(3, handler.Requests.Count);

        Assert.Single(OutputLines(result.Output));

        Assert.Contains("gap", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Watch_terminal_output_uses_explicit_utc_timestamp_and_event_type()
    {

        const string Payload = "{\"sequence\":11,\"timestamp\":\"2026-08-01T08:00:00-04:00\",\"level\":\"warning\",\"category\":\"Api\",\"message\":\"slow\",\"properties\":{}}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {Payload}\n\ndata: [DONE]\n\n"));

        CliTestResult result = RunCommand(
            handler,
            ["--plain", "watch", "logs"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(
            "[2026-08-01T12:00:00.000Z] warning",
            result.Output,
            StringComparison.Ordinal);

        Assert.Contains("Api: slow", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Watch_health_terminal_timestamp_is_invariant_utc()
    {

        HealthReportDto report = new(
            HealthStatus.Healthy,
            [new HealthComponentDto("API", HealthStatus.Healthy, "Ready")]);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ApiResponse<HealthReportDto>.FromResult(
                Result<HealthReportDto>.Success(report)),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        Queue<HttpResponseMessage> responses = new(
            [

                new HttpResponseMessage(HttpStatusCode.OK)
                {

                    Content = new ByteArrayContent(json),

                },

                new HttpResponseMessage(HttpStatusCode.BadGateway),

            ]);

        ScriptedHandler handler = new(_ => responses.Dequeue());

        ImmediateRecordingTimeProvider timeProvider = new(
            DateTimeOffset.Parse(
                "2026-08-01T12:00:00Z",
                CultureInfo.InvariantCulture));

        ServiceCollection services = CreateServices(handler);

        services.RemoveAll<TimeProvider>();

        services.AddSingleton<TimeProvider>(timeProvider);

        CultureInfo originalCulture = CultureInfo.CurrentCulture;

        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");

            CliTestResult result = CliTestHarness.Run(
                services,
                ["--plain", "watch", "health", "--interval", "1"]);

            Assert.Equal(1, result.ExitCode);

            Assert.Contains(
                "[2026-08-01T12:00:00.000Z] Healthy",
                result.Output,
                StringComparison.Ordinal);

            Assert.Contains("Ready", result.Output, StringComparison.Ordinal);

        }
        finally
        {

            CultureInfo.CurrentCulture = originalCulture;

            CultureInfo.CurrentUICulture = originalUiCulture;

        }

    }

    [Fact]

    public void Watch_json_keeps_prior_events_and_reports_late_exceptions_only_on_stderr()
    {

        const string First = "{\"sequence\":14,\"level\":\"information\",\"category\":\"Api\",\"message\":\"kept\",\"properties\":{}}";

        const string Second = "{\"sequence\":15,\"level\":\"information\",\"category\":\"Api\",\"message\":\"throws\",\"properties\":{}}";

        ScriptedHandler handler = new(
            _ => Sse($"data: {First}\n\ndata: {Second}\n\ndata: [DONE]\n\n"));

        ServiceCollection services = CreateServices(handler);

        services.RemoveAll<TimeProvider>();

        services.AddSingleton<TimeProvider>(new ThrowingTimeProvider());

        CliTestResult result = CliTestHarness.Run(
            services,
            ["--json", "watch", "logs"]);

        Assert.Equal(1, result.ExitCode);

        string line = Assert.Single(OutputLines(result.Output));

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Equal(14, document.RootElement.GetProperty("sequence").GetInt64());

        Assert.DoesNotContain("exitCode", result.Output, StringComparison.Ordinal);

        Assert.Contains("unexpected", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task Watch_caller_cancellation_returns_130()
    {

        ScriptedHandler handler = new(
            _ => Sse("data: [DONE]\n\n"));

        ServiceCollection services = CreateServices(handler);

        await using ServiceProvider provider = services.BuildServiceProvider();

        WatchCommands commands = provider.GetRequiredService<WatchCommands>();

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        int exitCode = await commands.Mcp(
            new WatchCommandOptions(false, [], []),
            cancellation.Token);

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);

    }

    [Fact]

    public async Task Watch_json_flushes_each_event_before_the_stream_completes()
    {

        const string FirstEvent = "data: {\"sequence\":12,\"timestamp\":\"2026-08-01T12:00:00Z\",\"level\":\"information\",\"category\":\"Api\",\"message\":\"live\",\"properties\":{}}\n\n";

        GatedSseStream stream = new(
            FirstEvent,
            "data: [DONE]\n\n");

        ScriptedHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = new StreamContent(stream),

            });

        ServiceCollection services = CreateServices(handler);

        await using ServiceProvider provider = services.BuildServiceProvider();

        TextWriter originalOutput = Console.Out;

        LiveCaptureWriter liveOutput = new();

        Console.SetOut(liveOutput);

        try
        {

            Task<int> invocation = CliApplicationFactory.RunAsync(
                ["--json", "watch", "logs"],
                provider);

            await stream.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(5));

            bool observedBeforeCompletion = await WaitForOutputAsync(
                liveOutput,
                "\"sequence\":12",
                TimeSpan.FromSeconds(1));

            Assert.False(invocation.IsCompleted);

            stream.Release();

            Assert.Equal(0, await invocation.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(
                observedBeforeCompletion,
                "The first NDJSON event was buffered until the watch completed.");

        }
        finally
        {

            stream.Release();

            Console.SetOut(originalOutput);

        }

    }

    private static CliTestResult RunCommand(ScriptedHandler handler, string[] args)
    {

        ServiceCollection services = CreateServices(handler);

        return CliTestHarness.Run(services, args);

    }

    private static ServiceCollection CreateServices(ScriptedHandler handler)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        return services;

    }

    private static async Task<bool> WaitForOutputAsync(
        LiveCaptureWriter writer,
        string expected,
        TimeSpan timeout)
    {

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {

            if (writer.ToString().Contains(expected, StringComparison.Ordinal))
            {

                return true;

            }

            await Task.Delay(10);

        }

        return false;

    }

    private static HttpResponseMessage Sse(string content) => new(HttpStatusCode.OK)
    {

        Content = new StringContent(content, Encoding.UTF8, "text/event-stream"),

    };

    private static string[] OutputLines(string output) =>
        output.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class FakeSecretStore(string? apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                apiKey is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(ScriptedHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {

            BaseAddress = new Uri("http://localhost:5001/"),

            Timeout = Timeout.InfiniteTimeSpan,

        };

    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        public bool SawApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            SawApiKey |= request.Headers.Contains("X-Arcanum-Key");

            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);

        }

    }

    private sealed class GatedSseStream(
        string firstChunk,
        string finalChunk) : Stream
    {

        private readonly byte[] _first = Encoding.UTF8.GetBytes(firstChunk);

        private readonly byte[] _final = Encoding.UTF8.GetBytes(finalChunk);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _readIndex;

        private TaskCompletionSource FirstReadSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstChunkRead => FirstReadSource.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {

            get => throw new NotSupportedException();

            set => throw new NotSupportedException();

        }

        public void Release() => _release.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            if (_readIndex == 0)
            {

                _readIndex++;

                _first.AsSpan().CopyTo(buffer.Span);

                FirstReadSource.TrySetResult();

                return _first.Length;

            }

            if (_readIndex == 1)
            {

                await _release.Task.WaitAsync(cancellationToken);

                _readIndex++;

                _final.AsSpan().CopyTo(buffer.Span);

                return _final.Length;

            }

            return 0;

        }

        public override void Flush()
        {

        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

    }

    private sealed class LiveCaptureWriter : TextWriter
    {

        private readonly object _gate = new();

        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {

            lock (_gate)
            {

                _buffer.Append(value);

            }

        }

        public override void Write(string? value)
        {

            lock (_gate)
            {

                _buffer.Append(value);

            }

        }

        public override string ToString()
        {

            lock (_gate)
            {

                return _buffer.ToString();

            }

        }

    }

    private sealed class ImmediateRecordingTimeProvider(
        DateTimeOffset? utcNow = null) : TimeProvider
    {

        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() =>
            utcNow ?? DateTimeOffset.UtcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {

            Delays.Add(dueTime);

            ThreadPool.QueueUserWorkItem(
                _ => callback(state));

            return NoopTimer.Instance;

        }

        private sealed class NoopTimer : ITimer
        {

            public static NoopTimer Instance { get; } = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {

            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        }

    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {

        private int _calls;

        public override DateTimeOffset GetUtcNow()
        {

            if (Interlocked.Increment(ref _calls) > 1)
            {

                throw new InvalidOperationException("Test clock failure.");

            }

            return DateTimeOffset.Parse(
                "2026-08-01T12:00:00Z",
                CultureInfo.InvariantCulture);

        }

    }

}
