using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The release verbs and the <c>memory erasure</c> administration verbs, driven through the registered
/// command tree against a recording host.
/// </summary>
/// <remarks>
/// One event list carries both the requests and the confirmation, so the order each verb depends on —
/// confirm, then release; prepare, then confirm, then reset — is asserted as an order. The prompt
/// snapshots everything rendered before it was asked, which is how a test proves a warning or a
/// measurement preceded the decision it informs, and how it proves Saga content never reached it.
/// </remarks>
[Collection("GlobalConsole")]
public sealed class MemoryErasureAdministrationCommandTests
{
    private const string C = "12345678-1234-1234-1234-123456789abc";

    private const string SagaContent = "Rotate the vault key.\n";

    private const string RelearnWarning = "Agents and extraction may write this again once it is released.";

    private const string MayHaveApplied = "may have been applied";

    private const string ResentNote = "was cut off, so it was sent once more";

    [Theory]
    [InlineData(new string[0], SagaMemoryScopeKind.Global, null)]
    [InlineData(new[] { "--scope", "unresolved" }, SagaMemoryScopeKind.LegacyUnresolved, null)]
    [InlineData(new[] { "--scope", "unclassified" }, SagaMemoryScopeKind.Unclassified, null)]
    [InlineData(new[] { "--campaign", C }, SagaMemoryScopeKind.Campaign, C)]
    [InlineData(new[] { "--scope", "campaign", "--campaign", C }, SagaMemoryScopeKind.Campaign, C)]
    public async Task Saga_release_maps_scope_flags_and_sends_the_file_bytes_verbatim(
        string[] flags,
        SagaMemoryScopeKind kind,
        string? campaign)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        RecordingPrompt prompt = new(handler, answer: true);

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "release", "--file", file.Path, .. flags], prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(["prompt", "POST /api/memory/saga/release"], handler.Events);

        using JsonDocument body = JsonDocument.Parse(handler.Body("/api/memory/saga/release"));

        // Trimming is the server's job: it tries the exact bytes and their trimmed form.
        Assert.Equal(SagaContent, body.RootElement.GetProperty("content").GetString());

        Assert.Equal((int)kind, body.RootElement.GetProperty("scopeKind").GetInt32());

        Assert.Equal(campaign, body.RootElement.GetProperty("campaignId").GetString());

        // The content is never rendered: not before the question, not in it, not after it.
        Assert.DoesNotContain("Rotate the vault key", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.DoesNotContain("Rotate the vault key", prompt.Question ?? "", StringComparison.Ordinal);

        Assert.DoesNotContain("Rotate the vault key", result.Output + result.Error, StringComparison.Ordinal);

        Assert.Contains(RelearnWarning, prompt.BeforePrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign")]
    [InlineData("orbit")]
    public async Task Saga_release_refuses_an_incomplete_or_unknown_scope_with_exit_two(string scope)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "release", "--file", file.Path, "--scope", scope],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);

        Assert.Contains(
            scope == "campaign" ? "--campaign" : "global|campaign|unresolved|unclassified",
            result.Error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("unresolved")]
    [InlineData("unclassified")]
    public async Task Saga_release_with_campaign_and_a_non_campaign_scope_exits_two(string scope)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "release", "--file", file.Path, "--campaign", C, "--scope", scope],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);
    }

    [Fact]
    public async Task Saga_release_of_a_file_that_does_not_exist_exits_two_before_any_request()
    {
        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "release", "--file", Path.Combine(Path.GetTempPath(), $"arcanum-missing-{Guid.NewGuid():N}.txt")],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);
    }

    /// <summary>
    /// Reading standard input to its end leaves no channel to ask on, so <c>--file -</c> without
    /// <c>--yes</c> is refused before a byte is read or a request is made.
    /// </summary>
    [Fact]
    public async Task Saga_release_from_stdin_without_yes_exits_two_before_reading_or_calling()
    {
        AdministrationHandler handler = new();

        TextReader original = Console.In;

        try
        {
            Console.SetIn(new ForbiddenReader());

            await using ServiceProvider provider = Services(handler).BuildServiceProvider();

            MemoryCommands commands = provider.GetRequiredService<MemoryCommands>();

            System.Reflection.MethodInfo? method = typeof(MemoryCommands).GetMethod("SagaRelease");

            Assert.NotNull(method);

            using IDisposable invocation = CliInvocationContext.Push(new CliInvocationOptions(Json: false, Plain: false, Yes: false));

            int exitCode = await (Task<int>)method.Invoke(commands, ["-", null, null, CancellationToken.None])!;

            Assert.Equal(2, exitCode);

            Assert.Empty(handler.Events);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public async Task Saga_release_from_stdin_with_yes_reads_stdin()
    {
        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "release", "--file", "-", "--yes"],
            new RecordingPrompt(handler, answer: false),
            input: "Rotate.\n");

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(["POST /api/memory/saga/release"], handler.Events);

        using JsonDocument body = JsonDocument.Parse(handler.Body("/api/memory/saga/release"));

        Assert.Equal("Rotate.\n", body.RootElement.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("lexicon", "Vault Keeper", null)]
    [InlineData("lexicon", "Vault Keeper", C)]
    [InlineData("covenant", "preference.vault", null)]
    [InlineData("covenant", "preference.vault", C)]
    public async Task Release_confirms_then_posts_the_exact_scope(string store, string identity, string? campaign)
    {
        AdministrationHandler handler = new();

        RecordingPrompt prompt = new(handler, answer: true);

        string[] args = campaign is null
            ? ["memory", store, "release", identity]
            : ["memory", store, "release", identity, "--campaign", campaign];

        CliTestResult result = await RunAsync(handler, args, prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(["prompt", $"POST /api/memory/{store}/release"], handler.Events);

        Assert.Contains(RelearnWarning, prompt.BeforePrompt, StringComparison.Ordinal);

        using JsonDocument body = JsonDocument.Parse(handler.Body($"/api/memory/{store}/release"));

        JsonElement root = body.RootElement;

        string expectedKind = campaign is null ? "Global" : "Campaign";

        if (store == "lexicon")
        {
            Assert.Equal(expectedKind, root.GetProperty("scope").GetProperty("kind").GetString());

            Assert.Equal(campaign, root.GetProperty("scope").GetProperty("campaignId").GetString());

            Assert.Equal(identity, root.GetProperty("name").GetString());
        }
        else
        {
            Assert.Equal(expectedKind, root.GetProperty("scope").GetString());

            Assert.Equal(campaign, root.GetProperty("campaignId").GetString());

            Assert.Equal(identity, root.GetProperty("key").GetString());
        }
    }

    [Fact]
    public async Task Lexicon_release_refuses_an_empty_campaign_before_any_request()
    {
        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "lexicon", "release", "Vault Keeper", "--campaign", Guid.Empty.ToString()],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);
    }

    [Fact]
    public async Task Covenant_release_refuses_a_malformed_key_before_any_request()
    {
        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "covenant", "release", "Preference.Vault"],
            new RecordingPrompt(handler, answer: true));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);
    }

    [Fact]
    public async Task NotFingerprinted_exits_zero_and_says_nothing_was_released()
    {
        AdministrationHandler handler = new()
        {
            Release = new(MemoryReviewStore.Lexicon, MemoryErasureReleaseOutcome.NotFingerprinted, 0),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "lexicon", "release", "Vault Keeper"], new RecordingPrompt(handler, answer: true));

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("No erasure fingerprint matched, so nothing was released.", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "Released 2 erasure fingerprints.")]
    [InlineData(1, "Released 1 erasure fingerprint.")]
    public async Task Released_prints_the_count(int count, string line)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new()
        {
            Release = new(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, count),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "release", "--file", file.Path], new RecordingPrompt(handler, answer: true));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains(line, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("covenant")]
    public async Task Release_with_json_and_yes_writes_exactly_one_result_document(string store)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            [.. ReleaseArgs(store, file.Path), "--json", "--yes"],
            new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        MemoryErasureReleaseResultDto released =
            JsonSerializer.Deserialize(result.Output, ArcanumJsonContext.Default.MemoryErasureReleaseResultDto)!;

        Assert.Equal(StoreOf(store), released.Store);

        Assert.DoesNotContain("prompt", handler.Events);

        // The warning is still written, beside the plan, on the diagnostic stream.
        Assert.Contains(RelearnWarning, result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lost key points the operator at status exactly once: the CLI adds the pointer only when the
    /// host's own message does not already give it.
    /// </summary>
    [Theory]
    [InlineData("saga", false)]
    [InlineData("lexicon", false)]
    [InlineData("covenant", false)]
    [InlineData("saga", true)]
    [InlineData("lexicon", true)]
    [InlineData("covenant", true)]
    public async Task A_KeyLost_refusal_exits_one_and_points_to_erasure_status_once(string store, bool hostNamesStatus)
    {
        using ContentFile file = new(SagaContent);

        string message = hostNamesStatus
            ? "The erasure key is lost. Run 'arcanum memory erasure status'."
            : "The erasure key is lost.";

        AdministrationHandler handler = new()
        {
            Failures =
            {
                [$"/api/memory/{store}/release"] = (HttpStatusCode.Conflict, new Error(ErrorCodes.MemoryErasure.KeyLost, message)),
            },
        };

        CliTestResult result = await RunAsync(handler, ReleaseArgs(store, file.Path), new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("The erasure key is lost.", result.Error, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(result.Error, Regex.Escape("arcanum memory erasure status")));

        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("release-lexicon")]
    [InlineData("release-covenant")]
    [InlineData("release-saga")]
    [InlineData("reset-key")]
    public async Task Decline_writes_one_cancellation_document_and_exits_zero(string verb)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(handler, [.. VerbArgs(verb, file.Path), "--json"], new RecordingPrompt(handler, answer: false));

        Assert.Equal(0, result.ExitCode);

        MemoryErasureCancellationPayload payload =
            JsonSerializer.Deserialize(result.Output, CliJsonContext.Default.MemoryErasureCancellationPayload)!;

        bool release = verb.StartsWith("release", StringComparison.Ordinal);

        Assert.Equal(
            (release ? "release" : "reset-key", release ? StoreOf(verb["release-".Length..]) : (MemoryReviewStore?)null, (Guid?)null, true),
            (payload.Operation, payload.Store, payload.MutationId, payload.Cancelled));

        Assert.Contains("prompt", handler.Events);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/release", StringComparison.Ordinal));

        Assert.DoesNotContain("POST /api/memory/erasure/reset-key", handler.Events);
    }

    /// <summary>
    /// The shipped prompt treats <c>--json</c> as non-interactive by declaration, so a structured run
    /// without <c>--yes</c> is refused rather than declined, and nothing is applied.
    /// </summary>
    [Theory]
    [InlineData("release-lexicon")]
    [InlineData("release-covenant")]
    [InlineData("release-saga")]
    [InlineData("reset-key")]
    public async Task A_json_run_without_yes_is_refused_by_the_shipped_prompt_and_never_applies(string verb)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await CliTestHarness.RunAsync(Services(handler), [.. VerbArgs(verb, file.Path), "--json"]);

        Assert.Equal(2, result.ExitCode);

        Assert.DoesNotContain(handler.Events, e => e.EndsWith("/release", StringComparison.Ordinal));

        Assert.DoesNotContain("POST /api/memory/erasure/reset-key", handler.Events);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.False(document.RootElement.TryGetProperty("cancelled", out _));
    }

    [Fact]
    public async Task Status_prints_key_state_per_store_counts_and_guidance_when_unverifiable()
    {
        AdministrationHandler handler = new()
        {
            Status = new(
                MemoryErasureKeyStatus.Lost,
                [
                    new(MemoryReviewStore.Covenant, 0, 0, 0),
                    new(MemoryReviewStore.Saga, 1, 1, 1),
                    new(MemoryReviewStore.Lexicon, 0, 0, 0),
                ],
                0),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "status"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(["GET /api/memory/erasure"], handler.Events);

        Assert.Contains("Erasure key: Lost", result.Output, StringComparison.Ordinal);

        Assert.Contains("Covenant: 0 fingerprints, 0 unverifiable, 0 receipts", result.Output, StringComparison.Ordinal);

        Assert.Contains("Saga: 1 fingerprint, 1 unverifiable, 1 receipt", result.Output, StringComparison.Ordinal);

        Assert.Contains("Run 'arcanum memory erasure reset-key'", result.Output, StringComparison.Ordinal);

        // Only the stores that hold fingerprints refuse their writers; a Lost key does not stop the others.
        Assert.Contains("Automatic writes to each store listed with fingerprints", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("stay refused", result.Output, StringComparison.Ordinal);

        AdministrationHandler jsonHandler = new() { Status = handler.Status };

        CliTestResult json = await RunAsync(jsonHandler, ["memory", "erasure", "status", "--json"], new RecordingPrompt(jsonHandler, answer: false));

        Assert.True(json.ExitCode == 0, json.Error);

        MemoryErasureStatusDto status = JsonSerializer.Deserialize(json.Output, ArcanumJsonContext.Default.MemoryErasureStatusDto)!;

        Assert.Equal(MemoryErasureKeyStatus.Lost, status.KeyStatus);

        Assert.Equal(1, status.Stores[1].Unverifiable);
    }

    [Theory]
    [InlineData(MemoryErasureKeyStatus.Present)]
    [InlineData(MemoryErasureKeyStatus.Absent)]
    public async Task Status_with_nothing_unverifiable_gives_no_reset_guidance(MemoryErasureKeyStatus keyStatus)
    {
        AdministrationHandler handler = new()
        {
            Status = new(
                keyStatus,
                [
                    new(MemoryReviewStore.Covenant, 0, 0, 0),
                    new(MemoryReviewStore.Saga, keyStatus is MemoryErasureKeyStatus.Present ? 2 : 0, 0, keyStatus is MemoryErasureKeyStatus.Present ? 2 : 0),
                    new(MemoryReviewStore.Lexicon, 0, 0, 0),
                ],
                0),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "status"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains($"Erasure key: {keyStatus}", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("reset-key", result.Output + result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("unknown", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_says_unverifiable_counts_are_unknown_while_the_key_is_unavailable()
    {
        AdministrationHandler handler = new()
        {
            Status = new(
                MemoryErasureKeyStatus.Unavailable,
                [
                    new(MemoryReviewStore.Covenant, 0, 0, 0),
                    new(MemoryReviewStore.Saga, 1, 0, 1),
                    new(MemoryReviewStore.Lexicon, 0, 0, 0),
                ],
                0),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "status"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains("Erasure key: Unavailable", result.Output, StringComparison.Ordinal);

        Assert.Contains("Unverifiable counts are unknown while the erasure key cannot be read.", result.Output, StringComparison.Ordinal);

        AssertKeyUnavailableRemedy(result.Output);
    }

    /// <summary>
    /// With no evidence recorded, an unreadable key leaves nothing to verify, so the zeros are
    /// measurements rather than unknowns; the operator is told how to recover the key.
    /// </summary>
    [Fact]
    public async Task Status_with_an_unreadable_key_and_no_evidence_says_nothing_is_recorded_and_how_to_recover()
    {
        AdministrationHandler handler = new()
        {
            Status = new(
                MemoryErasureKeyStatus.Unavailable,
                [
                    new(MemoryReviewStore.Covenant, 0, 0, 0),
                    new(MemoryReviewStore.Saga, 0, 0, 0),
                    new(MemoryReviewStore.Lexicon, 0, 0, 0),
                ],
                0),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "status"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains("Erasure key: Unavailable", result.Output, StringComparison.Ordinal);

        Assert.Contains("Nothing is recorded, so there is nothing to verify.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("unknown", result.Output, StringComparison.Ordinal);

        AssertKeyUnavailableRemedy(result.Output);
    }

    [Fact]
    public async Task Scrub_runs_without_confirmation_and_reports_counts()
    {
        AdministrationHandler handler = new()
        {
            Scrub = new(MemoryErasureWalCheckpointAttempt.Truncated, 1, 0),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "scrub"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(["POST /api/memory/erasure/scrub"], handler.Events);

        Assert.Contains("Verified 1", result.Output, StringComparison.Ordinal);

        Assert.Contains("still pending 0", result.Output, StringComparison.Ordinal);

        AdministrationHandler jsonHandler = new() { Scrub = handler.Scrub };

        CliTestResult json = await RunAsync(jsonHandler, ["memory", "erasure", "scrub", "--json"], new RecordingPrompt(jsonHandler, answer: false));

        Assert.True(json.ExitCode == 0, json.Error);

        MemoryErasureScrubResultDto scrubbed = JsonSerializer.Deserialize(json.Output, ArcanumJsonContext.Default.MemoryErasureScrubResultDto)!;

        Assert.Equal((MemoryErasureWalCheckpointAttempt.Truncated, 1L, 0L), (scrubbed.WalCheckpointAttempt, scrubbed.Verified, scrubbed.StillPending));
    }

    [Fact]
    public async Task Scrub_whose_checkpoint_could_not_run_says_so_and_to_run_it_again_later()
    {
        AdministrationHandler handler = new()
        {
            Scrub = new(MemoryErasureWalCheckpointAttempt.Unavailable, 0, 1),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "scrub"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains("Scrub: the log checkpoint could not run, so run it again later.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("connection", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_key_prepares_warns_about_relearning_confirms_then_applies_the_token()
    {
        AdministrationHandler handler = new()
        {
            ResetResult = new(MemoryErasureKeyStatus.Present, 2, 2, KeyCreated: true),
        };

        RecordingPrompt prompt = new(handler, answer: true);

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "reset-key"], prompt);

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(
            ["POST /api/memory/erasure/reset-key/prepare", "prompt", "POST /api/memory/erasure/reset-key"],
            handler.Events);

        // What the preview found, then what discarding it costs, all before the question.
        Assert.Contains("Erasure key: Lost", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains("Saga: 1 fingerprint, 1 unverifiable, 1 receipt", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains("Lexicon: 1 fingerprint, 1 unverifiable, 1 receipt", prompt.BeforePrompt, StringComparison.Ordinal);

        Assert.Contains("may be learned again by extraction or agent writes", prompt.BeforePrompt, StringComparison.Ordinal);

        using JsonDocument body = JsonDocument.Parse(handler.Body("/api/memory/erasure/reset-key"));

        Assert.Equal("reset-token", body.RootElement.GetProperty("preflightToken").GetString());

        Assert.Contains("Discarded 2 fingerprints and 2 receipts; created a new erasure key.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(ResentNote, result.Error, StringComparison.Ordinal);

        // The token authorizes the irreversible apply; it travels in the request body and nowhere else.
        Assert.DoesNotContain("reset-token", result.Output + result.Error + prompt.BeforePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_key_that_kept_its_key_says_so_without_claiming_a_new_one()
    {
        AdministrationHandler handler = new()
        {
            ResetResult = new(MemoryErasureKeyStatus.Present, 1, 1, KeyCreated: false),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "reset-key"], new RecordingPrompt(handler, answer: true));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Contains("Discarded 1 fingerprint and 1 receipt.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("created a new erasure key", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_key_non_interactive_without_yes_exits_two_and_never_applies()
    {
        AdministrationHandler handler = new();

        ServiceCollection services = Services(handler);

        // The production prompt, seeing redirected standard input: the refusal is the typed one that
        // names --yes, never a cancellation reported as success.
        services.AddSingleton<IConfirmationPrompt>(provider => new ConfirmationPrompt(
            provider.GetRequiredService<IConsoleDispatcher>(),
            new CliInvocationOptions(Json: false, Plain: false, Yes: false),
            TextReader.Null,
            isOutputRedirected: static () => false,
            isInputRedirected: static () => true));

        CliTestResult result = await CliTestHarness.RunAsync(services, ["memory", "erasure", "reset-key"]);

        Assert.Equal(2, result.ExitCode);

        Assert.Equal(["POST /api/memory/erasure/reset-key/prepare"], handler.Events);
    }

    [Fact]
    public async Task Reset_key_with_json_and_yes_writes_exactly_one_result_document()
    {
        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "reset-key", "--json", "--yes"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        MemoryErasureKeyResetResultDto reset =
            JsonSerializer.Deserialize(result.Output, ArcanumJsonContext.Default.MemoryErasureKeyResetResultDto)!;

        Assert.Equal((MemoryErasureKeyStatus.Present, 2L, 2L, true), (reset.KeyStatus, reset.FingerprintsDiscarded, reset.ReceiptsDiscarded, reset.KeyCreated));

        Assert.DoesNotContain("prompt", handler.Events);

        // The preview is still on the record, on the diagnostic stream.
        Assert.Contains("Erasure key: Lost", result.Error, StringComparison.Ordinal);

        Assert.Contains("may be learned again by extraction or agent writes", result.Error, StringComparison.Ordinal);

        // Neither the result document nor the preview carries the token.
        Assert.DoesNotContain("reset-token", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_key_stops_before_confirming_when_prepare_refuses()
    {
        AdministrationHandler handler = new()
        {
            Failures =
            {
                ["/api/memory/erasure/reset-key/prepare"] = (HttpStatusCode.ServiceUnavailable, new Error(ErrorCodes.MemoryErasure.KeyUnavailable, "The erasure key could not be read.")),
            },
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "reset-key"], new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(["POST /api/memory/erasure/reset-key/prepare"], handler.Events);

        // The CLI's own explanation replaces the host's, so the remedy is said once, with its caveats.
        Assert.DoesNotContain("The erasure key could not be read.", result.Error, StringComparison.Ordinal);

        Assert.Contains("nothing was discarded", result.Error, StringComparison.OrdinalIgnoreCase);

        AssertKeyUnavailableRemedy(result.Error);
    }

    /// <summary>
    /// Every typed refusal of the apply proves nothing was discarded, and each names what to do next:
    /// a changed measurement and an expired or restart-orphaned preview both need a fresh run, and a key
    /// that cannot be read names the remedy for a malformed item.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.MemoryErasure.StalePlan, 409, "Run 'arcanum memory erasure reset-key' again")]
    [InlineData(ErrorCodes.MemoryErasure.InvalidPreflight, 400, "Run 'arcanum memory erasure reset-key' again")]
    [InlineData(ErrorCodes.MemoryErasure.KeyUnavailable, 503, "unlock it and run this again")]
    [InlineData(ErrorCodes.MemoryErasure.Unavailable, 503, null)]
    [InlineData(ErrorCodes.Covenant.OperatorAuthorityUnavailable, 503, null)]
    public async Task Reset_key_refusals_at_apply_exit_one_with_one_explanation_and_a_remedy(string code, int status, string? remedy)
    {
        string message = $"The host refused the reset with {code}.";

        AdministrationHandler handler = new()
        {
            Failures = { ["/api/memory/erasure/reset-key"] = ((HttpStatusCode)status, new Error(code, message)) },
        };

        CliTestResult result = await RunAsync(handler, ["memory", "erasure", "reset-key"], new RecordingPrompt(handler, answer: true));

        Assert.Equal(1, result.ExitCode);

        if (remedy is not null)
        {
            // The CLI's explanation replaces the host's message, which says the same thing less exactly.
            Assert.DoesNotContain(message, result.Error, StringComparison.Ordinal);

            Assert.Contains(remedy, result.Error, StringComparison.Ordinal);

            Assert.Contains("nothing was discarded", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(message, result.Error, StringComparison.Ordinal);

            // Only a refusal the CLI can account for says nothing was discarded.
            Assert.DoesNotContain("nothing was discarded", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        if (code == ErrorCodes.MemoryErasure.StalePlan)
        {
            // A stale plan can follow the key the apply created before it re-measured.
            Assert.Contains("a new erasure key may have been created", result.Error, StringComparison.Ordinal);
        }

        if (code == ErrorCodes.MemoryErasure.KeyUnavailable)
        {
            AssertKeyUnavailableRemedy(result.Error);
        }

        Assert.DoesNotContain("Discarded", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(MayHaveApplied, result.Error, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> UnknownOutcomes
    {
        get
        {
            TheoryData<string, string> data = [];

            foreach (string verb in new[] { "release-saga", "release-lexicon", "release-covenant", "reset-key", "scrub" })
            {
                foreach (string failure in new[] { "unreachable", "unhandled", "typed" })
                {
                    data.Add(verb, failure);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A mutating call whose answer never proved a rollback may have committed, so the operator is told
    /// so; a typed refusal proves it did not, and adds nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnknownOutcomes))]
    public async Task A_mutation_whose_outcome_is_unknown_says_it_may_have_been_applied(string verb, string failure)
    {
        using ContentFile file = new(SagaContent);

        string path = verb switch
        {
            "reset-key" => "/api/memory/erasure/reset-key",
            "scrub" => "/api/memory/erasure/scrub",
            _ => $"/api/memory/{verb["release-".Length..]}/release",
        };

        AdministrationHandler handler = new();

        switch (failure)
        {
            case "unreachable":
                handler.Exceptions[path] = new HttpRequestException("offline");

                break;

            case "unhandled":
                handler.Failures[path] = (HttpStatusCode.InternalServerError, new Error(ErrorCodes.Hub.Unhandled, "An unexpected error occurred."));

                break;

            default:
                handler.Failures[path] = (HttpStatusCode.ServiceUnavailable, new Error(ErrorCodes.MemoryErasure.Unavailable, "Erasure is not available yet."));

                break;
        }

        CliTestResult result = await RunAsync(handler, VerbArgs(verb, file.Path), new RecordingPrompt(handler, answer: true));

        Assert.Equal(failure == "unreachable" ? 3 : 1, result.ExitCode);

        Assert.Contains($"POST {path}", handler.Events);

        Assert.Equal(failure != "typed", result.Error.Contains(MayHaveApplied, StringComparison.Ordinal));

        // An outcome nobody confirmed cannot be described as having discarded nothing.
        Assert.DoesNotContain("nothing was discarded", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A release, scrub or key reset whose answer was cut off after its headers arrived is sent once more,
    /// and the second answer describes only the second attempt — so the operator is told the first may
    /// already have taken effect.
    /// </summary>
    [Theory]
    [InlineData("release-saga", "release")]
    [InlineData("release-lexicon", "release")]
    [InlineData("release-covenant", "release")]
    [InlineData("scrub", "scrub")]
    [InlineData("reset-key", "key reset")]
    public async Task An_answer_cut_off_and_resent_says_the_first_attempt_may_already_have_taken_effect(string verb, string operation)
    {
        using ContentFile file = new(SagaContent);

        string path = MutationPath(verb);

        AdministrationHandler handler = new() { CutOffOnce = { path } };

        CliTestResult result = await RunAsync(handler, [.. VerbArgs(verb, file.Path), "--yes"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Equal(2, handler.Events.Count(e => e == $"POST {path}"));

        Assert.Contains($"The host's answer to this {operation} {ResentNote}.", result.Error, StringComparison.Ordinal);

        Assert.Contains("The first attempt may already have taken effect", result.Error, StringComparison.Ordinal);

        Assert.Contains("describe only the resend", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(ResentNote, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("release-saga")]
    [InlineData("release-lexicon")]
    [InlineData("release-covenant")]
    [InlineData("scrub")]
    [InlineData("reset-key")]
    public async Task An_answer_received_whole_adds_no_resend_note(string verb)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(handler, [.. VerbArgs(verb, file.Path), "--yes"], new RecordingPrompt(handler, answer: false));

        Assert.True(result.ExitCode == 0, result.Error);

        Assert.Single(handler.Events, e => e == $"POST {MutationPath(verb)}");

        Assert.DoesNotContain(ResentNote, result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal under <c>--json</c> writes the CLI error envelope as the one stdout document, the way
    /// every other direct verb's refusal does, with the message also on the diagnostic stream.
    /// </summary>
    [Theory]
    [InlineData("release-saga", "/api/memory/saga/release")]
    [InlineData("release-lexicon", "/api/memory/lexicon/release")]
    [InlineData("release-covenant", "/api/memory/covenant/release")]
    [InlineData("scrub", "/api/memory/erasure/scrub")]
    [InlineData("reset-key", "/api/memory/erasure/reset-key/prepare")]
    [InlineData("reset-key", "/api/memory/erasure/reset-key")]
    [InlineData("status", "/api/memory/erasure")]
    public async Task A_refusal_under_json_writes_one_error_envelope(string verb, string path)
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new()
        {
            Failures = { [path] = (HttpStatusCode.ServiceUnavailable, new Error(ErrorCodes.MemoryErasure.Unavailable, "Erasure is not available yet.")) },
        };

        string[] args = verb == "status" ? ["memory", "erasure", "status"] : VerbArgs(verb, file.Path);

        CliTestResult result = await RunAsync(handler, [.. args, "--json", "--yes"], new RecordingPrompt(handler, answer: false));

        Assert.Equal(1, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal("Erasure is not available yet.", document.RootElement.GetProperty("error").GetString());

        Assert.Equal(1, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task An_input_error_under_json_writes_one_error_envelope()
    {
        using ContentFile file = new(SagaContent);

        AdministrationHandler handler = new();

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "release", "--file", file.Path, "--scope", "orbit", "--json"],
            new RecordingPrompt(handler, answer: false));

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Events);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Contains("global|campaign|unresolved|unclassified", document.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);

        Assert.Equal(2, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// The remedy for a key that cannot be read: first the case that is no fault of the key, then removal
    /// only for an item confirmed malformed, and what removal costs.
    /// </summary>
    private static void AssertKeyUnavailableRemedy(string text)
    {
        int unlock = text.IndexOf("If the credential store is locked or did not answer, unlock it and run this again.", StringComparison.Ordinal);

        int remove = text.IndexOf("confirmed malformed", StringComparison.Ordinal);

        Assert.True(unlock >= 0, text);

        Assert.True(remove > unlock, text);

        Assert.Contains("OS credential tool", text, StringComparison.Ordinal);

        Assert.Contains("makes every erasure fingerprint unverifiable", text, StringComparison.Ordinal);

        Assert.Contains("erased content could be learned again", text, StringComparison.Ordinal);
    }

    private static string MutationPath(string verb) => verb switch
    {
        "reset-key" => "/api/memory/erasure/reset-key",
        "scrub" => "/api/memory/erasure/scrub",
        _ => $"/api/memory/{verb["release-".Length..]}/release",
    };

    private static string[] ReleaseArgs(string store, string file) => store switch
    {
        "saga" => ["memory", "saga", "release", "--file", file],
        "lexicon" => ["memory", "lexicon", "release", "Vault Keeper"],
        "covenant" => ["memory", "covenant", "release", "preference.vault", "--campaign", C],
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static string[] VerbArgs(string verb, string file) => verb switch
    {
        "reset-key" => ["memory", "erasure", "reset-key"],
        "scrub" => ["memory", "erasure", "scrub"],
        _ => ReleaseArgs(verb["release-".Length..], file),
    };

    private static MemoryReviewStore StoreOf(string store) => store switch
    {
        "saga" => MemoryReviewStore.Saga,
        "lexicon" => MemoryReviewStore.Lexicon,
        "covenant" => MemoryReviewStore.Covenant,
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static Task<CliTestResult> RunAsync(
        AdministrationHandler handler,
        string[] args,
        RecordingPrompt prompt,
        string? input = null)
    {
        ServiceCollection services = Services(handler);

        services.AddSingleton<IConfirmationPrompt>(prompt);

        services.AddSingleton<IConsoleDispatcher>(provider => new ObservingDispatcher(
            new ConsoleDispatcher(provider.GetRequiredService<ICliInvocationContext>()),
            prompt));

        return CliTestHarness.RunAsync(services, args, input);
    }

    private static ServiceCollection Services(AdministrationHandler handler)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.AddSingleton<IHttpClientFactory>(new LexiconCliFixture.Factory(handler));

        services.AddSingleton<ISecretStore>(new LexiconCliFixture.Secrets());

        CliTestHarness.AddKeyedArcanumResponder(services, LexiconCliFixture.Key);

        // A release names its own scope. Saved and active context must never choose it.
        services.AddSingleton<ICliContextStore>(new ForbiddenContext());

        return services;
    }

    /// <summary>A temporary file holding exactly the given text, removed when the test ends.</summary>
    private sealed class ContentFile : IDisposable
    {
        internal ContentFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arcanum-release-{Guid.NewGuid():N}.txt");

            File.WriteAllText(Path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal string Path { get; }

        public void Dispose() => File.Delete(Path);
    }

    /// <summary>A host that answers every release and administration route from fixed values.</summary>
    private sealed class AdministrationHandler : HttpMessageHandler
    {
        internal List<string> Events { get; } = [];

        internal Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, (HttpStatusCode Status, Error Error)> Failures { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, Exception> Exceptions { get; } = new(StringComparer.Ordinal);

        /// <summary>Paths whose first answer arrives with its headers and then loses its body.</summary>
        internal HashSet<string> CutOffOnce { get; } = new(StringComparer.Ordinal);

        internal MemoryErasureReleaseResultDto? Release { get; init; }

        internal MemoryErasureStatusDto Status { get; init; } = new(
            MemoryErasureKeyStatus.Present,
            [
                new(MemoryReviewStore.Covenant, 0, 0, 0),
                new(MemoryReviewStore.Saga, 0, 0, 0),
                new(MemoryReviewStore.Lexicon, 0, 0, 0),
            ],
            0);

        internal MemoryErasureScrubResultDto Scrub { get; init; } = new(MemoryErasureWalCheckpointAttempt.NotAttempted, 0, 0);

        internal MemoryErasureKeyResetPreflightDto ResetPreflight { get; init; } = new(
            MemoryErasureKeyStatus.Lost,
            [
                new(MemoryReviewStore.Covenant, 0, 0, 0),
                new(MemoryReviewStore.Saga, 1, 1, 1),
                new(MemoryReviewStore.Lexicon, 1, 1, 1),
            ],
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero),
            "reset-token");

        internal MemoryErasureKeyResetResultDto ResetResult { get; init; } = new(MemoryErasureKeyStatus.Present, 2, 2, KeyCreated: true);

        internal string Body(string path) =>
            Bodies.TryGetValue(path, out string? body)
                ? body
                : throw new Xunit.Sdk.XunitException($"No request reached {path}. Events: {string.Join(", ", Events)}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;

            Events.Add($"{request.Method} {path}");

            Bodies[path] = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (Exceptions.TryGetValue(path, out Exception? exception))
            {
                throw exception;
            }

            if (CutOffOnce.Remove(path))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() };
            }

            if (Failures.TryGetValue(path, out (HttpStatusCode Status, Error Error) failure))
            {
                return Respond(failure.Status, JsonSerializer.Serialize(
                    new ApiResponse<MemoryErasureReleaseResultDto>(null, false, failure.Error),
                    ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto));
            }

            return path switch
            {
                "/api/memory/erasure" => Respond(Envelope(Status, ArcanumJsonContext.Default.ApiResponseMemoryErasureStatusDto)),
                "/api/memory/erasure/scrub" => Respond(Envelope(Scrub, ArcanumJsonContext.Default.ApiResponseMemoryErasureScrubResultDto)),
                "/api/memory/erasure/reset-key/prepare" => Respond(Envelope(ResetPreflight, ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetPreflightDto)),
                "/api/memory/erasure/reset-key" => Respond(Envelope(ResetResult, ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetResultDto)),
                _ when path.EndsWith("/release", StringComparison.Ordinal) => Respond(Envelope(
                    Release ?? new MemoryErasureReleaseResultDto(StoreOfPath(path), MemoryErasureReleaseOutcome.Released, 1),
                    ArcanumJsonContext.Default.ApiResponseMemoryErasureReleaseResultDto)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static MemoryReviewStore StoreOfPath(string path) =>
            path.StartsWith("/api/memory/saga/", StringComparison.Ordinal) ? MemoryReviewStore.Saga
            : path.StartsWith("/api/memory/lexicon/", StringComparison.Ordinal) ? MemoryReviewStore.Lexicon
            : MemoryReviewStore.Covenant;

        private static string Envelope<T>(T value, JsonTypeInfo<ApiResponse<T>> typeInfo) =>
            JsonSerializer.Serialize(new ApiResponse<T>(value, true, null), typeInfo);

        private static HttpResponseMessage Respond(string json) => Respond(HttpStatusCode.OK, json);

        private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// Answers the question, having written it to the diagnostic stream exactly as the shipped prompt
    /// does, so every assertion over what reached a stream covers the question as well.
    /// </summary>
    /// <remarks>
    /// <see cref="BeforePrompt"/> is everything the operator saw before answering, the question included.
    /// </remarks>
    private sealed class RecordingPrompt(AdministrationHandler handler, bool answer) : IConfirmationPrompt
    {
        internal StringBuilder Rendered { get; } = new();

        internal string BeforePrompt { get; private set; } = "";

        internal string? Question { get; private set; }

        /// <summary>The dispatcher the run writes through, set when the run composes it.</summary>
        internal IConsoleDispatcher? Dispatcher { get; set; }

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken)
        {
            Dispatcher?.WriteDiagnostic($"{question} [y/N]");

            BeforePrompt = Rendered.ToString();

            Question = question;

            handler.Events.Add("prompt");

            return Task.FromResult(answer);
        }
    }

    private sealed class ObservingDispatcher : IConsoleDispatcher
    {
        private readonly IConsoleDispatcher inner;

        private readonly RecordingPrompt prompt;

        internal ObservingDispatcher(IConsoleDispatcher inner, RecordingPrompt prompt)
        {
            this.inner = inner;

            this.prompt = prompt;

            prompt.Dispatcher = this;
        }

        public void WritePayload(string value)
        {
            prompt.Rendered.Append(value).Append('\n');

            inner.WritePayload(value);
        }

        public void WriteDiagnostic(string value)
        {
            prompt.Rendered.Append(value).Append('\n');

            inner.WriteDiagnostic(value);
        }

        public void WriteVerbose(string value) => inner.WriteVerbose(value);

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo) => inner.WriteJson(value, typeInfo);

        public void WriteJson(JsonElement value) => inner.WriteJson(value);

        public void BeginJsonStream() => inner.BeginJsonStream();
    }

    /// <summary>A response whose headers arrived and whose body the connection lost.</summary>
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("The connection dropped mid-body.");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BrokenStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection dropped mid-body.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The connection dropped mid-body."));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ForbiddenReader : TextReader
    {
        public override string ReadToEnd() => throw new InvalidOperationException("stdin must not be read without --yes");

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("stdin must not be read without --yes");
    }

    private sealed class ForbiddenContext : ICliContextStore
    {
        public string FilePath => "unused";

        public CliContextDocument Load() => throw new InvalidOperationException("A release must not consult saved context.");
    }
}
