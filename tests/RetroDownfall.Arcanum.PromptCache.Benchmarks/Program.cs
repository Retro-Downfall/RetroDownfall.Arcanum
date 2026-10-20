using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.PromptCache.Benchmarks;

if (args is ["--help"])
{
    Console.WriteLine("Prompt cache benchmark: --fixture|--smoke OR --ollama-endpoint http://127.0.0.1:11434/v1 --model gemma4:e4b|qwen3:4b-instruct-2507-q4_K_M; --record FILE --source-root DIRECTORY [--pairs 2..12] [--samples 1..10] [--cold-start observed|reload]. Reload unloads only the selected model before each role block; it never stops the daemon. Observed labels the first call firstObserved, without a cold-cache guarantee. Fixture times only local planning/SDK serialization/fixture transport. Live reports charge zero for provider usage; energy/hardware are unknown. Exit 0 accepted fixture/live utility, 1 valid but utility rejected, 2 invalid/unmeasurable, 130 cancellation.");
    return 0;
}

if (!PromptCacheBenchmarkArguments.TryParse(args, out var parsed) || parsed is null)
{
    Console.Error.WriteLine("Invalid benchmark arguments. Use --help.");
    return 2;
}

using CancellationTokenSource cancellation = new();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

try
{
    var loaded = PromptCacheBenchmarkManifestReader.Load();

    var manifest = loaded.Manifest with { Model = parsed.Model, Pairs = parsed.Pairs ?? (parsed.Smoke ? 2 : loaded.Manifest.Pairs), Samples = parsed.Samples ?? (parsed.Smoke ? 1 : loaded.Manifest.Samples) };

    cancellation.CancelAfter(TimeSpan.FromSeconds(manifest.MaximumSeconds));

    var identity = await IdentityAsync(Path.GetFullPath(parsed.SourceRoot), cancellation.Token);

    using BenchmarkOllamaMetadata? metadata = parsed.Fixture ? null : new(parsed.Endpoint!, manifest.Model);

    if (metadata is not null) await metadata.VerifyAsync(cancellation.Token);

    var result = parsed.Fixture ? await PromptCacheBenchmarkRunner.CollectFixtureAsync(manifest, cancellation.Token)
        : await PromptCacheBenchmarkRunner.CollectAsync(manifest, parsed.Endpoint, parsed.ColdStart, metadata, cancellation.Token);

    var report = new PromptCacheBenchmarkReport(1, parsed.Fixture ? "fixture" : "liveOllama", loaded.Digest, loaded.CorpusDigest, result.ToolDigest, result.SettingsDigest,
        manifest.Model, metadata?.ModelDigest, metadata?.Version ?? "fixture", identity,
        manifest.Pairs, manifest.Samples, manifest.Warmup, manifest.MaxOutputTokens, parsed.Fixture ? "fixtureNoProviderCharge" : "localOllamaUsageChargeZero", 0, null, null,
        result.FailureCode == "complete" && result.Samples.All(s => s.WireValid), result.Samples, result.Invalidations,
        parsed.Fixture ? "fixture" : parsed.ColdStart, result.Probes, result.FailureCode);

    // The diagnostic report is retained before any verdict/exit gate, including incomplete runs.
    string output = Path.GetFullPath(parsed.Record);

    Directory.CreateDirectory(Path.GetDirectoryName(output)!);

    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkReport));

    var verdict = PromptCacheBenchmarkEvidence.Evaluate(report);

    await File.WriteAllTextAsync(output + ".verdict.json", JsonSerializer.Serialize(verdict, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkVerdict));

    Console.WriteLine(JsonSerializer.Serialize(verdict, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkVerdict));

    if (result.FailureCode != "complete") return cancellation.IsCancellationRequested ? 130 : 2;

    return parsed.Fixture ? verdict.CorrectnessAccepted ? 0 : 2 : verdict.LiveUtilityAccepted ? 0 : 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Benchmark cancelled or deadline exceeded.");
    return 130;
}
catch (Exception)
{
    // Provider bodies, paths, keys and exception messages never enter logs or reports.
    Console.Error.WriteLine("Benchmark initialization or report persistence failed.");
    return 2;
}

static async Task<PromptCacheBenchmarkIdentity> IdentityAsync(string root, CancellationToken ct)
{
    string revision = (await CommandAsync("git", root, ["rev-parse", "HEAD"], ct)).Trim();

    string tree = (await CommandAsync("git", root, ["rev-parse", "HEAD^{tree}"], ct)).Trim();

    bool dirty = (await CommandAsync("git", root, ["status", "--porcelain", "--untracked-files=all"], ct)).Length > 0;

    string[] tracked = (await CommandAsync("git", root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    foreach (string path in tracked.Where(p => p.StartsWith("src/", StringComparison.Ordinal)
        || p.StartsWith("tests/RetroDownfall.Arcanum.PromptCache.Benchmarks/", StringComparison.Ordinal)
        || p is "Directory.Build.props" or "Directory.Build.targets" or "global.json" or "NuGet.Config" or "scripts/benchmark-prompt-cache.sh").Order(StringComparer.Ordinal))
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);

        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, path), ct);

        byte[] length = new byte[8];

        BinaryPrimitives.WriteInt64LittleEndian(length, pathBytes.LongLength);
        hash.AppendData(length);
        hash.AppendData(pathBytes);
        BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    string hardware = RuntimeInformation.ProcessArchitecture.ToString();

    if (OperatingSystem.IsMacOS()) hardware = (await CommandAsync("/usr/sbin/sysctl", root, ["-n", "machdep.cpu.brand_string"], ct)).Trim();

    string sdk = (await CommandAsync("dotnet", root, ["--version"], ct)).Trim();

    using JsonDocument lockFile = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests/RetroDownfall.Arcanum.PromptCache.Benchmarks/packages.lock.json"), ct));

    var packages = lockFile.RootElement.GetProperty("dependencies").GetProperty("net10.0");

    string libraries = $"OpenAI={packages.GetProperty("OpenAI").GetProperty("resolved").GetString()};Microsoft.Extensions.AI={packages.GetProperty("Microsoft.Extensions.AI").GetProperty("resolved").GetString()};Microsoft.Extensions.AI.OpenAI={packages.GetProperty("Microsoft.Extensions.AI.OpenAI").GetProperty("resolved").GetString()}";

    return new(revision, tree, dirty, Convert.ToHexStringLower(hash.GetHashAndReset()), Environment.Version.ToString(), RuntimeInformation.RuntimeIdentifier, RuntimeInformation.OSDescription, hardware, sdk, libraries);
}

static async Task<string> CommandAsync(string file, string directory, string[] arguments, CancellationToken ct)
{
    ProcessStartInfo start = new(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

    foreach (string argument in arguments) start.ArgumentList.Add(argument);

    using Process process = Process.Start(start) ?? throw new InvalidDataException("identityUnavailable");

    Task<string> output = process.StandardOutput.ReadToEndAsync(ct);

    Task<string> error = process.StandardError.ReadToEndAsync(ct);

    await process.WaitForExitAsync(ct);

    string result = await output;

    _ = await error;

    if (process.ExitCode != 0) throw new InvalidDataException("identityUnavailable");

    return result;
}
