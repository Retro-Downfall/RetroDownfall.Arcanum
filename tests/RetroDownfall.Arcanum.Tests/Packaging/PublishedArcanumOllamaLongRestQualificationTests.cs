using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Opt-in, local-only qualification of Long Rest against the published Native AOT apphost and real Ollama models. The isolated
/// installation, the credential posture and the refusal to run in CI are those of
/// <see cref="PublishedArcanumOllamaQualificationTests"/>; this adds a labelled Saga corpus seeded through the host's own extraction,
/// a clean sweep, a sweep interrupted by SIGKILL and recovered after a restart, one ordinary turn on the default model, and a
/// calibration report of the discovery thresholds. It measures and asserts; it changes no product constant.
/// </summary>
[Collection(LocalNativeAotOllamaQualificationCollection.Name)]
public sealed class PublishedArcanumOllamaLongRestQualificationTests(ITestOutputHelper output)
{
    private const string GithubActionsVariable = "GITHUB_ACTIONS";

    private const int MaxModelCatalogResponseBytes = 1024 * 1024;

    private static readonly TimeSpan HostStopBudget = TimeSpan.FromSeconds(60);

    [SkippableFact]
    [Trait("Category", "LocalOllama")]
    public async Task Published_native_aot_long_rest_sweeps_recovers_a_killed_host_and_leaves_turns_unharmed()
    {
        string? optIn = global::System.Environment.GetEnvironmentVariable(LongRestQualificationOptions.OptInVariable);

        Skip.IfNot(
            string.Equals(optIn, "1", StringComparison.Ordinal),
            $"Set {LongRestQualificationOptions.OptInVariable}=1 through scripts/verify-local-ollama-long-rest.sh to run this local-only qualification.");

        Assert.False(
            LocalOllamaQualificationGuards.IsGitHubActions(
                global::System.Environment.GetEnvironmentVariable(GithubActionsVariable)),
            "Real-model qualification is local-only and cannot run in GitHub Actions.");

        LongRestQualificationOptions options = LongRestQualificationOptions.Read(
            global::System.Environment.GetEnvironmentVariable);

        Assert.True(File.Exists(options.Executable), $"Published executable does not exist: {options.Executable}");

        AssertNativeAotPublish(options.Executable);

        await AssertModelsAreInstalledAsync(options);

        string? progressPath = null;

        if (options.ResultsDirectory is { } results)
        {
            Directory.CreateDirectory(results);

            progressPath = Path.Combine(results, "progress.log");
        }

        LongRestQualificationLog log = new(output, progressPath);

        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("ollama-long-rest");

        LongRestQualificationRun run = new(options, isolation, log);

        Exception? primaryFailure = null;

        try
        {
            await run.PrepareAsync(CancellationToken.None);

            await run.SeedAsync(CancellationToken.None);

            await run.EnableLongRestAsync(CancellationToken.None);

            await run.RunCleanSweepAsync(CancellationToken.None);

            await run.RunKilledHostSweepAsync(CancellationToken.None);

            await run.RunOrdinaryTurnAsync(CancellationToken.None);

            await run.CompleteAsync(CancellationToken.None);

            AssertNoPlaintextCredentialFiles(isolation.ProcessTemporaryDirectory, run.SensitiveValues());
        }
        catch (Exception exception)
        {
            InvalidOperationException wrapped = LocalOllamaQualificationGuards.CreateSanitizedFailure(
                "Local Native AOT/Ollama Long Rest qualification failed.\n" + log.Transcript,
                exception,
                LongRestQualificationConfiguration.ChatCredentialPlaceholder,
                run.MasterApiKey,
                [LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder, .. run.SensitiveValues()]);

            primaryFailure = wrapped;

            throw wrapped;
        }
        finally
        {
            List<Exception> cleanupFailures = [];

            cleanupFailures.AddRange(await run.CleanupAsync(run.MasterApiKey));

            await WriteSummaryAsync(options, run, log, primaryFailure is null);

            try
            {
                isolation.Delete();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(
                    LocalOllamaQualificationGuards.CreateSanitizedFailure(
                        "Long Rest qualification temporary cleanup failed.",
                        cleanupFailure,
                        LongRestQualificationConfiguration.ChatCredentialPlaceholder,
                        run.MasterApiKey,
                        [LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder, .. run.SensitiveValues()]));
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
                    "Long Rest qualification failed and one or more sanitized cleanup failures occurred.",
                    cleanupFailures);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ReceiptPath))
        {
            await File.WriteAllTextAsync(
                options.ReceiptPath,
                LongRestQualificationOptions.SuccessReceipt + global::System.Environment.NewLine);
        }
    }

    private async Task WriteSummaryAsync(
        LongRestQualificationOptions options,
        LongRestQualificationRun run,
        LongRestQualificationLog log,
        bool succeeded)
    {
        StringBuilder text = new();

        _ = text.AppendLine("LONG REST QUALIFICATION SUMMARY")
            .AppendLine(succeeded ? "Result: every scenario passed." : "Result: the qualification FAILED; the observations below are those made before it stopped.")
            .AppendLine($"Total wall clock: {log.Elapsed:hh\\:mm\\:ss}")
            .AppendLine();

        foreach (string observation in run.Observations)
        {
            _ = text.Append("- ").AppendLine(observation);
        }

        foreach (string finding in run.Findings)
        {
            _ = text.Append("- FINDING: ").AppendLine(finding);
        }

        if (run.CalibrationText is { } calibration)
        {
            _ = text.AppendLine().AppendLine(calibration);
        }

        string summary = text.ToString();

        foreach (string line in summary.Split('\n'))
        {
            output.WriteLine(line.TrimEnd('\r'));
        }

        if (options.ResultsDirectory is { } directory)
        {
            Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(Path.Combine(directory, "long-rest-qualification.txt"), summary);
        }
    }

    private static async Task AssertModelsAreInstalledAsync(LongRestQualificationOptions options)
    {
        using SocketsHttpHandler handler = LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler();

        using HttpClient client = new(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        using HttpResponseMessage response = await client.GetAsync(
            new Uri(options.Endpoint, "models"),
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        byte[]? responseBytes = await LocalOllamaQualificationGuards.ReadCappedResponseBytesAsync(
            response.Content,
            MaxModelCatalogResponseBytes,
            timeout.Token);

        Assert.NotNull(responseBytes);

        string json = Encoding.UTF8.GetString(responseBytes);

        Assert.True(response.IsSuccessStatusCode, $"Ollama /v1/models returned {(int)response.StatusCode}: {json}");

        using JsonDocument document = JsonDocument.Parse(json);

        string[] installed =
        [
            .. document.RootElement.GetProperty("data").EnumerateArray()
                .Select(static candidate => candidate.GetProperty("id").GetString() ?? string.Empty),
        ];

        foreach ((string role, string model) in new[]
        {
            ("chat", options.ChatModel),
            ("default", options.DefaultModel),
            ("embedding", options.EmbeddingModel),
        })
        {
            // Ollama lists an untagged pull as <name>:latest, and accepts the bare name in a request.
            Assert.True(
                installed.Any(id => string.Equals(id, model, StringComparison.Ordinal)
                    || string.Equals(id, model + ":latest", StringComparison.Ordinal)),
                $"The {role} model '{model}' is not installed in Ollama.");
        }
    }

    private static void AssertNativeAotPublish(string executable)
    {
        Assert.True(
            LocalOllamaQualificationGuards.IsNativeBinary(executable),
            "The supplied Native AOT apphost must be a non-symlink Mach-O or PE native binary, not a script.");

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

    private static void AssertNoPlaintextCredentialFiles(string root, params string?[] credentials)
    {
        string?[] secrets =
        [
            LongRestQualificationConfiguration.ChatCredentialPlaceholder,
            LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder,
            .. credentials,
        ];

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] contents = File.ReadAllBytes(path);

            foreach (string? credential in secrets)
            {
                if (string.IsNullOrEmpty(credential))
                {
                    continue;
                }

                Assert.False(
                    contents.AsSpan().IndexOf(Encoding.UTF8.GetBytes(credential)) >= 0,
                    $"A plaintext credential was persisted to {path}.");
            }
        }
    }
}
