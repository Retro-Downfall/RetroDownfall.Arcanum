using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Opt-in, local-only qualification of Long Rest against the published Native AOT apphost and real Ollama models. The isolated
/// installation, the credential posture and the refusal to run in CI are those of
/// <see cref="PublishedArcanumOllamaQualificationTests"/>; this adds a labelled Saga corpus seeded through the host's own extraction,
/// a clean sweep, a sweep interrupted by SIGKILL and recovered after a restart, one ordinary turn on the default model, the Mutiny
/// Scenario (a second sweep in which the forwarder rewrites every adjudication answer into the all-pairs verdict, over a freshly seeded
/// installation), and a calibration report of the lexical bar, the vectors and each memory's Pairing Gate floor. It measures and asserts; it
/// changes no product constant.
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

        await AssertEmbeddingDimensionsAsync(options);

        string? progressPath = null;

        if (options.ResultsDirectory is { } results)
        {
            Directory.CreateDirectory(results);

            progressPath = Path.Combine(results, "progress.log");
        }

        LongRestQualificationLog log = new(output, progressPath);

        PublishedTestProcessIsolation isolation = PublishedTestProcessIsolation.Create("ollama-long-rest");

        LongRestQualificationRun run = new(options, isolation, log);

        // The Mutiny Scenario is a second, freshly seeded installation of its own: the first run's sweep has folded the corpus.
        PublishedTestProcessIsolation? mutinyIsolation = options.Mutiny ? PublishedTestProcessIsolation.Create("ollama-long-rest-mutiny") : null;

        LongRestQualificationRun? mutiny = mutinyIsolation is null ? null : new(options, mutinyIsolation, log);

        Exception? primaryFailure = null;

        try
        {
            await run.PrepareAsync(CancellationToken.None);

            await run.SeedAsync(CancellationToken.None);

            await run.EnableLongRestAsync(CancellationToken.None);

            await run.RunCleanSweepAsync(CancellationToken.None);

            await run.RunKilledHostSweepAsync(CancellationToken.None);

            await run.RunOrdinaryTurnAsync(CancellationToken.None);

            await run.StopAsync(CancellationToken.None);

            AssertNoPlaintextCredentialFiles(isolation.ProcessTemporaryDirectory, run.SensitiveValues());

            if (mutiny is not null)
            {
                await mutiny.PrepareAsync(CancellationToken.None);

                await mutiny.SeedAsync(CancellationToken.None);

                await mutiny.EnableLongRestAsync(CancellationToken.None);

                await mutiny.RunMutinySweepAsync(CancellationToken.None);

                await mutiny.StopAsync(CancellationToken.None);

                AssertNoPlaintextCredentialFiles(mutinyIsolation!.ProcessTemporaryDirectory, mutiny.SensitiveValues());
            }

            // Every run has run: the findings of all of them are reported together.
            string[] findings = [.. run.Findings, .. mutiny?.Findings ?? []];

            Assert.True(
                findings.Length == 0,
                "The scenarios completed, but Long Rest has product-level findings:\n- " + string.Join("\n- ", findings));
        }
        catch (Exception exception)
        {
            InvalidOperationException wrapped = LocalOllamaQualificationGuards.CreateSanitizedFailure(
                "Local Native AOT/Ollama Long Rest qualification failed.\n" + log.Transcript,
                exception,
                LongRestQualificationConfiguration.ChatCredentialPlaceholder,
                run.MasterApiKey,
                [LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder, .. run.SensitiveValues(), .. mutiny?.SensitiveValues() ?? []]);

            primaryFailure = wrapped;

            throw wrapped;
        }
        finally
        {
            List<Exception> cleanupFailures = [];

            cleanupFailures.AddRange(await run.CleanupAsync(run.MasterApiKey));

            if (mutiny is not null)
            {
                cleanupFailures.AddRange(await mutiny.CleanupAsync(mutiny.MasterApiKey));
            }

            await WriteSummaryAsync(options, run, mutiny, log, primaryFailure is null);

            foreach (PublishedTestProcessIsolation? toDelete in new[] { isolation, mutinyIsolation })
            {
                if (toDelete is null)
                {
                    continue;
                }

                try
                {
                    toDelete.Delete();
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(
                        LocalOllamaQualificationGuards.CreateSanitizedFailure(
                            "Long Rest qualification temporary cleanup failed.",
                            cleanupFailure,
                            LongRestQualificationConfiguration.ChatCredentialPlaceholder,
                            run.MasterApiKey,
                            [LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder, .. run.SensitiveValues(), .. mutiny?.SensitiveValues() ?? []]));
                }
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
        LongRestQualificationRun? mutiny,
        LongRestQualificationLog log,
        bool succeeded)
    {
        StringBuilder text = new();

        _ = text.AppendLine("LONG REST QUALIFICATION SUMMARY")
            .AppendLine(succeeded ? "Result: every scenario passed." : "Result: the qualification FAILED; the observations below are those made before it stopped.")
            .AppendLine($"Total wall clock: {log.Elapsed:hh\\:mm\\:ss}")
            .AppendLine();

        foreach (string observation in run.Observations.Concat(mutiny?.Observations ?? []))
        {
            _ = text.Append("- ").AppendLine(observation);
        }

        foreach (string finding in run.Findings.Concat(mutiny?.Findings ?? []))
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

    /// <summary>
    /// Asks the embedding model for one vector, as the host's own Saga writes do (no size requested), and requires its length to be the
    /// configured dimensions: the host refuses a vector of any other length, and an extraction that writes nothing would otherwise be
    /// found only after the seeding budget.
    /// </summary>
    private static async Task AssertEmbeddingDimensionsAsync(LongRestQualificationOptions options)
    {
        int measured = await LongRestQualificationPreflight.MeasureEmbeddingDimensionsAsync(
            options.Endpoint,
            options.EmbeddingModel,
            CancellationToken.None);

        string? finding = LongRestQualificationChecks.EmbeddingDimensionsFinding(options.EmbeddingModel, options.EmbeddingDimensions, measured);

        Assert.True(finding is null, finding);
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
