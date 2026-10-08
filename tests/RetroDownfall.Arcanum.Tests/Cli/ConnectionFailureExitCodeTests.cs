using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Exit 3 is the automation signal for "the host was unreachable". Every verb that calls the host
/// must give it, not only the families that happened to classify the failure themselves: a wrapper
/// that retries on 3 must see the same answer from <c>attachment list</c> as from <c>session show</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class ConnectionFailureExitCodeTests
{
    private const string SessionId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    public static TheoryData<string> Verbs => new()
    {
        { $"attachment list {SessionId}" },
        { "file list" },
        { "context inspect hello" },
        { "search anything" },
        { "browse https://example.com/" },
        { "research anything" },
        { "data retention show" },
        { "data status" },
        { "tool list" },
        { "mcp list" },
        { "workspace list" },
        { "operation list" },
        { $"operation show {SessionId}" },
        { $"operation cancel {SessionId}" },
        { $"operation retry {SessionId}" },
        { "operation reconcile" },
        { "daemon jobs" },
        { "daemon initiative job 5" },
        { "daemon alert hello" },
        { "trial run --target spell --target-value x" },
        { "ward list" },
        { "ward show w1" },
        { "ward resolve w1 --allow" },
        { "conclave dispatch --agent-url http://localhost:1/ --goal x" },
        { "conclave continue t1 --agent-url http://localhost:1/ --message x" },
        { "serve quit" },
        { "saga list" },
        { "saga divine anything" },
        { "saga stats" },
        { "saga delete 11111111-1111-4111-8111-111111111111 --yes" },
        { "model list" },
        { "watch health" },
        { "watch logs" },
        { "watch mcp" },
        { "watch daemons" },
        { "provider list" },
    };

    /// <summary>
    /// Every verb that accepts a name or unique prefix in place of an id lists the host to resolve it.
    /// A transport failure while resolving is the same unreachable host as a failure on the verb's own
    /// request, so it must reach exit 3 too: a wrapper that retries on 3 would otherwise treat
    /// <c>apprentice show &lt;name&gt;</c> as a domain failure and never retry it.
    /// </summary>
    public static TheoryData<string> NameResolvedVerbs => new()
    {
        { "apprentice show named-thing" },
        { "apprentice cancel named-thing --yes" },
        { "apprentice delete named-thing --yes" },
        { "mcp show named-thing" },
        { "mcp start named-thing" },
        { "mcp stop named-thing" },
        { "mcp restart named-thing" },
        { "mcp tools named-thing" },
        { "tool show named-thing" },
        { "tool invoke named-thing" },
        { "campaign show named-thing" },
        { "campaign update named-thing --name other" },
        { "campaign delete named-thing --yes" },
        { "campaign prompts named-thing" },
        { "prompt show named-thing" },
        { "prompt delete named-thing --yes" },
        { "spell show named-thing" },
        { "session show named-thing" },
        { "session rename named-thing --title other" },
        { "model show named-thing" },
        { "provider show named-thing" },
        { "workspace show named-thing" },
        { "open session named-thing" },
        { "open campaign named-thing" },
        { "attachment list named-thing" },
        { "watch session named-thing" },
        { "watch apprentice named-thing" },
        { "memory search anything --session named-thing" },
        { "use campaign named-thing" },
        { "mcp list --workspace named-thing" },
        { "spell show named-thing --workspace named-workspace" },
        { "open prompt named-thing" },
        { "open apprentice named-thing" },
        { "open spell named-thing" },
        { "search anything --attach-to-session named-thing" },
        { "browse https://example.com/ --attach-to-session named-thing" },
        { $"attachment show notes --session {SessionId}" },
        { $"attachment reference notes.md --workspace named-thing --session {SessionId}" },
        { $"session delete-entry some-entry --session {SessionId} --yes" },
        { "run --campaign named-thing hello" },
        { "run --spell named-thing hello" },
    };

    [Theory]
    [MemberData(nameof(Verbs))]
    [MemberData(nameof(NameResolvedVerbs))]
    public void A_host_that_cannot_be_reached_exits_3_with_nothing_on_stdout(string commandLine)
    {
        CliTestResult result = RunCommand(commandLine.Split(' '));

        Assert.True(
            result.ExitCode == (int)CliExitCode.NetworkError,
            $"exit {result.ExitCode}; stderr: {result.Error}");

        Assert.Equal(string.Empty, result.Output.Trim());
    }

    /// <summary>
    /// The same signal under <c>--json</c>: the exit code is the contract a wrapper reads, so structured
    /// output must not change it, and whatever the verb writes to stdout is at most one JSON document.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    [MemberData(nameof(NameResolvedVerbs))]
    public void A_host_that_cannot_be_reached_exits_3_under_json_too(string commandLine)
    {
        CliTestResult result = RunCommand([.. commandLine.Split(' '), "--json"]);

        Assert.True(
            result.ExitCode == (int)CliExitCode.NetworkError,
            $"exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error}");

        string stdout = result.Output.Trim();

        if (stdout.Length > 0)
        {
            using JsonDocument document = JsonDocument.Parse(stdout);

            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// The turn's own first host call, the Chronosync pattern sync, fails with the same unreachable host
    /// once the launcher has said the host is up (it answered the health probe a moment earlier), and
    /// that is exit 3 as well: the verbs above stop at name resolution or at the launcher, never here.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_chronosync_sync_the_host_cannot_answer_exits_3(bool json)
    {
        string[] args = json ? ["run", "hello", "--json"] : ["run", "hello"];

        CliTestResult result = RunCommand(
            args,
            static services =>
            {
                services.RemoveAll<IArcanumServeLauncher>();

                services.AddSingleton<IArcanumServeLauncher>(new ReadyServeLauncher());

                services.RemoveAll<IEyeOfTheWorld>();

                services.AddSingleton<IEyeOfTheWorld>(new EmptyEye());
            });

        Assert.True(
            result.ExitCode == (int)CliExitCode.NetworkError,
            $"exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error}");

        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one verb that asks the host and still does not exit 3: <c>context current</c> shows the saved
    /// context and checks it against the host on a best-effort basis, so a host that cannot be reached leaves
    /// the saved context unchecked (a stale-context warning is only ever about something the host answered)
    /// rather than failing a verb whose job, printing what is saved, was done. The Command Reference names
    /// it as the exception to the exit-3 rule.
    /// </summary>
    [Fact]
    public void Context_current_does_not_fail_on_an_unreachable_host()
    {
        CliTestResult result = RunCommand(["context", "current", "--no-context"]);

        Assert.True(
            result.ExitCode == (int)CliExitCode.Success,
            $"exit {result.ExitCode}; stdout: {result.Output}; stderr: {result.Error}");
    }

    private static CliTestResult RunCommand(string[] args, Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new UnreachableHostFactory());

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        CliTestHarness.AddKeyedArcanumResponder(services, "arc_test_0123456789abcdef0123456789abcdef");

        configure?.Invoke(services);

        return CliTestHarness.Run(services, args);
    }

    private sealed class ReadyServeLauncher : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(
                new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    HealthProbeState.Healthy,
                    TimeSpan.Zero,
                    null,
                    null));
    }

    private sealed class EmptyEye : IEyeOfTheWorld
    {
        public Task<PatternSnapshot> PerceivePatternAsync(string directoryPath, CancellationToken cancellationToken) =>
            Task.FromResult(new PatternSnapshot(DomainType.Unknown, directoryPath, []));
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

    private sealed class UnreachableHostFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new RefusingHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}
