using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Packaging;
using RetroDownfall.Arcanum.Tests.Support;
using OperatingProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    internal const string ProjectRepairOptIn = "ARCANUM_TEST_LOCAL_PROJECT_REPAIR_MODEL";

    internal static string ProjectRepairDotNet
    {
        get
        {
            string? path = System.Environment.GetEnvironmentVariable("ARCANUM_TEST_LOCAL_PROJECT_REPAIR_DOTNET");

            if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new InvalidOperationException("Set ARCANUM_TEST_LOCAL_PROJECT_REPAIR_DOTNET to the absolute path of the trusted .NET host.");
            }

            return path;
        }
    }

    private const string ProjectRepairQuestion = "Can I finish after the verified build?";

    private const string ProjectRepairConfirmation = "Yes, finish and make no further edits.";

    internal const string ProjectRepairCompletion = "PROJECT VERIFIED";

    private const string ProjectRepairSource = """
        static int Answer() => "obsolete";
        return Answer() == 42 ? 0 : 2;
        """;

    [SkippableFact]
    [Trait("Category", "LocalOllama")]
    public async Task Resident_local_model_repairs_a_failing_project_and_continues_the_same_session()
    {
        string? model = System.Environment.GetEnvironmentVariable(ProjectRepairOptIn);

        Skip.If(string.IsNullOrWhiteSpace(model), $"Set {ProjectRepairOptIn} to the resident tool-capable Ollama model.");

        Assert.True(OperatingSystem.IsMacOS(), "The actual workspace check requires the macOS Seatbelt runtime.");

        Assert.False(LocalOllamaQualificationGuards.IsGitHubActions(System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS")));

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        await using RestartableArcanumProfileFixture profile = new();

        string workspace = Directory.CreateTempSubdirectory("arcanum-live-project-repair-").FullName;

        string packageCache = Path.Combine(profile.TempHome, ".nuget", "packages");

        const string runtime = "project-repair-witness";
        const string endpoint = "http://127.0.0.1:11434";

        CancellationToken ct = watchdog.Token;

        try
        {
            await PrepareLocalProjectRepairAsync(workspace, packageCache, ct);

            ProjectRepairPhase("independent check reproduced CS0029");

            TaskCompletionSource beforeInference = new(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource releaseInference = new(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource beforeWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource gapWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

            AsyncLocal<TaskCompletionSource?> pendingMaintenance = new();

            using MeterListener meter = new();

            meter.InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork"
                    && instrument.Name == "arcanum.provider_work.waiting") observer.EnableMeasurementEvents(instrument);
            };

            meter.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "kind" && Equals(tag.Value, "background")) pendingMaintenance.Value?.TrySetResult();
            });

            meter.Start();

            ConcurrentQueue<ProjectRepairSend> sends = new();

            ProviderSettings provider = DefaultProvider() with
            {
                Name = runtime,
                Endpoint = endpoint + "/v1/",
                LocalRuntimeId = runtime,
                ContextWindowLimit = 262_144,
                Models = [new ModelEntry(model!, SupportsTools: true)],
            };

            await using var host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), profile, configure: settings =>
            {
                settings.DefaultModel = model;
                settings.FastModel = model;
                settings.Providers = [provider];
                settings.LocalRuntimes = [new() { Id = runtime, Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = endpoint }];
                settings.Features = DefaultSettings().Features with
                {
                    Embeddings = false, Saga = false, Covenant = false, Reasoning = false, WorkspaceChecks = true,
                };
                settings.Workspaces = new() { DefaultRoot = workspace, EnableFileWrite = true };
                settings.Security.SpellWorkspaceRoots = [workspace];
                settings.Security.PerceptionWorkspaceRoots = [workspace];
                settings.Security.CampaignRoots = [workspace];
                settings.Integrations.WorkspaceChecks.ExecutableCatalog.DotNet.Path = ProjectRepairDotNet;
            });

            Action<IServiceCollection>? previous = host.ServiceOverrides;

            host.ServiceOverrides = services =>
            {
                previous!(services);
                services.RemoveAll<IArcanumIntelligenceProvider>();
                services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
                services.AddHttpClient("OpenAiCompatibleProvider").AddHttpMessageHandler(() => new ProjectRepairSendObserver(sends));

                ServiceDescriptor original = services.Last(item => item.ServiceType == typeof(IChatClientFactory));
                services.Remove(original);
                services.AddSingleton<IChatClientFactory>(sp => new RecoveryWitnessFactory(
                    (IChatClientFactory)original.ImplementationFactory!(sp), beforeInference, releaseInference));
            };

            using HttpClient client = host.CreateAuthenticatedClient();

            var liveSettings = host.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue;

            WorkspaceCheckRuntime check = new(liveSettings.ResolveWorkspaceChecks(), host.Services.GetRequiredService<IServiceScopeFactory>());

            WorkspaceCheckToolResultEnvelope initialCheck = await check.RunAsync(
                new(workspace, WorkspaceCheckCatalogDefaults.DotNetBuildProfileId, new Dictionary<string, string>()), ct);

            ProjectRepairPhase($"workspace check preflight status {initialCheck.Status} code {initialCheck.Code} exit {initialCheck.ExitCode}");

            Assert.True(initialCheck.Status == "failed" && initialCheck.Diagnostics.Any(diagnostic => diagnostic.Code == "CS0029"),
                $"Expected the production compiler's CS0029. Actual status {initialCheck.Status}/{initialCheck.Code}. {initialCheck.Message}");

            Guid session = await CreateRecoverySessionAsync(client);

            ProjectRepairPhase("isolated Session created");

            async Task<Result<string>> MaintenanceAsync(string marker, TaskCompletionSource waiting)
            {
                pendingMaintenance.Value = waiting;
                try
                {
                    await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

                    return await scope.ServiceProvider.GetRequiredService<ITapestrySummarizer>().SummarizeAsync(
                        new TapestrySummaryRequest(TapestryScopeKind.Workspace, marker, 1, [$"Workspace summary source {marker}."]), ct);
                }
                finally
                {
                    pendingMaintenance.Value = null;
                }
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

            PingRequest initial = ProjectRepairRequest(workspace, session, model!);

            using HttpRequestMessage turn = RecoveryRequest("/api/intelligence/ping-stream", initial, keyed: false);

            Task<HttpResponseMessage> responseTask = client.SendAsync(turn, HttpCompletionOption.ResponseHeadersRead, cancel.Token);

            Task<Result<string>>? before = null;

            Task<Result<string>>? gap = null;

            try
            {
                if (await Task.WhenAny(beforeInference.Task, responseTask).WaitAsync(ct) == responseTask
                    && !(await responseTask).IsSuccessStatusCode)
                {
                    Assert.Equal(HttpStatusCode.OK, (await responseTask).StatusCode);
                }

                await beforeInference.Task.WaitAsync(ct);

                ProjectRepairPhase("foreground owner before first inference");

                before = MaintenanceAsync("project-maintenance-before", beforeWaiting);

                await beforeWaiting.Task.WaitAsync(ct);

                ProjectRepairPhase("first competing maintenance waiting");

                Assert.Empty(sends);

                releaseInference.TrySetResult();

                using HttpResponseMessage streaming = await responseTask.WaitAsync(ct);

                Assert.Equal(HttpStatusCode.OK, streaming.StatusCode);

                using StreamReader reader = new(await streaming.Content.ReadAsStreamAsync(ct));

                await ReadVerifiedProjectRepairBeforeCancellationAsync(reader, workspace, ct);

                ProjectRepairPhase("model repaired and verified project, awaiting human tool");

                gap = MaintenanceAsync("project-maintenance-gap", gapWaiting);

                await gapWaiting.Task.WaitAsync(ct);

                ProjectRepairPhase("second competing maintenance waiting in tool gap");

                ProjectRepairSend[] paused = sends.ToArray();

                Assert.All(paused, send => Assert.Equal("repair", send.Kind));

                Assert.True(paused.Length >= 3);

                await cancel.CancelAsync();

                streaming.Dispose();

                SessionRecoveryCheckpoint checkpoint = await WaitForProjectRepairCheckpointAsync(client, session, ct);

                ProjectRepairPhase($"checkpoint {checkpoint.Kind} next {checkpoint.NextAction} pending {checkpoint.InvocationId} lastVerified {checkpoint.LastVerifiedInvocationId}");

                if (checkpoint.InvocationId is { } pending)
                {
                    var evidence = await host.Services.GetRequiredService<IToolInvocationStore>().ReadAsync(session, pending, ct);

                    Assert.True(evidence.IsSuccess, evidence.IsFailure ? evidence.Error.Message : null);

                    ToolInvocationEvidence received = Assert.IsType<ToolInvocationEvidence>(evidence.Value);

                    ProjectRepairPhase($"pending receipt {received.ReceiptId:D} round {received.Round} call {received.Call} state {received.State}");
                }

                string? cancelledTerminal = null;

                while (cancelledTerminal is null)
                {
                    cancelledTerminal = await ReadRecoveryTerminalErrorAsync(host.Services, checkpoint.Id, ct);

                    if (cancelledTerminal is null) await Task.Delay(25, ct);
                }

                ProjectRepairPhase($"source terminal {cancelledTerminal}");

                Assert.Equal(ErrorCodes.Hub.SessionRecoveryCancelled, cancelledTerminal);

                AssertProjectRepairQuestion(checkpoint);

                ProjectRepairPhase("cancellation retained original Session checkpoint");

                Result<string>[] maintenance = await Task.WhenAll(before, gap).WaitAsync(ct);

                Assert.All(maintenance, result => Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null));

                ProjectRepairSend[] afterCancellation = sends.ToArray();

                Assert.Equal(paused, afterCancellation[..paused.Length]);

                Assert.Equal(new[] { "before", "gap" }, afterCancellation[paused.Length..].Select(send => send.Kind).Order(StringComparer.Ordinal));

                using HttpResponseMessage continued = await SendRecoveryPingAsync(client, ProjectRepairContinuation(initial, checkpoint.Id));

                Assert.Equal(HttpStatusCode.OK, continued.StatusCode);

                Assert.Contains(ProjectRepairCompletion, await continued.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

                Assert.Null(await ReadRecoveryCheckpointAsync(client, session));

                Assert.Equal(cancelledTerminal, await ReadRecoveryTerminalErrorAsync(host.Services, checkpoint.Id, ct));

                Assert.All(sends, send => Assert.Equal(model, send.Model));

                Assert.All(sends.Where(send => send.Kind == "repair"), send => Assert.False(send.HasResponseTokenLimit));

                Assert.Contains(sends.ToArray()[afterCancellation.Length..],
                    send => send.Kind == "repair" && send.HasRetainedCheckFailure && send.HasConfirmation);

                await VerifyLocalProjectRepairAsync(workspace, packageCache, ct);

                ProjectRepairPhase("same Session continued and independent executable verified");

                Console.WriteLine($"Live project witness: model={model} session={session:D} initial=CS0029 verifiedExitCode=0 repairProviderRounds={paused.Length} continued=true maintenanceBeforeAndGapDeferred=true.");
            }
            finally
            {
                releaseInference.TrySetResult();

                await watchdog.CancelAsync();

                try { using HttpResponseMessage response = await responseTask; }
                catch (OperationCanceledException) { }

                foreach (Task task in new Task?[] { before, gap }.OfType<Task>())
                {
                    try { await task; }
                    catch (OperationCanceledException) { }
                }
            }
        }
        finally
        {
            CaptureLocalProjectRepairDisk(workspace);

            Directory.Delete(workspace, recursive: true);
        }
    }

    internal static async Task PrepareLocalProjectRepairAsync(string workspace, string packageCache, CancellationToken ct)
    {
        Directory.CreateDirectory(packageCache);

        string sourcePath = Path.Combine(workspace, "Program.cs");

        var selectedSdk = await RunProjectRepairDotNetAsync(workspace, packageCache, ct, "--version");

        Assert.True(selectedSdk.ExitCode == 0, selectedSdk.Output);

        string sdk = selectedSdk.Output.Trim();

        Assert.True(Version.TryParse(sdk, out Version? version), "Expected the installed root-owned stable SDK version.");

        await File.WriteAllTextAsync(sourcePath, ProjectRepairSource, ct);

        await File.WriteAllTextAsync(Path.Combine(workspace, "Probe.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net{version.Major}.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(workspace, "global.json"), $$$"""{"sdk":{"version":"{{{sdk}}}","rollForward":"disable"}}""", ct);

        await File.WriteAllTextAsync(Path.Combine(workspace, "NuGet.Config"), "<configuration><packageSources><clear/></packageSources></configuration>", ct);

        var restore = await RunProjectRepairDotNetAsync(workspace, packageCache, ct, "restore", "Probe.csproj",
            "--configfile", "NuGet.Config", "--disable-build-servers");

        Assert.True(restore.ExitCode == 0, restore.Output);

        var rejected = await RunProjectRepairDotNetAsync(workspace, packageCache, ct, "build", "Probe.csproj",
            "--no-restore", "--disable-build-servers");

        Assert.NotEqual(0, rejected.ExitCode);

        Assert.Contains("CS0029", rejected.Output, StringComparison.Ordinal);
    }

    internal static PingRequest ProjectRepairRequest(string workspace, Guid session, string model) =>
        new(
            "Repair this workspace's tiny .NET project so Answer returns integer 42. First call workspace_check with profile dotnet-build "
            + "to observe its current failure. Read the relative path Program.cs directly, use edit_file to replace only the incorrect expression, then rerun "
            + $"workspace_check with profile dotnet-build to verify success. Then call ask_human with the exact question '{ProjectRepairQuestion}' "
            + "and wait. The tool paths are relative to this workspace. Do not inspect or edit anything other than Program.cs.",
            Model: model, WorkingDirectory: workspace, SessionId: session, SkipSpellRouting: true, Temperature: 0,
            ParallelToolCalls: false);

    internal static PingRequest ProjectRepairContinuation(PingRequest original, Guid checkpoint) => original with
    {
        Prompt = ProjectRepairConfirmation + $" Summarize the retained repair and reply with {ProjectRepairCompletion}.",
        RecoveryCheckpointId = checkpoint,
    };

    internal static async Task ReadVerifiedProjectRepairBeforeCancellationAsync(StreamReader reader, string workspace, CancellationToken ct)
    {
        string sourcePath = Path.Combine(workspace, "Program.cs");

        List<IntelligenceEvent> seen = [];

        bool providerProgress = false;

        IntelligenceEvent asked = await ReadRecoveryFrameAsync(reader, frame =>
        {
            if (!providerProgress && frame.Type is IntelligenceEventType.Token or IntelligenceEventType.Reasoning)
            {
                providerProgress = true;

                ProjectRepairPhase("first native provider progress frame");
            }

            if (frame.Type is IntelligenceEventType.ToolCall or IntelligenceEventType.ToolResult)
            {
                seen.Add(frame);

                ProjectRepairPhase($"{frame.Type} {frame.ToolCall?.Name}");

                if (frame is { Type: IntelligenceEventType.ToolResult, ToolCall.Name: "workspace_check", Data: { } data }
                    && data.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    ProjectRepairArtifact(".check-results.txt", data + System.Environment.NewLine);

                    using JsonDocument verdict = JsonDocument.Parse(data);

                    if (verdict.RootElement.TryGetProperty("status", out JsonElement status))
                    {
                        string exit = verdict.RootElement.TryGetProperty("exitCode", out JsonElement exitCode)
                            ? exitCode.ToString() : "absent";

                        string code = verdict.RootElement.TryGetProperty("code", out JsonElement codeValue) && codeValue.ValueKind == JsonValueKind.String
                            ? codeValue.GetString()! : "none";

                        ProjectRepairPhase($"workspace check status {status.GetString()} code {code} exit {exit} phase {frame.FailureEvidence?.Phase} effect {frame.FailureEvidence?.Effect} next {frame.FailureEvidence?.NextAction}");
                    }
                }
            }

            return frame.Type is IntelligenceEventType.Error or IntelligenceEventType.Result
                || frame is { Type: IntelligenceEventType.ToolCall, ToolCall.Name: "ask_human" };
        }, ct);

        if (asked.Type == IntelligenceEventType.Error)
        {
            ProjectRepairPhase($"terminal {asked.Data} phase {asked.FailureEvidence?.Phase} effect {asked.FailureEvidence?.Effect} next {asked.FailureEvidence?.NextAction}");
        }

        Assert.True(asked is { Type: IntelligenceEventType.ToolCall, ToolCall.Name: "ask_human" },
            $"{asked.Type}/{asked.Data}/{asked.FailureEvidence?.Phase}/{asked.FailureEvidence?.Effect}/{asked.FailureEvidence?.NextAction}. "
            + $"Observed tools: {string.Join(", ", seen.Select(frame => frame.ToolCall?.Name))}.");

        using JsonDocument arguments = JsonDocument.Parse(asked.ToolCall!.ArgumentsJson);

        Assert.Equal(ProjectRepairQuestion, arguments.RootElement.GetProperty("question").GetString());

        Assert.False(string.IsNullOrWhiteSpace(arguments.RootElement.GetProperty("promptId").GetString()));

        var checkResults = seen.Where(frame => frame is { Type: IntelligenceEventType.ToolResult, ToolCall.Name: "workspace_check" }).ToArray();

        Assert.True(checkResults.Length >= 2);

        Assert.Contains("CS0029", checkResults[0].Data ?? checkResults[0].Message, StringComparison.Ordinal);

        Assert.NotNull(checkResults[0].FailureEvidence);

        Assert.Null(checkResults[^1].FailureEvidence);

        Assert.Contains(seen, static frame => frame is { Type: IntelligenceEventType.ToolResult, ToolCall.Name: "edit_file" });

        foreach (IntelligenceEvent result in seen.Where(frame => frame is { Type: IntelligenceEventType.ToolResult, ToolCall: not null }))
            Assert.Contains(seen, call => call is { Type: IntelligenceEventType.ToolCall } && call.ToolCall?.CallId == result.ToolCall!.CallId);

        Assert.Equal(ProjectRepairSource.Replace("\"obsolete\"", "42", StringComparison.Ordinal),
            await File.ReadAllTextAsync(sourcePath, ct));
    }

    internal static async Task<SessionRecoveryCheckpoint> WaitForProjectRepairCheckpointAsync(HttpClient client, Guid session, CancellationToken ct)
    {
        SessionRecoveryCheckpoint? checkpoint = null;

        while (checkpoint is null)
        {
            checkpoint = await ReadRecoveryCheckpointAsync(client, session);

            if (checkpoint is null) await Task.Delay(25, ct);
        }

        return checkpoint;
    }

    internal static void AssertProjectRepairQuestion(SessionRecoveryCheckpoint checkpoint)
    {
        Assert.Equal(SessionRecoveryKind.AwaitingInput, checkpoint.Kind);

        Assert.Equal(ProjectRepairQuestion, checkpoint.Question);

        Assert.Equal(TurnRecoveryAction.ProvideInput, checkpoint.NextAction);
    }

    internal static async Task VerifyLocalProjectRepairAsync(string workspace, string packageCache, CancellationToken ct)
    {
        string sourcePath = Path.Combine(workspace, "Program.cs");

        var verified = await RunProjectRepairDotNetAsync(workspace, packageCache, ct, "run", "--project", "Probe.csproj",
            "--no-restore", "--disable-build-servers");

        Assert.True(verified.ExitCode == 0, verified.Output);

        Assert.Equal(ProjectRepairSource.Replace("\"obsolete\"", "42", StringComparison.Ordinal),
            await File.ReadAllTextAsync(sourcePath, ct));
    }

    private static readonly object ProjectRepairArtifactGate = new();

    internal static void ProjectRepairPhase(string message) =>
        ProjectRepairArtifact("", $"{DateTimeOffset.UtcNow:O} {message}{System.Environment.NewLine}");

    internal static void CaptureLocalProjectRepairDisk(string workspace)
    {
        string source = Path.Combine(workspace, "Program.cs");

        if (File.Exists(source))
        {
            ProjectRepairArtifact(".initial.cs.txt", ProjectRepairSource, append: false);
            ProjectRepairArtifact(".final.cs.txt", File.ReadAllText(source), append: false);
        }
    }

    private static void ProjectRepairArtifact(string suffix, string text, bool append = true)
    {
        string? path = System.Environment.GetEnvironmentVariable("ARCANUM_TEST_LOCAL_PROJECT_REPAIR_PHASES");

        if (string.IsNullOrWhiteSpace(path)) return;

        lock (ProjectRepairArtifactGate)
        {
            if (append) File.AppendAllText(path + suffix, text);
            else File.WriteAllText(path + suffix, text);
        }
    }

    private sealed record ProjectRepairSend(string Kind, string? Model, bool HasRetainedCheckFailure, bool HasConfirmation,
        bool HasResponseTokenLimit);

    private sealed class ProjectRepairSendObserver(ConcurrentQueue<ProjectRepairSend> sends) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) is true
                && request.Content is { } content)
            {
                string body = await content.ReadAsStringAsync(ct);

                using JsonDocument json = JsonDocument.Parse(body);

                string kind = body.Contains("project-maintenance-before", StringComparison.Ordinal) ? "before"
                    : body.Contains("project-maintenance-gap", StringComparison.Ordinal) ? "gap" : "repair";

                bool hasResponseTokenLimit = json.RootElement.TryGetProperty("max_tokens", out JsonElement max)
                    && max.ValueKind == JsonValueKind.Number;

                ProjectRepairPhase($"provider request {kind}, response token allocation {(hasResponseTokenLimit ? "present" : "omitted")}");

                sends.Enqueue(new(kind, json.RootElement.GetProperty("model").GetString(),
                    body.Contains("CS0029", StringComparison.Ordinal),
                    body.Contains(ProjectRepairConfirmation, StringComparison.Ordinal),
                    hasResponseTokenLimit));
            }

            HttpResponseMessage response = await base.SendAsync(request, ct);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) is true)
            {
                ProjectRepairPhase($"provider response headers ({(int)response.StatusCode})");
            }

            return response;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProjectRepairDotNetAsync(string workspace,
        string packageCache, CancellationToken ct, params string[] arguments)
    {
        ProcessStartInfo start = new(ProjectRepairDotNet)
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        string setup = Path.Combine(workspace, ".fixture-setup");
        string home = Path.Combine(setup, "home");
        string cliHome = Path.Combine(setup, "cli-home");
        string cache = Path.Combine(setup, "cache");
        string temp = Path.Combine(setup, "temp");

        foreach (string directory in new[] { home, cliHome, cache, temp }) Directory.CreateDirectory(directory);

        WorkspaceCheckEnvironmentBuilder.Apply(start, new(Path.GetDirectoryName(ProjectRepairDotNet)!, home, cliHome, cache,
            temp, packageCache, ProjectRepairDotNet));

        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using OperatingProcess process = OperatingProcess.Start(start) ?? throw new InvalidOperationException("Could not start the check process.");

        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);

        Task<string> error = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);

            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync();
            }
        }
    }
}
