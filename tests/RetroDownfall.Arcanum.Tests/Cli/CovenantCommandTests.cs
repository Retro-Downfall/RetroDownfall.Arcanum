using System.CommandLine;

using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands.Tower;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The operator's command line over their own Covenant.
/// </summary>
/// <remarks>
/// The assertions are about ordering and refusal, not formatting. What matters is that an operator
/// sees the server's own measurement before they are asked, and that declining reaches no mutating
/// route at all.
/// </remarks>
public sealed class CovenantCommandTests : IDisposable
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_declined_write_never_reaches_the_commit_route()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: false, out RecordingDispatcher dispatcher);

        int exitCode = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 0,
            reactivate: false,
            Token);

        Assert.Equal(0, exitCode);

        // The preflight is a read. Nothing after it may have happened.
        Assert.Equal(["POST /api/memory/covenant/set/prepare"], handler.Requests);

        Assert.Contains("cancelled", string.Join("\n", dispatcher.Diagnostics), StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task A_confirmed_write_prepares_first_then_commits()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: true, out _);

        int exitCode = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 0,
            reactivate: false,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(
            ["POST /api/memory/covenant/set/prepare", "PUT /api/memory/covenant"],
            handler.Requests);

    }

    [Fact]
    public async Task The_measurement_an_operator_sees_is_the_servers_own()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: false, out RecordingDispatcher dispatcher);

        _ = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 0,
            reactivate: false,
            Token);

        string shown = string.Join("\n", dispatcher.Payloads);

        // These values come from the prepare response, not from anything the client computed. A CLI
        // that printed its own guess would be asking the operator to approve the wrong number.
        Assert.Contains("4096 bytes", shown, StringComparison.Ordinal);

        Assert.Contains(RecordingHandler.RenderedHash, shown, StringComparison.Ordinal);

        Assert.Contains("3 Campaign(s)", shown, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_global_write_says_it_reaches_campaigns_created_later()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: false, out RecordingDispatcher dispatcher);

        _ = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 0,
            reactivate: false,
            Token);

        Assert.Contains(
            "Campaigns created later",
            string.Join("\n", dispatcher.Payloads),
            StringComparison.Ordinal);

    }

    /// <summary>
    /// There is no content argument to pass, so content cannot come from one.
    /// </summary>
    /// <remarks>
    /// Asserted against the command tree rather than by driving the handler with no file: with stdin
    /// redirected-but-open — which is exactly a test host — reading it blocks until the writer closes,
    /// as any pipe-reading tool does. The contract worth pinning is the shape of the verb, which is
    /// what stops a preference from landing in shell history in the first place.
    /// </remarks>
    [Fact]
    public void The_set_verb_offers_no_argument_that_could_carry_content()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(new RecordingHandler()));

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        Command set = Descend(root, "memory", "covenant", "set");

        Argument only = Assert.Single(set.Arguments);

        Assert.Equal("key", only.Name);

        Assert.Contains(set.Options, static option => option.Name == "--file");

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

    [Fact]
    public async Task A_missing_file_is_a_configuration_error_before_any_request()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: true, out _);

        int exitCode = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.txt"),
            expectedRevision: 0,
            reactivate: false,
            Token);

        Assert.Equal((int)CliExitCode.ConfigurationError, exitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task A_declined_retirement_never_reaches_the_commit_route()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: false, out _);

        int exitCode = await commands.Retire(
            "preference.builds",
            campaignId: null,
            CovenantLane.Confirmed,
            expectedRevision: 1,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(["POST /api/memory/covenant/retire/prepare"], handler.Requests);

    }

    [Fact]
    public async Task An_empty_scope_says_so_rather_than_printing_nothing()
    {

        RecordingHandler handler = new() { EmptyList = true };

        CovenantCommands commands = Commands(handler, confirm: true, out RecordingDispatcher dispatcher);

        int exitCode = await commands.List(campaignId: null, allScopes: false, lane: null, Token);

        Assert.Equal(0, exitCode);

        Assert.Contains(
            "No Covenant entries",
            string.Join("\n", dispatcher.Payloads),
            StringComparison.Ordinal);

    }

    /// <summary>
    /// Writes one throwaway content file and remembers to remove it.
    /// </summary>
    /// <remarks>
    /// Cleaned rather than left behind: the suite's shared temp directory accumulates across a run,
    /// and enough leftovers there make the whole suite stall on SQLite lock contention rather than
    /// fail. A test that litters is a test that eventually breaks an unrelated one.
    /// </remarks>
    private string WriteTempFile(string content)
    {

        string path = Path.Combine(Path.GetTempPath(), $"arcanum-covenant-{Guid.NewGuid():N}.txt");

        File.WriteAllText(path, content);

        _temporaryFiles.Add(path);

        return path;

    }

    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {

        foreach (string path in _temporaryFiles)
        {

            try
            {

                File.Delete(path);

            }
            catch (IOException)
            {

                // A file another process still holds is not worth failing a passing test over.

            }

        }

    }

    private static CovenantCommands Commands(
        RecordingHandler handler,
        bool confirm,
        out RecordingDispatcher dispatcher)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(handler));

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        using ServiceProvider provider = services.BuildServiceProvider();

        dispatcher = new RecordingDispatcher();

        return new CovenantCommands(
            provider.GetRequiredService<ArcanumApiClient>(),
            dispatcher,
            new FixedConfirmation(confirm),
            new FixedInvocationContext());

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
    /// Records the exact sequence of routes a verb reached, and answers each plausibly.
    /// </summary>
    /// <remarks>
    /// The sequence is the assertion. A handler that committed before confirming would still produce
    /// the same exit code and the same output; only the order of what it touched gives it away.
    /// </remarks>
    private sealed class RecordingHandler : HttpMessageHandler
    {

        internal const string RenderedHash = "aa11bb22cc33dd44";

        internal List<string> Requests { get; } = [];

        internal bool EmptyList { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string path = request.RequestUri!.AbsolutePath;

            Requests.Add($"{request.Method} {path}");

            string body = path.EndsWith("prepare", StringComparison.Ordinal)
                ? Preflight()
                : path.EndsWith("list", StringComparison.Ordinal)
                    ? Page()
                    : Mutation();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        }

        private static string Preflight() =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantMutationPreflightDto>.FromResult(
                    Result<CovenantMutationPreflightDto>.Success(new CovenantMutationPreflightDto(
                        CovenantScope.Global,
                        null,
                        "preference.builds",
                        CovenantLane.Confirmed,
                        CovenantOperation.Set,
                        Guid.NewGuid(),
                        "00",
                        "11",
                        RenderedHash,
                        4096,
                        0,
                        1,
                        new CovenantMutationEffectDto(
                            CovenantEffectDecision.HeadCreated,
                            3,
                            [],
                            false,
                            AppliesToFutureCampaigns: true,
                            false,
                            false,
                            false,
                            4096,
                            8192,
                            "22",
                            "33"),
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        "token")),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);

        private static string Mutation() =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantMutationResultDto>.FromResult(
                    Result<CovenantMutationResultDto>.Success(new CovenantMutationResultDto(
                        Guid.NewGuid(),
                        CovenantMutationOutcome.Applied,
                        CovenantOperation.Set,
                        CovenantScope.Global,
                        null,
                        "preference.builds",
                        CovenantLane.Confirmed,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        1,
                        "44",
                        "55",
                        false)),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);

        private string Page() =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantPageDto>.FromResult(
                    Result<CovenantPageDto>.Success(new CovenantPageDto(
                        EmptyList ? [] : [],
                        null,
                        "66",
                        new CovenantSearchHealthDto(
                            CovenantSearchHealthState.Healthy,
                            CovenantSearchExecutionMode.CanonicalFallback,
                            CovenantSearchRebuildGuidance.None),
                        false,
                        CovenantPageTruncation.None)),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantPageDto);

    }

    private sealed class RecordingDispatcher : IConsoleDispatcher
    {

        internal List<string> Payloads { get; } = [];

        internal List<string> Diagnostics { get; } = [];

        public void WritePayload(string value) => Payloads.Add(value);

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WriteVerbose(string value) => Diagnostics.Add(value);

        public void WriteLine(string value) => Payloads.Add(value);

        public void WriteJson<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            Payloads.Add(JsonSerializer.Serialize(value, typeInfo));

        public void WriteJson(JsonElement value) => Payloads.Add(value.GetRawText());

        public void BeginJsonStream()
        {
        }

    }

    private sealed class FixedConfirmation(bool answer) : IConfirmationPrompt
    {

        public Task<bool> PromptForConfirmationAsync(string question, CancellationToken cancellationToken) =>
            Task.FromResult(answer);

    }

    private sealed class FixedInvocationContext : ICliInvocationContext
    {

        public CliInvocationOptions Options { get; } = new(false, false, false, false, false, false);

    }

}
