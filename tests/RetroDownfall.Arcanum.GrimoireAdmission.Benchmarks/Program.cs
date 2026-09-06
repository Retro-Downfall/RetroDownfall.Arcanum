using System.Runtime;

using System.Runtime.CompilerServices;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal static class Program
{

    private const int InvalidEvidence = 2;

    internal static async Task<int> Main(string[] args)
    {

        if (global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME") is not null)
        {

            Console.Error.WriteLine("ARCANUM_TEST_HOME must be unset; the benchmark owns its isolated home.");

            return InvalidEvidence;

        }

        string sessionId = Option(args, "--session") ?? Guid.NewGuid().ToString("N");

        AdmissionBenchmarkHome? home = null;

        int exitCode;

        try
        {

            home = AdmissionBenchmarkHome.Create(sessionId);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", home.ChildPath);

            if (RuntimeFeature.IsDynamicCodeSupported)
            {

                Console.Error.WriteLine("The Grimoire admission benchmark must run as published Native AOT.");

                return InvalidEvidence;

            }

            AdmissionBenchmarkManifest manifest = AdmissionBenchmarkManifestLoader.Load();

            RunSchemaSelfTest(manifest, home);

            exitCode = await ExecuteModeAsync(args, manifest, sessionId).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            exitCode = 130;

        }
        catch (Exception exception)
        {

            Console.Error.WriteLine(exception.Message);

            exitCode = InvalidEvidence;

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", null);

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);

            if (home is not null && !home.TryDelete())
            {

                Console.Error.WriteLine($"Benchmark home safety validation failed; retained: {home.ChildPath}");

                exitCode = InvalidEvidence;

            }

        }

        return exitCode;

    }

    private static async Task<int> ExecuteModeAsync(
        string[] args,
        AdmissionBenchmarkManifest manifest,
        string sessionId)
    {

        if (args.Length == 1 && args[0] == "--smoke")
        {

            AdmissionBenchmarkRevisionRun run = await RunAsync(
                manifest,
                "smoke",
                new string('0', 40),
                sessionId,
                0,
                0,
                "H",
                Directory.GetCurrentDirectory()).ConfigureAwait(false);

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
                sourceRoot).ConfigureAwait(false);

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
        string sourceRoot)
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

        await using BenchmarkComposition composition = BenchmarkComposition.Create();

        GrimoireAdmissionWorkloadBed bed = new(composition);

        AdmissionBenchmarkCellResult[] cells = bed.Run(manifest, profile, CancellationToken.None);

        AdmissionBenchmarkHistoricalChurnResult[] historicalChurn = await bed
            .MeasureHistoricalChurnAsync(CancellationToken.None)
            .ConfigureAwait(false);

        AdmissionBenchmarkFinalState finalState = await bed.ValidateFinalStateAsync(CancellationToken.None)
            .ConfigureAwait(false);

        string digest = Sha256Hex(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            manifest,
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkManifest)));

        AdmissionBenchmarkInputIdentity inputs = CreateInputIdentity(sourceRoot, digest);

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
            GCSettings.LatencyMode != GCLatencyMode.Batch,
            RuntimeFeature.IsDynamicCodeSupported);

        return new(
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

    }

    private static AdmissionBenchmarkRevisionRun CreateSchemaRun(
        string role,
        int pairIndex,
        int orderPosition,
        string sessionId)
    {

        AdmissionBenchmarkConcurrency[] concurrency = AdmissionBenchmarkManifest.CreateDefault().Concurrency;

        AdmissionBenchmarkCellResult[] cells = AdmissionBenchmarkOperations.All
            .SelectMany(
                _ => concurrency,
                static (operation, concurrency) => new AdmissionBenchmarkCellResult(
                    operation,
                    concurrency.Id,
                    concurrency.Workers == 0 ? 1 : concurrency.Workers,
                    100,
                    10,
                    10,
                    10,
                    0,
                    0,
                    0,
                    1,
                    0,
                    1,
                    0))
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
        string manifestDigest)
    {

        string root = Path.GetFullPath(sourceRoot);

        string benchmarkRoot = Path.Combine(
            root,
            "tests",
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks");

        string[] harnessFiles = Directory.GetFiles(benchmarkRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => Path.GetFileName(path) != "packages.lock.json")
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] projectFiles =
        [
            Path.Combine(root, "Directory.Build.props"),
            Path.Combine(benchmarkRoot, "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj"),
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Core", "RetroDownfall.Arcanum.Core.csproj"),
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Infrastructure", "RetroDownfall.Arcanum.Infrastructure.csproj"),
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Secrets", "RetroDownfall.Arcanum.Secrets.csproj"),
        ];

        string lockfile = Path.Combine(benchmarkRoot, "packages.lock.json");

        string nativeRoot = Path.Combine(
            root,
            "src",
            "RetroDownfall.Arcanum.NativeSqlCipher");

        string[] nativeBuildInputs =
        [
            Path.Combine(nativeRoot, "native-source-manifest.json"),
            Path.Combine(nativeRoot, "RetroDownfall.Arcanum.NativeSqlCipher.csproj"),
            Path.Combine(nativeRoot, "build", "RetroDownfall.Arcanum.NativeSqlCipher.targets"),
            Path.Combine(nativeRoot, "buildTransitive", "RetroDownfall.Arcanum.NativeSqlCipher.targets"),
        ];

        string runtimeIdentifier = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

        string nativeBinary = Directory.GetFiles(
                nativeRoot,
                "*sqlcipher*",
                SearchOption.AllDirectories)
            .Where(path => path.Contains(
                    Path.DirectorySeparatorChar + runtimeIdentifier + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                && !path.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidDataException("The selected native SQLCipher binary is missing.");

        string[] sourceRoots =
        [
            benchmarkRoot,
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Core"),
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Infrastructure"),
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Secrets"),
        ];

        AdmissionBenchmarkDigestEntry[] sources = sourceRoots
            .SelectMany(static source => Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories))
            .Where(static path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(path => new AdmissionBenchmarkDigestEntry(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                Sha256Hex(File.ReadAllBytes(path)),
                true))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();

        string epochPath = "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs";

        if (!sources.Any(source => source.Path == epochPath))
        {

            sources = [.. sources, new(epochPath, string.Empty, false)];

            Array.Sort(sources, static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

        }

        return new(
            manifestDigest,
            DigestFiles(harnessFiles),
            DigestFiles(projectFiles),
            Sha256Hex(File.ReadAllBytes(lockfile)),
            Option(global::System.Environment.GetCommandLineArgs(), "--toolchain-digest")
                ?? Sha256Hex(Encoding.UTF8.GetBytes(global::System.Environment.Version.ToString())),
            DigestFiles(nativeBuildInputs),
            Sha256Hex(File.ReadAllBytes(nativeBinary)),
            sources);

    }

    private static string DigestFiles(IEnumerable<string> paths)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string path in paths)
        {

            byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(path));

            hash.AppendData(name);

            hash.AppendData(File.ReadAllBytes(path));

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

}

internal sealed class AdmissionBenchmarkHome
{

    private const string ParentPrefix = "arcanum-grimoire-admission-benchmark-";

    private const string ChildPrefix = "home-";

    private const string MarkerName = ".arcanum-benchmark-owner";

    private readonly string _markerContents;

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

        AdmissionBenchmarkHome home = new(nonce, sessionId, Path.GetFullPath(parent), Path.GetFullPath(child));

        using FileStream marker = new(
            Path.Combine(home.ChildPath, MarkerName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        using StreamWriter writer = new(marker, new UTF8Encoding(false));

        writer.Write(home._markerContents);

        return home;

    }

    internal bool TryDelete()
    {

        try
        {

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
            || parentInfo.Parent?.FullName != canonicalTemp
            || parentInfo.Name != ParentPrefix + nonce
            || childInfo.Parent?.FullName != canonicalParent
            || childInfo.Name != ChildPrefix + nonce)
        {

            return false;

        }

        string markerPath = Path.Combine(canonicalChild, MarkerName);

        if (!File.Exists(markerPath)
            || new FileInfo(markerPath).LinkTarget is not null)
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
