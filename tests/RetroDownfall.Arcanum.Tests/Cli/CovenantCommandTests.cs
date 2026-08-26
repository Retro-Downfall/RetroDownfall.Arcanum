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

    /// <summary>
    /// A write whose expectation cannot match is refused before the operator is asked.
    /// </summary>
    /// <remarks>
    /// <c>--expected-revision</c> has no default, so omitting it sends zero, and zero means "create".
    /// Against a lane that already has a head the commit refuses with <c>Covenant.RevisionConflict</c>
    /// — a message that names neither the revision the operator needed nor the flag that carries it —
    /// and it does so only after they had approved a confirmation screen every line of which was true.
    /// The natural command for updating an existing preference was therefore the one that always
    /// failed, and the assertion here is that nothing was asked and nothing was written.
    /// </remarks>
    [Fact]
    public async Task A_write_against_a_stale_expectation_is_refused_before_the_question_is_put()
    {

        RecordingHandler handler = new() { HeadRevision = 3 };

        CovenantCommands commands = Commands(handler, confirm: true, out RecordingDispatcher dispatcher);

        int exitCode = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 0,
            reactivate: false,
            Token);

        Assert.NotEqual(0, exitCode);

        // The confirmation answers yes in this fixture, so reaching the commit route would mean the
        // operator had been asked and their approval spent on a write that could not succeed.
        Assert.Equal(["POST /api/memory/covenant/set/prepare"], handler.Requests);

        string said = string.Join("\n", dispatcher.Diagnostics);

        // The number the operator needs, not just the fact that something is stale.
        Assert.Contains("revision 3", said, StringComparison.Ordinal);

        Assert.Contains("--expected-revision 3", said, StringComparison.Ordinal);

    }

    /// <summary>
    /// The same write, once its expectation matches the head, still reaches the commit route.
    /// </summary>
    /// <remarks>
    /// Paired with the refusal above so the guard cannot be "refuse everything": a comparison that
    /// rejected every write would satisfy the test that only asserts the stale one is caught.
    /// </remarks>
    [Fact]
    public async Task A_write_whose_expectation_matches_the_head_still_commits()
    {

        RecordingHandler handler = new() { HeadRevision = 3 };

        CovenantCommands commands = Commands(handler, confirm: true, out _);

        int exitCode = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 3,
            reactivate: false,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(
            ["POST /api/memory/covenant/set/prepare", "PUT /api/memory/covenant"],
            handler.Requests);

    }

    /// <summary>
    /// Retirement refuses to run at all without the revision it is retiring.
    /// </summary>
    /// <remarks>
    /// A live head is never at revision zero, so an omitted flag on this verb is not a stale
    /// expectation an operator might reasonably hold — it is the one value that can never succeed.
    /// Refusing at parse time is what stops the command from reaching a preflight and a confirmation
    /// screen on the way to a guaranteed refusal.
    /// </remarks>
    [Fact]
    public void A_retirement_without_an_expected_revision_never_reaches_a_route()
    {

        RecordingHandler handler = new();

        ParseResult parsed = Tree(handler).Parse("memory covenant retire preference.builds");

        Assert.NotEmpty(parsed.Errors);

        StringWriter output = new();

        int exitCode = parsed.Invoke(new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = output,
            Error = output,
        });

        Assert.NotEqual(0, exitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--expected-revision", output.ToString(), StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_declined_retirement_never_reaches_the_commit_route()
    {

        RecordingHandler handler = new() { HeadRevision = 1 };

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

        int exitCode = await commands.List(
            campaignId: null,
            allScopes: false,
            lane: null,
            CovenantLifecycle.Set,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Contains(
            "No Covenant entries",
            string.Join("\n", dispatcher.Payloads),
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A reactivating write says so on both halves of the protocol.
    /// </summary>
    /// <remarks>
    /// Reactivation is bound into the digest the preflight token seals, so a prepare and a commit that
    /// disagree about it are refused rather than silently honoured. The failure this catches is the
    /// quieter one: a flag that reached neither request would make <c>--reactivate</c> a word the
    /// operator typed and nothing acted on, and the route sequence would look identical.
    /// </remarks>
    [Fact]
    public async Task A_reactivating_write_carries_the_flag_into_both_the_prepare_and_the_commit()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: true, out _);

        int exitCode = await commands.Set(
            "preference.builds",
            campaignId: null,
            file: WriteTempFile("Run build commands from the repository root."),
            expectedRevision: 0,
            reactivate: true,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(
            ["POST /api/memory/covenant/set/prepare", "PUT /api/memory/covenant"],
            handler.Requests);

        Assert.All(
            handler.Bodies,
            body => Assert.Contains("\"reactivate\":true", body, StringComparison.OrdinalIgnoreCase));

    }

    /// <summary>
    /// The lane the server resolved is the lane the operator is shown.
    /// </summary>
    /// <remarks>
    /// The confirmation screen is the last place a mistyped <c>--lane</c> can be caught. Without the
    /// lane on it, an operator asked to approve a retirement was shown the key, the revision, the cost
    /// and the hash — every field except the one that says which of the two standings is about to be
    /// withdrawn.
    /// </remarks>
    [Fact]
    public async Task The_confirmation_screen_names_the_lane_the_server_resolved()
    {

        RecordingHandler handler = new() { HeadRevision = 1 };

        CovenantCommands commands = Commands(handler, confirm: false, out RecordingDispatcher dispatcher);

        _ = await commands.Retire(
            "preference.builds",
            campaignId: null,
            CovenantLane.Confirmed,
            expectedRevision: 1,
            Token);

        Assert.Contains(
            "Confirmed lane",
            string.Join("\n", dispatcher.Payloads),
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A misspelled lane fails the command instead of retiring the other one.
    /// </summary>
    /// <remarks>
    /// <c>--lane propsed</c> used to parse as absence, which <c>retire</c> coalesced to Confirmed — so
    /// a typo aimed at an agent's proposal sent a well-formed request that withdrew the operator's own
    /// standing preference and exited zero. The route sequence is asserted alongside the exit code
    /// because a refusal that still prepared would have taken a lease and measured an effect for a
    /// command that was never valid.
    /// </remarks>
    [Fact]
    public void A_misspelled_lane_fails_the_command_before_it_reaches_any_route()
    {

        RecordingHandler handler = new();

        ParseResult parsed = Tree(handler).Parse(
            "memory covenant retire preference.builds --lane propsed --expected-revision 1");

        Assert.NotEmpty(parsed.Errors);

        StringWriter output = new();

        int exitCode = parsed.Invoke(new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = output,
            Error = output,
        });

        Assert.NotEqual(0, exitCode);

        Assert.Empty(handler.Requests);

        // The valid names travel with the refusal. "Not a Covenant lane" alone would leave an operator
        // guessing at a vocabulary of exactly two words.
        Assert.Contains("Confirmed", output.ToString(), StringComparison.Ordinal);

        Assert.Contains("Proposed", output.ToString(), StringComparison.Ordinal);

    }

    /// <summary>
    /// A correctly spelled lane still parses, in whatever case an operator typed it.
    /// </summary>
    /// <remarks>
    /// Paired with the refusal above so the fix cannot be "reject everything": a check that failed
    /// every value would satisfy the test that only asserts the typo is caught.
    /// </remarks>
    [Fact]
    public void A_lane_named_in_any_casing_still_parses()
    {

        ParseResult parsed = Tree(new RecordingHandler()).Parse(
            "memory covenant list --lane proposed");

        Assert.Empty(parsed.Errors);

    }

    /// <summary>
    /// A pipe already spent on content is not an answer to the confirmation question.
    /// </summary>
    /// <remarks>
    /// <c>set</c> reads the whole pipe as authored content, so by the time it asks for confirmation
    /// standard input is exhausted. The read returned null, which the prompt scored as "no", and the
    /// command printed "cancelled" and exited zero — reporting a decision the operator never made and
    /// giving a script no way to tell a refusal from a successful no-op.
    /// </remarks>
    [Fact]
    public async Task A_write_that_cannot_ask_refuses_rather_than_reporting_a_cancellation()
    {

        RecordingHandler handler = new();

        CliInvocationOptions options = new(Json: false, Plain: true, Yes: false);

        ConfirmationPrompt prompt = new(
            new ConsoleDispatcher(new StringWriter(), new StringWriter(), options),
            options,
            new StringReader(string.Empty),
            isOutputRedirected: static () => false,
            isInputRedirected: static () => true);

        CovenantCommands commands = Commands(handler, prompt, out _);

        await Assert.ThrowsAsync<NonInteractiveConfirmationException>(
            () => commands.Set(
                "preference.builds",
                campaignId: null,
                file: WriteTempFile("Run build commands from the repository root."),
                expectedRevision: 0,
                reactivate: false,
                Token));

        // The refusal has to land before the commit, not merely be reported after it.
        Assert.Equal(["POST /api/memory/covenant/set/prepare"], handler.Requests);

    }

    /// <summary>
    /// Every page is followed, because nothing an operator holds could ask for the next one.
    /// </summary>
    /// <remarks>
    /// The continuation is an AEAD-sealed cursor minted by the server. A listing that stopped at the
    /// first page and printed "more entries exist" announced entries no command could reach — and
    /// among them are exactly the retired heads an operator needs to see before a reactivating write.
    /// </remarks>
    [Fact]
    public async Task A_listing_follows_the_servers_cursor_rather_than_announcing_what_it_cannot_reach()
    {

        RecordingHandler handler = new() { ListPages = 3 };

        CovenantCommands commands = Commands(handler, confirm: true, out RecordingDispatcher dispatcher);

        int exitCode = await commands.List(
            campaignId: null,
            allScopes: false,
            lane: null,
            CovenantLifecycle.Retired,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(3, handler.Requests.Count);

        string rendered = string.Join("\n", dispatcher.Payloads);

        Assert.Contains("preference.page1", rendered, StringComparison.Ordinal);

        Assert.Contains("preference.page3", rendered, StringComparison.Ordinal);

        // The second request has to carry the cursor the first one handed back; a client that dropped
        // it would re-read page one forever or stop after it.
        Assert.Contains("cursor-1", handler.Bodies[1], StringComparison.Ordinal);

        // The lifecycle an operator asked for is the lifecycle that is sent. A hardcoded Set made
        // retired heads unlistable, which is what blocks an informed reactivation.
        Assert.All(
            handler.Bodies,
            body => Assert.Contains("\"lifecycle\":\"Retired\"", body, StringComparison.OrdinalIgnoreCase));

    }

    /// <summary>
    /// History answers "who changed this preference, and when".
    /// </summary>
    /// <remarks>
    /// The version page has carried operation, origin, revision, and mutation identity since the route
    /// shipped; there was no verb that asked for it. Nothing here prints authored content, because a
    /// history is a record of changes rather than a second way to read what a key says.
    /// </remarks>
    [Fact]
    public async Task History_prints_each_revisions_operation_and_origin()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: true, out RecordingDispatcher dispatcher);

        int exitCode = await commands.Show("preference.builds", campaignId: null, history: true, Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(
            ["POST /api/memory/covenant/detail", "POST /api/memory/covenant/versions"],
            handler.Requests);

        string rendered = string.Join("\n", dispatcher.Payloads);

        Assert.Contains("revision 2", rendered, StringComparison.Ordinal);

        Assert.Contains("Operator", rendered, StringComparison.Ordinal);

    }

    /// <summary>
    /// Without <c>--history</c> the version route is not touched at all.
    /// </summary>
    /// <remarks>
    /// A version page costs an authenticated installation read. Spending one on every <c>show</c>
    /// would make a lookup's cost depend on how many times the key had ever been written.
    /// </remarks>
    [Fact]
    public async Task A_show_without_history_reads_no_version_page()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: true, out _);

        _ = await commands.Show("preference.builds", campaignId: null, history: false, Token);

        Assert.Equal(["POST /api/memory/covenant/detail"], handler.Requests);

    }

    private static RootCommand Tree(RecordingHandler handler)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.AddSingleton<IHttpClientFactory>(new SingleHandlerFactory(handler));

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        using ServiceProvider provider = services.BuildServiceProvider();

        return CliCommandTree.Build(provider, out _);

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

    [Fact]
    public async Task A_declined_pin_never_reaches_the_commit_route()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: false, out RecordingDispatcher dispatcher);

        int exitCode = await commands.Curate(
            CovenantCurationKind.Pin,
            "preference.builds",
            campaignId: null,
            CovenantLane.Confirmed,
            expectedRevision: 0,
            Token);

        Assert.Equal(0, exitCode);

        // The preflight is a read. Nothing after it may have happened.
        Assert.Equal(["POST /api/memory/covenant/curate/prepare"], handler.Requests);

        Assert.Contains("cancelled", string.Join("\n", dispatcher.Diagnostics), StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task A_confirmed_pin_prepares_first_then_commits()
    {

        RecordingHandler handler = new();

        CovenantCommands commands = Commands(handler, confirm: true, out _);

        int exitCode = await commands.Curate(
            CovenantCurationKind.Pin,
            "preference.builds",
            campaignId: null,
            CovenantLane.Confirmed,
            expectedRevision: 0,
            Token);

        Assert.Equal(0, exitCode);

        Assert.Equal(
            ["POST /api/memory/covenant/curate/prepare", "POST /api/memory/covenant/curate"],
            handler.Requests);

    }

    /// <summary>
    /// The sentence an operator has to see before they approve a mask: what applies here afterwards.
    /// It is read off the server's preflight, never off the flags this process parsed.
    /// </summary>
    [Fact]
    public async Task A_mask_screen_states_that_nothing_replaces_the_Global_entry()
    {

        RecordingHandler handler = new() { GlobalConfirmedSuppressed = true };

        CovenantCommands commands = Commands(handler, confirm: true, out RecordingDispatcher dispatcher);

        _ = await commands.Curate(
            CovenantCurationKind.Mask,
            "preference.builds",
            campaignId: Guid.CreateVersion7(),
            CovenantLane.Confirmed,
            expectedRevision: 0,
            Token);

        Assert.Contains(
            "nothing replaces it",
            string.Join("\n", dispatcher.Payloads),
            StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Refused before the question is put, not after. The kernel compares exactly these two numbers,
    /// so asking first renders a screen every line of which is true and which describes a change that
    /// cannot succeed.
    /// </summary>
    [Fact]
    public async Task A_pin_whose_expected_revision_is_stale_is_refused_before_the_confirmation()
    {

        RecordingHandler handler = new() { CurationHeadRevision = 4 };

        CovenantCommands commands = Commands(handler, confirm: true, out RecordingDispatcher dispatcher);

        int exitCode = await commands.Curate(
            CovenantCurationKind.Pin,
            "preference.builds",
            campaignId: null,
            CovenantLane.Confirmed,
            expectedRevision: 0,
            Token);

        Assert.NotEqual(0, exitCode);

        Assert.Equal(["POST /api/memory/covenant/curate/prepare"], handler.Requests);

        Assert.Contains(
            "--expected-revision 4",
            string.Join("\n", dispatcher.Diagnostics.Concat(dispatcher.Payloads)),
            StringComparison.Ordinal);

    }

    private static CovenantCommands Commands(
        RecordingHandler handler,
        bool confirm,
        out RecordingDispatcher dispatcher) =>
        Commands(handler, new FixedConfirmation(confirm), out dispatcher);

    private static CovenantCommands Commands(
        RecordingHandler handler,
        IConfirmationPrompt prompt,
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
            prompt,
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

        /// <summary>Every request body, in the order it was sent.</summary>
        /// <remarks>
        /// The route sequence says which surfaces were reached; only the bodies say what was asked of
        /// them. A flag that never left the client would produce exactly the same sequence.
        /// </remarks>
        internal List<string> Bodies { get; } = [];

        internal bool EmptyList { get; init; }

        /// <summary>The revision the stubbed head sits at, as the preflight would report it.</summary>
        internal long HeadRevision { get; init; }

        /// <summary>The curation revision the stubbed subject sits at.</summary>
        internal long CurationHeadRevision { get; init; }

        /// <summary>Whether the stubbed preflight reports the Global entry being suppressed.</summary>
        internal bool GlobalConfirmedSuppressed { get; init; }

        /// <summary>How many list pages exist before the cursor runs out.</summary>
        internal int ListPages { get; init; } = 1;

        private int _listCalls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string path = request.RequestUri!.AbsolutePath;

            Requests.Add($"{request.Method} {path}");

            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            string body;

            if (path.EndsWith("curate/prepare", StringComparison.Ordinal))
            {

                body = CurationPreflight(CurationHeadRevision, ExpectedRevisionOf(Bodies[^1]));

            }
            else if (path.EndsWith("curate", StringComparison.Ordinal))
            {

                body = CurationResult();

            }
            else if (path.EndsWith("prepare", StringComparison.Ordinal))
            {

                // Echoed from the request, exactly as the service echoes it. A stub that reported its
                // own expectation could never disagree with the head, which is the disagreement the
                // confirmation path exists to catch.
                body = Preflight(HeadRevision, ExpectedRevisionOf(Bodies[^1]));

            }
            else if (path.EndsWith("list", StringComparison.Ordinal))
            {

                _listCalls++;

                body = Page(_listCalls);

            }
            else if (path.EndsWith("versions", StringComparison.Ordinal))
            {

                body = Versions();

            }
            else if (path.EndsWith("detail", StringComparison.Ordinal))
            {

                body = Detail();

            }
            else
            {

                body = Mutation();

            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        }

        private static CovenantHeadDto Head(string key) =>
            new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                CovenantScope.Global,
                null,
                key,
                CovenantLane.Confirmed,
                1,
                CovenantLifecycle.Set,
                CovenantOrigin.Operator,
                "77",
                "88",
                64,
                0,
                "99",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                CovenantEffectiveShadowState.NotEvaluated,
                CovenantEffectiveMaterialization.NotEvaluated);

        private static string Detail() =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantDetailDto>.FromResult(
                    Result<CovenantDetailDto>.Success(new CovenantDetailDto(
                        CovenantScope.Global,
                        null,
                        "preference.builds",
                        DetailEntryId,
                        Head("preference.builds"),
                        null,
                        1,
                        null,
                        null)),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantDetailDto);

        internal static readonly Guid DetailEntryId = new("44444444-4444-4444-8444-444444444444");

        private static string Versions() =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantVersionPageDto>.FromResult(
                    Result<CovenantVersionPageDto>.Success(new CovenantVersionPageDto(
                        [
                            new CovenantVersionDto(
                                Guid.NewGuid(),
                                DetailEntryId,
                                CovenantLane.Confirmed,
                                2,
                                CovenantOperation.Set,
                                CovenantOrigin.Operator,
                                "99",
                                "aa",
                                64,
                                1,
                                1,
                                null,
                                Guid.NewGuid(),
                                0,
                                "bb",
                                DateTimeOffset.UtcNow),
                        ],
                        NextCursor: null,
                        "cc",
                        Truncated: false)),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantVersionPageDto);

        private static long ExpectedRevisionOf(string requestBody)
        {

            using JsonDocument parsed = JsonDocument.Parse(requestBody);

            foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
            {

                if (string.Equals(property.Name, "expectedRevision", StringComparison.OrdinalIgnoreCase))
                {

                    return property.Value.GetInt64();

                }

            }

            return 0;

        }

        private string CurationPreflight(long headRevision, long expectedRevision) =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantCurationPreflightDto>.FromResult(
                    Result<CovenantCurationPreflightDto>.Success(new CovenantCurationPreflightDto(
                        CovenantCurationKind.Pin,
                        CovenantScope.Global,
                        null,
                        "preference.builds",
                        CovenantLane.Confirmed,
                        Guid.NewGuid(),
                        "00",
                        IsPinned: false,
                        IsMasked: false,
                        headRevision,
                        expectedRevision,
                        1,
                        GlobalConfirmedSuppressed,
                        GlobalConfirmedResurfaces: false,
                        ChangesAnything: true,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        "token")),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantCurationPreflightDto);

        private static string CurationResult() =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantCurationResultDto>.FromResult(
                    Result<CovenantCurationResultDto>.Success(new CovenantCurationResultDto(
                        Guid.NewGuid(),
                        CovenantMutationOutcome.Applied,
                        CovenantCurationKind.Pin,
                        CovenantScope.Global,
                        null,
                        "preference.builds",
                        CovenantLane.Confirmed,
                        IsPinned: true,
                        IsMasked: false,
                        Guid.NewGuid(),
                        1,
                        "00",
                        "11",
                        Replayed: false)),
                    "trace"),
                ArcanumJsonContext.Default.ApiResponseCovenantCurationResultDto);

        private static string Preflight(long headRevision, long expectedRevision) =>
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
                        headRevision,
                        expectedRevision,
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

        /// <summary>
        /// Answers one page, handing back a fresh cursor while pages remain.
        /// </summary>
        /// <remarks>
        /// The cursor changes per page on purpose. A client that reused the previous one, or stopped
        /// at the first page, would leave every entry after the first page unreachable — and the
        /// cursor is AEAD-sealed, so there is no value an operator could supply themselves.
        /// </remarks>
        private string Page(int call) =>
            JsonSerializer.Serialize(
                ApiResponse<CovenantPageDto>.FromResult(
                    Result<CovenantPageDto>.Success(new CovenantPageDto(
                        EmptyList ? [] : [Head($"preference.page{call}")],
                        call < ListPages ? $"cursor-{call}" : null,
                        "66",
                        new CovenantSearchHealthDto(
                            CovenantSearchHealthState.Healthy,
                            CovenantSearchExecutionMode.CanonicalFallback,
                            CovenantSearchRebuildGuidance.None),
                        call < ListPages,
                        call < ListPages
                            ? CovenantPageTruncation.PageSizeReached
                            : CovenantPageTruncation.None)),
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
