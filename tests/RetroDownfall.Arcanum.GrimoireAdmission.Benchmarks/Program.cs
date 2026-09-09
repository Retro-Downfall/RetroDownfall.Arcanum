using System.Buffers.Binary;

using System.Runtime;

using System.Runtime.CompilerServices;

using System.Runtime.InteropServices;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal static class Program
{
    private const int InvalidEvidence = 2;

    private const string BenchmarkPrefix = "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/";

    private const string CatalogPath = BenchmarkPrefix + "grimoire-admission-input-catalog-v1.txt";

    private const string EpochPath = "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs";

    private const string SchemaPrefix = "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/";

    private static readonly string[] RuntimeCSharpRoots =
    [
        "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
        "src/RetroDownfall.Arcanum.Core",
        "src/RetroDownfall.Arcanum.Infrastructure",
        "src/RetroDownfall.Arcanum.Secrets",
    ];

    private static readonly string[] FixedRuntimeInputs =
    [
        "Directory.Build.props",
        "scripts/benchmark-grimoire-admission.sh",
        CatalogPath,
        BenchmarkPrefix + "grimoire-admission-workload-v1.json",
        BenchmarkPrefix + "packages.lock.json",
        BenchmarkPrefix + "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj",
        "src/RetroDownfall.Arcanum.Core/RetroDownfall.Arcanum.Core.csproj",
        "src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj",
        "src/RetroDownfall.Arcanum.Secrets/RetroDownfall.Arcanum.Secrets.csproj",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/RetroDownfall.Arcanum.NativeSqlCipher.csproj",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/build/RetroDownfall.Arcanum.NativeSqlCipher.targets",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/buildTransitive/RetroDownfall.Arcanum.NativeSqlCipher.targets",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/osx-arm64/native/libe_sqlcipher.dylib",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-arm64/native/e_sqlcipher.dll",
        "src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-x64/native/e_sqlcipher.dll",
    ];

    internal static async Task<int> Main(string[] args)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            Console.Error.WriteLine("The Grimoire admission benchmark must run as published Native AOT.");

            return InvalidEvidence;
        }

        if (global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME") is not null)
        {
            Console.Error.WriteLine("ARCANUM_TEST_HOME must be unset; the benchmark owns its isolated home.");

            return InvalidEvidence;
        }

        string sessionId = Option(args, "--session") ?? Guid.NewGuid().ToString("N");

        AdmissionBenchmarkHome? home = null;

        int exitCode = InvalidEvidence;

        using CancellationTokenSource processCancellation = new();

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;

            processCancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        using PosixSignalRegistration termination = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;

                processCancellation.Cancel();
            });

        try
        {
            home = AdmissionBenchmarkHome.Create(sessionId);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", home.ChildPath);

            AdmissionBenchmarkManifest manifest = AdmissionBenchmarkManifestLoader.Load();

            RunSchemaSelfTest(manifest, home);

            exitCode = await ExecuteModeAsync(
                args,
                manifest,
                sessionId,
                home,
                processCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (processCancellation.IsCancellationRequested)
        {
            exitCode = 130;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The benchmark watchdog expired or a participant failed to reach a bounded terminal state.");

            exitCode = InvalidEvidence;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);

            exitCode = InvalidEvidence;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", null);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);

            if (home is not null && !home.TryDelete())
            {
                Console.Error.WriteLine($"Benchmark home safety validation failed; retained: {home.ChildPath}");

                if (exitCode != 130)
                {
                    exitCode = InvalidEvidence;
                }
            }
        }

        return exitCode;
    }

    private static async Task<int> ExecuteModeAsync(
        string[] args,
        AdmissionBenchmarkManifest manifest,
        string sessionId,
        AdmissionBenchmarkHome home,
        CancellationToken processCancellation)
    {
        if (args.Length == 3 && args[0] == "--smoke")
        {
            string sourceRoot = RequiredOption(args, "--source-root");

            AdmissionBenchmarkRevisionRun run = await RunAsync(
                manifest,
                "smoke",
                new string('0', 40),
                sessionId,
                0,
                0,
                "H",
                sourceRoot,
                home,
                processCancellation).ConfigureAwait(false);

            Console.WriteLine($"Grimoire admission Native AOT smoke passed ({run.Cells.Length} cells).");

            return run.ExitCode;
        }

        if (args.Length >= 1 && args[0] == "--measure")
        {
            if (args.Length != 23)
            {
                throw new InvalidDataException("The measure mode requires its exact closed argument set.");
            }

            string profile = RequiredOption(args, "--profile");

            string revision = RequiredOption(args, "--revision");

            string output = RequiredOption(args, "--out");

            int pair = ParseNonnegativeInt(RequiredOption(args, "--pair"));

            int order = ParseNonnegativeInt(RequiredOption(args, "--order"));

            string role = RequiredOption(args, "--role");

            string sourceRoot = RequiredOption(args, "--source-root");

            AdmissionBenchmarkRevisionRun run = await RunAsync(
                manifest,
                profile,
                revision,
                sessionId,
                pair,
                order,
                role,
                sourceRoot,
                home,
                processCancellation).ConfigureAwait(false);

            WriteAtomic(
                output,
                JsonSerializer.Serialize(
                    run,
                    AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkRevisionRun));

            return run.ExitCode;
        }

        if (args.Length >= 1 && args[0] == "--compare")
        {
            if (args.Length != 9)
            {
                throw new InvalidDataException("The compare mode requires its exact closed argument set.");
            }

            string runsDirectory = RequiredOption(args, "--runs-dir");

            string output = RequiredOption(args, "--out");

            string bundleOutput = RequiredOption(args, "--bundle-out");

            AdmissionBenchmarkPair[] pairs = new AdmissionBenchmarkPair[6];

            for (int pair = 0; pair < pairs.Length; pair++)
            {
                AdmissionBenchmarkRevisionRun baseline = ReadRun(
                    Path.Combine(runsDirectory, $"pair-{pair}-B.json"));

                AdmissionBenchmarkRevisionRun candidate = ReadRun(
                    Path.Combine(runsDirectory, $"pair-{pair}-C.json"));

                pairs[pair] = new(
                    pair,
                    pair % 2 == 0 ? "B" : "C",
                    pair % 2 == 0 ? "C" : "B",
                    baseline,
                    candidate);
            }

            AdmissionBenchmarkEvidenceBundle bundle = new(sessionId, pairs);

            AdmissionBenchmarkEvidenceValidation validation = AdmissionBenchmarkEvidence.Validate(
                manifest,
                bundle,
                baseIsAncestor: true);

            AdmissionBenchmarkComparisonReport report = validation.Valid
                ? AdmissionBenchmarkComparison.Compare(manifest, bundle)
                : new(false, false, InvalidEvidence, 0, 0, 0, validation.Errors);

            WriteAtomic(
                bundleOutput,
                JsonSerializer.Serialize(
                    bundle,
                    AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkEvidenceBundle));

            WriteAtomic(
                output,
                JsonSerializer.Serialize(
                    report,
                    AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkComparisonReport));

            return report.ExitCode;
        }

        Console.Error.WriteLine("Expected --smoke, --measure, or --compare with the closed benchmark arguments.");

        return InvalidEvidence;
    }

    internal static void RunSchemaSelfTest(
        AdmissionBenchmarkManifest manifest,
        AdmissionBenchmarkHome home)
    {
        string manifestJson = JsonSerializer.Serialize(
            manifest,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkManifest);

        AdmissionBenchmarkManifest parsedManifest = AdmissionBenchmarkManifest.Parse(
            manifestJson,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkManifest);

        if (parsedManifest.Operations.Length != 9 || parsedManifest.Concurrency.Length != 4)
        {
            throw new InvalidDataException("Manifest JSON self-test did not retain the closed schema.");
        }

        AdmissionBenchmarkRevisionRun run = CreateSchemaRun("B", 0, 0, "schema");

        string runJson = JsonSerializer.Serialize(
            run,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkRevisionRun);

        AdmissionBenchmarkRevisionRun parsedRun = JsonSerializer.Deserialize(
            runJson,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkRevisionRun)
            ?? throw new InvalidDataException("Revision-run JSON self-test returned null.");

        AdmissionBenchmarkPair[] pairs = new AdmissionBenchmarkPair[6];

        for (int index = 0; index < pairs.Length; index++)
        {
            string first = index % 2 == 0 ? "B" : "C";

            string second = first == "B" ? "C" : "B";

            pairs[index] = new(
                index,
                first,
                second,
                CreateSchemaRun("B", index, first == "B" ? 0 : 1, "schema"),
                CreateSchemaRun("C", index, first == "C" ? 0 : 1, "schema"));
        }

        AdmissionBenchmarkEvidenceBundle bundle = new("schema", pairs);

        string bundleJson = JsonSerializer.Serialize(
            bundle,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkEvidenceBundle);

        AdmissionBenchmarkEvidenceBundle parsedBundle = AdmissionBenchmarkEvidence.ParseBundle(
            bundleJson,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkEvidenceBundle);

        AdmissionBenchmarkComparisonReport accepted = new(true, true, 0, 1.2, 1.2, 1, []);

        AdmissionBenchmarkComparisonReport rejected = new(true, false, 1, 1, 1, 1, ["schema rejection"]);

        AdmissionBenchmarkComparisonReport? parsedAccepted = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(
                accepted,
                AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkComparisonReport),
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkComparisonReport);

        AdmissionBenchmarkComparisonReport? parsedRejected = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(
                rejected,
                AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkComparisonReport),
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkComparisonReport);

        if (parsedRun.Cells.Length != 36
            || parsedRun.HistoricalChurn.Length != 3
            || parsedBundle.Pairs.Length != 6
            || parsedAccepted is not { Accepted: true, ExitCode: 0 }
            || parsedRejected is not { Accepted: false, ExitCode: 1 })
        {
            throw new InvalidDataException("Qualification JSON roots failed the closed-schema self-test.");
        }

        home.RunSafetySelfTest();
    }

    private static async Task<AdmissionBenchmarkRevisionRun> RunAsync(
        AdmissionBenchmarkManifest manifest,
        string profileName,
        string revision,
        string sessionId,
        int pairIndex,
        int orderPosition,
        string role,
        string sourceRoot,
        AdmissionBenchmarkHome home,
        CancellationToken processCancellation)
    {
        AdmissionBenchmarkProfile profile = manifest.Profiles.SingleOrDefault(
            item => item.Name == profileName)
            ?? throw new InvalidDataException("The requested benchmark profile is not declared in the manifest.");

        if (revision.Length != 40 || revision.Any(static value => !char.IsAsciiHexDigit(value) || char.IsUpper(value)))
        {
            throw new InvalidDataException("The revision must be exact lowercase 40-hex.");
        }

        if (role is not "H" and not "B" and not "C")
        {
            throw new InvalidDataException("The benchmark role must be H, B, or C.");
        }

        using CancellationTokenSource watchdog = new(
            TimeSpan.FromSeconds(profile.MaximumDurationSeconds));

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            processCancellation,
            watchdog.Token);

        CancellationToken cancellationToken = linked.Token;

        string digest = Sha256Hex(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            manifest,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkManifest)));

        AdmissionBenchmarkInputIdentity inputs = CreateInputIdentity(sourceRoot, manifest, digest);

        BenchmarkComposition? composition = null;

        GrimoireAdmissionWorkloadBed? bed = null;

        bool bedDisposalAttempted = false;

        bool measuredResourcesDisposed = false;

        Exception? primaryException = null;

        try
        {
            home.MarkRuntimeResourcesCreated();

            composition = BenchmarkComposition.Create();

            bed = new(composition);

            AdmissionBenchmarkCellResult[] cells = bed.Run(manifest, profile, cancellationToken);

            AdmissionBenchmarkHistoricalChurnResult[] historicalChurn = await bed
                .MeasureHistoricalChurnAsync(cancellationToken)
                .ConfigureAwait(false);

            AdmissionBenchmarkFinalState finalState = await AdmissionBenchmarkLifecycleCoordinator
                .DisposeThenSampleAsync(
                    () =>
                    {
                        bedDisposalAttempted = true;

                        bed.Dispose();

                        AdmissionBenchmarkMeasuredResourceWitness witness = bed.MeasuredResourceWitness;

                        measuredResourcesDisposed = witness.HomeDeletionAuthorized;

                        home.RecordMeasuredResourceTeardown(witness);

                        return witness;
                    },
                    bed.ValidateFinalStateAsync,
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyDictionary<string, object> gcConfiguration = GC.GetConfigurationVariables();

            if (!gcConfiguration.TryGetValue("ConcurrentGC", out object? concurrentGcValue)
                || concurrentGcValue is not bool concurrentGc)
            {
                throw new InvalidDataException("The runtime did not expose an exact Boolean ConcurrentGC configuration.");
            }

            AdmissionBenchmarkEnvironmentIdentity environment = new(
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                global::System.Environment.Version.ToString(),
                Option(global::System.Environment.GetCommandLineArgs(), "--sdk") ?? "unknown",
                Option(global::System.Environment.GetCommandLineArgs(), "--cpu") ?? "unknown",
                global::System.Environment.ProcessorCount,
                System.Diagnostics.Stopwatch.Frequency,
                GCSettings.IsServerGC,
                concurrentGc,
                RuntimeFeature.IsDynamicCodeSupported);

            AdmissionBenchmarkRevisionRun run = new(
                revision,
                true,
                sessionId,
                pairIndex,
                orderPosition,
                role,
                profile.Name,
                inputs,
                environment,
                cells,
                finalState,
                0)
            {
                HistoricalChurn = historicalChurn,
            };

            string[] validationErrors = AdmissionBenchmarkRunValidator.Validate(
                manifest,
                run,
                profile.Name,
                sessionId);

            if (validationErrors.Length != 0)
            {
                throw new InvalidDataException(
                    "The benchmark run failed exact invariant validation: "
                    + string.Join("; ", validationErrors));
            }

            return run;
        }
        catch (Exception exception)
        {
            primaryException = exception;

            throw;
        }
        finally
        {
            if (bed is not null && !bedDisposalAttempted)
            {
                bedDisposalAttempted = true;

                try
                {
                    bed.Dispose();

                    AdmissionBenchmarkMeasuredResourceWitness witness = bed.MeasuredResourceWitness;

                    measuredResourcesDisposed = witness.HomeDeletionAuthorized;

                    home.RecordMeasuredResourceTeardown(witness);

                    if (!witness.HomeDeletionAuthorized)
                    {
                        throw new InvalidDataException(
                            "Benchmark workers or their measured connections did not complete teardown.");
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Benchmark measured-resource teardown failed: {exception.Message}");
                }
            }

            if (composition is not null)
            {
                AdmissionBenchmarkTeardownWitness witness = await composition.TeardownAsync().ConfigureAwait(false);

                home.RecordTeardown(witness);

                if (!witness.HomeDeletionAuthorized)
                {
                    Console.Error.WriteLine(
                        "Benchmark runtime teardown was incomplete: " + string.Join("; ", witness.Errors));

                    if (primaryException is null)
                    {
                        throw new InvalidDataException("Benchmark runtime teardown was incomplete.");
                    }
                }
            }

            if (!measuredResourcesDisposed && bed is not null)
            {
                Console.Error.WriteLine("Benchmark measured resources did not produce a positive disposal witness.");
            }
        }
    }

    private static AdmissionBenchmarkRevisionRun CreateSchemaRun(
        string role,
        int pairIndex,
        int orderPosition,
        string sessionId)
    {
        AdmissionBenchmarkManifest manifest = AdmissionBenchmarkManifest.CreateDefault();

        AdmissionBenchmarkConcurrency[] concurrency = manifest.Concurrency;

        AdmissionBenchmarkProfile profile = manifest.Profiles[0];

        AdmissionBenchmarkCellResult[] cells = AdmissionBenchmarkOperations.All
            .SelectMany(
                operation => concurrency.Select(concurrency => new AdmissionBenchmarkCellResult(
                    operation,
                    concurrency.Id,
                    concurrency.Workers == 0 ? 1 : concurrency.Workers,
                    profile.WarmupIterations,
                    profile.LatencySampleCount,
                    checked((long)profile.LatencySampleCount * profile.LatencyBundleSize),
                    profile.ThroughputIterations,
                    100,
                    10,
                    10,
                    10,
                    0,
                    profile.ThroughputIterations,
                    0,
                    0,
                    0,
                    AdmissionBenchmarkExpected.OperationCount(profile),
                    0,
                    AdmissionBenchmarkExpected.Checksum(
                        profile,
                        operation,
                        concurrency.Workers == 0 ? 1 : concurrency.Workers),
                    0)))
            .ToArray();

        string digest = new('1', 64);

        AdmissionBenchmarkInputIdentity inputs = new(
            digest,
            digest,
            digest,
            digest,
            digest,
            digest,
            digest,
            digest,
            digest,
            []);

        AdmissionBenchmarkEnvironmentIdentity environment = new(
            "osx-arm64",
            "Arm64",
            "Arm64",
            "schema",
            "10.0.0",
            "10.0.100",
            "schema",
            1,
            System.Diagnostics.Stopwatch.Frequency,
            false,
            false,
            false);

        return new(
            role == "B" ? new string('a', 40) : new string('b', 40),
            true,
            sessionId,
            pairIndex,
            orderPosition,
            role,
            "qualification",
            inputs,
            environment,
            cells,
            new(0, 0, 0, 0, 0, true, true),
            0)
        {
            HistoricalChurn =
            [
                new(0, 1, 1, 1, true),
                new(64, 1, 1, 1, true),
                new(640, 1, 1, 1, true),
            ],
        };
    }

    private static AdmissionBenchmarkRevisionRun ReadRun(string path)
    {
        using FileStream stream = File.OpenRead(path);

        return JsonSerializer.Deserialize(
            stream,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkRevisionRun)
            ?? throw new InvalidDataException($"The revision run '{path}' was empty.");
    }

    private static AdmissionBenchmarkInputIdentity CreateInputIdentity(
        string sourceRoot,
        AdmissionBenchmarkManifest manifest,
        string manifestDigest)
    {
        string root = ValidateSourceRoot(sourceRoot);

        string catalogContents = File.ReadAllText(Path.Combine(root, CatalogPath), Encoding.UTF8);

        using Stream catalogStream = typeof(Program).Assembly.GetManifestResourceStream(
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.grimoire-admission-input-catalog-v1.txt")
            ?? throw new InvalidDataException("The embedded input catalog is missing.");

        using StreamReader catalogReader = new(catalogStream, Encoding.UTF8);

        if (catalogReader.ReadToEnd() != catalogContents)
        {
            throw new InvalidDataException("The embedded and source-root input catalogs differ.");
        }

        AdmissionBenchmarkCatalogEntry[] catalog = ParseCatalog(catalogContents);

        AdmissionBenchmarkCatalogEntry[] independentlySelected = EnumerateRuntimeInputs(root);

        if (!catalog.SequenceEqual(independentlySelected))
        {
            throw new InvalidDataException(
                "The input catalog does not match the host's independently enumerated runtime input set.");
        }

        ValidateEmbeddedSchema(independentlySelected);

        string shapeDigest = CatalogShapeDigest(catalog);

        if (shapeDigest != manifest.InputCatalogShapeDigest)
        {
            throw new InvalidDataException("The input catalog shape does not match the immutable manifest pin.");
        }

        AdmissionBenchmarkDigestEntry[] inputs = independentlySelected.Select(
                entry =>
                {
                    string fullPath = Path.Combine(root, entry.Path);

                    bool present = File.Exists(fullPath);

                    if (!present && !entry.Optional)
                    {
                        throw new InvalidDataException($"Required benchmark input '{entry.Path}' is missing.");
                    }

                    return new AdmissionBenchmarkDigestEntry(
                        entry.Path,
                        present ? Sha256Hex(File.ReadAllBytes(fullPath)) : string.Empty,
                        present);
                })
            .ToArray();

        string runtimeIdentifier = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

        AdmissionBenchmarkDigestEntry nativeBinary = inputs.SingleOrDefault(
                entry => entry.Path.StartsWith(
                        $"src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/{runtimeIdentifier}/",
                        StringComparison.Ordinal)
                    && entry.Present)
            ?? throw new InvalidDataException("The selected native SQLCipher binary is missing.");

        string toolchainDigest = Option(global::System.Environment.GetCommandLineArgs(), "--toolchain-digest")
            ?? Sha256Hex(Encoding.UTF8.GetBytes(global::System.Environment.Version.ToString()));

        if (!IsDigest(toolchainDigest))
        {
            throw new InvalidDataException("The toolchain digest must be nonempty lowercase 64-hex.");
        }

        return new(
            shapeDigest,
            DigestContents(
                root,
                inputs.Where(entry => !manifest.SourceDifferenceAllowlist.Contains(entry.Path, StringComparer.Ordinal))),
            manifestDigest,
            DigestContents(root, inputs.Where(static entry => entry.Path.StartsWith(BenchmarkPrefix, StringComparison.Ordinal))),
            DigestContents(root, inputs.Where(static entry => entry.Path == "Directory.Build.props" || entry.Path.EndsWith(".csproj", StringComparison.Ordinal) || entry.Path.EndsWith(".targets", StringComparison.Ordinal))),
            inputs.Single(static entry => entry.Path.EndsWith("/packages.lock.json", StringComparison.Ordinal)).Digest,
            toolchainDigest,
            inputs.Single(static entry => entry.Path.EndsWith("/native-source-manifest.json", StringComparison.Ordinal)).Digest,
            nativeBinary.Digest,
            inputs);
    }

    private static string ValidateSourceRoot(string sourceRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));

        DirectoryInfo rootInfo = new(root);

        if (!rootInfo.Exists
            || rootInfo.LinkTarget is not null
            || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The source root must be an existing canonical non-link directory.");
        }

        return root;
    }

    private static AdmissionBenchmarkCatalogEntry[] EnumerateRuntimeInputs(string root)
    {
        List<AdmissionBenchmarkCatalogEntry> entries = [];

        foreach (string relativeRoot in RuntimeCSharpRoots)
        {
            foreach (string path in EnumerateSelectedFiles(root, relativeRoot, ".cs"))
            {
                if (path != EpochPath)
                {
                    entries.Add(new(path, false));
                }
            }
        }

        foreach (string path in EnumerateSelectedFiles(
                     root,
                     "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema",
                     ".sql"))
        {
            entries.Add(new(path, false));
        }

        entries.AddRange(FixedRuntimeInputs.Select(static path => new AdmissionBenchmarkCatalogEntry(path, false)));

        entries.Add(new(EpochPath, true));

        AdmissionBenchmarkCatalogEntry[] selected = [.. entries.OrderBy(static entry => entry.Path, StringComparer.Ordinal)];

        if (selected.Select(static entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != selected.Length)
        {
            throw new InvalidDataException("The host's independently enumerated runtime input set contains a duplicate.");
        }

        return selected;
    }

    private static IEnumerable<string> EnumerateSelectedFiles(
        string root,
        string relativeRoot,
        string extension)
    {
        string selectionRoot = Path.Combine(root, relativeRoot);

        DirectoryInfo selectionRootInfo = new(selectionRoot);

        if (!selectionRootInfo.Exists
            || selectionRootInfo.LinkTarget is not null
            || selectionRootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Runtime input root '{relativeRoot}' is missing or is a link.");
        }

        Stack<DirectoryInfo> pending = new();

        pending.Push(selectionRootInfo);

        while (pending.TryPop(out DirectoryInfo? directory))
        {
            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos())
            {
                if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"Runtime input path '{child.FullName}' is a link.");
                }

                if (child is DirectoryInfo childDirectory)
                {
                    if (childDirectory.Name is not "bin" and not "obj")
                    {
                        pending.Push(childDirectory);
                    }

                    continue;
                }

                if (child is FileInfo file
                    && file.Extension.Equals(extension, StringComparison.Ordinal))
                {
                    string relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');

                    if (relative.StartsWith("../", StringComparison.Ordinal)
                        || relative == ".."
                        || Path.IsPathFullyQualified(relative))
                    {
                        throw new InvalidDataException("A runtime input escaped the canonical source root.");
                    }

                    yield return relative;
                }
            }
        }
    }

    private static void ValidateEmbeddedSchema(IEnumerable<AdmissionBenchmarkCatalogEntry> inputs)
    {
        const string resourcePrefix = "RetroDownfall.Arcanum.Infrastructure.Data.Schema.";

        const string resourceSuffix = ".sql";

        string[] sourceSchema = inputs
            .Where(static entry => entry.Path.StartsWith(SchemaPrefix, StringComparison.Ordinal)
                && entry.Path.EndsWith(resourceSuffix, StringComparison.Ordinal))
            .Select(static entry => entry.Path[SchemaPrefix.Length..^resourceSuffix.Length].Replace('/', '.'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] embeddedSchema = typeof(RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContext)
            .Assembly
            .GetManifestResourceNames()
            .Where(static name => name.StartsWith(resourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(resourceSuffix, StringComparison.Ordinal))
            .Select(static name => name[resourcePrefix.Length..^resourceSuffix.Length])
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (!sourceSchema.SequenceEqual(embeddedSchema, StringComparer.Ordinal))
        {
            int mismatch = Enumerable.Range(0, Math.Min(sourceSchema.Length, embeddedSchema.Length))
                .FirstOrDefault(index => sourceSchema[index] != embeddedSchema[index], -1);

            throw new InvalidDataException(
                "The source-root schema input set does not match the embedded Infrastructure schema resources. "
                + $"Source count {sourceSchema.Length}, embedded count {embeddedSchema.Length}, first mismatch "
                + $"{mismatch}: '{(mismatch >= 0 ? sourceSchema[mismatch] : "<count>")}' versus "
                + $"'{(mismatch >= 0 ? embeddedSchema[mismatch] : "<count>")}'.");
        }
    }

    private static AdmissionBenchmarkCatalogEntry[] ParseCatalog(string contents)
    {
        if (string.IsNullOrEmpty(contents)
            || !contents.EndsWith('\n')
            || contents.Contains('\r'))
        {
            throw new InvalidDataException("The input catalog must use exact nonempty LF-delimited framing.");
        }

        AdmissionBenchmarkCatalogEntry[] entries = contents[..^1].Split('\n')
            .Select(
                static line =>
                {
                    string[] parts = line.Split('\t');

                    if (parts.Length != 2
                        || parts[0] is not "R" and not "O"
                        || string.IsNullOrEmpty(parts[1]))
                    {
                        throw new InvalidDataException("The input catalog contains a malformed entry.");
                    }

                    string itemPath = parts[1];

                    if (Path.IsPathFullyQualified(itemPath)
                        || itemPath.Contains('\\')
                        || itemPath.Split('/').Any(static segment => segment is "" or "." or ".."))
                    {
                        throw new InvalidDataException("The input catalog contains a non-normalized path.");
                    }

                    return new AdmissionBenchmarkCatalogEntry(itemPath, parts[0] == "O");
                })
            .ToArray();

        if (entries.Length == 0
            || !entries.SequenceEqual(entries.OrderBy(static entry => entry.Path, StringComparer.Ordinal))
            || entries.Select(static entry => entry.Path).Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw new InvalidDataException("The input catalog must be nonempty, sorted, and unique.");
        }

        return entries;
    }

    private static string CatalogShapeDigest(IEnumerable<AdmissionBenchmarkCatalogEntry> entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> length = stackalloc byte[sizeof(int)];

        foreach (AdmissionBenchmarkCatalogEntry entry in entries)
        {
            byte[] path = Encoding.UTF8.GetBytes(entry.Path);

            BinaryPrimitives.WriteInt32LittleEndian(length, path.Length);

            hash.AppendData(length);

            hash.AppendData(path);

            hash.AppendData([entry.Optional ? (byte)1 : (byte)0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string DigestContents(
        string root,
        IEnumerable<AdmissionBenchmarkDigestEntry> entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> pathLength = stackalloc byte[sizeof(int)];

        Span<byte> contentLength = stackalloc byte[sizeof(long)];

        foreach (AdmissionBenchmarkDigestEntry entry in entries)
        {
            byte[] path = Encoding.UTF8.GetBytes(entry.Path);

            byte[] contents = entry.Present
                ? File.ReadAllBytes(Path.Combine(root, entry.Path))
                : [];

            BinaryPrimitives.WriteInt32LittleEndian(pathLength, path.Length);

            BinaryPrimitives.WriteInt64LittleEndian(contentLength, contents.LongLength);

            hash.AppendData(pathLength);

            hash.AppendData(path);

            hash.AppendData([entry.Present ? (byte)1 : (byte)0]);

            hash.AppendData(contentLength);

            hash.AppendData(contents);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string RequiredOption(string[] args, string name) =>
        Option(args, name) ?? throw new InvalidDataException($"Missing required option {name}.");

    private static string? Option(string[] args, string name)
    {
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int ParseNonnegativeInt(string value) =>
        int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : throw new InvalidDataException("A numeric benchmark option is invalid.");

    private static void WriteAtomic(string path, string contents)
    {
        string fullPath = Path.GetFullPath(path);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        string temporary = fullPath + ".tmp." + Guid.NewGuid().ToString("N");

        File.WriteAllText(temporary, contents, new UTF8Encoding(false));

        File.Move(temporary, fullPath, overwrite: false);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsDigest(string value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed class AdmissionBenchmarkHome
{
    private const string ParentPrefix = "arcanum-grimoire-admission-benchmark-";

    private const string ChildPrefix = "home-";

    private const string MarkerName = ".arcanum-benchmark-owner";

    private readonly string _markerContents;

    private bool _deletionAuthorized = true;

    private bool _measuredResourcesDisposed;

    private AdmissionBenchmarkHome(
        string nonce,
        string sessionId,
        string parentPath,
        string childPath)
    {
        Nonce = nonce;

        SessionId = sessionId;

        ParentPath = parentPath;

        ChildPath = childPath;

        _markerContents = MarkerContents(nonce, sessionId, global::System.Environment.ProcessId, childPath);
    }

    internal string Nonce { get; }

    internal string SessionId { get; }

    internal string ParentPath { get; }

    internal string ChildPath { get; }

    internal static AdmissionBenchmarkHome Create(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string nonce = Guid.NewGuid().ToString("N");

        string parent = Path.Combine(Path.GetTempPath(), ParentPrefix + nonce);

        string child = Path.Combine(parent, ChildPrefix + nonce);

        Directory.CreateDirectory(child);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            File.SetUnixFileMode(
                child,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        AdmissionBenchmarkHome home = new(nonce, sessionId, Path.GetFullPath(parent), Path.GetFullPath(child));

        using FileStream marker = new(
            Path.Combine(home.ChildPath, MarkerName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        using StreamWriter writer = new(marker, new UTF8Encoding(false));

        writer.Write(home._markerContents);

        writer.Flush();

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Combine(home.ChildPath, MarkerName),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return home;
    }

    internal void MarkRuntimeResourcesCreated() => _deletionAuthorized = false;

    internal void RecordMeasuredResourceTeardown(AdmissionBenchmarkMeasuredResourceWitness witness) =>
        _measuredResourcesDisposed = witness.HomeDeletionAuthorized;

    internal void RecordTeardown(AdmissionBenchmarkTeardownWitness witness) =>
        _deletionAuthorized = _measuredResourcesDisposed && witness.HomeDeletionAuthorized;

    internal bool TryDelete()
    {
        try
        {
            if (!_deletionAuthorized)
            {
                return false;
            }

            if (!CanDelete(ParentPath, ChildPath, Nonce, SessionId, _markerContents))
            {
                return false;
            }

            Directory.Delete(ChildPath, recursive: true);

            Directory.Delete(ParentPath, recursive: false);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal void RunSafetySelfTest()
    {
        string fixture = Path.Combine(ChildPath, "safety-fixtures");

        Directory.CreateDirectory(fixture);

        string preexisting = Path.Combine(fixture, "pre-existing");

        Directory.CreateDirectory(preexisting);

        if (CanDelete(fixture, preexisting, Nonce, SessionId, _markerContents))
        {
            throw new InvalidDataException("A pre-existing directory passed benchmark-home ownership validation.");
        }

        string userHome = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.UserProfile);

        if (CanDelete(userHome, userHome, Nonce, SessionId, _markerContents))
        {
            throw new InvalidDataException("The default user home passed benchmark-home validation.");
        }

        if (CanDelete(ParentPath, ChildPath, Nonce, SessionId, _markerContents + "changed"))
        {
            throw new InvalidDataException("A changed ownership marker passed benchmark-home validation.");
        }

        string wrongChild = Path.Combine(ParentPath, ChildPrefix + Guid.NewGuid().ToString("N"));

        if (CanDelete(ParentPath, wrongChild, Nonce, SessionId, _markerContents))
        {
            throw new InvalidDataException("A path mismatch passed benchmark-home validation.");
        }

        string missingMarker = Path.Combine(ParentPath, ChildPrefix + Nonce + "-missing");

        if (CanDelete(ParentPath, missingMarker, Nonce, SessionId, _markerContents))
        {
            throw new InvalidDataException("A missing marker passed benchmark-home validation.");
        }

        if (!OperatingSystem.IsWindows())
        {
            string attackNonce = Guid.NewGuid().ToString("N");

            string targetParent = Path.Combine(Path.GetTempPath(), "arcanum-grimoire-admission-sentinel-" + attackNonce);

            string targetChild = Path.Combine(targetParent, ChildPrefix + attackNonce);

            string sentinel = Path.Combine(targetChild, "sentinel");

            string linkParent = Path.Combine(Path.GetTempPath(), ParentPrefix + attackNonce);

            Directory.CreateDirectory(targetChild);

            File.WriteAllText(sentinel, "survive", Encoding.UTF8);

            Directory.CreateSymbolicLink(linkParent, targetParent);

            try
            {
                string linkChild = Path.Combine(linkParent, ChildPrefix + attackNonce);

                string marker = MarkerContents(
                    attackNonce,
                    SessionId,
                    global::System.Environment.ProcessId,
                    Path.GetFullPath(linkChild));

                File.WriteAllText(Path.Combine(targetChild, MarkerName), marker, Encoding.UTF8);

                if (CanDelete(linkParent, linkChild, attackNonce, SessionId, marker)
                    || !File.Exists(sentinel))
                {
                    throw new InvalidDataException("A real symlink replacement passed cleanup validation or changed its target.");
                }
            }
            finally
            {
                Directory.Delete(linkParent);

                Directory.Delete(targetParent, recursive: true);
            }
        }
    }

    private static bool CanDelete(
        string parent,
        string child,
        string nonce,
        string sessionId,
        string expectedMarker)
    {
        string canonicalTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        string canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));

        string canonicalChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));

        DirectoryInfo parentInfo = new(canonicalParent);

        DirectoryInfo childInfo = new(canonicalChild);

        if (!parentInfo.Exists
            || !childInfo.Exists
            || parentInfo.LinkTarget is not null
            || childInfo.LinkTarget is not null
            || parentInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || childInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || parentInfo.Parent?.FullName != canonicalTemp
            || parentInfo.Name != ParentPrefix + nonce
            || childInfo.Parent?.FullName != canonicalParent
            || childInfo.Name != ChildPrefix + nonce)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(canonicalParent)
                    != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                || File.GetUnixFileMode(canonicalChild)
                    != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)))
        {
            return false;
        }

        string markerPath = Path.Combine(canonicalChild, MarkerName);

        FileInfo markerInfo = new(markerPath);

        if (!markerInfo.Exists
            || markerInfo.LinkTarget is not null
            || markerInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !OperatingSystem.IsWindows()
                && File.GetUnixFileMode(markerPath) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
        {
            return false;
        }

        string actual = File.ReadAllText(markerPath, Encoding.UTF8);

        return actual == expectedMarker
            && actual == MarkerContents(nonce, sessionId, global::System.Environment.ProcessId, canonicalChild);
    }

    private static string MarkerContents(
        string nonce,
        string sessionId,
        int processId,
        string canonicalChild) =>
        $"nonce={nonce}\nsession={sessionId}\npid={processId}\npath={canonicalChild}\n";
}
