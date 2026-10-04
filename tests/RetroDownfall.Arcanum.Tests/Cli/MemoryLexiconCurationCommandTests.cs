using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class MemoryLexiconCurationCommandTests
{
    private const string Correction = "{\"type\":\"Preference\",\"facts\":[\"A corrected fact\"]}";

    [Fact]
    public async Task Exact_plain_show_maps_current_facts_to_attachment_sources_without_dumping_history()
    {
        LexiconEntryDetail detail = DetailWithCurrentSources();

        using LexiconCliFixture.Handler handler = new() { DetailResponse = detail };

        CliTestResult result = await CliTestHarness.RunAsync(LexiconCliFixture.Services(handler),
            ["memory", "lexicon", "show", "Operator", "--plain"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["POST /api/memory/lexicon/show"], handler.Requests);

        string first = Assert.Single(result.Output.Split('\n'), line => line.Contains("Fact 1 source:", StringComparison.Ordinal));

        Assert.Contains("Available", first, StringComparison.Ordinal);

        Assert.Contains("visible-source-one", first, StringComparison.Ordinal);

        Assert.Contains("version 7", first, StringComparison.Ordinal);

        string second = Assert.Single(result.Output.Split('\n'), line => line.Contains("Fact 2 source:", StringComparison.Ordinal));

        Assert.Contains("Unavailable", second, StringComparison.Ordinal);

        Assert.Contains("visible-source-two", second, StringComparison.Ordinal);

        Assert.Contains("version 8", second, StringComparison.Ordinal);

        foreach (LexiconFactProvenance provenance in detail.Entry.FactProvenance!)
        {
            string line = provenance.Fact == "First current fact" ? first : second;

            Assert.Contains(provenance.Source.SessionId.ToString("D"), line, StringComparison.Ordinal);

            Assert.Contains(provenance.Source.AttachmentId.ToString("D"), line, StringComparison.Ordinal);

            Assert.Contains(provenance.Source.ContentHash, line, StringComparison.Ordinal);

            Assert.Contains(provenance.Source.SourceType, line, StringComparison.Ordinal);

            Assert.Contains("2026-09-22 13:14:15Z", line, StringComparison.Ordinal);
        }

        Assert.Contains("First current fact", result.Output, StringComparison.Ordinal);

        Assert.Contains("Second current fact", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("history-only-source", result.Output + result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(new string('E', 64), result.Output + result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("historicalFactProvenance", result.Output, StringComparison.Ordinal);

        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Exact_json_show_retains_complete_current_and_content_free_historical_provenance()
    {
        LexiconEntryDetail detail = DetailWithCurrentSources();

        using LexiconCliFixture.Handler handler = new() { DetailResponse = detail };

        CliTestResult result = await CliTestHarness.RunAsync(LexiconCliFixture.Services(handler),
            ["memory", "lexicon", "show", "Operator", "--json"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(JsonSerializer.Serialize(detail, ArcanumJsonContext.Default.LexiconEntryDetail), result.Output.Trim());

        using JsonDocument document = JsonDocument.Parse(result.Output);

        JsonElement historical = Assert.Single(document.RootElement.GetProperty("historicalFactProvenance").EnumerateArray());

        Assert.Equal("history-only-source", historical.GetProperty("logicalKey").GetString());

        Assert.False(historical.TryGetProperty("fact", out _));

        Assert.False(historical.TryGetProperty("facts", out _));

        Assert.Empty(result.Error);
    }

    private static LexiconEntryDetail DetailWithCurrentSources()
    {
        Guid sessionId = Guid.Parse("12345678-aaaa-4444-8888-111111111111");

        Guid attachmentId = Guid.Parse("12345678-bbbb-4444-8888-222222222222");

        DateTimeOffset materialized = new(2026, 9, 22, 13, 14, 15, TimeSpan.Zero);

        return LexiconCliFixture.Detail with
        {
            Entry = LexiconCliFixture.Detail.Entry with
            {
                Facts = ["First current fact", "Second current fact"],
                // Reverse the provenance rows: the displayed index must identify the fact, not this array's order.
                FactProvenance =
                [
                    new("Second current fact", new(sessionId, attachmentId, "visible-source-two", 8, new string('D', 64), materialized,
                        "AttachmentExtract", AttachmentSourceAvailability.Unavailable)),
                    new("First current fact", new(sessionId, attachmentId, "visible-source-one", 7, new string('F', 64), materialized,
                        "AttachmentText", AttachmentSourceAvailability.Available)),
                ],
            },
            HistoricalFactProvenance =
            [
                new("prior-version", 0, sessionId, attachmentId, "history-only-source", 6, new string('E', 64), materialized, "AttachmentText"),
            ],
        };
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--campaign")]
    [InlineData("-C")]
    public async Task Exact_show_posts_the_requested_scope_without_reading_saved_context(string? campaignOption)
    {
        using LexiconCliFixture.Handler handler = new();

        ServiceCollection services = LexiconCliFixture.Services(handler);

        services.AddSingleton<ICliContextStore>(new ForbiddenContext());

        string[] args = campaignOption is null
            ? ["memory", "lexicon", "show", "Operator", "--json"]
            : ["memory", "lexicon", "show", "Operator", campaignOption, LexiconCliFixture.Campaign.ToString(), "--json"];

        CliTestResult result = await CliTestHarness.RunAsync(services, args);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["POST /api/memory/lexicon/show"], handler.Requests);

        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Bodies));

        JsonElement scope = body.RootElement.GetProperty("scope");

        Assert.Equal(campaignOption is null ? "Global" : "Campaign", scope.GetProperty("kind").GetString());

        if (campaignOption is null)
        {
            Assert.Equal(JsonValueKind.Null, scope.GetProperty("campaignId").ValueKind);
        }
        else
        {
            Assert.Equal(LexiconCliFixture.Campaign, scope.GetProperty("campaignId").GetGuid());
        }

        using JsonDocument output = JsonDocument.Parse(result.Output);

        Assert.Equal("Server Name", output.RootElement.GetProperty("entry").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Mutation_shows_then_renders_then_prompts_then_submits_the_unchanged_target(string verb)
    {
        using LexiconCliFixture.Handler handler = new();

        RecordingPrompt prompt = new(handler, true);

        CliTestResult result = await RunAsync(handler, verb, prompt: prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["show", "prompt", "submit"], handler.Events);

        Assert.Equal(["POST /api/memory/lexicon/show", $"POST /api/memory/lexicon/{verb}"], handler.Requests);

        using JsonDocument show = JsonDocument.Parse(handler.Bodies[0]);

        Assert.Equal("Global", show.RootElement.GetProperty("scope").GetProperty("kind").GetString());

        Assert.Equal(JsonValueKind.Null, show.RootElement.GetProperty("scope").GetProperty("campaignId").ValueKind);

        Assert.Contains("Server Name", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains("73", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains(LexiconCliFixture.Campaign.ToString(), prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains("Stored fact", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains("Effect:", prompt.BeforePrompt, StringComparison.Ordinal);

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[1]);

        Assert.Equal(LexiconCliFixture.TargetJson, body.RootElement.GetProperty("target").GetRawText());

        if (verb == "correct")
        {
            Assert.Equal("A corrected fact", body.RootElement.GetProperty("content").GetProperty("facts")[0].GetString());

            Assert.Contains("A corrected fact", prompt.BeforePrompt, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Yes_skips_only_prompt_and_json_stdout_is_one_final_result(string verb)
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await RunAsync(handler, verb, ["--yes", "--json"], new RecordingPrompt(handler, false));

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["show", "submit"], handler.Events);

        using JsonDocument output = JsonDocument.Parse(result.Output);

        Assert.Equal("Applied", output.RootElement.GetProperty("outcome").GetString());

        Assert.Contains("Server Name", result.Error, StringComparison.Ordinal);

        Assert.Contains("Effect:", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Mutation_preflight_honors_the_explicit_campaign(string verb)
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await RunAsync(handler, verb, ["--yes", "--campaign", LexiconCliFixture.Campaign.ToString()]);

        Assert.Equal(0, result.ExitCode);

        using JsonDocument show = JsonDocument.Parse(handler.Bodies[0]);

        Assert.Equal("Campaign", show.RootElement.GetProperty("scope").GetProperty("kind").GetString());

        Assert.Equal(LexiconCliFixture.Campaign, show.RootElement.GetProperty("scope").GetProperty("campaignId").GetGuid());
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Decline_sends_only_show_and_applies_nothing(string verb)
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await RunAsync(handler, verb, [], new RecordingPrompt(handler, false));

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["show", "prompt"], handler.Events);

        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"X\",\"facts\":[]}")]
    [InlineData("{\"type\":\" \",\"facts\":[\"fact\"]}")]
    [InlineData("{\"type\":\"X\",\"facts\":\"wrong shape\"}")]
    public async Task Invalid_correction_is_refused_before_show_and_confirmation(string content)
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await RunAsync(handler, "correct", ["--yes"], content: content);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);

        Assert.NotEmpty(result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("invalid")]
    public async Task Expected_file_errors_are_safe_validation_failures(string kind)
    {
        using LexiconCliFixture.Handler handler = new();

        string path = kind switch
        {
            "missing" => Path.Combine(Path.GetTempPath(), $"arcanum-absent-{Guid.NewGuid():N}"),
            "directory" => Path.GetTempPath(),
            _ => "invalid\0path",
        };

        CliTestResult result = await CliTestHarness.RunAsync(LexiconCliFixture.Services(handler),
            ["memory", "lexicon", "correct", "Operator", "--file", path, "--yes", "--json"]);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.DoesNotContain("Exception", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correction_requires_file_even_when_stdin_is_available()
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await CliTestHarness.RunAsync(LexiconCliFixture.Services(handler),
            ["memory", "lexicon", "correct", "Operator", "--yes"], Correction);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--file", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Literal_dash_reads_stdin_with_yes()
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await CliTestHarness.RunAsync(LexiconCliFixture.Services(handler),
            ["memory", "lexicon", "correct", "Operator", "-f", "-", "--yes"], Correction);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(["show", "submit"], handler.Events);

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[1]);

        Assert.Equal("A corrected fact", body.RootElement.GetProperty("content").GetProperty("facts")[0].GetString());
    }

    [Fact]
    public async Task Literal_dash_without_yes_is_refused_before_reading_or_show()
    {
        using LexiconCliFixture.Handler handler = new();

        TextReader original = Console.In;

        try
        {
            Console.SetIn(new ForbiddenReader());

            using ServiceProvider provider = LexiconCliFixture.Services(handler).BuildServiceProvider();

            MemoryCommands commands = provider.GetRequiredService<MemoryCommands>();

            System.Reflection.MethodInfo? method = typeof(MemoryCommands).GetMethod("LexiconCorrect");

            Assert.NotNull(method);

            int result = await (Task<int>)method.Invoke(commands, ["Operator", null, "-", CancellationToken.None])!;

            Assert.Equal(2, result);

            Assert.Empty(handler.Requests);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_campaign_is_exit_two_without_http(string campaign)
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await CliTestHarness.RunAsync(LexiconCliFixture.Services(handler),
            ["memory", "lexicon", "show", "Operator", "--campaign", campaign]);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authored_input_converts_io_failure_but_preserves_cancellation(bool cancel)
    {
        TextReader original = Console.In;

        FaultingReader reader = new(cancel);

        try
        {
            Console.SetIn(reader);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuthoredContentReader.ReadAsync("-", "Lexicon", null, CancellationToken.None));
            }
            else
            {
                Result<string> result = await AuthoredContentReader.ReadAsync("-", "Lexicon", null, CancellationToken.None);

                Assert.True(result.IsFailure);

                Assert.Equal(ErrorCodes.Validation.InvalidBody, result.Error.Code);

                Assert.DoesNotContain("sensitive exception details", result.Error.Message, StringComparison.Ordinal);
            }

            Assert.Equal(1, reader.Reads);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_failures_are_exit_one_and_preserve_the_hosts_message(bool mutation)
    {
        using LexiconCliFixture.Handler handler = new()
        {
            Failure = new Error(ErrorCodes.Lexicon.StaleCurationTarget, "The exact target changed; inspect again."),
            FailMutationOnly = mutation,
        };

        CliTestResult result = await RunAsync(handler, "pin", ["--yes", "--json"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(mutation ? 2 : 1, handler.Requests.Count);

        Assert.Contains("The exact target changed; inspect again.", result.Error, StringComparison.Ordinal);

        using JsonDocument output = JsonDocument.Parse(result.Output);
    }

    [Fact]
    public async Task Unreachable_host_is_exit_three()
    {
        using LexiconCliFixture.Handler handler = new() { Exception = new HttpRequestException("offline") };

        CliTestResult result = await RunAsync(handler, "pin", ["--yes"]);

        Assert.Equal(3, result.ExitCode);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancellation_during_confirmation_is_exit_130_without_mutation()
    {
        using LexiconCliFixture.Handler handler = new();

        CliTestResult result = await RunAsync(handler, "pin", prompt: new CancelledPrompt());

        Assert.Equal(130, result.ExitCode);

        Assert.Equal(["show"], handler.Events);
    }

    [Theory]
    [InlineData("correct", LexiconCurationOutcomeKind.Applied, "Corrected")]
    [InlineData("retire", LexiconCurationOutcomeKind.Applied, "Retired")]
    [InlineData("reinstate", LexiconCurationOutcomeKind.Applied, "Reinstated")]
    [InlineData("pin", LexiconCurationOutcomeKind.Applied, "Pinned")]
    [InlineData("unpin", LexiconCurationOutcomeKind.Applied, "Unpinned")]
    [InlineData("correct", LexiconCurationOutcomeKind.Unchanged, "Unchanged")]
    [InlineData("retire", LexiconCurationOutcomeKind.AlreadyRetired, "Already retired")]
    [InlineData("reinstate", LexiconCurationOutcomeKind.NotRetired, "Not retired")]
    [InlineData("pin", LexiconCurationOutcomeKind.AlreadyPinned, "Already pinned")]
    [InlineData("unpin", LexiconCurationOutcomeKind.NotPinned, "Not pinned")]
    public async Task Every_outcome_is_distinguished_in_the_final_human_result(string verb, LexiconCurationOutcomeKind outcome, string label)
    {
        using LexiconCliFixture.Handler handler = new() { Outcome = outcome };

        CliTestResult result = await RunAsync(handler, verb, ["--yes"]);

        Assert.Equal(0, result.ExitCode);

        string lastLine = result.Output.TrimEnd().Split('\n')[^1];

        Assert.StartsWith(label, lastLine, StringComparison.Ordinal);

        Assert.Contains("Server Name", lastLine, StringComparison.Ordinal);
    }

    private static async Task<CliTestResult> RunAsync(LexiconCliFixture.Handler handler, string verb,
        string[]? extra = null, IConfirmationPrompt? prompt = null, string content = Correction)
    {
        string? path = verb == "correct" ? Path.GetTempFileName() : null;

        try
        {
            if (path is not null)
            {
                await File.WriteAllTextAsync(path, content);
            }

            List<string> args = ["memory", "lexicon", verb, "Operator"];

            if (path is not null)
            {
                args.AddRange(["--file", path]);
            }

            args.AddRange(extra ?? []);

            ServiceCollection services = LexiconCliFixture.Services(handler, prompt);

            services.AddSingleton<ICliContextStore>(new ForbiddenContext());

            if (prompt is RecordingPrompt recording)
            {
                services.AddSingleton<IConsoleDispatcher>(provider => new ObservingDispatcher(
                    new ConsoleDispatcher(provider.GetRequiredService<ICliInvocationContext>()), recording));
            }

            return await CliTestHarness.RunAsync(services, [.. args]);
        }
        finally
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
    }

    private sealed class RecordingPrompt(LexiconCliFixture.Handler handler, bool answer) : IConfirmationPrompt
    {
        internal string BeforePrompt { get; private set; } = "";

        internal string Rendered { get; set; } = "";

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            BeforePrompt = Rendered;

            handler.Events.Add("prompt");

            return Task.FromResult(answer);
        }
    }

    private sealed class CancelledPrompt : IConfirmationPrompt
    {
        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class ForbiddenReader : TextReader
    {
        public override string ReadToEnd() => throw new InvalidOperationException("stdin must not be read without --yes");

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("stdin must not be read without --yes");
    }

    private sealed class FaultingReader(bool cancel) : TextReader
    {
        internal int Reads { get; private set; }

        public override string ReadToEnd()
        {
            Reads++;

            throw cancel
                ? new OperationCanceledException()
                : new IOException("sensitive exception details");
        }

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) => Task.FromResult(ReadToEnd());

        // The authored-content reader is capped, so it reads in chunks rather than to the end; the
        // fault has to arrive on that read for the conversion under test to be exercised at all.
        public override int Read(char[] buffer, int index, int count)
        {
            _ = ReadToEnd();

            return 0;
        }
    }

    private sealed class ObservingDispatcher(IConsoleDispatcher inner, RecordingPrompt prompt) : IConsoleDispatcher
    {
        public void WritePayload(string value)
        {
            prompt.Rendered += value + "\n";

            inner.WritePayload(value);
        }

        public void WriteDiagnostic(string value)
        {
            prompt.Rendered += value + "\n";

            inner.WriteDiagnostic(value);
        }

        public void WriteVerbose(string value) => inner.WriteVerbose(value);

        public void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => inner.WriteJson(value, typeInfo);

        public void WriteJson(JsonElement value) => inner.WriteJson(value);

        public void BeginJsonStream() => inner.BeginJsonStream();
    }

    private sealed class ForbiddenContext : ICliContextStore
    {
        public string FilePath => "unused";

        public CliContextDocument Load() => throw new InvalidOperationException("Lexicon curation must not consult context");
    }
}
