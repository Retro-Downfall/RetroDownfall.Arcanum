using System.CommandLine;

using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The operator's command line over one Saga memory.
/// </summary>
/// <remarks>
/// Every case enters through <see cref="CliTestHarness"/>, which runs the same argument parsing,
/// option binding, and console wiring <c>Program.Main</c> does. Calling a handler method directly
/// would leave the two things this surface actually adds — what the operator typed and what they see —
/// untested.
///
/// <para>The write verbs are asserted in both directions: the outcome that changed the memory names
/// itself and does not carry the other outcome's words, and the outcome that changed nothing does the
/// converse. A pair asserted only by <c>Contains</c> survives a rendering that prints both, which is
/// exactly the collapse that would make "already retired" indistinguishable from "retired".</para>
/// </remarks>
[Collection("GlobalConsole")]
public sealed class MemorySagaCurationCommandTests
{

    private const string MemoryId = "m-1";

    private const string StoredContent = "the operator prefers tabs";

    /// <summary>
    /// The digest the host publishes for <see cref="StoredContent"/>, as this fixture's host publishes it.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> the digest of <see cref="StoredContent"/>. The CLI must render and send
    /// what the host handed it, and a fixture whose hash happened to equal the one the client could have
    /// computed would pass either way — which is exactly the drift a client-side digest would hide.
    /// </remarks>
    private const string ServerContentHash =
        "1111111122222222333333334444444455555555666666667777777788888888";

    [Fact]
    public async Task Show_renders_the_lifecycle_and_the_eligibility_reason()
    {

        RecordingHandler handler = new()
        {
            Detail = Detail(retiredAtUtc: DateTimeOffset.UnixEpoch, eligibility: SagaRetrievalEligibility.Retired),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "show", MemoryId]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal([$"GET /api/memory/saga/{MemoryId}"], handler.Requests);

        Assert.Contains("Retrieval:    Retired", result.Output, StringComparison.Ordinal);

        Assert.Contains(StoredContent, result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// The digest the three proof-taking verbs require is printed here or it is unobtainable.
    /// </summary>
    /// <remarks>
    /// <c>correct</c>, <c>retire</c>, and <c>reinstate</c> all refuse without
    /// <c>--expected-content-hash</c>, and no other Arcanum surface renders it. Asserted against the
    /// digest of the content this same output shows, so the two cannot drift apart.
    /// </remarks>
    [Fact]
    public async Task Show_prints_the_content_hash_the_write_verbs_require()
    {

        RecordingHandler handler = new() { Detail = Detail() };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "show", MemoryId]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        // The fixture's published digest differs from the one this content hashes to, so a CLI that
        // computed its own would print the other value and fail here. Without this the assertion would
        // be the client's arithmetic compared against itself.
        Assert.NotEqual(
            ServerContentHash,
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory(StoredContent)));

        Assert.Contains($"Content hash: {ServerContentHash}", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Show_names_the_memory_the_host_could_not_find()
    {

        RecordingHandler handler = new()
        {
            Failure = (HttpStatusCode.NotFound, new Error(ErrorCodes.Saga.NotFound, "No Saga memory exists with that identity.")),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "show", MemoryId]);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains("No Saga memory exists with that identity.", result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// Replacement text arrives through the pipe, never as an argument, for the reason the Covenant's
    /// own write verbs take content that way: it would otherwise sit in shell history and in the
    /// process list of a shared machine.
    /// </summary>
    [Fact]
    public async Task Correct_reads_the_replacement_text_from_piped_standard_input()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "correct", MemoryId, "--expected-content-hash", ServerContentHash, "--yes"],
            input: "the operator prefers spaces");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal([$"POST /api/memory/saga/{MemoryId}/correct"], handler.Requests);

        Assert.Contains(
            "\"content\":\"the operator prefers spaces\"",
            handler.Bodies[0],
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            $"\"expectedContentHash\":\"{ServerContentHash}\"",
            handler.Bodies[0],
            StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// A replacement file that is not there is refused before anything is asked or sent.
    /// </summary>
    /// <remarks>
    /// The refusal names Saga, not the store the shared reader was extracted from: the subject noun
    /// travels with the call precisely so a corrected memory's failure does not read as a Covenant one.
    /// </remarks>
    [Fact]
    public async Task A_missing_replacement_file_is_refused_before_the_operator_is_asked()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        string absent = Path.Combine(Path.GetTempPath(), $"arcanum-saga-absent-{Guid.NewGuid():N}.txt");

        CliTestResult result = await RunAsync(
            handler,
            [
                "memory",
                "saga",
                "correct",
                MemoryId,
                "--expected-content-hash",
                ServerContentHash,
                "--file",
                absent,
                "--yes",
            ]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains(absent, result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// A file holding nothing is refused, because a path cannot say that nothing was what was meant.
    /// </summary>
    /// <remarks>
    /// The same payload was refused through the pipe and sent through a file, so which source an
    /// operator reached for decided whether a mistyped <c>--file</c> path was caught. Both sources now
    /// meet one guard.
    ///
    /// <para>This does not make a Saga memory unable to hold blank text, and the refusal says so: the
    /// correct route accepts the content it is given, and an operator who meant it is pointed at it.
    /// The two layers answer different questions — a path can lie about what it holds, and a request
    /// body cannot.</para>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public async Task A_replacement_file_holding_nothing_is_refused_because_a_path_cannot_say_it_was_meant(
        string contents)
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunCorrectAsync(handler, contents);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        // The wording has to cover the whitespace row too, which the earlier "was empty." did not.
        Assert.Contains(
            "Saga memory content was empty or whitespace-only",
            result.Error,
            StringComparison.Ordinal);

        // And an operator who meant blank text is told where it is accepted rather than left guessing.
        Assert.Contains("/api/memory/saga", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Correct_reports_the_memory_whose_text_it_replaced()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunCorrectAsync(handler, "a better sentence");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(
            $"\"expectedContentHash\":\"{ServerContentHash}\"",
            handler.Bodies[0],
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "\"content\":\"a better sentence\"",
            handler.Bodies[0],
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"Corrected Saga memory '{MemoryId}'.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Unchanged", result.Output, StringComparison.Ordinal);

        Assert.Contains("No other memory store was touched.", result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// Correcting a memory to the text it already holds is a success that says nothing was written.
    /// The operator asked for text X and the memory holds X.
    /// </summary>
    [Fact]
    public async Task Correcting_to_the_text_already_stored_says_so_rather_than_claiming_a_change()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Unchanged };

        CliTestResult result = await RunCorrectAsync(handler, StoredContent);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(
            $"Unchanged: Saga memory '{MemoryId}' already holds that text",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Corrected Saga memory", result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// The one curation refusal an operator cannot see coming from the memory alone, so it is passed
    /// through in the host's own words rather than restated by the CLI.
    /// </summary>
    [Fact]
    public async Task Correcting_a_retired_memory_is_refused_with_the_hosts_own_remedy()
    {

        RecordingHandler handler = new()
        {
            Failure = (
                HttpStatusCode.Conflict,
                new Error(ErrorCodes.Saga.AlreadyRetired, "This memory is retired. Reinstate it before correcting it.")),
        };

        CliTestResult result = await RunCorrectAsync(handler, "a better sentence");

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Contains(
            "This memory is retired. Reinstate it before correcting it.",
            result.Error,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Corrected Saga memory", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Retire_reports_the_memory_it_took_out_of_retrieval()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "retire", MemoryId, "--expected-content-hash", ServerContentHash, "--yes"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal([$"POST /api/memory/saga/{MemoryId}/retire"], handler.Requests);

        // The route string alone would be identical for a client that sent an empty hash, which is the
        // one field the host compares inside the write transaction.
        Assert.Contains(
            $"\"expectedContentHash\":\"{ServerContentHash}\"",
            handler.Bodies[0],
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"Retired Saga memory '{MemoryId}'.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Already", result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// Retiring what is already retired is a success, and it says which of the two things happened.
    /// </summary>
    /// <remarks>
    /// The operator asked for a state and the memory is in that state; nothing was written. A rendering
    /// that could not tell this from the call that did the work would defeat that at the last surface,
    /// so the two sentences are asserted to be mutually exclusive rather than merely present.
    /// </remarks>
    [Fact]
    public async Task Retiring_a_memory_that_is_already_retired_succeeds_and_says_it_was_already_retired()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.AlreadyRetired };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "retire", MemoryId, "--expected-content-hash", ServerContentHash, "--yes"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(
            $"Already retired: Saga memory '{MemoryId}' was out of retrieval before this call",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Retired Saga memory", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Retire_asks_before_it_acts_and_does_nothing_when_the_operator_declines()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "retire", MemoryId, "--expected-content-hash", ServerContentHash],
            confirm: false);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        // There is no preflight route here, so a declined retirement reaches nothing at all.
        Assert.Empty(handler.Requests);

        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Refused at parse time, so the verb never reaches a route it could not have satisfied.
    /// </summary>
    /// <remarks>
    /// There is no value of the flag that means "whatever is stored now": the host compares it inside
    /// the write transaction, so an omitted one could only be a request to skip the comparison.
    /// </remarks>
    [Fact]
    public async Task A_retirement_without_the_hash_it_names_never_reaches_a_route()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "retire", MemoryId]);

        Assert.NotEqual((int)CliExitCode.Success, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains(
            "--expected-content-hash",
            result.Output + result.Error,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task Reinstate_reports_the_memory_it_put_back_into_retrieval()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "reinstate", MemoryId, "--expected-content-hash", ServerContentHash, "--yes"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal([$"POST /api/memory/saga/{MemoryId}/reinstate"], handler.Requests);

        Assert.Contains(
            $"\"expectedContentHash\":\"{ServerContentHash}\"",
            handler.Bodies[0],
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"Reinstated Saga memory '{MemoryId}'.", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Not retired", result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// Reinstating what was never retired is a success, and it says which of the two things happened.
    /// </summary>
    [Fact]
    public async Task Reinstating_a_memory_that_was_never_retired_succeeds_and_says_it_was_not_retired()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.NotRetired };

        CliTestResult result = await RunAsync(
            handler,
            ["memory", "saga", "reinstate", MemoryId, "--expected-content-hash", ServerContentHash, "--yes"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(
            $"Not retired: Saga memory '{MemoryId}' was already reaching retrieval",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Reinstated Saga memory", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Pin_reports_the_memory_retention_will_now_keep()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "pin", MemoryId, "--yes"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal([$"POST /api/memory/saga/{MemoryId}/pin"], handler.Requests);

        // The route takes none, and sending one would be a media type the host has to refuse.
        Assert.Equal(string.Empty, handler.Bodies[0]);

        Assert.Contains(
            $"Pinned Saga memory '{MemoryId}'. Retention will not prune it.",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Unpinned", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Pinning_a_memory_the_host_cannot_find_fails_rather_than_reporting_a_pin()
    {

        RecordingHandler handler = new()
        {
            Failure = (HttpStatusCode.NotFound, new Error(ErrorCodes.Saga.NotFound, "No Saga memory exists with that identity.")),
        };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "pin", MemoryId, "--yes"]);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.DoesNotContain("Pinned Saga memory", result.Output, StringComparison.Ordinal);

        Assert.Contains("No Saga memory exists with that identity.", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Unpin_reports_the_memory_retention_may_prune_again()
    {

        RecordingHandler handler = new() { Outcome = SagaCurationOutcomeKind.Applied };

        CliTestResult result = await RunAsync(handler, ["memory", "saga", "unpin", MemoryId, "--yes"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal([$"POST /api/memory/saga/{MemoryId}/unpin"], handler.Requests);

        Assert.Equal(string.Empty, handler.Bodies[0]);

        Assert.Contains(
            $"Unpinned Saga memory '{MemoryId}'. Retention may prune it again.",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Retention will not prune it", result.Output, StringComparison.Ordinal);

    }

    [Fact]
    public void Every_curation_verb_is_registered_under_memory_saga()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        foreach (string verb in new[] { "show", "correct", "retire", "reinstate", "pin", "unpin" })
        {

            Assert.NotNull(Descend(root, "memory", "saga", verb));

        }

    }

    private static Command Descend(Command root, params string[] path)
    {

        Command current = root;

        foreach (string name in path)
        {

            current = Assert.Single(
                current.Subcommands,
                candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

        }

        return current;

    }

    /// <summary>
    /// Drives <c>correct</c> with its replacement text in a file, so the case under test is the
    /// rendering rather than the reader.
    /// </summary>
    private static async Task<CliTestResult> RunCorrectAsync(RecordingHandler handler, string replacement)
    {

        string path = Path.Combine(Path.GetTempPath(), $"arcanum-saga-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(path, replacement);

        try
        {

            return await RunAsync(
                handler,
                [
                    "memory",
                    "saga",
                    "correct",
                    MemoryId,
                    "--expected-content-hash",
                    ServerContentHash,
                    "--file",
                    path,
                    "--yes",
                ]);

        }
        finally
        {

            File.Delete(path);

        }

    }

    private static Task<CliTestResult> RunAsync(
        RecordingHandler handler,
        string[] args,
        string? input = null,
        bool? confirm = null)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        if (confirm is { } answer)
        {

            services.RemoveAll<IConfirmationPrompt>();

            services.AddSingleton<IConfirmationPrompt>(new FixedConfirmation(answer));

        }

        return CliTestHarness.RunAsync(services, args, input);

    }

    private static SagaMemoryDetail Detail(
        DateTimeOffset? retiredAtUtc = null,
        DateTimeOffset? pinnedAtUtc = null,
        SagaRetrievalEligibility eligibility = SagaRetrievalEligibility.Eligible) =>
        new(
            new SagaMemoryDto(
                MemoryId,
                StoredContent,
                DateTimeOffset.UnixEpoch,
                SessionId: null,
                Tags: null,
                Source: "session"),
            ServerContentHash,
            new SagaMemoryLifecycle(retiredAtUtc, pinnedAtUtc),
            eligibility,
            Claim: null,
            History: []);

    private sealed class FixedConfirmation(bool answer) : IConfirmationPrompt
    {

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken) =>
            Task.FromResult(answer);

    }

    private sealed class FixedSecretStore : ISecretStore
    {

        private const string Key = "arc_test_0123456789abcdef0123456789abcdef";

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(Key);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(Key));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://127.0.0.1:9/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };

    }

    /// <summary>
    /// Records which route each verb reached and what it sent, and answers with one configured outcome.
    /// </summary>
    /// <remarks>
    /// The route sequence is asserted alongside the rendering because a declined write and a write that
    /// silently failed produce the same exit code; only what was touched separates them.
    /// </remarks>
    private sealed class RecordingHandler : HttpMessageHandler
    {

        internal List<string> Requests { get; } = [];

        internal List<string> Bodies { get; } = [];

        internal SagaCurationOutcomeKind Outcome { get; init; } = SagaCurationOutcomeKind.Applied;

        internal SagaMemoryDetail? Detail { get; init; }

        internal (HttpStatusCode Status, Error Error)? Failure { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            bool isDetailRoute = request.Method == HttpMethod.Get;

            if (Failure is { } failure)
            {

                return isDetailRoute
                    ? Json(
                        failure.Status,
                        new ApiResponse<SagaMemoryDetail>(null, false, failure.Error),
                        ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail)
                    : Json(
                        failure.Status,
                        new ApiResponse<SagaCurationResult>(null, false, failure.Error),
                        ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

            }

            SagaMemoryDetail detail = Detail ?? MemorySagaCurationCommandTests.Detail();

            return isDetailRoute
                ? Json(
                    HttpStatusCode.OK,
                    new ApiResponse<SagaMemoryDetail>(detail, true, null),
                    ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail)
                : Json(
                    HttpStatusCode.OK,
                    new ApiResponse<SagaCurationResult>(new SagaCurationResult(Outcome, detail), true, null),
                    ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        }

        private static HttpResponseMessage Json<T>(
            HttpStatusCode status,
            T envelope,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(status)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo)),
            };

    }

}
