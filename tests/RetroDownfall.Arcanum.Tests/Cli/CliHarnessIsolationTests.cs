using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The CLI harness builds production dependency injection, and a name-resolved selection such as
/// <c>workspace show demo</c> remembers the pick in <c>recent-resources.txt</c> through the real
/// mutation boundary. Left to the ambient environment, which under <c>dotnet test</c> is not
/// <c>Testing</c>, every such run reads and writes the developer's real profile directory.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliHarnessIsolationTests
{
    [Fact]
    public void Name_resolved_resource_selection_stays_inside_the_test_home()
    {
        PathRecordingHandler handler = new();

        CliTestResult result = RunWorkspaceShowDemo(handler);

        Assert.Equal(0, result.ExitCode);

        // Resolving the name already issues requests the handler observes; asserting how many
        // would pin the command's request choreography, which this test is not about. It only
        // needs something to have been observed, or the loop below would pass vacuously.
        Assert.NotEmpty(handler.Observed);

        foreach ((string grimoire, string secrets) in handler.Observed)
        {
            Assert.False(
                TestHomeGuard.IsUnderRealDirectory(
                    grimoire,
                    TestProcessPaths.OriginalUserProfile,
                    TestProcessPaths.OriginalApplicationData),
                $"The Grimoire directory resolved to the real profile mid-command: {grimoire}");

            Assert.False(
                TestHomeGuard.IsUnderRealDirectory(
                    secrets,
                    TestProcessPaths.OriginalUserProfile,
                    TestProcessPaths.OriginalApplicationData),
                $"The secret store directory resolved to the real profile mid-command: {secrets}");
        }
    }

    [Fact]
    public void A_harness_owned_home_is_released_when_the_command_finishes()
    {
        bool redirectedBefore = !TestHomeGuard.AmbientHomeIsUnredirected();

        string? testHomeBefore =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        _ = RunWorkspaceShowDemo(new PathRecordingHandler());

        Assert.Equal(redirectedBefore, !TestHomeGuard.AmbientHomeIsUnredirected());

        Assert.Equal(
            testHomeBefore,
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));
    }

    [Fact]
    public void A_home_the_test_already_redirected_is_left_alone()
    {
        using ArcanumTestHomeScope home = new("arcanum-cli-harness-isolation-tests");

        PathRecordingHandler handler = new();

        CliTestResult result = RunWorkspaceShowDemo(handler);

        Assert.Equal(0, result.ExitCode);

        Assert.NotEmpty(handler.Observed);

        Assert.All(
            handler.Observed,
            observed => Assert.Equal(
                Path.Combine(home.Root, ".config", "arcanum"),
                observed.Grimoire));

        Assert.Equal(
            home.Root,
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME"));
    }

    private static CliTestResult RunWorkspaceShowDemo(
        PathRecordingHandler handler)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new HandlerHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FixedKeySecretStore("test-key"));

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        return CliTestHarness.Run(services, ["workspace", "show", "demo"]);
    }

    private static HttpResponseMessage Respond<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo)),
        };

    private sealed class PathRecordingHandler : HttpMessageHandler
    {
        private static readonly WorkspaceInfo Workspace = new(
            "ws-demo",
            "demo",
            "/srv/projects/demo",
            WorkspaceType.Custom,
            DateTimeOffset.Parse("2026-07-31T12:00:00Z"));

        public List<(string Grimoire, string SecretStore)> Observed { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Observed.Add((ArcanumPaths.GrimoireDirectory, ArcanumPaths.SecretStoreDirectory));

            HttpResponseMessage response = request.RequestUri!.AbsolutePath == "/api/workspaces"
                ? Respond(
                    new ApiResponse<WorkspaceInfo[]>([Workspace], true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfoArray)
                : Respond(
                    new ApiResponse<WorkspaceInfo>(Workspace, true, null),
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfo);

            return Task.FromResult(response);
        }
    }

    private sealed class HandlerHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class FixedKeySecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;
    }
}
