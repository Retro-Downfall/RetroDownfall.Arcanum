using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.Infrastructure;
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
        { "provider list" },
    };

    [Theory]
    [MemberData(nameof(Verbs))]
    public void A_host_that_cannot_be_reached_exits_3_with_nothing_on_stdout(string commandLine)
    {
        CliTestResult result = RunCommand(commandLine.Split(' '));

        Assert.True(
            result.ExitCode == (int)CliExitCode.NetworkError,
            $"exit {result.ExitCode}; stderr: {result.Error}");

        Assert.Equal(string.Empty, result.Output.Trim());
    }

    private static CliTestResult RunCommand(string[] args)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new UnreachableHostFactory());

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FixedSecretStore());

        CliTestHarness.AddKeyedArcanumResponder(services, "arc_test_0123456789abcdef0123456789abcdef");

        return CliTestHarness.Run(services, args);
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
