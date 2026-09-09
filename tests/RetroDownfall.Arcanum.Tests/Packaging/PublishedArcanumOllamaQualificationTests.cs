using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Serialization;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Packaging;

[Collection(LocalNativeAotOllamaQualificationCollection.Name)]
public sealed class PublishedArcanumOllamaQualificationTests
{
    private const string OptInVariable = "ARCANUM_RUN_LOCAL_OLLAMA_AOT_QUALIFICATION";

    private const string PublishedExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    private const string ImageVariable = "ARCANUM_OLLAMA_QUALIFICATION_IMAGE";

    private const string ModelVariable = "ARCANUM_OLLAMA_QUALIFICATION_MODEL";

    private const string EndpointVariable = "ARCANUM_OLLAMA_QUALIFICATION_ENDPOINT";

    private const string ProviderKeyVariable = "ARCANUM_OLLAMA_QUALIFICATION_PROVIDER_KEY";

    private const string SuccessReceiptVariable = "ARCANUM_OLLAMA_QUALIFICATION_RECEIPT";

    private const string SuccessReceipt = "local-ollama-aot-qualification:v1";

    private const string TestCredentialOptInVariable = "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    private const string GithubActionsVariable = "GITHUB_ACTIONS";

    private const string DefaultModel = "gemma4:e4b";

    private const string DefaultEndpoint = "http://127.0.0.1:11434/v1/";

    private const string ProviderKey = "local-ollama-qualification-placeholder";

    private const string LogicalKey = "durable-fact";

    private const string InitialTurnMaxTokens = "96";

    private const string ContextRecallMaxTokens = "96";

    private const string VisionRecallMaxTokens = "160";

    private const int MaxModelCatalogResponseBytes = 1024 * 1024;

    private const string SecondTurnPrompt =
        "The pinned durable-fact file is authoritative. Recall the conversation marker from turn one "
        + "and reply with exactly MARKER=<conversation marker>; TOKEN=<CURRENT_TOKEN> and no other text.";

    private const string RequiredImageSha256 =
        "88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0";

    private static readonly TimeSpan HostStartupBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TurnBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan HostCleanupAttemptBudget = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CliCleanupAttemptBudget = TimeSpan.FromSeconds(5);

    [SkippableFact]
    [Trait("Category", "LocalOllama")]
    public async Task Published_native_aot_preserves_corrected_file_context_and_runs_vision_across_restart()
    {
        string? optIn = global::System.Environment.GetEnvironmentVariable(OptInVariable);

        Skip.IfNot(
            string.Equals(optIn, "1", StringComparison.Ordinal),
            $"Set {OptInVariable}=1 through scripts/verify-local-ollama-aot.sh to run this local-only qualification.");

        Assert.False(
            LocalOllamaQualificationGuards.IsGitHubActions(
                global::System.Environment.GetEnvironmentVariable(GithubActionsVariable)),
            "Real-model qualification is local-only and cannot run in GitHub Actions.");

        string executable = RequireEnvironmentPath(PublishedExecutableVariable);
        string sourceImage = RequireEnvironmentPath(ImageVariable);
        string model = global::System.Environment.GetEnvironmentVariable(ModelVariable)?.Trim()
            ?? DefaultModel;
        Uri endpoint = ValidateLocalOllamaEndpoint(
            global::System.Environment.GetEnvironmentVariable(EndpointVariable)
            ?? DefaultEndpoint);
        string conversationMarker = CreateOpaqueToken();
        string wrongToken = CreateOpaqueToken();
        string rightToken = CreateOpaqueToken();

        Assert.False(string.IsNullOrWhiteSpace(model), $"{ModelVariable} must not be empty.");
        Assert.NotEqual(wrongToken, rightToken);
        Assert.False(
            LocalOllamaQualificationGuards.ContainsVisionAnswerLeakage(
                LocalOllamaQualificationGuards.VisionAttachmentName,
                LocalOllamaQualificationGuards.VisionPrompt),
            "Provider-visible vision inputs must not disclose any expected answer token.");
        Assert.True(File.Exists(executable), $"Published executable does not exist: {executable}");
        Assert.True(File.Exists(sourceImage), $"Qualification image does not exist: {sourceImage}");

        AssertNativeAotPublish(executable);
        AssertStopSignImage(sourceImage);
        await AssertModelIsInstalledAsync(endpoint, model);

        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("ollama-aot");
        string testHome = isolation.TestHome;
        string workspace = Path.Combine(testHome, "workspace");
        string configurationDirectory = Path.Combine(testHome, ".config", "arcanum");
        string configurationPath = Path.Combine(configurationDirectory, "arcanum.json");
        string sourceFact = Path.Combine(workspace, "durable-fact.txt");
        string isolatedImage = Path.Combine(
            workspace,
            LocalOllamaQualificationGuards.VisionAttachmentName);

        string? masterApiKey = null;
        Exception? primaryFailure = null;
        TcpListener? portReservation = null;
        HttpClient? api = null;
        PublishedHost? firstHost = null;
        PublishedHost? restartedHost = null;

        try
        {
            isolation.Prepare();

            Directory.CreateDirectory(configurationDirectory);
            Directory.CreateDirectory(workspace);

            await File.WriteAllTextAsync(sourceFact, $"CURRENT_TOKEN={wrongToken}\n");
            File.Copy(sourceImage, isolatedImage, overwrite: false);
            AssertStopSignImage(isolatedImage);
            byte[] firstFactBytes = Encoding.UTF8.GetBytes($"CURRENT_TOKEN={wrongToken}\n");
            byte[] secondFactBytes = Encoding.UTF8.GetBytes($"CURRENT_TOKEN={rightToken}\n");
            byte[] imageBytes = await File.ReadAllBytesAsync(isolatedImage);

            portReservation = new TcpListener(IPAddress.Loopback, 0);

            portReservation.Start();

            int hostPort = ((IPEndPoint)portReservation.LocalEndpoint).Port;

            await WriteConfigurationAsync(
                configurationPath,
                workspace,
                hostPort,
                endpoint,
                model);

            TestEnvironment environment = new(isolation, workspace);

            portReservation.Stop();
            portReservation.Dispose();
            portReservation = null;

            // The reservation closes immediately before launch. A concurrent claimant can still win
            // this unavoidable hand-off; startup is allowed to fail rather than mutating persisted
            // configuration or silently testing a different port.

            firstHost = PublishedHost.Start(
                executable,
                environment,
                knownMasterApiKey: null);

            masterApiKey = await firstHost.WaitForInitialReadyAsync(HostStartupBudget);
            environment.MasterApiKey = masterApiKey;

            api = CreateApiClient(hostPort, masterApiKey);

            await AssertNativeAotMetadataAsync(
                api,
                configurationDirectory,
                configurationPath,
                hostPort);

            CliResult firstTurn = await RunCliAsync(
                executable,
                environment,
                TurnBudget,
                "--plain",
                "--print",
                "--no-context",
                "run",
                "--new",
                "--model",
                model,
                "--temperature",
                "0",
                "--max-tokens",
                InitialTurnMaxTokens,
                "--unattended",
                FirstTurnPrompt(conversationMarker));

            Assert.Equal(
                $"SESSION_READY; MARKER={conversationMarker}",
                NormalizeModelAnswer(firstTurn.StandardOutput));

            Guid sessionId = await ReadOnlySessionIdAsync(
                executable,
                environment,
                expectedEntryCount: 2);
            JsonElement firstVersion = ParseObject((await RunCliAsync(
                executable,
                environment,
                TimeSpan.FromSeconds(45),
                "--json",
                "--plain",
                "--print",
                "--no-context",
                "attachment",
                "reference",
                "durable-fact.txt",
                "--name",
                LogicalKey,
                "--session",
                sessionId.ToString("D"))).StandardOutput);

            Guid firstVersionId = ReadGuid(firstVersion, "id");

            Assert.Equal(LogicalKey, firstVersion.GetProperty("logicalKey").GetString());
            Assert.Equal(1, firstVersion.GetProperty("version").GetInt32());
            Assert.True(firstVersion.GetProperty("isRefreshable").GetBoolean());

            JsonElement firstPin = ParseObject((await RunCliAsync(
                executable,
                environment,
                TimeSpan.FromSeconds(45),
                "--json",
                "--plain",
                "--print",
                "--no-context",
                "attachment",
                "pin",
                firstVersionId.ToString("D"),
                "--session",
                sessionId.ToString("D"))).StandardOutput);

            Assert.Equal(firstVersionId, ReadGuid(firstPin, "targetIdentifier"));

            CliResult secondTurn = await RunCliAsync(
                executable,
                environment,
                TurnBudget,
                "--plain",
                "--print",
                "--no-context",
                "run",
                "--session",
                sessionId.ToString("D"),
                "--model",
                model,
                "--temperature",
                "0",
                "--max-tokens",
                ContextRecallMaxTokens,
                "--unattended",
                SecondTurnPrompt);

            Assert.Equal(
                $"MARKER={conversationMarker}; TOKEN={wrongToken}",
                NormalizeModelAnswer(secondTurn.StandardOutput));

            await File.WriteAllBytesAsync(sourceFact, secondFactBytes);

            JsonElement refresh = ParseObject((await RunCliAsync(
                executable,
                environment,
                TimeSpan.FromSeconds(45),
                "--json",
                "--plain",
                "--print",
                "--no-context",
                "attachment",
                "refresh",
                firstVersionId.ToString("D"),
                "--session",
                sessionId.ToString("D"))).StandardOutput);

            Guid secondVersionId = ReadGuid(refresh, "attachmentId");

            Assert.NotEqual(firstVersionId, secondVersionId);
            Assert.Equal(LogicalKey, refresh.GetProperty("logicalKey").GetString());
            Assert.Equal(2, refresh.GetProperty("version").GetInt32());
            Assert.True(refresh.GetProperty("newVersionCreated").GetBoolean());
            Assert.NotEqual(
                firstVersion.GetProperty("contentSha256").GetString(),
                refresh.GetProperty("contentSha256").GetString());

            await AssertAttachmentHistoryAsync(
                executable,
                environment,
                sessionId,
                secondVersionId,
                firstVersionId);

            _ = await RunCliAsync(
                executable,
                environment,
                TimeSpan.FromSeconds(45),
                "--json",
                "--plain",
                "--print",
                "--no-context",
                "attachment",
                "unpin",
                firstVersionId.ToString("D"),
                "--session",
                sessionId.ToString("D"));

            JsonElement secondPin = ParseObject((await RunCliAsync(
                executable,
                environment,
                TimeSpan.FromSeconds(45),
                "--json",
                "--plain",
                "--print",
                "--no-context",
                "attachment",
                "pin",
                secondVersionId.ToString("D"),
                "--session",
                sessionId.ToString("D"))).StandardOutput);

            Assert.Equal(secondVersionId, ReadGuid(secondPin, "targetIdentifier"));

            JsonElement image = ParseObject((await RunCliAsync(
                executable,
                environment,
                TimeSpan.FromSeconds(45),
                "--json",
                "--plain",
                "--print",
                "--no-context",
                "attachment",
                "add",
                isolatedImage,
                "--mime",
                "image/jpeg",
                "--name",
                LocalOllamaQualificationGuards.VisionAttachmentName,
                "--session",
                sessionId.ToString("D"))).StandardOutput);

            Guid imageId = ReadGuid(image, "id");
            string? imageLogicalKey = image.GetProperty("logicalKey").GetString();
            string? imageOriginalFileName = image.GetProperty("originalFileName").GetString();

            Assert.Equal("image/jpeg", image.GetProperty("mimeType").GetString());
            Assert.False(
                LocalOllamaQualificationGuards.ContainsVisionAnswerLeakage(
                    imageLogicalKey,
                    imageOriginalFileName),
                "Persisted provider-visible image metadata disclosed an expected answer token.");
            Assert.True(
                string.Equals(
                    imageLogicalKey,
                    LocalOllamaQualificationGuards.VisionAttachmentName,
                    StringComparison.Ordinal)
                && string.Equals(
                    imageOriginalFileName,
                    LocalOllamaQualificationGuards.VisionAttachmentName,
                    StringComparison.Ordinal),
                "Persisted image metadata did not retain the required neutral name.");

            await StopHostAsync(api, firstHost, TimeSpan.FromSeconds(45));

            restartedHost = PublishedHost.Start(
                executable,
                environment,
                masterApiKey);

            await restartedHost.WaitForRestartReadyAsync(HostStartupBudget);

            Assert.True(
                restartedHost.DisclosedMasterApiKey is null,
                "Restarted host disclosed a replacement master API key.");

            await AssertNativeAotMetadataAsync(
                api,
                configurationDirectory,
                configurationPath,
                hostPort);

            Guid restartedSessionId = await ReadOnlySessionIdAsync(
                executable,
                environment,
                expectedEntryCount: 4);

            Assert.Equal(sessionId, restartedSessionId);

            await AssertAttachmentHistoryAsync(
                executable,
                environment,
                sessionId,
                secondVersionId,
                firstVersionId);
            await AssertLatestAttachmentSelectionAsync(
                executable,
                environment,
                sessionId,
                secondVersionId);
            await AssertOnlyCorrectedVersionIsPinnedAsync(
                api,
                sessionId,
                secondVersionId,
                firstVersionId);
            await AssertExportedAttachmentAsync(
                executable,
                environment,
                sessionId,
                firstVersionId,
                firstFactBytes,
                Path.Combine(testHome, "exports", "v1.bin"));
            await AssertExportedAttachmentAsync(
                executable,
                environment,
                sessionId,
                secondVersionId,
                secondFactBytes,
                Path.Combine(testHome, "exports", "v2.bin"));
            await AssertExportedAttachmentAsync(
                executable,
                environment,
                sessionId,
                imageId,
                imageBytes,
                Path.Combine(testHome, "exports", "image.bin"));
            AssertEncryptedAttachmentPayloads(
                configurationDirectory,
                wrongToken,
                rightToken,
                imageBytes);

            CliResult thirdTurn = await RunCliAsync(
                executable,
                environment,
                TurnBudget,
                "--plain",
                "--print",
                "--no-context",
                "run",
                "--session",
                sessionId.ToString("D"),
                "--attachment",
                imageId.ToString("D"),
                "--model",
                model,
                "--temperature",
                "0",
                "--max-tokens",
                VisionRecallMaxTokens,
                "--unattended",
                LocalOllamaQualificationGuards.VisionPrompt);

            Assert.Equal(
                $"MARKER={conversationMarker}; TOKEN={rightToken}; OBJECT=STOP SIGN; COLOR=RED; SHAPE=OCTAGON; SIDES=8; TEXT=STOP",
                NormalizeModelAnswer(thirdTurn.StandardOutput));

            await AssertThreeDurableTurnsAsync(
                executable,
                environment,
                sessionId,
                conversationMarker,
                wrongToken,
                rightToken);

            await StopHostAsync(api, restartedHost, TimeSpan.FromSeconds(45));

            AssertNoPlaintextCredentialFiles(
                isolation.ProcessTemporaryDirectory,
                masterApiKey,
                ProviderKey);
        }
        catch (Exception exception)
        {
            InvalidOperationException wrappedFailure =
                LocalOllamaQualificationGuards.CreateSanitizedFailure(
                    "Local Native AOT/Ollama qualification failed. The test proves attachment-backed "
                    + "context replacement, not in-place transcript or extracted-memory editing.",
                    exception,
                    ProviderKey,
                    masterApiKey);
            primaryFailure = wrappedFailure;
            throw wrappedFailure;
        }
        finally
        {
            List<Exception> cleanupFailures = [];

            await CollectHostCleanupFailuresAsync(
                restartedHost,
                "restarted",
                cleanupFailures,
                masterApiKey);

            await CollectHostCleanupFailuresAsync(
                firstHost,
                "initial",
                cleanupFailures,
                masterApiKey);

            if (api is not null)
            {
                try
                {
                    api.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Local qualification API-client cleanup failed.",
                            cleanupFailure,
                            ProviderKey,
                            masterApiKey));
                }
            }

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
                            "Local qualification port-reservation cleanup failed.",
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
                InvalidOperationException sanitizedCleanupFailure =
                    LocalOllamaQualificationGuards.CreateSanitizedFailure(
                        "Local qualification temporary cleanup failed.",
                        cleanupFailure,
                        ProviderKey,
                        masterApiKey);

                cleanupFailures.Add(sanitizedCleanupFailure);
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
                    "Local qualification failed and one or more sanitized cleanup failures occurred.",
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

    private static async Task CollectHostCleanupFailuresAsync(
        PublishedHost? host,
        string phase,
        ICollection<Exception> cleanupFailures,
        string? masterApiKey)
    {
        if (host is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<Exception> hostFailures = await host.CleanupAsync(phase);

            foreach (Exception hostFailure in hostFailures)
            {
                cleanupFailures.Add(hostFailure);
            }
        }
        catch (Exception cleanupFailure)
        {
            cleanupFailures.Add(
                LocalOllamaQualificationGuards.CreateSanitizedFailure(
                    $"Local qualification {phase} published-host cleanup failed.",
                    cleanupFailure,
                    ProviderKey,
                    masterApiKey));
        }
    }

    private static async Task<Guid> ReadOnlySessionIdAsync(
        string executable,
        TestEnvironment environment,
        int expectedEntryCount)
    {
        CliResult result = await RunCliAsync(
            executable,
            environment,
            TimeSpan.FromSeconds(45),
            "--json",
            "--plain",
            "--print",
            "--no-context",
            "session",
            "list",
            "--limit",
            "10");
        JsonElement sessions = ParseArray(result.StandardOutput);
        JsonElement session = Assert.Single(sessions.EnumerateArray());

        Assert.Equal(expectedEntryCount, session.GetProperty("entryCount").GetInt32());

        return ReadGuid(session, "id");
    }

    private static async Task AssertAttachmentHistoryAsync(
        string executable,
        TestEnvironment environment,
        Guid sessionId,
        Guid latestId,
        Guid priorId)
    {
        CliResult result = await RunCliAsync(
            executable,
            environment,
            TimeSpan.FromSeconds(45),
            "--json",
            "--plain",
            "--print",
            "--no-context",
            "attachment",
            "versions",
            LogicalKey,
            "--session",
            sessionId.ToString("D"));
        JsonElement versions = ParseArray(result.StandardOutput);
        JsonElement[] rows = versions.EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal(latestId, ReadGuid(rows[0], "id"));
        Assert.Equal(2, rows[0].GetProperty("version").GetInt32());
        Assert.Equal(priorId, ReadGuid(rows[1], "id"));
        Assert.Equal(1, rows[1].GetProperty("version").GetInt32());
    }

    private static async Task AssertLatestAttachmentSelectionAsync(
        string executable,
        TestEnvironment environment,
        Guid sessionId,
        Guid latestId)
    {
        CliResult result = await RunCliAsync(
            executable,
            environment,
            TimeSpan.FromSeconds(45),
            "--json",
            "--plain",
            "--print",
            "--no-context",
            "attachment",
            "list",
            "--session",
            sessionId.ToString("D"));
        JsonElement latest = ParseArray(result.StandardOutput);
        JsonElement durableFact = Assert.Single(
            latest.EnumerateArray(),
            row => string.Equals(
                row.GetProperty("logicalKey").GetString(),
                LogicalKey,
                StringComparison.Ordinal));

        Assert.Equal(latestId, ReadGuid(durableFact, "id"));
        Assert.Equal(2, durableFact.GetProperty("version").GetInt32());
    }

    private static async Task AssertExportedAttachmentAsync(
        string executable,
        TestEnvironment environment,
        Guid sessionId,
        Guid attachmentId,
        byte[] expectedBytes,
        string destination)
    {
        _ = await RunCliAsync(
            executable,
            environment,
            TimeSpan.FromSeconds(45),
            "--yes",
            "--json",
            "--plain",
            "--print",
            "--no-context",
            "attachment",
            "export",
            attachmentId.ToString("D"),
            "--output",
            destination,
            "--session",
            sessionId.ToString("D"));

        byte[] actualBytes = await File.ReadAllBytesAsync(destination);

        Assert.Equal(expectedBytes, actualBytes);
        Assert.Equal(ComputeSha256(expectedBytes), ComputeSha256(actualBytes));
    }

    private static void AssertEncryptedAttachmentPayloads(
        string configurationDirectory,
        string wrongToken,
        string rightToken,
        byte[] imageBytes)
    {
        string attachmentsDirectory = Path.Combine(configurationDirectory, "attachments");
        string[] paths = Directory.EnumerateFiles(attachmentsDirectory, "*", SearchOption.AllDirectories).ToArray();

        Assert.True(
            paths.Length >= 3,
            $"Expected the two text versions and image attachment payloads, found {paths.Length}.");

        byte[][] forbidden =
        [
            Encoding.UTF8.GetBytes(wrongToken),
            Encoding.UTF8.GetBytes(rightToken),
            imageBytes,
        ];

        try
        {
            foreach (string path in paths)
            {
                byte[] contents = File.ReadAllBytes(path);

                try
                {
                    Assert.True(
                        contents.AsSpan().StartsWith("ARCABLOB"u8),
                        $"Attachment payload is missing its ARCABLOB envelope: {path}");

                    foreach (byte[] value in forbidden)
                    {
                        Assert.True(
                            contents.AsSpan().IndexOf(value) < 0,
                            $"Attachment payload contains plaintext content: {path}");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(contents);
                }
            }
        }
        finally
        {
            foreach (byte[] value in forbidden)
            {
                if (!ReferenceEquals(value, imageBytes))
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }
    }

    private static async Task AssertOnlyCorrectedVersionIsPinnedAsync(
        HttpClient api,
        Guid sessionId,
        Guid correctedVersionId,
        Guid supersededVersionId)
    {
        using HttpResponseMessage response = await api.GetAsync(
            $"api/sessions/{sessionId:D}/context-pins");
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"Context-pin query returned {(int)response.StatusCode}: {json}");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement data = document.RootElement.GetProperty("data");
        JsonElement attachmentPin = Assert.Single(
            data.EnumerateArray(),
            pin => string.Equals(
                pin.GetProperty("kind").GetString(),
                "Attachment",
                StringComparison.Ordinal));

        Assert.Equal(correctedVersionId, ReadGuid(attachmentPin, "targetIdentifier"));
        Assert.DoesNotContain(
            data.EnumerateArray(),
            pin => string.Equals(
                pin.GetProperty("targetIdentifier").GetString(),
                supersededVersionId.ToString("D"),
                StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AssertThreeDurableTurnsAsync(
        string executable,
        TestEnvironment environment,
        Guid sessionId,
        string conversationMarker,
        string wrongToken,
        string rightToken)
    {
        CliResult result = await RunCliAsync(
            executable,
            environment,
            TimeSpan.FromSeconds(45),
            "--json",
            "--plain",
            "--print",
            "--no-context",
            "session",
            "entries",
            sessionId.ToString("D"),
            "--limit",
            "10");
        JsonElement entries = ParseArray(result.StandardOutput);
        JsonElement[] rows = entries.EnumerateArray().ToArray();

        Assert.Equal(6, rows.Length);

        AssertExactDurableTurn(
            rows,
            rowOffset: 0,
            sessionId,
            FirstTurnPrompt(conversationMarker),
            $"SESSION_READY; MARKER={conversationMarker}");

        AssertExactDurableTurn(
            rows,
            rowOffset: 2,
            sessionId,
            SecondTurnPrompt,
            $"MARKER={conversationMarker}; TOKEN={wrongToken}");

        AssertExactDurableTurn(
            rows,
            rowOffset: 4,
            sessionId,
            LocalOllamaQualificationGuards.VisionPrompt,
            $"MARKER={conversationMarker}; TOKEN={rightToken}; OBJECT=STOP SIGN; COLOR=RED; SHAPE=OCTAGON; SIDES=8; TEXT=STOP");
    }

    private static void AssertExactDurableTurn(
        IReadOnlyList<JsonElement> rows,
        int rowOffset,
        Guid sessionId,
        string expectedPrompt,
        string expectedReply)
    {
        JsonElement user = rows[rowOffset];

        JsonElement assistant = rows[rowOffset + 1];

        Assert.Equal(sessionId, ReadGuid(user, "sessionId"));

        Assert.Equal("user", user.GetProperty("role").GetString());

        Assert.Equal(expectedPrompt, user.GetProperty("content").GetString());

        Assert.Equal(sessionId, ReadGuid(assistant, "sessionId"));

        Assert.Equal("assistant", assistant.GetProperty("role").GetString());

        Assert.Equal(
            expectedReply,
            NormalizeModelAnswer(assistant.GetProperty("content").GetString() ?? string.Empty));
    }

    private static async Task WriteConfigurationAsync(
        string path,
        string workspace,
        int hostPort,
        Uri endpoint,
        string model)
    {
        ArcanumConfigurationFile configuration = new()
        {
            Arcanum = new ArcanumSettings
            {
                Host = new HostSettings
                {
                    Port = hostPort,
                    ListenAny = false,
                },
                DefaultModel = model,
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "local-ollama-qualification",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = endpoint.AbsoluteUri,
                        CredentialEnvironmentVariable = ProviderKeyVariable,
                        Models = [new ModelEntry(model, SupportsVision: true, SupportsTools: false)],
                        ContextWindowLimit = 131_072,
                    },
                ],
                Features = new FeatureSettings
                {
                    Apprentices = false,
                    Lexicon = false,
                    ArchiveSearch = false,
                    Metrics = false,
                    Embeddings = false,
                    SessionSearch = false,
                    CodebaseRetrieval = false,
                    AttachmentRetrieval = false,
                    Saga = false,
                    SagaExtraction = false,
                    Tapestry = false,
                    SemanticSpellRouting = false,
                    Scrying = true,
                    Attachments = true,
                    ClientTools = false,
                    WebBrowsing = false,
                    Reasoning = false,
                    ReasoningSummaries = false,
                    Guardrails = false,
                    WorkspaceChecks = false,
                    MemoryManagement = false,
                    Covenant = false,
                    CampaignScopedMemory = false,
                    Annals = false,
                    Conclave = false,
                    A2AServer = false,
                    A2AClient = false,
                },
                Security = new SecuritySettings
                {
                    AllowedUploadMimeTypes = ["text/plain", "image/jpeg"],
                    AllowedImageMimeTypes = ["image/jpeg"],
                },
                Workspaces = new WorkspaceSettings
                {
                    DefaultRoot = workspace,
                    EnableFileWrite = false,
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

    private static Uri ValidateLocalOllamaEndpoint(string raw)
    {
        Assert.True(
            LocalOllamaQualificationGuards.IsLocalOllamaEndpoint(raw, out Uri? endpoint),
            $"{EndpointVariable} must be an HTTP /v1 URI on a literal loopback address without credentials, query, or fragment.");

        return endpoint!;
    }

    private static async Task AssertModelIsInstalledAsync(
        Uri endpoint,
        string model)
    {
        using SocketsHttpHandler handler =
            LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler();

        using HttpClient client = new(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(endpoint, "models"),
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        byte[]? responseBytes =
            await LocalOllamaQualificationGuards.ReadCappedResponseBytesAsync(
                response.Content,
                MaxModelCatalogResponseBytes,
                timeout.Token);

        Assert.NotNull(responseBytes);

        string json = Encoding.UTF8.GetString(responseBytes);

        Assert.True(
            response.IsSuccessStatusCode,
            $"Ollama /v1/models returned {(int)response.StatusCode}: {json}");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement models = document.RootElement.GetProperty("data");

        Assert.Contains(
            models.EnumerateArray(),
            candidate => string.Equals(
                candidate.GetProperty("id").GetString(),
                model,
                StringComparison.Ordinal));
    }

    private static async Task AssertNativeAotMetadataAsync(
        HttpClient api,
        string expectedGrimoireDirectory,
        string expectedConfigurationPath,
        int expectedPort)
    {
        using HttpResponseMessage response = await api.GetAsync("api/meta");
        string json = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.IsSuccessStatusCode,
            $"Published metadata returned {(int)response.StatusCode}: {json}");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement data = document.RootElement.GetProperty("data");

        Assert.True(data.GetProperty("nativeAot").GetBoolean());
        Assert.Equal(expectedGrimoireDirectory, data.GetProperty("grimoireDirectory").GetString());
        Assert.Equal(expectedConfigurationPath, data.GetProperty("configPath").GetString());
        Assert.Equal(expectedPort, data.GetProperty("port").GetInt32());
        Assert.False(data.GetProperty("listenAny").GetBoolean());
    }

    private static HttpClient CreateApiClient(
        int hostPort,
        string masterApiKey)
    {
        HttpClient client = new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{hostPort}/"),
            Timeout = TimeSpan.FromSeconds(45),
        };

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, masterApiKey);

        return client;
    }

    private static async Task StopHostAsync(
        HttpClient api,
        PublishedHost host,
        TimeSpan timeout)
    {
        using CancellationTokenSource deadline = new(timeout);
        using HttpResponseMessage response = await api.PostAsync(
            "api/server/quit",
            content: null,
            deadline.Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await host.WaitForExitAsync(deadline.Token);

        Assert.Equal(0, host.ExitCode);
    }

    private static async Task<CliResult> RunCliAsync(
        string executable,
        TestEnvironment environment,
        TimeSpan timeout,
        params string[] arguments)
    {
        ProcessStartInfo start = environment.CreateStartInfo(executable, arguments);

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
            primaryFailure = environment.CreateSanitizedFailure(
                $"Published CLI command exceeded {timeout}: {environment.Redact(SafeCommand(arguments))}",
                new TimeoutException("The bounded command deadline elapsed."));
        }
        catch (Exception exception)
        {
            primaryFailure = environment.CreateSanitizedFailure(
                $"Published CLI command wait failed: {environment.Redact(SafeCommand(arguments))}",
                exception);
        }

        CliProcessCleanupResult cleanup = await CleanupCliProcessAsync(
            process,
            standardOutput,
            standardError,
            environment);

        string output = cleanup.StandardOutput;
        string error = cleanup.StandardError;

        if (primaryFailure is null && exitCode is not 0)
        {
            primaryFailure = environment.CreateSanitizedFailure(
                $"Published CLI command exited {exitCode}: {environment.Redact(SafeCommand(arguments))}\n"
                + $"stdout:\n{output}\nstderr:\n{error}",
                new InvalidOperationException("The command returned a non-success exit code."));
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

        return new CliResult(output.Trim(), error.Trim());
    }

    private static async Task<CliProcessCleanupResult> CleanupCliProcessAsync(
        DiagnosticsProcess process,
        Task<string> standardOutput,
        Task<string> standardError,
        TestEnvironment environment)
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
            cleanupFailures.Add(environment.CreateSanitizedFailure(
                "Published CLI process termination did not complete after three bounded attempts.",
                terminationFailure));
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
            cleanupFailures.Add(environment.CreateSanitizedFailure(
                "Published CLI output drain did not complete after three bounded attempts.",
                drainFailure));
        }

        string output = standardOutput.IsCompletedSuccessfully
            ? environment.Redact(standardOutput.Result)
            : string.Empty;
        string error = standardError.IsCompletedSuccessfully
            ? environment.Redact(standardError.Result)
            : string.Empty;

        try
        {
            process.Dispose();
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(environment.CreateSanitizedFailure(
                "Published CLI process-handle cleanup failed.",
                exception));
        }

        return new CliProcessCleanupResult(output, error, cleanupFailures);
    }

    private static string SafeCommand(IEnumerable<string> arguments) =>
        "arcanum " + string.Join(' ', arguments.Select(static argument =>
            argument.Contains(' ')
                ? "<prompt>"
                : argument));

    private static JsonElement ParseObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        return document.RootElement.Clone();
    }

    private static JsonElement ParseArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        return document.RootElement.Clone();
    }

    private static Guid ReadGuid(
        JsonElement element,
        string propertyName)
    {
        string? value = element.GetProperty(propertyName).GetString();

        Assert.True(Guid.TryParse(value, out Guid parsed), $"{propertyName} was not a Guid: {value}");

        return parsed;
    }

    private static string FirstTurnPrompt(string conversationMarker) =>
        $"Reply with exactly SESSION_READY; MARKER={conversationMarker} and no other text.";

    // Some small local models wrap an otherwise exact answer in one inline-code pair or one
    // triple-backtick fence. Nothing inside the answer is normalized or rewritten.
    private static string NormalizeModelAnswer(string value)
    {
        string answer = value.Trim();

        if (answer.Length >= 6
            && answer.StartsWith("```", StringComparison.Ordinal)
            && answer.EndsWith("```", StringComparison.Ordinal))
        {
            return answer[3..^3].Trim();
        }

        if (answer.Length >= 2
            && answer[0] == '`'
            && answer[^1] == '`')
        {
            return answer[1..^1].Trim();
        }

        return answer;
    }

    private static string RequireEnvironmentPath(string variable)
    {
        string? value = global::System.Environment.GetEnvironmentVariable(variable);

        Assert.False(string.IsNullOrWhiteSpace(value), $"{variable} is required after local qualification is enabled.");

        try
        {
            return Path.GetFullPath(value!);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{variable} must contain a valid filesystem path.");
        }
    }

    private static void AssertStopSignImage(string path)
    {
        Assert.True(
            LocalOllamaQualificationGuards.IsQualifiedImage(path, RequiredImageSha256),
            $"Qualification image must be the bounded JPEG fixture with SHA-256 {RequiredImageSha256}.");
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CreateOpaqueToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static void AssertNativeAotPublish(string executable)
    {
        AssertNativeBinary(executable);

        string publishDirectory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("Published executable has no parent directory.");

        Assert.False(
            File.Exists(Path.Combine(publishDirectory, "RetroDownfall.Arcanum.Cli.dll")),
            "Native AOT must not ship the managed CLI assembly.");
        Assert.DoesNotContain(
            Directory.EnumerateFiles(publishDirectory),
            static path => Path.GetFileName(path).Contains("hostfxr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(publishDirectory),
            static path => Path.GetFileName(path).Contains("hostpolicy", StringComparison.OrdinalIgnoreCase));

        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(executable);

            Assert.NotEqual(
                (UnixFileMode)0,
                mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
        }
    }

    private static void AssertNativeBinary(string executable)
    {
        Assert.True(
            LocalOllamaQualificationGuards.IsNativeBinary(executable),
            "The supplied Native AOT apphost must be a non-symlink Mach-O or PE native binary, not a script.");
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

    private sealed record CliResult(
        string StandardOutput,
        string StandardError);

    private sealed record CliProcessCleanupResult(
        string StandardOutput,
        string StandardError,
        IReadOnlyList<Exception> Failures);

    private sealed record TestEnvironment(
        PublishedTestProcessIsolation Isolation,
        string Workspace)
    {
        public string TestHome => Isolation.TestHome;

        public string? MasterApiKey { get; set; }

        public ProcessStartInfo CreateStartInfo(
            string executable,
            IEnumerable<string> arguments)
        {
            ProcessStartInfo start = new(executable)
            {
                WorkingDirectory = Workspace,
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
            start.Environment["ARCANUM_TEST_HOME"] = TestHome;
            start.Environment[TestCredentialOptInVariable] = "1";
            start.Environment[ProviderKeyVariable] = ProviderKey;
            start.Environment["ARCANUM_NO_AUTO_SERVE"] = "1";
            start.Environment["NO_COLOR"] = "1";
            start.Environment["HOME"] = TestHome;
            start.Environment["USERPROFILE"] = TestHome;
            start.Environment["DOTNET_CLI_HOME"] = TestHome;
            start.Environment["APPDATA"] = Path.Combine(TestHome, "AppData", "Roaming");
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(TestHome, ".config");
            start.Environment["XDG_DATA_HOME"] = Path.Combine(TestHome, ".local", "share");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(TestHome, ".cache");

            Isolation.ApplyTemporaryEnvironment(start);

            return start;
        }

        public string Redact(string value)
        {
            return LocalOllamaQualificationGuards.Redact(value, ProviderKey, MasterApiKey);
        }

        public InvalidOperationException CreateSanitizedFailure(
            string context,
            Exception failure) =>
            LocalOllamaQualificationGuards.CreateSanitizedFailure(
                context,
                failure,
                ProviderKey,
                MasterApiKey);

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
    }

    private sealed class PublishedHost
    {
        private readonly ConcurrentQueue<string> _safeLines = new();

        private readonly DiagnosticsProcess _process;

        private readonly string? _knownMasterApiKey;

        private readonly TaskCompletionSource<string> _masterApiKey =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Task _standardOutput;

        private readonly Task _standardError;

        private int _cleanupStarted;

        private PublishedHost(
            DiagnosticsProcess process,
            string? knownMasterApiKey)
        {
            _process = process;
            _knownMasterApiKey = knownMasterApiKey;
            _standardOutput = PumpAsync(process.StandardOutput, observeStartup: true);
            _standardError = PumpAsync(process.StandardError, observeStartup: false);
        }

        public string? DisclosedMasterApiKey { get; private set; }

        public int ExitCode => _process.ExitCode;

        public static PublishedHost Start(
            string executable,
            TestEnvironment environment,
            string? knownMasterApiKey)
        {
            ProcessStartInfo start = environment.CreateStartInfo(
                executable,
                ["serve", "--plain"]);
            DiagnosticsProcess process = DiagnosticsProcess.Start(start)
                ?? throw new InvalidOperationException("The published Arcanum host did not start.");

            return new PublishedHost(process, knownMasterApiKey);
        }

        public async Task<string> WaitForInitialReadyAsync(TimeSpan timeout)
        {
            using CancellationTokenSource deadline = new(timeout);
            string masterApiKey = await _masterApiKey.Task.WaitAsync(deadline.Token);

            await _ready.Task.WaitAsync(deadline.Token);

            return masterApiKey;
        }

        public async Task WaitForRestartReadyAsync(TimeSpan timeout)
        {
            using CancellationTokenSource deadline = new(timeout);

            await _ready.Task.WaitAsync(deadline.Token);
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(_standardOutput, _standardError).WaitAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<Exception>> CleanupAsync(string phase)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(phase);

            if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
            {
                return [];
            }

            List<Exception> cleanupFailures = [];

            Exception? terminationFailure = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }

                    await _process
                        .WaitForExitAsync()
                        .WaitAsync(HostCleanupAttemptBudget);

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
                cleanupFailures.Add(SanitizeCleanupFailure(
                    $"Local qualification {phase} published-host termination did not complete after three bounded attempts.",
                    terminationFailure));
            }

            Exception? drainFailure = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await Task.WhenAll(_standardOutput, _standardError)
                        .WaitAsync(HostCleanupAttemptBudget);

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
                cleanupFailures.Add(SanitizeCleanupFailure(
                    $"Local qualification {phase} published-host output cleanup did not complete after three bounded attempts."
                    + global::System.Environment.NewLine
                    + SafeDiagnostics(),
                    drainFailure));
            }

            try
            {
                _process.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(SanitizeCleanupFailure(
                    $"Local qualification {phase} published-host process-handle cleanup failed.",
                    exception));
            }

            return cleanupFailures;
        }

        private InvalidOperationException SanitizeCleanupFailure(
            string context,
            Exception failure) =>
            LocalOllamaQualificationGuards.CreateSanitizedFailure(
                context,
                failure,
                ProviderKey,
                DisclosedMasterApiKey ?? _knownMasterApiKey);

        private async Task PumpAsync(
            StreamReader reader,
            bool observeStartup)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (observeStartup && TryReadMasterApiKey(line, out string? key))
                {
                    DisclosedMasterApiKey = key;
                    _masterApiKey.TrySetResult(key);
                    Enqueue(LocalOllamaQualificationGuards.Redact(
                        line,
                        ProviderKey,
                        key));
                }
                else
                {
                    Enqueue(Redact(line));
                }

                if (observeStartup
                    && line.Contains("Listening on http://127.0.0.1:", StringComparison.Ordinal))
                {
                    _ready.TrySetResult();
                }
            }

            if (observeStartup)
            {
                string diagnostics = SafeDiagnostics();

                _masterApiKey.TrySetException(
                    new InvalidOperationException(
                        "Published host exited before generating its initial master API key.\n"
                        + diagnostics));
                _ready.TrySetException(
                    new InvalidOperationException(
                        "Published host exited before reporting readiness.\n"
                        + diagnostics));
            }
        }

        private string Redact(string line)
        {
            string? masterApiKey = _knownMasterApiKey;
            string redacted = LocalOllamaQualificationGuards.Redact(line, ProviderKey, masterApiKey);

            return LocalOllamaQualificationGuards.Redact(redacted, ProviderKey, DisclosedMasterApiKey);
        }

        private string SafeDiagnostics() =>
            string.Join(global::System.Environment.NewLine, _safeLines.Select(Redact));

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

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalNativeAotOllamaQualificationCollection
{
    public const string Name = "Local Native AOT Ollama qualification";
}
