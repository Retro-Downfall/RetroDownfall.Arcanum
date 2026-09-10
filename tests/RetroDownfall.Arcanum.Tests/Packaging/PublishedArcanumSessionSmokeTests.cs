using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Serialization;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Exercises the published product rather than the managed test host. This is the regression gate
/// for packaging modes that can compile cleanly while failing the first real EF query, write, or
/// OpenAI-compatible transport exchange. The provider is an in-process deterministic stub; real
/// model qualification is deliberately local-only.
/// </summary>
public sealed class PublishedArcanumSessionSmokeTests
{
    private const string PublishedExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    private const string ProviderKeyVariable = "ARCANUM_PUBLISHED_SMOKE_PROVIDER_KEY";

    private const string SuccessReceiptVariable = "ARCANUM_PUBLISHED_SMOKE_RECEIPT";

    private const string SuccessReceipt = "published-session-smoke:v1";

    private const string TestCredentialOptInVariable = "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    private const string ExpectedModel = "published-smoke-model";

    private const string ExpectedPrompt =
        "Prove that the published executable can complete its first turn.";

    private const string ExpectedReply = "published-pong";

    private const string ProviderKey = "published-smoke-provider-key";

    private static readonly TimeSpan CliCleanupAttemptBudget = TimeSpan.FromSeconds(5);

    [SkippableFact]
    public async Task Published_executable_creates_initial_session_and_completes_provider_contract_exchange()
    {
        string executable = global::System.Environment.GetEnvironmentVariable(PublishedExecutableVariable)
            ?? string.Empty;

        Skip.If(
            string.IsNullOrWhiteSpace(executable),
            $"Set {PublishedExecutableVariable} to an Arcanum publish apphost to run this gate.");

        Assert.True(File.Exists(executable), $"Published executable does not exist: {executable}");

        AssertNativeAotPublish(executable);

        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create(
            "published-session-smoke");
        string testHome = isolation.TestHome;
        string configurationDirectory = Path.Combine(testHome, ".config", "arcanum");
        string configurationPath = Path.Combine(configurationDirectory, "arcanum.json");
        string? masterApiKey = null;
        Exception? primaryFailure = null;
        FakeOpenAiProvider? provider = null;
        TcpListener? portReservation = null;
        DiagnosticsProcess? process = null;
        ProcessOutputCapture? output = null;

        try
        {
            isolation.Prepare();

            Directory.CreateDirectory(configurationDirectory);

            provider = await FakeOpenAiProvider.StartAsync();
            portReservation = new TcpListener(IPAddress.Loopback, 0);

            portReservation.Start();

            int hostPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

            await WriteConfigurationAsync(
                configurationPath,
                testHome,
                hostPort,
                provider.Endpoint);

            ProcessStartInfo startInfo = CreateHostStartInfo(executable, isolation);

            portReservation.Stop();
            portReservation.Dispose();
            portReservation = null;

            process = DiagnosticsProcess.Start(startInfo)
                ?? throw new InvalidOperationException("The published Arcanum executable did not start.");
            output = new ProcessOutputCapture(process, ProviderKey);
            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            masterApiKey = await output.WaitForReadyAsync(deadline.Token);

            using HttpClient client = new()
            {
                BaseAddress = new Uri($"http://127.0.0.1:{hostPort}/"),
                Timeout = TimeSpan.FromSeconds(45),
            };

            client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, masterApiKey);

            using HttpResponseMessage metadataResponse = await client.GetAsync(
                "api/meta",
                deadline.Token);
            string metadataJson = await metadataResponse.Content.ReadAsStringAsync(deadline.Token);

            Assert.True(
                metadataResponse.IsSuccessStatusCode,
                $"Published metadata returned {(int)metadataResponse.StatusCode}: {metadataJson}");

            using JsonDocument metadata = JsonDocument.Parse(metadataJson);
            JsonElement metadataData = metadata.RootElement.GetProperty("data");

            Assert.True(
                metadataData.GetProperty("nativeAot").GetBoolean(),
                "The published Arcanum process reports that dynamic code is available; it is not Native AOT.");

            Assert.Equal(
                configurationDirectory,
                metadataData.GetProperty("grimoireDirectory").GetString());

            Assert.Equal(
                configurationPath,
                metadataData.GetProperty("configPath").GetString());

            Assert.Equal(hostPort, metadataData.GetProperty("port").GetInt32());

            Assert.False(metadataData.GetProperty("listenAny").GetBoolean());

            CliResult cli = await RunPublishedCliAsync(
                executable,
                isolation,
                masterApiKey,
                TimeSpan.FromSeconds(45),
                "--plain",
                "--print",
                "--no-context",
                "run",
                "--new",
                "--model",
                ExpectedModel,
                "--temperature",
                "0",
                "--max-tokens",
                "32",
                "--unattended",
                ExpectedPrompt);

            Assert.Equal(0, cli.ExitCode);
            Assert.Equal(
                ExpectedReply + global::System.Environment.NewLine,
                cli.StandardOutput);
            Assert.Equal(
                "Mage is generating response..." + global::System.Environment.NewLine,
                cli.StandardError);

            ProviderRequest providerRequest = await provider.WaitForRequestAsync(deadline.Token);

            Assert.Equal(ExpectedModel, providerRequest.Model);
            Assert.Equal($"Bearer {ProviderKey}", providerRequest.Authorization);
            Assert.Equal(ExpectedPrompt, providerRequest.Prompt);
            Assert.True(providerRequest.Streaming);
            Assert.Equal(1, provider.RequestCount);

            using HttpResponseMessage sessionsResponse = await client.GetAsync(
                "api/sessions",
                deadline.Token);
            string sessionsJson = await sessionsResponse.Content.ReadAsStringAsync(deadline.Token);

            Assert.True(
                sessionsResponse.IsSuccessStatusCode,
                $"Published session query returned {(int)sessionsResponse.StatusCode}: {sessionsJson}");

            using JsonDocument sessions = JsonDocument.Parse(sessionsJson);
            JsonElement root = sessions.RootElement;

            Assert.True(root.GetProperty("isSuccess").GetBoolean());
            JsonElement[] summaries = root
                .GetProperty("data")
                .GetProperty("summaries")
                .EnumerateArray()
                .ToArray();
            JsonElement summary = Assert.Single(summaries);
            Guid sessionId = summary.GetProperty("id").GetGuid();

            Assert.Equal(2, summary.GetProperty("entryCount").GetInt32());

            using HttpResponseMessage entriesResponse = await client.GetAsync(
                $"api/sessions/{sessionId:D}/entries?limit=10",
                deadline.Token);
            string entriesJson = await entriesResponse.Content.ReadAsStringAsync(deadline.Token);

            Assert.True(
                entriesResponse.IsSuccessStatusCode,
                $"Published session entries returned {(int)entriesResponse.StatusCode}: {entriesJson}");

            using JsonDocument persistedTurn = JsonDocument.Parse(entriesJson);

            Assert.True(persistedTurn.RootElement.GetProperty("isSuccess").GetBoolean());

            JsonElement[] entries = persistedTurn.RootElement
                .GetProperty("data")
                .EnumerateArray()
                .ToArray();

            Assert.Equal(2, entries.Length);
            Assert.All(
                entries,
                entry => Assert.Equal(sessionId, entry.GetProperty("sessionId").GetGuid()));

            JsonElement userEntry = Assert.Single(
                entries,
                static entry =>
                    string.Equals(
                        entry.GetProperty("role").GetString(),
                        "user",
                        StringComparison.Ordinal));
            JsonElement assistantEntry = Assert.Single(
                entries,
                static entry =>
                    string.Equals(
                        entry.GetProperty("role").GetString(),
                        "assistant",
                        StringComparison.Ordinal));

            Assert.Equal(ExpectedPrompt, userEntry.GetProperty("content").GetString());
            Assert.Equal(ExpectedReply, assistantEntry.GetProperty("content").GetString());

            using HttpResponseMessage quitResponse = await client.PostAsync(
                "api/server/quit",
                content: null,
                deadline.Token);

            Assert.Equal(HttpStatusCode.Accepted, quitResponse.StatusCode);

            await process.WaitForExitAsync(deadline.Token);
            await output.WaitForCompletionAsync(deadline.Token);

            Assert.Equal(0, process.ExitCode);

            AssertNoPlaintextCredentialFiles(
                isolation.ProcessTemporaryDirectory,
                masterApiKey,
                ProviderKey);
        }
        catch (Exception exception)
        {
            string safeDiagnostics = output?.SafeDiagnostics(masterApiKey) ?? string.Empty;
            InvalidOperationException sanitizedFailure =
                LocalOllamaQualificationGuards.CreateSanitizedFailure(
                    "Published Arcanum session/provider-contract smoke failed."
                    + global::System.Environment.NewLine
                    + safeDiagnostics,
                    exception,
                    ProviderKey,
                    masterApiKey);
            primaryFailure = sanitizedFailure;
            throw sanitizedFailure;
        }
        finally
        {
            List<Exception> cleanupFailures = [];

            if (portReservation is not null)
            {
                try
                {
                    portReservation.Stop();
                    portReservation.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Published smoke port-reservation cleanup failed.",
                            cleanupFailure,
                            ProviderKey,
                            masterApiKey));
                }
            }

            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process
                            .WaitForExitAsync()
                            .WaitAsync(TimeSpan.FromSeconds(15));
                    }
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Published smoke process cleanup failed.",
                            cleanupFailure,
                            ProviderKey,
                            masterApiKey));
                }
            }

            if (output is not null)
            {
                try
                {
                    await output.DisposeAsync();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Published smoke output-capture cleanup failed.",
                            cleanupFailure,
                            ProviderKey,
                            masterApiKey));
                }
            }

            if (process is not null)
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Published smoke process-handle cleanup failed.",
                            cleanupFailure,
                            ProviderKey,
                            masterApiKey));
                }
            }

            if (provider is not null)
            {
                try
                {
                    await provider
                        .DisposeAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Published smoke provider-stub cleanup failed.",
                            cleanupFailure,
                            ProviderKey,
                            masterApiKey));
                }
            }

            try
            {
                isolation.Delete();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(
                    LocalOllamaQualificationGuards.CreateSanitizedFailure(
                        "Published smoke isolated temporary-directory cleanup failed.",
                        cleanupFailure,
                        ProviderKey,
                        masterApiKey));
            }

            if (cleanupFailures.Count > 0)
            {
                if (primaryFailure is not null)
                {
                    cleanupFailures.Insert(0, primaryFailure);
                }

                if (cleanupFailures.Count == 1)
                {
                    throw cleanupFailures[0];
                }

                throw new AggregateException(
                    "Published smoke failed and one or more sanitized cleanup failures occurred.",
                    cleanupFailures);
            }
        }

        string? receiptPath = global::System.Environment.GetEnvironmentVariable(
            SuccessReceiptVariable);

        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            await File.WriteAllTextAsync(
                receiptPath,
                SuccessReceipt + global::System.Environment.NewLine);
        }
    }

    private static async Task<CliResult> RunPublishedCliAsync(
        string executable,
        PublishedTestProcessIsolation isolation,
        string masterApiKey,
        TimeSpan timeout,
        params string[] arguments)
    {
        ProcessStartInfo start = CreateCliStartInfo(executable, isolation, arguments);

        DiagnosticsProcess process = DiagnosticsProcess.Start(start)
            ?? throw new InvalidOperationException("The published Arcanum CLI process did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource deadline = new(timeout);
        Exception? primaryFailure = null;
        int? exitCode = null;

        try
        {
            await process.WaitForExitAsync(deadline.Token);

            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            primaryFailure = SanitizeCliFailure(
                $"Published CLI command exceeded {timeout}: {SafeCommand(arguments)}",
                new TimeoutException("The bounded command deadline elapsed."),
                masterApiKey);
        }
        catch (Exception exception)
        {
            primaryFailure = SanitizeCliFailure(
                $"Published CLI command wait failed: {SafeCommand(arguments)}",
                exception,
                masterApiKey);
        }

        CliProcessCleanupResult cleanup = await CleanupCliProcessAsync(
            process,
            standardOutput,
            standardError,
            masterApiKey);

        if (primaryFailure is null && exitCode is not 0)
        {
            primaryFailure = SanitizeCliFailure(
                $"Published CLI command exited {exitCode}: {SafeCommand(arguments)}\n"
                + $"stdout:\n{cleanup.StandardOutput}\nstderr:\n{cleanup.StandardError}",
                new InvalidOperationException("The command returned a non-success exit code."),
                masterApiKey);
        }

        List<Exception> failures = [];

        if (primaryFailure is not null)
        {
            failures.Add(primaryFailure);
        }

        failures.AddRange(cleanup.Failures);

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Published CLI execution failed and one or more sanitized cleanup failures occurred.",
                failures);
        }

        return new CliResult(
            exitCode!.Value,
            cleanup.StandardOutput,
            cleanup.StandardError);
    }

    private static async Task<CliProcessCleanupResult> CleanupCliProcessAsync(
        DiagnosticsProcess process,
        Task<string> standardOutput,
        Task<string> standardError,
        string masterApiKey)
    {
        List<Exception> cleanupFailures = [];
        Exception? terminationFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process
                    .WaitForExitAsync()
                    .WaitAsync(CliCleanupAttemptBudget);

                terminationFailure = null;
                break;
            }
            catch (Exception exception)
            {
                terminationFailure = exception;
            }
        }

        if (terminationFailure is not null)
        {
            cleanupFailures.Add(SanitizeCliFailure(
                "Published CLI process termination did not complete after three bounded attempts.",
                terminationFailure,
                masterApiKey));
        }

        Exception? drainFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await Task.WhenAll(standardOutput, standardError)
                    .WaitAsync(CliCleanupAttemptBudget);

                drainFailure = null;
                break;
            }
            catch (Exception exception)
            {
                drainFailure = exception;
            }
        }

        if (drainFailure is not null)
        {
            cleanupFailures.Add(SanitizeCliFailure(
                "Published CLI output drain did not complete after three bounded attempts.",
                drainFailure,
                masterApiKey));
        }

        string output = standardOutput.IsCompletedSuccessfully
            ? LocalOllamaQualificationGuards.Redact(
                standardOutput.Result,
                ProviderKey,
                masterApiKey)
            : string.Empty;
        string error = standardError.IsCompletedSuccessfully
            ? LocalOllamaQualificationGuards.Redact(
                standardError.Result,
                ProviderKey,
                masterApiKey)
            : string.Empty;

        try
        {
            process.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(SanitizeCliFailure(
                "Published CLI process-handle cleanup failed.",
                exception,
                masterApiKey));
        }

        return new CliProcessCleanupResult(output, error, cleanupFailures);
    }

    private static InvalidOperationException SanitizeCliFailure(
        string context,
        Exception failure,
        string masterApiKey) =>
        LocalOllamaQualificationGuards.CreateSanitizedFailure(
            LocalOllamaQualificationGuards.Redact(context, ProviderKey, masterApiKey),
            failure,
            ProviderKey,
            masterApiKey);

    private static string SafeCommand(IEnumerable<string> arguments) =>
        "arcanum " + string.Join(' ', arguments.Select(static argument =>
            argument.Contains(' ')
                ? "<prompt>"
                : argument));

    private static async Task WriteConfigurationAsync(
        string path,
        string testHome,
        int hostPort,
        Uri providerEndpoint)
    {
        ArcanumConfigurationFile configuration = new()
        {
            Arcanum = new ArcanumSettings
            {
                Host = new HostSettings
                {
                    Port = hostPort,
                },
                DefaultModel = ExpectedModel,
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "published-smoke",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = providerEndpoint.AbsoluteUri,
                        CredentialEnvironmentVariable = ProviderKeyVariable,
                        Models = [new ModelEntry(ExpectedModel, SupportsTools: false)],
                        ContextWindowLimit = 8192,
                    },
                ],
                Features = new FeatureSettings
                {
                    Apprentices = false,
                    Lexicon = false,
                    ArchiveSearch = false,
                    Metrics = false,
                    Scrying = false,
                    Attachments = false,
                    Reasoning = false,
                    WorkspaceChecks = false,
                    Annals = false,
                },
                Workspaces = new WorkspaceSettings
                {
                    DefaultRoot = testHome,
                },
            },
        };

        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        await JsonSerializer.SerializeAsync(
            stream,
            configuration,
            ConfigurationJsonContext.Default.ArcanumConfigurationFile);
    }

    private static ProcessStartInfo CreateHostStartInfo(
        string executable,
        PublishedTestProcessIsolation isolation) =>
        CreateProcessStartInfo(executable, isolation, ["serve", "--plain"]);

    private static ProcessStartInfo CreateCliStartInfo(
        string executable,
        PublishedTestProcessIsolation isolation,
        IEnumerable<string> arguments) =>
        CreateProcessStartInfo(executable, isolation, arguments);

    private static ProcessStartInfo CreateProcessStartInfo(
        string executable,
        PublishedTestProcessIsolation isolation,
        IEnumerable<string> arguments)
    {
        string testHome = isolation.TestHome;

        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = testHome,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        string? systemRoot = global::System.Environment.GetEnvironmentVariable("SystemRoot");
        string? comSpec = global::System.Environment.GetEnvironmentVariable("ComSpec");
        string? path = global::System.Environment.GetEnvironmentVariable("PATH");

        start.Environment.Clear();
        CopyIfPresent(start, "SystemRoot", systemRoot);
        CopyIfPresent(start, "ComSpec", comSpec);
        CopyIfPresent(start, "PATH", path);
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["ARCANUM_TEST_HOME"] = testHome;
        start.Environment[TestCredentialOptInVariable] = "1";
        start.Environment[ProviderKeyVariable] = ProviderKey;
        start.Environment["ARCANUM_NO_AUTO_SERVE"] = "1";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["HOME"] = testHome;
        start.Environment["USERPROFILE"] = testHome;
        start.Environment["APPDATA"] = Path.Combine(testHome, "AppData", "Roaming");
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(testHome, ".dotnet");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(testHome, ".config");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(testHome, ".local", "share");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(testHome, ".cache");

        isolation.ApplyTemporaryEnvironment(start);

        return start;
    }

    private static void CopyIfPresent(
        ProcessStartInfo start,
        string name,
        string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            start.Environment[name] = value;
        }
    }

    private static void AssertNoPlaintextCredentialFiles(
        string root,
        params string?[] credentials)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] contents = File.ReadAllBytes(path);

            try
            {
                foreach (string? credential in credentials)
                {
                    if (string.IsNullOrEmpty(credential))
                    {
                        continue;
                    }

                    byte[] encoded = Encoding.UTF8.GetBytes(credential);

                    try
                    {
                        Assert.False(
                            contents.AsSpan().IndexOf(encoded) >= 0,
                            $"A plaintext credential was persisted to {path}.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encoded);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contents);
            }
        }
    }

    private static void AssertNativeAotPublish(string executable)
    {
        Assert.True(
            LocalOllamaQualificationGuards.IsNativeBinary(executable),
            $"Published executable must be a direct non-link Mach-O or PE Native AOT binary: {executable}");

        string publishDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("Published executable has no parent directory.");
        string assemblyPath = Path.Combine(
            publishDirectory,
            "RetroDownfall.Arcanum.Cli.dll");

        Assert.False(
            File.Exists(assemblyPath),
            $"Native AOT must not ship the managed CLI assembly: {assemblyPath}");

        Assert.DoesNotContain(
            Directory.EnumerateFiles(publishDirectory),
            static path => Path.GetFileName(path).Contains("hostfxr", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            Directory.EnumerateFiles(publishDirectory),
            static path => Path.GetFileName(path).Contains("hostpolicy", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record CliProcessCleanupResult(
        string StandardOutput,
        string StandardError,
        IReadOnlyList<Exception> Failures);

    private sealed class FakeOpenAiProvider : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private readonly TaskCompletionSource<ProviderRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _requestCount;

        private FakeOpenAiProvider(
            WebApplication application,
            Uri endpoint)
        {
            _application = application;
            Endpoint = endpoint;
        }

        public Uri Endpoint { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static async Task<FakeOpenAiProvider> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions
                {
                    EnvironmentName = "Testing",
                });

            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(
                static options => options.Listen(IPAddress.Loopback, 0));

            WebApplication application = builder.Build();
            FakeOpenAiProvider? provider = null;

            application.MapPost("/v1/chat/completions", async context =>
            {
                using JsonDocument request = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);
                string model = request.RootElement.GetProperty("model").GetString() ?? string.Empty;
                string authorization = context.Request.Headers.Authorization.ToString();
                string prompt = ReadUserPrompt(request.RootElement);
                bool streaming = request.RootElement.GetProperty("stream").GetBoolean();

                _ = Interlocked.Increment(ref provider!._requestCount);
                _ = provider._request.TrySetResult(
                    new ProviderRequest(model, authorization, prompt, streaming));

                if (!streaming)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;

                    await context.Response.WriteAsync(
                        "The published CLI must use the provider streaming contract.",
                        context.RequestAborted);

                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";

                await context.Response.WriteAsync(
                    "data: {\"id\":\"chatcmpl-published-smoke\",\"object\":\"chat.completion.chunk\","
                    + "\"created\":0,\"model\":\"published-smoke-model\",\"choices\":[{\"index\":0,"
                    + "\"delta\":{\"role\":\"assistant\",\"content\":\"published-pong\"},"
                    + "\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: [DONE]\n\n",
                    context.RequestAborted);
            });

            await application.StartAsync();

            IServer server = application.Services.GetRequiredService<IServer>();
            IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The fake provider did not expose a listen address.");
            Uri endpoint = new(new Uri(addresses.Addresses.Single()), "v1");

            provider = new FakeOpenAiProvider(application, endpoint);

            return provider;
        }

        public Task<ProviderRequest> WaitForRequestAsync(CancellationToken cancellationToken) =>
            _request.Task.WaitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }

        private static string ReadUserPrompt(JsonElement request)
        {
            JsonElement user = request
                .GetProperty("messages")
                .EnumerateArray()
                .Last(message => string.Equals(
                    message.GetProperty("role").GetString(),
                    "user",
                    StringComparison.Ordinal));
            JsonElement content = user.GetProperty("content");

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Concat(
                    content
                        .EnumerateArray()
                        .Where(static part => string.Equals(
                            part.GetProperty("type").GetString(),
                            "text",
                            StringComparison.Ordinal))
                        .Select(static part => part.GetProperty("text").GetString()));
            }

            return string.Empty;
        }
    }

    private sealed record ProviderRequest(
        string Model,
        string Authorization,
        string Prompt,
        bool Streaming);

    private sealed class ProcessOutputCapture : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _safeLines = new();

        private readonly string _knownProviderKey;

        private readonly TaskCompletionSource<string> _masterApiKey =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Task _standardOutput;

        private readonly Task _standardError;

        public ProcessOutputCapture(
            DiagnosticsProcess process,
            string knownProviderKey)
        {
            _knownProviderKey = knownProviderKey;
            _standardOutput = PumpAsync(process.StandardOutput, observeStartup: true);
            _standardError = PumpAsync(process.StandardError, observeStartup: false);
        }

        public async Task<string> WaitForReadyAsync(CancellationToken cancellationToken)
        {
            string masterApiKey = await _masterApiKey.Task.WaitAsync(cancellationToken);

            await _ready.Task.WaitAsync(cancellationToken);

            return masterApiKey;
        }

        public Task WaitForCompletionAsync(CancellationToken cancellationToken) =>
            Task.WhenAll(_standardOutput, _standardError).WaitAsync(cancellationToken);

        public string SafeDiagnostics(string? masterApiKey)
        {
            string joined = string.Join(global::System.Environment.NewLine, _safeLines);

            return LocalOllamaQualificationGuards.Redact(
                joined,
                _knownProviderKey,
                masterApiKey);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.WhenAll(_standardOutput, _standardError)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        private async Task PumpAsync(
            StreamReader reader,
            bool observeStartup)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (observeStartup && TryReadMasterApiKey(line, out string? key))
                {
                    _masterApiKey.TrySetResult(key);
                    Enqueue(LocalOllamaQualificationGuards.Redact(
                        line,
                        _knownProviderKey,
                        key));
                }
                else
                {
                    Enqueue(line);
                }

                if (observeStartup
                    && line.Contains("Listening on http://127.0.0.1:", StringComparison.Ordinal))
                {
                    _ready.TrySetResult();
                }
            }

            if (observeStartup)
            {
                _masterApiKey.TrySetException(
                    new InvalidOperationException("Published host exited before generating its master API key."));
                _ready.TrySetException(
                    new InvalidOperationException("Published host exited before reporting readiness."));
            }
        }

        private void Enqueue(string line)
        {
            _safeLines.Enqueue(line);

            while (_safeLines.Count > 200)
            {
                _safeLines.TryDequeue(out _);
            }
        }

        private static bool TryReadMasterApiKey(
            string line,
            out string key)
        {
            key = line.Trim();

            try
            {
                byte[] decoded = Convert.FromBase64String(key);

                bool isMasterApiKey = decoded.Length == 32;

                CryptographicOperations.ZeroMemory(decoded);

                return isMasterApiKey;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
