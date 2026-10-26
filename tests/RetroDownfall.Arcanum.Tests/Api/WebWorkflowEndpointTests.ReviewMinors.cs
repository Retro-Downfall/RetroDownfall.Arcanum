using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class WebWorkflowEndpointTests
{
    [SkippableTheory]
    [InlineData(null, false, 50)]
    [InlineData(null, true, 50)]
    [InlineData(3, false, 3)]
    [InlineData(60, false, 60)]
    public async Task Review_fetch_ceiling_applies_even_when_pages_cannot_consume_the_prompt(int? target, bool refused, int expectedReads)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        WebCitation[] citations = Enumerable.Range(1, 120).Select(i => new WebCitation(i, $"https://example.test/{i}", "Source")).ToArray();
        StubWebProvider provider = new()
        {
            SearchResults = [new WebSearchResult("Summary", citations, new WebResearchUsage(SearchQueries: 1, CostUsd: 0.01m))],
            ReadMarkdown = string.Empty,
            ReadResults = refused ? citations.ToDictionary(c => c.Url, _ => Result<WebReadResult>.Failure(new Error(ErrorCodes.WebResearch.SsrfBlocked, "private-canary"))) : [],
        };
        StubIntelligence intelligence = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence);
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/research",
            new WebResearchWorkflowRequest { Question = "Evidence?", SourceTarget = target }, ArcanumJsonContext.Default.WebResearchWorkflowRequest);
        string body = await response.Content.ReadAsStringAsync();
        WebResearchStreamFrame[] frames = ParseResearchFrames(body).ToArray();
        Assert.Equal(expectedReads, provider.ReadCalls);
        Assert.DoesNotContain(frames, f => f.Stage == "source_budget_reached");
        Assert.Equal(WebResearchStreamFrameType.Result, frames[^1].Type);
        Assert.Empty(frames[^1].Result!.Citations);
        Assert.Contains("No source pages were read", intelligence.Request!.Prompt);
        Assert.DoesNotContain("private-canary", body);
    }

    [SkippableFact]
    public async Task Review_original_long_summary_scenario_preserves_pages_citations_newer_evidence_and_spend()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new()
        {
            SearchResults = Enumerable.Range(1, 14).Select(i => new WebSearchResult(
                $"Evidence pass {i}. " + new string('s', 4_000) + " [1]",
                Enumerable.Range((i - 1) * 3 + 1, 3).Select(n => new WebCitation(n, $"https://example.test/{n}", "Source")).ToArray(),
                new WebResearchUsage(SearchQueries: 1, CostUsd: 0.01m))).ToArray(),
        };
        StubIntelligence intelligence = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence);
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/research",
            new WebResearchWorkflowRequest { Question = "What changed?" }, ArcanumJsonContext.Default.WebResearchWorkflowRequest);
        WebResearchStreamFrame[] frames = ParseResearchFrames(await response.Content.ReadAsStringAsync()).ToArray();
        WebResearchWorkflowResult result = frames[^1].Result!;
        Assert.Equal(15, provider.SearchCalls);
        Assert.Equal(42, provider.ReadCalls);
        Assert.Equal(42, result.Citations.Length);
        Assert.Equal(15, result.Usage.SearchQueries);
        Assert.Equal(0.15m, result.Usage.CostUsd);
        Assert.Contains("Evidence pass 14.", intelligence.Request!.Prompt);
        Assert.DoesNotContain("Evidence pass 1. ", intelligence.Request.Prompt);
        Assert.Contains("Rendered evidence.", intelligence.Request.Prompt);
        Assert.True(intelligence.Request.Prompt.Length <= 32_768);
        Assert.Equal(0, (intelligence.Request.Prompt.Split("\n```\n").Length - 1) % 2);
        Assert.DoesNotContain(frames, f => f.Stage is "source_budget_reached" or "summary_budget_reached");
        foreach (WebWorkflowCitation citation in result.Citations)
        {
            Assert.Contains($"Source [{citation.Index}]", intelligence.Request.Prompt);
            Assert.Contains(citation.Url, intelligence.Request.Prompt);
        }
    }

    [SkippableFact]
    public async Task Review_pass_counter_exhaustion_synthesizes_paid_work_without_wrapping()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new() { ChangingCitationRounds = 3, ThrowOnSearchCall = 3 };
        StubIntelligence intelligence = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence);
        using IServiceScope scope = factory.Services.CreateScope();
        WebResearchWorkflowService service = scope.ServiceProvider.GetRequiredService<WebResearchWorkflowService>();
        await using IAsyncEnumerator<WebResearchStreamFrame> iterator = service.ResearchAsync(
            new WebResearchWorkflowRequest { Question = "Evidence?" }, CancellationToken.None).GetAsyncEnumerator();
        List<WebResearchStreamFrame> frames = [];
        while (await iterator.MoveNextAsync())
        {
            frames.Add(iterator.Current);
            if (iterator.Current.Stage == "searching")
            {
                break;
            }
        }
        // Advance the real compiler-generated iterator to its numeric boundary without fake paid
        // calls or a production-only test hook. All subsequent search and synthesis work is real.
        FieldInfo pass = Assert.Single(iterator.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            f => f.FieldType == typeof(int) && f.Name.StartsWith("<pass>", StringComparison.Ordinal));
        pass.SetValue(iterator, int.MaxValue - 1);
        while (await iterator.MoveNextAsync())
        {
            frames.Add(iterator.Current);
        }
        Assert.Equal(2, provider.SearchCalls);
        Assert.Contains(frames, f => f.Stage == "search_pass_limit_reached");
        Assert.Equal(WebResearchStreamFrameType.Result, frames[^1].Type);
        Assert.Equal(0.02m, frames[^1].Result!.Usage.CostUsd);
        Assert.Equal(2, frames[^1].Result!.Citations.Length);
        Assert.NotNull(intelligence.Request);
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public void Review_follow_up_query_preserves_the_maximum_question_within_provider_limit(int pass, bool unicode)
    {
        int length = WebResearchWorkflowService.MaxResearchQuestionChars;
        string question = unicode ? string.Concat(Enumerable.Repeat("🌍", length / 2)) + (length % 2 == 1 ? "?" : "") : new string('q', length);
        MethodInfo method = typeof(WebResearchWorkflowService).GetMethod("BuildFollowUpQuery", BindingFlags.NonPublic | BindingFlags.Static)!;
        string query = (string)method.Invoke(null, [question, pass])!;
        Assert.StartsWith(question + " ", query, StringComparison.Ordinal);
        Assert.Contains(pass.ToString(System.Globalization.CultureInfo.InvariantCulture), query);
        Assert.Contains("corroborating evidence", query);
        Assert.True(query.Length <= WebResearchConstants.MaxInputQueryChars, $"query length {query.Length} exceeds provider limit {WebResearchConstants.MaxInputQueryChars}");
        Assert.Equal(query, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(query)));
    }

    [SkippableTheory]
    [InlineData("{\"query\":\" \"}", "query", "nonempty")]
    [InlineData("{\"query\":\"private-canary\",\"resultCount\":50}", "resultCount", "1-20")]
    [InlineData("{\"query\":\"private-canary\",\"freshness\":\"private-canary\"}", "freshness", "day")]
    [InlineData("{\"query\":\"q\",\"includeDomains\":[\"ok.test\",\"https://private-canary\"]}", "includeDomains[1]", "bare host")]
    [InlineData("{\"query\":\"q\",\"excludeDomains\":[\"private-canary:443\"]}", "excludeDomains[0]", "bare host")]
    [InlineData("{\"query\":\"q\",\"includeDomains\":[\"private-canary/path\"]}", "includeDomains[0]", "bare host")]
    [InlineData("{\"query\":\"q\",\"includeDomains\":[\"private-canary \" ]}", "includeDomains[0]", "bare host")]
    [InlineData("{\"query\":\"q\",\"includeDomains\":[null]}", "includeDomains[0]", "bare host")]
    public async Task Review_search_validation_names_the_first_invalid_field_without_echo(string body, string field, string rule)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence());
        if (!body.Contains("resultCount", StringComparison.Ordinal))
        {
            body = body.Insert(1, "\"resultCount\":5,");
        }

        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsync("/api/web/search", new StringContent(body, Encoding.UTF8, "application/json"));
        ApiResponse<WebSearchWorkflowResult>? envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseWebSearchWorkflowResult);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.WebResearch.RequestRejected, envelope!.Error!.Value.Code);
        Assert.StartsWith(field, envelope.Error!.Value.Message, StringComparison.Ordinal);
        Assert.Contains(rule, envelope.Error!.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-canary", envelope.Error!.Value.Message);
        Assert.Equal(0, provider.SearchCalls);
    }

    [SkippableTheory]
    [InlineData("{\"question\":\" \"}", "question", "nonempty")]
    [InlineData("{\"question\":\"private-canary\",\"sourceTarget\":0}", "sourceTarget", "positive")]
    [InlineData("{\"question\":\"private-canary\",\"tokenBudget\":0}", "tokenBudget", "positive")]
    [InlineData("{\"question\":\"private-canary\",\"costBudgetUsd\":-1}", "costBudgetUsd", "nonnegative")]
    public async Task Review_research_validation_names_field_and_is_a_traced_terminal_http_200_error(string body, string field, string rule)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        CapturedLogProvider logs = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence(), additionalServices: services => CaptureLogs(services, logs));
        if (!body.Contains("tokenBudget", StringComparison.Ordinal))
        {
            body = body.Insert(1, "\"tokenBudget\":2000,");
        }

        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsync("/api/web/research", new StringContent(body, Encoding.UTF8, "application/json"));
        string wire = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(wire.Trim());
        JsonElement frame = document.RootElement;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Error", frame.GetProperty("type").GetString());
        Assert.Contains(field, frame.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains(rule, frame.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-canary", wire);
        Assert.Equal(0, provider.SearchCalls);
        AssertFailureDiagnostics(frame, logs, null, 0, null);
    }

    [SkippableTheory]
    [InlineData("javascript", "renderMode", "static")]
    [InlineData("private-canary", "renderMode", "static")]
    public async Task Review_browse_render_guidance_uses_api_fields(string mode, string field, string rule)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence());
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/browse", new WebBrowseWorkflowRequest { Url = "https://example.test", RenderMode = mode }, ArcanumJsonContext.Default.WebBrowseWorkflowRequest);
        ApiResponse<WebBrowseWorkflowResult> envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseWebBrowseWorkflowResult)!;
        Assert.False(envelope.IsSuccess);
        Assert.Contains(field, envelope.Error!.Value.Message);
        Assert.Contains(rule, envelope.Error!.Value.Message);
        Assert.DoesNotContain("--render", envelope.Error!.Value.Message);
        Assert.DoesNotContain("private-canary", envelope.Error!.Value.Message);
        Assert.Equal(0, provider.ReadCalls);
    }

    [SkippableTheory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Review_late_research_failure_reports_known_spend_and_the_same_safe_trace(bool budget, bool reportedCost)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new()
        {
            ThrowOnSearchCall = budget ? 0 : 2,
            SearchResults = [new WebSearchResult("private-provider-summary", [new WebCitation(1, "https://example.test/1", "Source")], new WebResearchUsage(SearchQueries: 1, CostUsd: reportedCost ? 0.01m : null))],
        };
        StubIntelligence intelligence = new();
        CapturedLogProvider logs = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence, additionalServices: services => CaptureLogs(services, logs));
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/research", new WebResearchWorkflowRequest { Question = "Research?", CostBudgetUsd = budget ? 0.015m : null }, ArcanumJsonContext.Default.WebResearchWorkflowRequest);
        string wire = await response.Content.ReadAsStringAsync();
        string[] lines = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument document = JsonDocument.Parse(lines[^1]);
        JsonElement frame = document.RootElement;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(lines.Length > 1);
        Assert.Equal("Error", frame.GetProperty("type").GetString());
        Assert.Equal(budget ? ErrorCodes.WebResearch.BudgetExceeded : ErrorCodes.Hub.Error, frame.GetProperty("code").GetString());
        Assert.Null(intelligence.Request);
        AssertFailureDiagnostics(frame, logs, provider.SearchActivityId, budget ? 2 : 1, reportedCost ? (budget ? 0.02m : 0.01m) : null);
        Assert.DoesNotContain("private-provider-summary", wire);
        Assert.All(logs.Entries.Where(e => e.Category.Contains("WebWorkflowEndpoints", StringComparison.Ordinal)), e =>
        {
            Assert.Null(e.Exception);
            Assert.DoesNotContain("citation index exploded", e.Message);
            Assert.DoesNotContain("private-provider-summary", e.Message);
        });
    }

    [SkippableTheory]
    [InlineData("query", 4_001, "4000")]
    [InlineData("includeDomains", 21, "20")]
    [InlineData("excludeDomains[0]", 254, "253")]
    public async Task Review_search_length_rules_are_field_specific_and_preflighted(string field, int size, string limit)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        WebSearchWorkflowRequest request = new()
        {
            Query = field == "query" ? new string('q', size) : "q",
            IncludeDomains = field == "includeDomains" ? Enumerable.Repeat("example.test", size).ToArray() : [],
            ExcludeDomains = field.StartsWith("excludeDomains", StringComparison.Ordinal) ? [new string('x', size)] : [],
        };
        StubWebProvider provider = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence());
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/search", request, ArcanumJsonContext.Default.WebSearchWorkflowRequest);
        ApiResponse<WebSearchWorkflowResult> envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseWebSearchWorkflowResult)!;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith(field, envelope.Error!.Value.Message, StringComparison.Ordinal);
        Assert.Contains(limit, envelope.Error.Value.Message);
        Assert.Equal(0, provider.SearchCalls);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task Review_search_valid_boundaries_preserve_provider_options(int count)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence());
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/search",
            new WebSearchWorkflowRequest { Query = new string('q', 4_000), ResultCount = count, Freshness = " YEAR ", IncludeDomains = Enumerable.Repeat(new string('x', 253), 20).ToArray() },
            ArcanumJsonContext.Default.WebSearchWorkflowRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, provider.SearchCalls);
        Assert.Equal(count, provider.LastSearchOptions!.ResultCount);
        Assert.Equal("year", provider.LastSearchOptions.Freshness);
        Assert.Equal(20, provider.LastSearchOptions.IncludeDomains.Length);
    }

    [SkippableTheory]
    [InlineData("search", "{\"query\":\"\"}")]
    [InlineData("browse", "{\"url\":\"private-canary\"}")]
    public async Task Review_disabled_workflows_remain_disabled_before_field_validation(string workflow, string body)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence(), webEnabled: false);
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsync($"/api/web/{workflow}", new StringContent(body, Encoding.UTF8, "application/json"));
        string wire = await response.Content.ReadAsStringAsync();
        Assert.Contains(ErrorCodes.WebResearch.UnsupportedOperation, wire);
        Assert.DoesNotContain("private-canary", wire);
        Assert.Equal(0, provider.SearchCalls + provider.ReadCalls);
    }

    [SkippableFact]
    public async Task Review_success_at_exact_cost_limit_retains_usage_and_one_trace_on_every_frame()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        CapturedLogProvider logs = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence(), additionalServices: services => CaptureLogs(services, logs));
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/research",
            new WebResearchWorkflowRequest { Question = "Evidence?", SourceTarget = 1, CostBudgetUsd = 0.01m }, ArcanumJsonContext.Default.WebResearchWorkflowRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(provider.SearchActivityId, document.RootElement.GetProperty("traceId").GetString());
        }
        WebResearchStreamFrame terminal = ParseResearchFrames(string.Join('\n', lines)).Last();
        Assert.Equal(WebResearchStreamFrameType.Result, terminal.Type);
        Assert.Equal(0.01m, terminal.Result!.Usage.CostUsd);
        Assert.Single(terminal.Result.Citations);
        Assert.DoesNotContain(logs.Entries, e => e.Category.Contains("WebWorkflowEndpoints", StringComparison.Ordinal) && e.Level >= LogLevel.Warning);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_initial_error_uses_activity_identity_or_http_trace_fallback(bool activityPresent)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        CapturedLogProvider logs = new();
        await using ArcanumWebApplicationFactory factory = Factory(new StubWebProvider(), new StubIntelligence(), additionalServices: services => CaptureLogs(services, logs));
        using IServiceScope scope = factory.Services.CreateScope();
        Microsoft.AspNetCore.Http.DefaultHttpContext context = new()
        {
            RequestServices = scope.ServiceProvider,
            TraceIdentifier = "http-fallback-trace",
        };
        using MemoryStream output = new();
        context.Response.Body = output;
        Activity? prior = Activity.Current;
        using Activity activity = new("review-research");
        Activity.Current = activityPresent ? activity.Start() : null;
        string expected = Activity.Current?.Id ?? context.TraceIdentifier;
        try
        {
            MethodInfo handler = typeof(WebResearchWorkflowService).Assembly.GetType("RetroDownfall.Arcanum.Api.Intelligence.WebWorkflowEndpoints")!
                .GetMethod("HandleResearchAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)handler.Invoke(null, [new WebResearchWorkflowRequest(), scope.ServiceProvider.GetRequiredService<WebResearchWorkflowService>(), context, CancellationToken.None])!;
            using JsonDocument document = JsonDocument.Parse(output.ToArray());
            AssertFailureDiagnostics(document.RootElement, logs, expected, 0, null);
        }
        finally
        {
            Activity.Current = prior;
        }
    }

    [SkippableFact]
    public async Task Review_caller_cancel_after_paid_search_does_not_emit_or_log_a_failure()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        using CancellationTokenSource cancelled = new();
        int searches = 0;
        StubWebProvider provider = new()
        {
            BeforeSearch = token =>
            {
                if (++searches == 2)
                {
                    cancelled.Cancel();
                    throw new OperationCanceledException(token);
                }
            },
        };
        CapturedLogProvider logs = new();
        StubIntelligence intelligence = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence, additionalServices: services => CaptureLogs(services, logs));
        using HttpClient client = factory.CreateAuthenticatedClient();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsJsonAsync("/api/web/research",
            new WebResearchWorkflowRequest { Question = "Evidence?" }, ArcanumJsonContext.Default.WebResearchWorkflowRequest, cancelled.Token));
        Assert.Equal(2, searches);
        Assert.Null(intelligence.Request);
        Assert.DoesNotContain(logs.Entries, e => e.Category.Contains("WebWorkflowEndpoints", StringComparison.Ordinal) && e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Review_source_generated_requests_preserve_declared_omitted_numeric_defaults(bool search)
    {
        if (search)
        {
            WebSearchWorkflowRequest request = JsonSerializer.Deserialize("{\"query\":\"Evidence?\"}", ArcanumJsonContext.Default.WebSearchWorkflowRequest)!;
            Assert.Equal(5, request.ResultCount);
        }
        else
        {
            WebResearchWorkflowRequest request = JsonSerializer.Deserialize("{\"question\":\"Evidence?\"}", ArcanumJsonContext.Default.WebResearchWorkflowRequest)!;
            Assert.Equal(2_000, request.TokenBudget);
        }
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Review_raw_requests_accept_omitted_numeric_defaults(bool search)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        StubWebProvider provider = new();
        StubIntelligence intelligence = new();
        await using ArcanumWebApplicationFactory factory = Factory(provider, intelligence);
        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsync(search ? "/api/web/search" : "/api/web/research",
            new StringContent(search ? "{\"query\":\"Evidence?\"}" : "{\"question\":\"Evidence?\",\"sourceTarget\":1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        if (search)
        {
            Assert.Equal(5, provider.LastSearchOptions!.ResultCount);
        }
        else
        {
            Assert.Equal(WebResearchStreamFrameType.Result, ParseResearchFrames(body).Last().Type);
            Assert.Equal(2_000, intelligence.Request!.MaxOutputTokens);
        }
    }

    [SkippableTheory]
    [InlineData("private-canary")]
    [InlineData(null)]
    [InlineData("ftp://private-canary")]
    public async Task Review_browse_url_guidance_names_field_without_echo(string? url)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        StubWebProvider provider = new();

        await using ArcanumWebApplicationFactory factory = Factory(provider, new StubIntelligence());

        using HttpResponseMessage response = await factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/web/browse",
            new WebBrowseWorkflowRequest { Url = url! }, ArcanumJsonContext.Default.WebBrowseWorkflowRequest);

        ApiResponse<WebBrowseWorkflowResult> envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseWebBrowseWorkflowResult)!;

        Assert.Equal(ErrorCodes.WebResearch.InvalidUrl, envelope.Error!.Value.Code);

        Assert.StartsWith("url", envelope.Error.Value.Message, StringComparison.Ordinal);

        Assert.Contains("HTTP or HTTPS", envelope.Error.Value.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-canary", envelope.Error.Value.Message, StringComparison.Ordinal);

        Assert.Equal(0, provider.ReadCalls);
    }

    private static void CaptureLogs(IServiceCollection services, CapturedLogProvider logs)
    {
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(new LoggerFactory([logs]));
    }

    private static void AssertFailureDiagnostics(JsonElement frame, CapturedLogProvider logs, string? expectedTrace, int queries, decimal? cost)
    {
        string trace = frame.GetProperty("traceId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(trace));
        if (expectedTrace is not null)
        {
            Assert.Equal(expectedTrace, trace);
        }
        Assert.False(frame.GetProperty("partialResultsRetained").GetBoolean());
        JsonElement usage = frame.GetProperty("usage");
        Assert.Equal(queries, usage.GetProperty("searchQueries").GetInt32());
        if (cost is decimal value)
        {
            Assert.Equal(value, usage.GetProperty("costUsd").GetDecimal());
        }
        else
        {
            Assert.True(!usage.TryGetProperty("costUsd", out JsonElement actual) || actual.ValueKind == JsonValueKind.Null);
        }
        CapturedLogEntry diagnostic = Assert.Single(logs.Entries, e => e.Category.Contains("WebWorkflowEndpoints", StringComparison.Ordinal) && e.Level >= LogLevel.Warning);
        Assert.Contains(trace, diagnostic.Message);
        Assert.Null(diagnostic.Exception);
    }
}
