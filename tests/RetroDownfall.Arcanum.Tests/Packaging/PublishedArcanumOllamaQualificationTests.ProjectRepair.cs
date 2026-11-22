using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using static RetroDownfall.Arcanum.Tests.Intelligence.WizardIntelligenceProviderTests;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class PublishedArcanumOllamaQualificationTests
{
    [SkippableFact]
    [Trait("Category", "LocalOllama")]
    public async Task Published_native_host_lets_resident_model_repair_project_resume_session_after_restart()
    {
        Skip.IfNot(System.Environment.GetEnvironmentVariable("ARCANUM_TEST_LOCAL_PROJECT_REPAIR_NATIVE") == "1",
            "Set ARCANUM_TEST_LOCAL_PROJECT_REPAIR_NATIVE=1 and the published executable and resident model.");

        Assert.True(OperatingSystem.IsMacOS());

        Assert.False(LocalOllamaQualificationGuards.IsGitHubActions(System.Environment.GetEnvironmentVariable(GithubActionsVariable)));

        string executable = RequireEnvironmentPath(PublishedExecutableVariable);

        string model = System.Environment.GetEnvironmentVariable(ProjectRepairOptIn)
            ?? throw new InvalidOperationException("The isolated project-repair fixture needs the exact resident Ollama model.");

        const string runtime = "project-repair-witness";
        const string management = "http://127.0.0.1:11434";

        Uri endpoint = ValidateLocalOllamaEndpoint(management + "/v1/");

        AssertNativeAotPublish(executable);

        await AssertModelIsInstalledAsync(endpoint, model);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        CancellationToken ct = watchdog.Token;

        var isolation = PublishedTestProcessIsolation.Create("ollama-project-repair");

        string workspace = Path.Combine(isolation.TestHome, "workspace");

        string packageCache = Path.Combine(isolation.TestHome, ".nuget", "packages");

        PublishedHost? host = null;

        TestEnvironment? environment = null;

        try
        {
            isolation.Prepare();

            Directory.CreateDirectory(workspace);

            await PrepareLocalProjectRepairAsync(workspace, packageCache, ct);

            ProjectRepairPhase("native project independent check reproduced CS0029");

            string directory = Directory.CreateDirectory(Path.Combine(isolation.TestHome, ".config", "arcanum")).FullName;

            string configPath = Path.Combine(directory, "arcanum.json");

            using TcpListener reservation = new(IPAddress.Loopback, 0);

            reservation.Start();

            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;

            await WriteConfigurationAsync(configPath, workspace, port, endpoint, model);

            var settings = JsonSerializer.Deserialize(await File.ReadAllTextAsync(configPath, ct),
                ConfigurationJsonContext.Default.ArcanumConfigurationFile) ?? throw new InvalidOperationException("Test configuration failed.");

            settings.Arcanum.Providers[0] = settings.Arcanum.Providers[0] with
            {
                LocalRuntimeId = runtime,
                ContextWindowLimit = 262_144,
            };
            settings.Arcanum.LocalRuntimes = [new() { Id = runtime, Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = management }];
            settings.Arcanum.Features.WorkspaceChecks = true;
            settings.Arcanum.Workspaces.EnableFileWrite = true;
            settings.Arcanum.Security.SpellWorkspaceRoots = [workspace];
            settings.Arcanum.Security.PerceptionWorkspaceRoots = [workspace];
            settings.Arcanum.Security.CampaignRoots = [workspace];
            settings.Arcanum.Integrations.WorkspaceChecks.ExecutableCatalog.DotNet.Path = ProjectRepairDotNet;

            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(settings, ConfigurationJsonContext.Default.ArcanumConfigurationFile), ct);

            environment = new(isolation, workspace, PerplexityKey: null);

            reservation.Stop();

            host = PublishedHost.Start(executable, environment, knownMasterApiKey: null);

            string key = await host.WaitForInitialReadyAsync(HostStartupBudget);

            environment.MasterApiKey = key;

            using HttpClient api = CreateApiClient(port, key);

            await AssertNativeAotMetadataAsync(api, directory, configPath, port);

            ProjectRepairPhase("published Native AOT host is ready");

            await RequireProjectRepairWorkspaceCheckAsync(api, workspace, ct);

            Guid session = await CreateRecoverySessionAsync(api);

            PingRequest original = ProjectRepairRequest(workspace, session, model);

            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct);

            using HttpRequestMessage start = RecoveryRequest("/api/intelligence/ping-stream", original, keyed: false);

            using HttpResponseMessage response = await api.SendAsync(start, HttpCompletionOption.ResponseHeadersRead, interrupted.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using StreamReader stream = new(await response.Content.ReadAsStreamAsync(ct));

            await ReadVerifiedProjectRepairBeforeCancellationAsync(stream, workspace, ct);

            ProjectRepairPhase("native project repaired and verified, awaiting human tool");

            await interrupted.CancelAsync();

            response.Dispose();

            SessionRecoveryCheckpoint retained = await WaitForProjectRepairCheckpointAsync(api, session, ct);

            AssertProjectRepairQuestion(retained);

            await StopHostAsync(api, host, HostCleanupAttemptBudget);

            var stopped = await host.CleanupAsync("first isolated project-repair host");

            Assert.Empty(stopped);

            host = PublishedHost.Start(executable, environment, knownMasterApiKey: key);

            await host.WaitForRestartReadyAsync(HostStartupBudget);

            SessionRecoveryCheckpoint recovered = await WaitForProjectRepairCheckpointAsync(api, session, ct);

            Assert.Equal(retained.Id, recovered.Id);

            AssertProjectRepairQuestion(recovered);

            ProjectRepairPhase("published host restarted with the original Session checkpoint");

            using HttpResponseMessage continued = await SendRecoveryPingAsync(api, ProjectRepairContinuation(original, recovered.Id));

            Assert.Equal(HttpStatusCode.OK, continued.StatusCode);

            Assert.Contains(ProjectRepairCompletion, await continued.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            Assert.Null(await ReadRecoveryCheckpointAsync(api, session));

            await VerifyLocalProjectRepairAsync(workspace, packageCache, ct);

            ProjectRepairPhase("published host resumed the original Session and the executable succeeded");

            await using FileStream binary = File.OpenRead(executable);

            string sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(binary, ct));

            Console.WriteLine($"Native project witness: apphostSha256={sha256} model={model} session={session:D} initial=CS0029 verifiedExitCode=0 restarted=true continued=true.");

            await StopHostAsync(api, host, HostCleanupAttemptBudget);
        }
        catch (Exception failure) when (environment is not null)
        {
            throw environment.CreateSanitizedFailure("The isolated published project-repair witness failed.", failure);
        }
        finally
        {
            if (host is not null)
            {
                var cleanup = await host.CleanupAsync("isolated published project-repair host");

                Assert.Empty(cleanup);
            }

            CaptureLocalProjectRepairDisk(workspace);

            isolation.Delete();
        }
    }

    private static async Task RequireProjectRepairWorkspaceCheckAsync(HttpClient api, string workspace, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = JsonContent.Create(new OptionalWorkspaceRequest(workspace), ArcanumJsonContext.Default.OptionalWorkspaceRequest);

            using HttpResponseMessage response = await api.PostAsync("/api/intelligence/arsenal", request, cancellationToken);

            response.EnsureSuccessStatusCode();

            var arsenal = await response.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseWorkspaceArsenalDto, cancellationToken);

            WorkspaceCheckCapabilityDto capability = Assert.IsType<WorkspaceCheckCapabilityDto>(arsenal?.Data?.WorkspaceCheck);

            if (capability.Available)
            {
                ProjectRepairPhase("published host confirmed workspace_check for the project");

                return;
            }

            Assert.True(capability.Reason.Contains("refresh is in progress", StringComparison.Ordinal),
                $"The project repair witness requires workspace_check before inference. The host reported: {capability.Reason}");

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}
