using System.Xml.Linq;

using System.Diagnostics;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class GrimoireAdmissionBenchmarkPackagingTests
{

    private static readonly string[] ExactSharedSources =
    [
        "AdmissionBenchmarkComparison.cs",
        "AdmissionBenchmarkContracts.cs",
        "AdmissionBenchmarkEvidence.cs",
        "AdmissionBenchmarkManifest.cs",
        "PersistentWorkerHarness.cs",
    ];

    [Fact]
    public void Host_is_outside_solution_and_tests_compile_the_exact_five_pure_sources()
    {

        string root = FindRepositoryRoot();

        string hostProjectPath = Path.Combine(
            root,
            "tests",
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj");

        Assert.True(File.Exists(hostProjectPath), "The dedicated benchmark project is missing.");

        XDocument hostProject = XDocument.Load(hostProjectPath);

        Assert.Equal("Exe", Property(hostProject, "OutputType"));

        Assert.Equal("true", Property(hostProject, "PublishAot"));

        Assert.Equal("true", Property(hostProject, "IsAotCompatible"));

        Assert.Equal("true", Property(hostProject, "IsTrimmable"));

        Assert.Equal("true", Property(hostProject, "RestorePackagesWithLockFile"));

        Assert.True(
            File.Exists(Path.Combine(Path.GetDirectoryName(hostProjectPath)!, "packages.lock.json")),
            "The outside-solution benchmark must carry its own locked restore graph.");

        Assert.DoesNotContain(
            new[]
            {
                "RetroDownfall.Arcanum.Core",
                "RetroDownfall.Arcanum.Infrastructure",
                "RetroDownfall.Arcanum.Secrets",
            },
            project => File.Exists(Path.Combine(root, "src", project, "packages.lock.json")));

        XDocument solution = XDocument.Load(Path.Combine(root, "RetroDownfall.Arcanum.slnx"));

        Assert.DoesNotContain(
            solution.Descendants("Project"),
            project => (project.Attribute("Path")?.Value ?? string.Empty).Contains(
                "GrimoireAdmission.Benchmarks",
                StringComparison.Ordinal));

        XDocument tests = XDocument.Load(Path.Combine(
            root,
            "tests",
            "RetroDownfall.Arcanum.Tests",
            "RetroDownfall.Arcanum.Tests.csproj"));

        string[] linked = tests.Descendants("Compile")
            .Select(static item => item.Attribute("Include")?.Value ?? string.Empty)
            .Where(static path => path.Contains("GrimoireAdmission.Benchmarks", StringComparison.Ordinal))
            .Select(static path => Path.GetFileName(path.Replace('\\', '/'))!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExactSharedSources, linked);

        foreach (string sourceName in ExactSharedSources)
        {

            string sourcePath = Path.Combine(Path.GetDirectoryName(hostProjectPath)!, sourceName);

            string source = File.ReadAllText(sourcePath);

            CSharpSyntaxTree tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(source, path: sourcePath);

            Assert.DoesNotContain(
                tree.GetDiagnostics(),
                static diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

            Assert.DoesNotContain(tree.GetRoot().DescendantTrivia(), static trivia => trivia.IsDirective);

            Assert.DoesNotContain(" partial ", " " + source.ReplaceLineEndings(" ") + " ", StringComparison.Ordinal);

            Assert.DoesNotContain(
                tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
                static invocation => invocation.Expression.ToString().Contains(
                        "JsonSerializer",
                        StringComparison.Ordinal)
                    && invocation.ArgumentList.Arguments.Count < 2);

            Assert.DoesNotContain(
                tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>(),
                static identifier => identifier.Identifier.ValueText is
                    "GrimoireConnectionAdmissionGate" or "ArcanumDbContext");

        }

        string program = File.ReadAllText(Path.Combine(Path.GetDirectoryName(hostProjectPath)!, "Program.cs"));

        Assert.Contains("AdmissionBenchmarkJsonContext", program, StringComparison.Ordinal);

        Assert.Contains("RunSchemaSelfTest", program, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(130)]
    public async Task Smoke_script_publishes_once_runs_the_binary_directly_and_preserves_exit_code(
        int hostExitCode)
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string root = FindRepositoryRoot();

        string script = Path.Combine(root, "scripts", "benchmark-grimoire-admission.sh");

        Assert.True(File.Exists(script), "The Grimoire-admission operator script is missing.");

        string fixture = Path.Combine(
            Path.GetTempPath(),
            "arcanum-admission-script-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(fixture);

        try
        {

            string fakeBin = Path.Combine(fixture, "bin");

            Directory.CreateDirectory(fakeBin);

            string log = Path.Combine(fixture, "calls.log");

            string fakeDotnet = Path.Combine(fakeBin, "dotnet");

            await File.WriteAllTextAsync(
                fakeDotnet,
                "#!/bin/sh\n" +
                "printf 'dotnet:%s\\n' \"$*\" >> \"$BENCHMARK_FIXTURE_LOG\"\n" +
                "output=''\n" +
                "previous=''\n" +
                "for argument in \"$@\"; do\n" +
                "  if [ \"$previous\" = '-o' ]; then output=$argument; fi\n" +
                "  previous=$argument\n" +
                "done\n" +
                "mkdir -p \"$output\"\n" +
                "host=\"$output/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks\"\n" +
                "printf '%s\\n' '#!/bin/sh' 'printf '\"'\"'host:%s home:%s\\n'\"'\"' \"$*\" \"${ARCANUM_TEST_HOME-unset}\" >> \"$BENCHMARK_FIXTURE_LOG\"' 'exit \"$BENCHMARK_FIXTURE_EXIT\"' > \"$host\"\n" +
                "chmod +x \"$host\"\n");

            File.SetUnixFileMode(
                fakeDotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            ProcessStartInfo start = new("/bin/sh", script + " --smoke")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            start.Environment["PATH"] = fakeBin + ":/usr/bin:/bin";

            start.Environment["BENCHMARK_FIXTURE_LOG"] = log;

            start.Environment["BENCHMARK_FIXTURE_EXIT"] = hostExitCode.ToString(
                global::System.Globalization.CultureInfo.InvariantCulture);

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            string calls = await File.ReadAllTextAsync(log);

            Assert.Equal(hostExitCode, process.ExitCode);

            Assert.Equal(1, calls.Split('\n').Count(static line => line.StartsWith("dotnet:publish ", StringComparison.Ordinal)));

            Assert.Contains("-c Release", calls, StringComparison.Ordinal);

            Assert.Contains("-p:RestoreLockedMode=true", calls, StringComparison.Ordinal);

            Assert.Contains("host:--smoke home:unset", calls, StringComparison.Ordinal);

            Assert.DoesNotContain("dotnet:run", calls, StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(fixture, recursive: true);

        }

    }

    [Fact]
    public async Task Script_rejects_unknown_mode_before_publishing()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string root = FindRepositoryRoot();

        string fixture = Path.Combine(
            Path.GetTempPath(),
            "arcanum-admission-script-invalid-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(fixture);

        try
        {

            string fakeDotnet = Path.Combine(fixture, "dotnet");

            await File.WriteAllTextAsync(fakeDotnet, "#!/bin/sh\nexit 99\n");

            File.SetUnixFileMode(
                fakeDotnet,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            ProcessStartInfo start = new("/bin/sh", Path.Combine(root, "scripts", "benchmark-grimoire-admission.sh") + " --unknown")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            start.Environment["PATH"] = fixture + ":/usr/bin:/bin";

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            Assert.Equal(2, process.ExitCode);

        }
        finally
        {

            Directory.Delete(fixture, recursive: true);

        }

    }

    [Fact]
    public async Task Qualification_uses_two_immutable_publishes_and_six_counterbalanced_process_pairs()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string root = FindRepositoryRoot();

        string fixture = Path.Combine(
            Path.GetTempPath(),
            "arcanum-admission-qualification-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(fixture);

        try
        {

            string fakeBin = Path.Combine(fixture, "bin");

            Directory.CreateDirectory(fakeBin);

            string log = Path.Combine(fixture, "calls.log");

            string hostTemplate = Path.Combine(fixture, "host-template");

            await File.WriteAllTextAsync(
                hostTemplate,
                "#!/bin/sh\n" +
                "printf 'host:%s\\n' \"$*\" >> \"$BENCHMARK_FIXTURE_LOG\"\n" +
                "previous=''\n" +
                "for argument in \"$@\"; do\n" +
                "  if [ \"$previous\" = '--out' ] || [ \"$previous\" = '--bundle-out' ]; then\n" +
                "    mkdir -p \"$(dirname \"$argument\")\"\n" +
                "    printf '{}\\n' > \"$argument\"\n" +
                "  fi\n" +
                "  previous=$argument\n" +
                "done\n" +
                "exit 0\n");

            File.SetUnixFileMode(
                hostTemplate,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await WriteExecutableAsync(
                Path.Combine(fakeBin, "dotnet"),
                "#!/bin/sh\n" +
                "case \"$1\" in\n" +
                "  --version) printf '10.0.400\\n'; exit 0;;\n" +
                "  --info) printf 'fake toolchain\\n'; exit 0;;\n" +
                "esac\n" +
                "printf 'dotnet:%s\\n' \"$*\" >> \"$BENCHMARK_FIXTURE_LOG\"\n" +
                "output=''\n" +
                "previous=''\n" +
                "for argument in \"$@\"; do\n" +
                "  if [ \"$previous\" = '-o' ]; then output=$argument; fi\n" +
                "  previous=$argument\n" +
                "done\n" +
                "mkdir -p \"$output\"\n" +
                "cp \"$BENCHMARK_FAKE_HOST\" \"$output/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks\"\n");

            string baseline = new('a', 40);

            string candidate = new('b', 40);

            await WriteExecutableAsync(
                Path.Combine(fakeBin, "git"),
                "#!/bin/sh\n" +
                "case \"$3\" in\n" +
                "  status) exit 0;;\n" +
                "  rev-parse) case \"$4\" in a*) printf '%s\\n' '" + baseline + "';; *) printf '%s\\n' '" + candidate + "';; esac; exit 0;;\n" +
                "  merge-base|archive|show) exit 0;;\n" +
                "esac\n" +
                "exit 2\n");

            await WriteExecutableAsync(Path.Combine(fakeBin, "cmp"), "#!/bin/sh\nexit 0\n");

            await WriteExecutableAsync(Path.Combine(fakeBin, "tar"), "#!/bin/sh\nexit 0\n");

            await WriteExecutableAsync(Path.Combine(fakeBin, "uuidgen"), "#!/bin/sh\nprintf '00000000-0000-0000-0000-000000000001\\n'\n");

            string output = Path.Combine(fixture, "evidence");

            ProcessStartInfo start = new(
                "/bin/sh",
                Path.Combine(root, "scripts", "benchmark-grimoire-admission.sh")
                    + " --qualify --base " + baseline
                    + " --candidate " + candidate
                    + " --out " + output)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            start.Environment["PATH"] = fakeBin + ":/usr/bin:/bin";

            start.Environment["BENCHMARK_FIXTURE_LOG"] = log;

            start.Environment["BENCHMARK_FAKE_HOST"] = hostTemplate;

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            string calls = await File.ReadAllTextAsync(log);

            Assert.Equal(0, process.ExitCode);

            Assert.Equal(2, calls.Split('\n').Count(static line => line.StartsWith("dotnet:publish ", StringComparison.Ordinal)));

            Assert.Equal(13, calls.Split('\n').Count(static line => line.StartsWith("host:", StringComparison.Ordinal)));

            int pairZeroBaseline = calls.IndexOf("--pair 0 --order 0 --role B", StringComparison.Ordinal);

            int pairZeroCandidate = calls.IndexOf("--pair 0 --order 1 --role C", StringComparison.Ordinal);

            int pairOneCandidate = calls.IndexOf("--pair 1 --order 0 --role C", StringComparison.Ordinal);

            int pairOneBaseline = calls.IndexOf("--pair 1 --order 1 --role B", StringComparison.Ordinal);

            Assert.True(pairZeroBaseline >= 0 && pairZeroBaseline < pairZeroCandidate);

            Assert.True(pairOneCandidate > pairZeroCandidate && pairOneCandidate < pairOneBaseline);

            Assert.True(File.Exists(Path.Combine(output, "evidence.json")));

            Assert.True(File.Exists(Path.Combine(output, "comparison.json")));

        }
        finally
        {

            Directory.Delete(fixture, recursive: true);

        }

    }

    [Fact]
    public async Task Calibration_keeps_the_requested_result_path_after_publish()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string root = FindRepositoryRoot();

        string fixture = Path.Combine(
            Path.GetTempPath(),
            "arcanum-admission-calibration-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(fixture);

        try
        {

            string fakeBin = Path.Combine(fixture, "bin");

            Directory.CreateDirectory(fakeBin);

            string log = Path.Combine(fixture, "calls.log");

            string result = Path.Combine(fixture, "calibration.json");

            string hostTemplate = Path.Combine(fixture, "host-template");

            await WriteExecutableAsync(
                hostTemplate,
                "#!/bin/sh\n" +
                "printf 'host:%s\\n' \"$*\" >> \"$BENCHMARK_FIXTURE_LOG\"\n" +
                "previous=''\n" +
                "for argument in \"$@\"; do\n" +
                "  if [ \"$previous\" = '--out' ]; then printf '{}\\n' > \"$argument\"; fi\n" +
                "  previous=$argument\n" +
                "done\n" +
                "exit 0\n");

            await WriteExecutableAsync(
                Path.Combine(fakeBin, "dotnet"),
                "#!/bin/sh\n" +
                "case \"$1\" in --version) printf '10.0.400\\n'; exit 0;; --info) printf 'fake toolchain\\n'; exit 0;; esac\n" +
                "output=''\n" +
                "previous=''\n" +
                "for argument in \"$@\"; do if [ \"$previous\" = '-o' ]; then output=$argument; fi; previous=$argument; done\n" +
                "mkdir -p \"$output\"\n" +
                "cp \"$BENCHMARK_FAKE_HOST\" \"$output/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks\"\n");

            string revision = new('c', 40);

            await WriteExecutableAsync(
                Path.Combine(fakeBin, "git"),
                "#!/bin/sh\n" +
                "case \"$3\" in status) exit 0;; rev-parse) printf '%s\\n' '" + revision + "'; exit 0;; esac\n" +
                "exit 2\n");

            ProcessStartInfo start = new(
                "/bin/sh",
                Path.Combine(root, "scripts", "benchmark-grimoire-admission.sh")
                    + " --calibrate --revision " + revision
                    + " --out " + result)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            start.Environment["PATH"] = fakeBin + ":/usr/bin:/bin";

            start.Environment["BENCHMARK_FIXTURE_LOG"] = log;

            start.Environment["BENCHMARK_FAKE_HOST"] = hostTemplate;

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);

            Assert.True(File.Exists(result));

            Assert.Contains("--out " + result, await File.ReadAllTextAsync(log), StringComparison.Ordinal);

        }
        finally
        {

            Directory.Delete(fixture, recursive: true);

        }

    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {

        if (OperatingSystem.IsWindows())
        {

            throw new PlatformNotSupportedException();

        }

        await File.WriteAllTextAsync(path, contents);

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    }

    private static string Property(XDocument document, string name) =>
        document.Descendants(name).Single().Value;

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}
