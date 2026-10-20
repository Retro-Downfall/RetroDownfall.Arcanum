using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Secrets.Security;
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
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Exercises the published product rather than the managed test host. This is the regression gate
/// for packaging modes that can compile cleanly while failing the first real EF query, write, or
/// OpenAI-compatible transport exchange. The provider is an in-process deterministic stub; real
/// model qualification is deliberately local-only.
/// </summary>
[Collection("ApiHost")]
public sealed class PublishedArcanumSessionSmokeTests
{
    private const string PublishedExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    private const string ProviderKeyVariable = "ARCANUM_PUBLISHED_SMOKE_PROVIDER_KEY";

    private const string SuccessReceiptVariable = "ARCANUM_PUBLISHED_SMOKE_RECEIPT";

    private const string SuccessReceipt = "published-session-smoke:v1";

    private const string TestCredentialOptInVariable = "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    internal const string ExpectedModel = "published-smoke-model";

    private const string ExpectedPrompt =
        "Prove that the published executable can complete its first turn.";

    private const string ExpectedReply = "published-pong";

    internal const string ProviderKey = "published-smoke-provider-key";

    internal const string EvaluationMemory = "published-evaluation-memory-secret";

    internal const string EvaluationAnswer = "memory-known-answer";

    internal const string DelegationParentPrompt = "published-delegation-parent";

    internal const string DelegationChildPrompt = "published-delegation-child";

    internal const string DelegationSelected = "published-delegation-selected-secret";

    internal const string DelegationUnselected = "published-delegation-unselected-secret";

    internal const string DelegationHistory = "published-delegation-history-secret";

    internal const string DelegationChildAnswer = "published-delegation-child-answer " + DelegationSelected;

    internal const string DelegationFinal = "published-delegation-final";

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

            await ExerciseMemoryEvaluationAsync(client, provider, priorCalls: 1, deadline.Token);

            await ExerciseMemoryDelegationAsync(client, provider, priorCalls: 7, deadline.Token);

            await AssertCampaignRegistrationFoldsUnicodeNormalizationAsync(
                client,
                testHome,
                deadline.Token);

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

    /// <summary>
    /// The published executable runs with invariant globalization, where <see cref="string.Normalize()"/> hands a non-ASCII string back
    /// unchanged and says nothing, so the equivalence of two spellings of an accented directory name has to come from the product's own
    /// Unicode tables. A directory whose name is written composed and decomposed is one directory on a volume that folds normalization
    /// and two on a volume that does not, so only on the first is the second registration a duplicate. The spellings are scalar values
    /// and never the output of <see cref="string.Normalize()"/>, which would agree with the product whatever either did.
    /// </summary>
    private static async Task AssertCampaignRegistrationFoldsUnicodeNormalizationAsync(
        HttpClient client,
        string testHome,
        CancellationToken cancellationToken)
    {
        string composed = Path.Combine(testHome, "café");
        string decomposed = Path.Combine(testHome, "café");

        _ = Directory.CreateDirectory(composed);

        if (!Directory.Exists(decomposed))
        {
            return;
        }

        using HttpResponseMessage registered = await PostCampaignAsync(
            client,
            "published-smoke-composed",
            composed,
            cancellationToken);
        string registeredJson = await registered.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            registered.StatusCode == HttpStatusCode.Created,
            $"Published campaign registration returned {(int)registered.StatusCode}: {registeredJson}");

        using HttpResponseMessage duplicate = await PostCampaignAsync(
            client,
            "published-smoke-decomposed",
            decomposed,
            cancellationToken);
        string duplicateJson = await duplicate.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            duplicate.StatusCode == HttpStatusCode.BadRequest
                && duplicateJson.Contains("Campaign.DuplicatePath", StringComparison.Ordinal),
            $"The decomposed spelling of a registered directory was not a duplicate path ({(int)duplicate.StatusCode}): {duplicateJson}");

        using HttpResponseMessage found = await client.GetAsync(
            "api/campaigns/by-path?path=" + Uri.EscapeDataString(decomposed),
            cancellationToken);
        string foundJson = await found.Content.ReadAsStringAsync(cancellationToken);

        Assert.True(
            found.IsSuccessStatusCode,
            $"The registration was not found by the decomposed spelling ({(int)found.StatusCode}): {foundJson}");
    }

    private static async Task<HttpResponseMessage> PostCampaignAsync(
        HttpClient client,
        string name,
        string path,
        CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(
            new RegisterCampaignRequest(name, path, WorkspaceType.Campaign, null),
            ArcanumJsonContext.Default.RegisterCampaignRequest);

        using StringContent content = new(body, Encoding.UTF8, "application/json");

        return await client.PostAsync("api/campaigns", content, cancellationToken);
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

    [Fact]
    public async Task Loopback_fixture_accepts_original_stream_and_six_bounded_nonstreaming_arms()
    {
        await using var provider = await FakeOpenAiProvider.StartAsync();
        using HttpClient client = new();
        for (int index = 0; index < 7; index++)
        {
            bool streaming = index == 0;
            string json = "{\"model\":\"published-smoke-model\",\"stream\":" + (streaming ? "true" : "false")
                + ",\"messages\":[{\"role\":\"user\",\"content\":\"fixture\"}]}";
            using var response = await client.PostAsync(new Uri(provider.Endpoint.AbsoluteUri + "/chat/completions"), new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (!streaming)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(ExpectedModel, body.RootElement.GetProperty("model").GetString());
                Assert.Equal("stop", body.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
                Assert.True(body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);
            }
        }
        Assert.Equal(7, provider.RequestCount);
        using var overflow = await client.PostAsync(new Uri(provider.Endpoint.AbsoluteUri + "/chat/completions"), new StringContent("{\"model\":\"published-smoke-model\",\"messages\":[{\"role\":\"user\",\"content\":\"fixture\"}]}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
        Assert.Equal(7, provider.Requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loopback_fixture_delegates_one_child_then_continues_and_refuses_a_fourth_send(bool streaming)
    {
        await using var provider = await FakeOpenAiProvider.StartAsync();
        using HttpClient client = new();

        string[] prompts = ["published-delegation-parent", "published-delegation-child", "published-delegation-parent"];

        for (int index = 0; index < prompts.Length; index++)
        {
            string json = "{\"model\":\"published-smoke-model\",\"stream\":" + (streaming ? "true" : "false")
                + ",\"messages\":[{\"role\":\"user\",\"content\":\"" + prompts[index] + "\"}]}";

            using var response = await client.PostAsync(new Uri(provider.Endpoint.AbsoluteUri + "/chat/completions"), new StringContent(json, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.Contains(index == 0 ? "tool_calls" : index == 1 ? "published-delegation-child-answer" : "published-delegation-final", body);
        }

        using var overflow = await client.PostAsync(new Uri(provider.Endpoint.AbsoluteUri + "/chat/completions"), new StringContent("{\"model\":\"published-smoke-model\",\"messages\":[{\"role\":\"user\",\"content\":\"published-delegation-parent\"}]}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);

        Assert.Equal(3, provider.Requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loopback_fixture_ordinary_tool_turn_refuses_a_third_send(bool streaming)
    {
        await using var provider = await FakeOpenAiProvider.StartAsync();
        using HttpClient client = new();
        string json = "{\"model\":\"published-smoke-model\",\"stream\":" + (streaming ? "true" : "false")
            + ",\"messages\":[{\"role\":\"user\",\"content\":\"" + FakeOpenAiProvider.OrdinaryToolPrompt + "\"}]}";
        for (int index = 0; index < 3; index++)
        {
            using var response = await client.PostAsync(new Uri(provider.Endpoint.AbsoluteUri + "/chat/completions"),
                new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(index < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (index < 2) Assert.Contains(index == 0 ? "get_local_system_time" : FakeOpenAiProvider.OrdinaryToolFinal,
                await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, provider.Requests.Length);
    }

    internal static void ConfigureEvaluationPricing(ArcanumSettings settings)
    {
        settings.Cost.Pricing.ModelPricing[ExpectedModel] = new ModelPricingEntry { InputPer1M = 1m, OutputPer1M = 2m };
        settings.Cost.Budget.Enabled = true;
        settings.Cost.Budget.DailyLimitUsd = 1m;
    }

    internal static async Task<MemoryEvaluationRequest> SeedEvaluationAsync(HttpClient client, CancellationToken ct)
    {
        var driver = new MemoryErasureRouteDriver(client);
        var committed = await driver.SetCovenantAsync(CovenantScope.Global, null, "published.evaluation", EvaluationMemory, ct);
        Assert.NotNull(committed.ResultingVersionId);
        using var explainResponse = await client.PostAsync("api/memory/covenant/explain",
            JsonContent.Create(new CovenantExplainRequest(null, false), ArcanumJsonContext.Default.CovenantExplainRequest), ct);
        Assert.Equal(HttpStatusCode.OK, explainResponse.StatusCode);
        var explain = await ReadDataAsync(explainResponse, ArcanumJsonContext.Default.ApiResponseCovenantExplainDto, ct);
        Assert.True(explain.Enabled);
        Assert.True(explain.Available);
        var selected = Assert.Single(explain.Decisions, decision => decision.EntryId == committed.EntryId && decision.VersionId == committed.ResultingVersionId);
        Assert.Equal(CovenantLane.Confirmed, selected.Lane);
        Assert.NotNull(selected.Placement);
        Assert.Equal(CovenantPlanDecision.EligibleConfirmed, selected.Decision);
        return new MemoryEvaluationRequest(Guid.NewGuid(), new(MemoryEvaluationScopeKind.Global), "published-smoke", ExpectedModel,
            new(Temperature: 0, MaxOutputTokens: 32), new("Answer the bounded workload.", "Use the selected memory if present."),
            new([new(committed.EntryId, committed.ResultingVersionId.Value, new CovenantDigest(Convert.FromHexString(selected.FragmentDigest)))], [], []),
            [new MemoryEvaluationRegexCheck("^" + EvaluationAnswer + "$")], PairCount: 3);
    }

    internal static async Task ExerciseMemoryEvaluationAsync(HttpClient client, FakeOpenAiProvider provider, int priorCalls, CancellationToken ct)
    {
        var request = await SeedEvaluationAsync(client, ct);
        var target = request.Memory.Covenant[0];
        async Task<string> Detail()
        {
            using var response = await client.PostAsync("api/memory/covenant/detail",
                JsonContent.Create(new CovenantDetailRequest(CovenantScope.Global, null, "published.evaluation"), ArcanumJsonContext.Default.CovenantDetailRequest), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.GetProperty("data").GetRawText();
        }

        string before = await Detail();
        using var run = await client.PostAsync("api/proving-grounds/memory-evaluations/run", JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), ct);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await ReadDataAsync(run, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse, ct);
        Assert.Equal(MemoryEvaluationState.Completed, result.State);
        Assert.NotNull(result.Report);
        Assert.NotNull(result.ReportDigest);
        Assert.True(result.Report.Decision.Outcome == MemoryEvaluationOutcome.Benefit, $"Expected Benefit; closed reason={result.Report.Decision.Reason}, calls={provider.RequestCount}, pairs={result.Report.Aggregate.CompletedPairs}.");
        Assert.Equal(3, result.Report.Aggregate.ValidPairs);
        Assert.Equal(3, result.Report.Aggregate.ImprovedPairs);
        Assert.Equal(120, result.Report.InputTokens);
        Assert.Equal(24, result.Report.OutputTokens);
        Assert.Equal(0.000168m, result.Report.Cost);
        Assert.Equal(target.EntryId.ToString("D"), Assert.Single(result.Report.Sources).SubjectId);
        string report = JsonSerializer.Serialize(result, ArcanumJsonContext.Default.MemoryEvaluationResponse);
        foreach (string secret in new[] { EvaluationMemory, EvaluationAnswer, request.Workload.SystemPrompt, request.Workload.UserPrompt, ProviderKey })
            Assert.DoesNotContain(secret, report);
        Assert.Equal("no-store, private", run.Headers.CacheControl!.ToString());
        Assert.Equal(priorCalls + 6, provider.RequestCount);
        bool[] order = [false, true, true, false, false, true];
        var arms = provider.Requests.Skip(priorCalls).ToArray();
        for (int index = 0; index < arms.Length; index++)
        {
            var arm = arms[index];
            Assert.False(arm.Streaming);
            Assert.Equal(ExpectedModel, arm.Model);
            Assert.Equal("Bearer " + ProviderKey, arm.Authorization);
            Assert.Equal(request.Workload.UserPrompt, arm.Prompt);
            Assert.Equal(order[index], arm.Payload.GetProperty("messages").GetRawText().Contains(EvaluationMemory, StringComparison.Ordinal));
            Assert.False(arm.Payload.TryGetProperty("tools", out _));
            Assert.False(arm.Payload.TryGetProperty("tool_choice", out _));
            Assert.Equal(0, arm.Payload.GetProperty("temperature").GetDouble());
            Assert.Equal(32, arm.Payload.GetProperty("max_completion_tokens").GetInt32());
        }

        using var replay = await client.PostAsync("api/proving-grounds/memory-evaluations/run", JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await ReadDataAsync(replay, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse, ct);
        Assert.Equal(report, JsonSerializer.Serialize(replayed, ArcanumJsonContext.Default.MemoryEvaluationResponse));
        using var get = await client.PostAsync("api/proving-grounds/memory-evaluations/get", JsonContent.Create(new MemoryEvaluationGetRequest(request.EvaluationId), ArcanumJsonContext.Default.MemoryEvaluationGetRequest), ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(report, JsonSerializer.Serialize(await ReadDataAsync(get, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationResponse, ct), ArcanumJsonContext.Default.MemoryEvaluationResponse));
        Assert.Equal(before, await Detail());
        Assert.Equal(priorCalls + 6, provider.RequestCount);
        var cleanup = new MemoryEvaluationDeleteRequest(request.EvaluationId, result.ReportDigest.Value, true);
        using var wrongDigest = await client.PostAsync("api/proving-grounds/memory-evaluations/delete", JsonContent.Create(cleanup with { ExpectedReportDigest = new CovenantDigest(new byte[32]) }, ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest), ct);
        Assert.Equal(HttpStatusCode.Conflict, wrongDigest.StatusCode);
        using var unconfirmed = await client.PostAsync("api/proving-grounds/memory-evaluations/delete", JsonContent.Create(cleanup with { Confirm = false }, ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest), ct);
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        using var deleted = await client.PostAsync("api/proving-grounds/memory-evaluations/delete", JsonContent.Create(cleanup, ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest), ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await ReadDataAsync(deleted, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationDeleteResult, ct)).Deleted);
        using var repeated = await client.PostAsync("api/proving-grounds/memory-evaluations/delete", JsonContent.Create(cleanup, ArcanumJsonContext.Default.MemoryEvaluationDeleteRequest), ct);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.False((await ReadDataAsync(repeated, ArcanumJsonContext.Default.ApiResponseMemoryEvaluationDeleteResult, ct)).Deleted);
        using var gone = await client.PostAsync("api/proving-grounds/memory-evaluations/get", JsonContent.Create(new MemoryEvaluationGetRequest(request.EvaluationId), ArcanumJsonContext.Default.MemoryEvaluationGetRequest), ct);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        using var erasedReplay = await client.PostAsync("api/proving-grounds/memory-evaluations/run", JsonContent.Create(request, ArcanumJsonContext.Default.MemoryEvaluationRequest), ct);
        Assert.Equal(HttpStatusCode.Gone, erasedReplay.StatusCode);
        Assert.Equal(before, await Detail());
        Assert.Equal(priorCalls + 6, provider.RequestCount);
    }

    internal sealed record DelegationEvidence(Guid SessionId, Guid CallEntryId, Guid ResultEntryId, Guid AssistantEntryId);

    internal static async Task<DelegationEvidence> ExerciseMemoryDelegationAsync(HttpClient client, FakeOpenAiProvider provider,
        int priorCalls, CancellationToken ct, Func<Guid, Task>? beforeTurn = null, Guid? campaignId = null, Func<Task<Guid>>? resolveCreatedSession = null)
    {
        var driver = new MemoryErasureRouteDriver(client);
        var selected = await driver.SetCovenantAsync(CovenantScope.Global, null, "published.delegation.selected", DelegationSelected, ct);
        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, "published.delegation.unselected", DelegationUnselected, ct);
        using var explainResponse = await client.PostAsync("api/memory/covenant/explain",
            JsonContent.Create(new CovenantExplainRequest(null, false), ArcanumJsonContext.Default.CovenantExplainRequest), ct);
        Assert.Equal(HttpStatusCode.OK, explainResponse.StatusCode);
        var explain = await ReadDataAsync(explainResponse, ArcanumJsonContext.Default.ApiResponseCovenantExplainDto, ct);
        var decision = Assert.Single(explain.Decisions, item => item.EntryId == selected.EntryId && item.VersionId == selected.ResultingVersionId);
        Assert.Equal(CovenantLane.Confirmed, decision.Lane);
        Assert.Equal(CovenantPlanDecision.EligibleConfirmed, decision.Decision);
        Assert.NotNull(decision.Placement);

        Guid sessionId = Guid.Empty;
        if (resolveCreatedSession is null)
        {
            using var create = await client.PostAsync("api/sessions",
                JsonContent.Create(new CreateSessionRequest(campaignId, "published delegation"), ArcanumJsonContext.Default.CreateSessionRequest), ct);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            sessionId = (await ReadDataAsync(create, ArcanumJsonContext.Default.ApiResponseSessionDetailDto, ct)).Id;
            using var append = await client.PostAsync($"api/sessions/{sessionId:D}/entries",
                JsonContent.Create(new AppendEntryRequest(RetroDownfall.Arcanum.Core.Storage.Entities.MessageRole.User, DelegationHistory), ArcanumJsonContext.Default.AppendEntryRequest), ct);
            Assert.True(append.IsSuccessStatusCode);
        }

        var request = new MemoryDelegationRequest(new(DelegationParentPrompt, Model: ExpectedModel, SessionId: resolveCreatedSession is null ? sessionId : null,
            DisableMcpTools: true, SkipSpellRouting: true, MaxOutputTokens: 256),
            new(new(campaignId.HasValue ? MemoryEvaluationScopeKind.Campaign : MemoryEvaluationScopeKind.Global, campaignId), new([new(selected.EntryId, selected.ResultingVersionId!.Value,
                new CovenantDigest(Convert.FromHexString(decision.FragmentDigest)))], [], [])));

        if (beforeTurn is not null) await beforeTurn(sessionId);

        using var response = await client.PostAsync("api/intelligence/memory-delegation",
            JsonContent.Create(request, ArcanumJsonContext.Default.MemoryDelegationRequest), ct);
        string diagnostic = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Delegation returned {(int)response.StatusCode}: {diagnostic}; provider calls={provider.RequestCount}.");
        AssertProtectedHeaders(response);
        var result = await ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponsePromptResponseDto, ct);
        Assert.Equal(DelegationFinal, result.Text);
        Assert.Equal(priorCalls + 3, provider.RequestCount);
        var calls = provider.Requests.Skip(priorCalls).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal(ExpectedModel, call.Model);
            Assert.Equal("Bearer " + ProviderKey, call.Authorization);
        });
        var function = Assert.Single(calls[0].Payload.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("function").GetProperty("name").GetString() == "delegate_task").GetProperty("function");
        var schema = function.GetProperty("parameters");
        Assert.Equal("memory", Assert.Single(schema.GetProperty("properties").GetProperty("memory_grant").GetProperty("enum").EnumerateArray()).GetString());
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "memory_grant");
        if (resolveCreatedSession is null) Assert.Contains(DelegationHistory, calls[0].Payload.GetProperty("messages").GetRawText());
        string child = calls[1].Payload.GetProperty("messages").GetRawText();
        Assert.Contains(DelegationSelected, child);
        Assert.DoesNotContain(DelegationUnselected, child);
        Assert.DoesNotContain(DelegationHistory, child);
        Assert.DoesNotContain(DelegationParentPrompt, child);
        Assert.Equal(DelegationChildPrompt, calls[1].Prompt);
        Assert.Equal(256, calls[1].Payload.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(calls[1].Payload.TryGetProperty("tools", out _));
        Assert.False(calls[1].Payload.TryGetProperty("tool_choice", out _));
        var toolResult = Assert.Single(calls[2].Payload.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "tool");
        Assert.Equal("call-published-delegation", toolResult.GetProperty("tool_call_id").GetString());
        Assert.Contains(DelegationChildAnswer, toolResult.GetProperty("content").GetRawText());

        if (resolveCreatedSession is not null) sessionId = await resolveCreatedSession();

        using var entriesResponse = await client.GetAsync($"api/sessions/{sessionId:D}/entries?limit=20", ct);
        Assert.Equal(HttpStatusCode.OK, entriesResponse.StatusCode);
        var entries = await ReadDataAsync(entriesResponse, ArcanumJsonContext.Default.ApiResponseEntryDtoArray, ct);
        var callEntry = Assert.Single(entries, entry => entry.ToolName == "delegate_task");
        var resultEntry = Assert.Single(entries, entry => entry.Content.StartsWith("[ToolResult:", StringComparison.Ordinal));
        var finalEntry = Assert.Single(entries, entry => entry.Content == DelegationFinal);
        Assert.Equal("assistant", callEntry.Role);
        Assert.Equal("system", resultEntry.Role);
        Assert.Contains(DelegationChildAnswer, resultEntry.Content);
        Assert.Equal("assistant", finalEntry.Role);

        var invalid = request with { Grant = request.Grant with { Memory = request.Grant.Memory with
        {
            Covenant = [request.Grant.Memory.Covenant[0] with { FragmentDigest = new CovenantDigest(new byte[32]) }]
        } } };
        using var refused = await client.PostAsync("api/intelligence/memory-delegation",
            JsonContent.Create(invalid, ArcanumJsonContext.Default.MemoryDelegationRequest), ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        AssertProtectedHeaders(refused);
        var failure = await JsonSerializer.DeserializeAsync(await refused.Content.ReadAsStreamAsync(ct), ArcanumJsonContext.Default.ApiResponsePromptResponseDto, ct);
        Assert.False(failure!.IsSuccess);
        Assert.Equal("MemoryDelegation.StaleSource", failure.Error!.Value.Code);
        string refusal = await refused.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(DelegationSelected, refusal);
        Assert.DoesNotContain(DelegationUnselected, refusal);
        Assert.Equal(priorCalls + 3, provider.RequestCount);
        return new(sessionId, callEntry.Id, resultEntry.Id, finalEntry.Id);
    }

    private static void AssertProtectedHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());
        Assert.Equal("no-cache", response.Headers.Pragma.ToString());
        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> info, CancellationToken ct)
    {
        var body = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), info, ct);
        Assert.NotNull(body);
        Assert.True(body.IsSuccess, body.Error?.Code);
        Assert.NotNull(body.Data);
        return body.Data;
    }

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
                        Models = [new ModelEntry(ExpectedModel, SupportsTools: true)],
                        // The tool-capable fake model must fit the real advertised server schemas.
                        ContextWindowLimit = 16384,
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
                    Covenant = true,
                },
                Workspaces = new WorkspaceSettings
                {
                    DefaultRoot = testHome,
                },
                Security = new SecuritySettings
                {
                    CampaignRoots = [testHome],
                },
            },
        };

        ConfigureEvaluationPricing(configuration.Arcanum);

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

    internal sealed class FakeOpenAiProvider : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private readonly TaskCompletionSource<ProviderRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _requestCount;

        private int? _delegationStart;

        private int? _ordinaryStart;

        internal const string OrdinaryToolPrompt = "published-ordinary-protected-tool";

        internal const string OrdinaryToolFinal = "published-ordinary-protected-final";

        private readonly ConcurrentQueue<ProviderRequest> _requests = new();

        internal readonly TaskCompletionSource FirstArm = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource? ResumeArms;

        internal bool InvalidOutput;

        internal ProviderRequest[] Requests => _requests.ToArray();

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
            builder.WebHost.ConfigureKestrel(static options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Limits.MaxRequestBodySize = 1024 * 1024;
            });

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
                bool streaming = request.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();

                int count = Interlocked.Increment(ref provider!._requestCount);
                // The dedicated parent marker begins one bounded scenario; default fixtures retain seven sends.
                if (provider._delegationStart is null && prompt == DelegationParentPrompt) provider._delegationStart = count;

                if (provider._ordinaryStart is null && prompt == OrdinaryToolPrompt) provider._ordinaryStart = count;

                int? ordinaryPhase = provider._ordinaryStart is { } ordinaryStart ? count - ordinaryStart : null;

                int? phase = provider._delegationStart is { } start ? count - start : null;

                if (ordinaryPhase is >= 2 || ordinaryPhase is null && (phase is >= 3 || phase is null && count > 7))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }

                var captured = new ProviderRequest(model, authorization, prompt, streaming, request.RootElement.Clone());
                provider._requests.Enqueue(captured);
                _ = provider._request.TrySetResult(captured);

                if (ordinaryPhase is { } ordinaryToolPhase)
                {
                    await WriteDelegationAsync(context, ordinaryToolPhase, streaming, ordinary: true);
                    return;
                }

                if (phase is { } delegationPhase)
                {
                    await WriteDelegationAsync(context, delegationPhase, streaming);
                    return;
                }

                if (!streaming)
                {
                    provider.FirstArm.TrySetResult();
                    if (provider.ResumeArms is { } resume) await resume.Task.WaitAsync(context.RequestAborted);
                    bool treatment = request.RootElement.GetProperty("messages").GetRawText().Contains(EvaluationMemory, StringComparison.Ordinal);
                    string answer = treatment ? EvaluationAnswer : "memory-unavailable";
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        "{\"id\":\"chatcmpl-evaluation\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"published-smoke-model\","
                        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + answer
                        + "\"},\"finish_reason\":\"" + (provider.InvalidOutput ? "length" : "stop") + "\"}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":4,\"total_tokens\":24,"
                        + "\"prompt_tokens_details\":{\"cached_tokens\":0},\"completion_tokens_details\":{\"reasoning_tokens\":0}}}", context.RequestAborted);
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

        private static async Task WriteDelegationAsync(HttpContext context, int phase, bool streaming, bool ordinary = false)
        {
            string arguments = ordinary ? "{}" : "{\"prompt\":\"published-delegation-child\",\"max_tokens\":256,\"memory_grant\":\"memory\"}";
            string name = ordinary ? "get_local_system_time" : "delegate_task";
            string content = ordinary ? OrdinaryToolFinal : phase == 1 ? DelegationChildAnswer : DelegationFinal;
            string message = phase == 0
                ? "\"role\":\"assistant\",\"tool_calls\":[{" + (streaming ? "\"index\":0," : "")
                    + "\"id\":\"call-published-delegation\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":\"" + JsonEncodedText.Encode(arguments) + "\"}}]"
                : "\"role\":\"assistant\",\"content\":\"" + JsonEncodedText.Encode(content) + "\"";
            string body = "{\"id\":\"chatcmpl-delegation\",\"object\":\"" + (streaming ? "chat.completion.chunk" : "chat.completion")
                + "\",\"created\":0,\"model\":\"published-smoke-model\",\"choices\":[{\"index\":0,\"" + (streaming ? "delta" : "message")
                + "\":{" + message + "},\"finish_reason\":\"" + (phase == 0 ? "tool_calls" : "stop")
                + "\"}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":4,\"total_tokens\":24}}";
            context.Response.ContentType = streaming ? "text/event-stream" : "application/json";
            await context.Response.WriteAsync(streaming ? "data: " + body + "\n\ndata: [DONE]\n\n" : body, context.RequestAborted);
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

    internal sealed record ProviderRequest(
        string Model,
        string Authorization,
        string Prompt,
        bool Streaming,
        JsonElement Payload);

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
