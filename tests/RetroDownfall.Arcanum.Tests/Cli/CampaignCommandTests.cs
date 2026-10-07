using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;

using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class CampaignCommandTests
{
    /// <summary>
    /// CSI and OSC sequences, so an assertion about a message's text is not also an assertion
    /// about whether the terminal Spectre found supports colour.
    /// </summary>
    private static readonly Regex AnsiEscapePattern =
        new("\u001b\\[[0-9;?]*[ -/]*[@-~]|\u001b\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)", RegexOptions.Compiled);

    private static readonly Guid SampleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Campaign_list_calls_get_campaigns()
    {
        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(new ListPageResult<CampaignDto>([campaign], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Campaign_list_rejects_an_undocumented_type_without_calling_the_api()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["campaign", "list", "--type", "bogus"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--type", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_create_rejects_an_undocumented_type_without_calling_the_api()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["campaign", "create", "--name", "Demo", "--path", "/tmp/demo", "--type", "bogus"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--type", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every <c>Result.IsFailure</c> exit in this file returned the generic exit code, so a
    /// server-down failure was indistinguishable from a real domain failure. Routed through
    /// <c>CliFailureExit</c>, a <c>Connection.*</c> failure now exits 3 and names the address tried.
    /// A non-default port is configured (rather than asserting the harness's own default address)
    /// so the assertion is load-bearing on <c>CampaignCommands.WriteError</c> actually reading
    /// <c>Arcanum:Host</c>, not just coinciding with a hardcoded default.
    /// </summary>
    [Fact]
    public void Campaign_list_reports_a_network_failure_and_names_the_configured_base_address()
    {
        const int ConfiguredPort = 19999;

        RecordingHandler handler = new(_ => throw new HttpRequestException("Connection refused"));

        CliTestResult result = RunCommand(
            handler,
            ["campaign", "list"],
            configureServices: services => services.Configure<ArcanumSettings>(s => s.Host.Port = ConfiguredPort));

        Assert.Equal((int)CliExitCode.NetworkError, result.ExitCode);

        string expectedAddress = ArcanumLocalApiAddress.ResolveBaseUrl(new HostSettings { Port = ConfiguredPort });

        Assert.Contains(expectedAddress, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_get_binds_id_argument()
    {
        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignDto>(campaign, true, null),
            ArcanumJsonContext.Default.ApiResponseCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "show", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Campaign_get_reports_missing_name_candidate_after_list_lookup()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(
                new ListPageResult<CampaignDto>([], false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "show", "not-a-guid"]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);
    }

    [Fact]

    public void Campaign_operation_in_a_workspace_without_a_campaign_offers_registration()
    {
        WorkspaceInfo workspace = new(
            "ws-current",
            "current",
            Path.GetFullPath(global::System.Environment.CurrentDirectory),
            WorkspaceType.Custom,
            DateTimeOffset.UtcNow);

        RecordingHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/workspaces")
            {
                return CreateResponse(
                    new ApiResponse<WorkspaceInfo[]>([workspace], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray);
            }

            return CreateResponse(
                new ApiResponse<ListPageResult<CampaignDto>>(
                    new ListPageResult<CampaignDto>([], false),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto);
        });

        CliTestResult result = RunCommand(handler, ["campaign", "show"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains(
            "campaign create",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "server path",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Campaign_create_posts_register_request()
    {
        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignDto>(campaign, true, null),
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            HttpStatusCode.Created));

        CliTestResult result = RunCommand(
            handler,
            ["campaign", "create", "--name", "Demo", "--path", "/tmp/demo"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"name\":\"Demo\"", body, StringComparison.Ordinal);

        Assert.Contains("\"path\":\"/tmp/demo\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_delete_binds_id_and_handles_no_content()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["--yes", "campaign", "delete", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}", request.RequestUri!.AbsolutePath);
    }

    /// <summary>An irreversible delete must ask before it acts.</summary>
    [Fact]
    public void Campaign_delete_requires_confirmation_before_sending_request()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["campaign", "delete", SampleId.ToString()]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
    }

    /// <summary>Campaign codex delete is also irreversible and must ask before it acts.</summary>
    [Fact]
    public void Campaign_codex_delete_requires_confirmation_before_sending_request()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["campaign", "codex", "delete", SampleId.ToString()]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_codex_delete_binds_id_when_confirmed()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["--yes", "campaign", "codex", "delete", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}/codex", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Campaign_get_surfaces_not_found_error()
    {
        Error error = new("Campaign.NotFound", "No campaign exists with that identifier.");

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignDto>(null, false, error),
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            HttpStatusCode.NotFound));

        CliTestResult result = RunCommand(handler, ["campaign", "show", SampleId.ToString()]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/campaigns/{SampleId:D}", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// An unwritable <c>--output</c> must name the path and the OS reason, the way the sibling
    /// <c>import</c> path already reports an unreadable <c>--file</c>. Letting the exception escape
    /// leaves the operator with the generic "An unexpected CLI error occurred." and no path at all.
    /// </summary>
    [Fact]
    public void Campaign_export_reports_the_path_and_cause_when_the_output_cannot_be_written()
    {
        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignExportDto>(new CampaignExportDto(campaign, [], []), true, null),
            ArcanumJsonContext.Default.ApiResponseCampaignExportDto));

        // A directory is never a writable file destination, on any supported platform.
        string output = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-export-blocked-{Guid.NewGuid():N}");

        Directory.CreateDirectory(output);

        try
        {
            CliTestResult result = RunCommand(handler, ["campaign", "export", SampleId.ToString(), "--output", output]);

            Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

            // Spectre wraps at the profile width, so the path can carry a line break -- and it styles,
            // so the path can carry colour escapes too. Both are presentation the assertion below is
            // not about, and both depend on the terminal Spectre detects: this passed on a developer
            // machine and failed on a CI runner, where Detect answered yes and the wrap landed inside
            // the path with an escape on either side of it.
            string reported = AnsiEscapePattern.Replace(
                (result.Output + result.Error)
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal),
                string.Empty);

            Assert.Contains(output, reported, StringComparison.Ordinal);

            Assert.DoesNotContain(
                "An unexpected CLI error occurred.",
                reported,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>
    /// R-327: <c>export --output</c> over an existing file asks first, like <c>file download</c>, and a
    /// refusal leaves the file untouched.
    /// </summary>
    [Fact]
    public void Export_prompts_before_overwriting_an_existing_output_file()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-campaign-export-{Guid.NewGuid():N}.json");

        File.WriteAllText(output, "original");

        try
        {
            RecordingPrompt prompt = new(answer: false);

            RecordingHandler handler = new(_ => CampaignExportResponse());

            CliTestResult result = RunCommand(
                handler,
                ["campaign", "export", SampleId.ToString(), "--output", output],
                configureServices: services => UsePrompt(services, prompt));

            Assert.Equal(0, result.ExitCode);

            // The overwrite question is settled before the export is fetched, so a refusal costs no request.
            Assert.Empty(handler.Requests);

            Assert.Equal("original", File.ReadAllText(output));

            string question = Assert.Single(prompt.Questions);

            Assert.Contains(Path.GetFullPath(output), question, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void Export_replaces_an_existing_output_file_once_confirmed_and_leaves_no_temporary_sibling()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-campaign-export-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        string output = Path.Combine(directory, "campaign.json");

        File.WriteAllText(output, "original");

        try
        {
            CliTestResult result = RunCommand(
                new RecordingHandler(_ => CampaignExportResponse()),
                ["--yes", "campaign", "export", SampleId.ToString(), "--output", output]);

            Assert.Equal(0, result.ExitCode);

            Assert.Contains("\"campaign\"", File.ReadAllText(output), StringComparison.OrdinalIgnoreCase);

            Assert.Equal(
                ["campaign.json"],
                Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// R-327: an <c>--output</c> that cannot be written is found before the export is fetched, with the same
    /// exit code and wording as a write that fails afterwards.
    /// </summary>
    [Fact]
    public void Export_to_an_unwritable_destination_fails_before_the_export_is_fetched()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-campaign-export-missing-{Guid.NewGuid():N}",
            "campaign.json");

        RecordingHandler handler = new(_ => CampaignExportResponse());

        CliTestResult result = RunCommand(
            handler,
            ["campaign", "export", SampleId.ToString(), "--output", output]);

        Assert.Equal(1, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("Could not write", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_over_an_existing_file_refuses_a_non_interactive_run_without_yes()
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-campaign-export-json-{Guid.NewGuid():N}.json");

        File.WriteAllText(output, "original");

        try
        {
            CliTestResult result = RunCommand(
                new RecordingHandler(_ => CampaignExportResponse()),
                ["--json", "campaign", "export", SampleId.ToString(), "--output", output]);

            Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

            Assert.Equal("original", File.ReadAllText(output));

            Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static HttpResponseMessage CampaignExportResponse()
    {
        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        return CreateResponse(
            new ApiResponse<CampaignExportDto>(new CampaignExportDto(campaign, [], []), true, null),
            ArcanumJsonContext.Default.ApiResponseCampaignExportDto);
    }

    private static void UsePrompt(ServiceCollection services, IConfirmationPrompt prompt)
    {
        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton(prompt);
    }

    private sealed class RecordingPrompt(bool answer) : IConfirmationPrompt
    {
        public List<string> Questions { get; } = [];

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            Questions.Add(question);

            return Task.FromResult(answer);
        }
    }

    /// <summary>
    /// Every direct command answers <c>--json</c> with exactly one JSON document on stdout. These two
    /// listings rendered a Spectre table in every mode, so an automation caller received a box-drawn
    /// grid where its parser expected a document — and, with the table laid out at the redirected
    /// 80-column default, one that had also been wrapped.
    /// </summary>
    [Fact]
    public void Campaign_sessions_under_json_emits_one_document_rather_than_a_table()
    {
        SessionSummaryDto session = new(
            SampleId,
            SampleId,
            "Session title",
            "active",
            3,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SessionQueryResult>(
                new SessionQueryResult([session], null, false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult));

        CliTestResult result = RunCommand(handler, ["--json", "campaign", "sessions", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        SessionSummaryDto[]? emitted = JsonSerializer.Deserialize(
            result.Output,
            ArcanumJsonContext.Default.SessionSummaryDtoArray);

        Assert.NotNull(emitted);

        Assert.Equal(session.Id, Assert.Single(emitted).Id);
    }

    /// <summary>
    /// A Session title is model-authored, and Markup escaping does not remove the control sequences a
    /// terminal acts on, so the table strips them.
    /// </summary>
    [Fact]
    public void Campaign_sessions_strips_terminal_controls_from_titles()
    {
        SessionSummaryDto session = new(
            SampleId,
            SampleId,
            "ok\u001b]52;c;QUFBQQ==\u0007title\u001b[2J\u009b",
            "active",
            3,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SessionQueryResult>(
                new SessionQueryResult([session], null, false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult));

        CliTestResult result = RunCommand(handler, ["campaign", "sessions", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("oktitle", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', result.Output);
        Assert.DoesNotContain('\u0007', result.Output);
    }

    /// <summary>
    /// <c>--json</c> writes one host page as a bare array. When the host holds more, a script must be told
    /// so, and told where the next page starts: otherwise a partial listing reads as a complete one. The
    /// notice goes to stderr so the stdout document stays one array.
    /// </summary>
    [Fact]
    public void Campaign_sessions_under_json_says_on_stderr_when_more_sessions_remain()
    {
        DateTimeOffset cursor = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        SessionSummaryDto session = new(
            SampleId,
            SampleId,
            "Session title",
            "active",
            3,
            DateTimeOffset.UnixEpoch,
            cursor.AddMinutes(5));

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SessionQueryResult>(
                new SessionQueryResult([session], cursor, true),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult));

        CliTestResult result = RunCommand(handler, ["--json", "campaign", "sessions", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);
        SessionSummaryDto[]? emitted = JsonSerializer.Deserialize(
            result.Output,
            ArcanumJsonContext.Default.SessionSummaryDtoArray);
        Assert.NotNull(emitted);
        Assert.Single(emitted);
        Assert.Contains("--before-updated-at", result.Error, StringComparison.Ordinal);
        Assert.Contains(cursor.ToString("O"), result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A complete listing under <c>--json</c> says nothing on stderr.
    /// </summary>
    [Fact]
    public void Campaign_sessions_under_json_is_silent_on_stderr_when_the_page_is_complete()
    {
        SessionSummaryDto session = new(
            SampleId,
            SampleId,
            "Session title",
            "active",
            3,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SessionQueryResult>(
                new SessionQueryResult([session], null, false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult));

        CliTestResult result = RunCommand(handler, ["--json", "campaign", "sessions", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("--before-updated-at", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Campaign_prompts_under_json_emits_one_document_rather_than_a_table()
    {
        PromptSummaryDto prompt = new(
            SampleId,
            SampleId,
            "prompt-name",
            "1.0.0",
            null,
            ["tag"],
            DateTimeOffset.UnixEpoch);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<PromptSummaryDto>>(
                new ListPageResult<PromptSummaryDto>([prompt], false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto));

        CliTestResult result = RunCommand(handler, ["--json", "campaign", "prompts", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        PromptSummaryDto[]? emitted = JsonSerializer.Deserialize(
            result.Output,
            ArcanumJsonContext.Default.PromptSummaryDtoArray);

        Assert.NotNull(emitted);

        Assert.Equal(prompt.Name, Assert.Single(emitted).Name);
    }

    /// <summary>
    /// The host serves campaigns one page at a time and names the rest in <c>nextOffset</c>. A list that
    /// stopped at the first page looked complete on the registry an operator reads before deleting.
    /// </summary>
    [Fact]
    public void List_follows_hasMore_until_exhausted()
    {
        CampaignDto first = new(SampleId, "First-page-campaign", "/tmp/one", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        CampaignDto second = new(Guid.NewGuid(), "Second-page-campaign", "/tmp/two", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(request => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(
                request.RequestUri!.Query.Contains("offset=1", StringComparison.Ordinal)
                    ? new ListPageResult<CampaignDto>([second], false)
                    : new ListPageResult<CampaignDto>([first], true, NextOffset: 1),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "list"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Contains("offset=1", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        Assert.Contains("First-page-campaign", result.Output, StringComparison.Ordinal);

        Assert.Contains("Second-page-campaign", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void List_fails_without_a_partial_table_when_the_host_repeats_its_offset()
    {
        CampaignDto row = new(SampleId, "Looping-campaign", "/tmp/one", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(
                new ListPageResult<CampaignDto>([row], true, NextOffset: 1),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "list"]);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("Api.PaginationNoProgress", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("Looping-campaign", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompts_follows_hasMore_until_exhausted()
    {
        PromptSummaryDto first = new(Guid.NewGuid(), SampleId, "first-page-prompt", "1", null, [], DateTimeOffset.UtcNow);

        PromptSummaryDto second = new(Guid.NewGuid(), SampleId, "second-page-prompt", "1", null, [], DateTimeOffset.UtcNow);

        RecordingHandler handler = new(request => CreateResponse(
            new ApiResponse<ListPageResult<PromptSummaryDto>>(
                request.RequestUri!.Query.Contains("offset=1", StringComparison.Ordinal)
                    ? new ListPageResult<PromptSummaryDto>([second], false)
                    : new ListPageResult<PromptSummaryDto>([first], true, NextOffset: 1),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto));

        CliTestResult result = RunCommand(handler, ["--json", "campaign", "prompts", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        Assert.Equal($"/api/campaigns/{SampleId:D}/prompts", handler.Requests[1].RequestUri!.AbsolutePath);

        Assert.Contains("offset=1", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(2, document.RootElement.GetArrayLength());
    }

    /// <summary>
    /// An update that names nothing to change is not a success: it used to send an empty update and print
    /// "Campaign updated", so a mistyped or forgotten option looked applied.
    /// </summary>
    [Fact]
    public void Update_without_any_field_exits_2_and_sends_nothing()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["campaign", "update", SampleId.ToString()]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--name", result.Error, StringComparison.Ordinal);
    }

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args,
        Action<ServiceCollection>? configureServices = null)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        configureServices?.Invoke(services);

        return CliTestHarness.Run(services, args);
    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(json),
        };
    }

    private static string ReadBody(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return string.Empty;
        }

        return request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {
                byte[] body = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

                snapshot.Content = new ByteArrayContent(body);

                foreach (KeyValuePair<string, IEnumerable<string>> contentHeader in request.Content.Headers)
                {
                    snapshot.Content.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
                }
            }

            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);
        }
    }
}
