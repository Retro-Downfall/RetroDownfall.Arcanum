using System.Xml.Linq;

using System.Diagnostics;

using System.Runtime.InteropServices;

using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed partial class GrimoireAdmissionBenchmarkPackagingTests
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
    public void Script_namespaces_every_function_owned_variable_for_posix_sh()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "benchmark-grimoire-admission.sh"));

        MatchCollection functions = Regex.Matches(
            script,
            @"(?ms)^(?<name>[a-z][a-z0-9_]*)\(\)\n\{\n(?<body>.*?)^\}");

        Assert.NotEmpty(functions);

        foreach (Match function in functions)
        {
            string name = function.Groups["name"].Value;

            string prefix = name + "__";

            string body = function.Groups["body"].Value;

            IEnumerable<string> assignments = Regex.Matches(
                    body,
                    @"(?m)^\s*(?<variable>[a-z][a-z0-9_]*)=")
                .Select(static match => match.Groups["variable"].Value);

            IEnumerable<string> iterations = Regex.Matches(
                    body,
                    @"(?m)^\s*for\s+(?<variable>[a-z][a-z0-9_]*)\s+in(?:\s|$)")
                .Select(static match => match.Groups["variable"].Value);

            IEnumerable<string> reads = Regex.Matches(
                    body,
                    @"(?m)\bread[ \t]+-r[ \t]+(?<variables>[a-z][a-z0-9_]*(?:[ \t]+[a-z][a-z0-9_]*)*)")
                .SelectMany(static match => match.Groups["variables"].Value.Split(
                    [' ', '\t'],
                    StringSplitOptions.RemoveEmptyEntries));

            string[] owned = assignments.Concat(iterations).Concat(reads).Distinct().ToArray();

            string[] allowedShared = name switch
            {
                "create_workspace" => ["temp_root", "temp_parent", "temp_root_identity"],
                "run_host" => ["child_pid", "watchdog_pid"],
                _ => [],
            };

            Assert.All(
                owned,
                variable => Assert.True(
                    variable.StartsWith(prefix, StringComparison.Ordinal)
                        || allowedShared.Contains(variable, StringComparer.Ordinal),
                    $"Function '{name}' owns unnamespaced POSIX-global variable '{variable}'."));
        }
    }

    [SkippableFact]
    public async Task Archived_host_publishes_through_symlinked_temp_parent_and_closes_the_runtime_source_set()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS()
                && RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            "Requires the shipping macOS arm64 runtime.");

        string root = FindRepositoryRoot();

        // The SDK's own Exec scripts require a space-free TMPDIR; fast workspace tests cover spaces.

        string fixture = Path.Combine(
            "/tmp",
            "arcanum-admission-host-closure-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(fixture);

        try
        {
            string physicalParent = Path.Combine(fixture, "physical-parent");

            Directory.CreateDirectory(physicalParent);

            string alias = Path.Combine(fixture, "temporary-alias");

            Directory.CreateSymbolicLink(alias, physicalParent);

            ProcessResult workspace = await RunLauncherFunctionsAsync(
                root,
                "create_workspace\ntrap - EXIT\nprintf '%s\\n' \"$temp_root\"",
                [],
                new Dictionary<string, string?> { ["TMPDIR"] = alias });

            Assert.Equal(0, workspace.ExitCode);

            string workspaceRoot = workspace.StandardOutput.Trim();

            string publish = Path.Combine(workspaceRoot, "publish");

            string sourceRoot = Path.Combine(workspaceRoot, "source");

            string workingDirectory = Path.Combine(fixture, "working");

            Directory.CreateDirectory(sourceRoot);

            Directory.CreateDirectory(workingDirectory);

            ProcessResult archive = await RunProcessAsync(
                "git",
                ["archive", "HEAD", "--output=" + Path.Combine(workspaceRoot, "source.tar")],
                root);

            Assert.Equal(0, archive.ExitCode);

            ProcessResult extract = await RunProcessAsync(
                "tar",
                ["-xf", Path.Combine(workspaceRoot, "source.tar"), "-C", sourceRoot],
                root);

            Assert.Equal(0, extract.ExitCode);

            ProcessResult publishResult = await RunLauncherFunctionsAsync(
                root,
                "publish_host \"$1\" \"$2\"",
                [sourceRoot, publish],
                new Dictionary<string, string?>
                {
                    ["TMPDIR"] = alias,
                    ["MSBUILDDISABLENODEREUSE"] = "1",
                    ["UseSharedCompilation"] = "false",
                });

            Assert.True(
                publishResult.ExitCode == 0,
                $"Archived publish exited {publishResult.ExitCode}. stdout: {publishResult.StandardOutput} stderr: {publishResult.StandardError}");

            string host = Path.Combine(
                publish,
                "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks");

            ProcessResult accepted = await RunProcessAsync(
                host,
                ["--smoke", "--source-root", sourceRoot],
                workingDirectory);

            Assert.True(
                accepted.ExitCode == 0,
                $"Host exited {accepted.ExitCode}. stdout: {accepted.StandardOutput} stderr: {accepted.StandardError}");

            string extraCSharp = Path.Combine(
                sourceRoot,
                "src",
                "RetroDownfall.Arcanum.Core",
                "UnexpectedBenchmarkInput.cs");

            await File.WriteAllTextAsync(extraCSharp, "namespace Unexpected; internal sealed class Input;\n");

            ProcessResult extraCSharpResult = await RunProcessAsync(
                host,
                ["--smoke", "--source-root", sourceRoot],
                workingDirectory);

            Assert.Equal(2, extraCSharpResult.ExitCode);

            File.Delete(extraCSharp);

            string extraSql = Path.Combine(
                sourceRoot,
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Data",
                "Schema",
                "UnexpectedBenchmarkInput.sql");

            await File.WriteAllTextAsync(extraSql, "SELECT 1;\n");

            ProcessResult extraSqlResult = await RunProcessAsync(
                host,
                ["--smoke", "--source-root", sourceRoot],
                workingDirectory);

            Assert.Equal(2, extraSqlResult.ExitCode);

            File.Delete(extraSql);

            string requiredSql = Directory.EnumerateFiles(
                    Path.Combine(
                        sourceRoot,
                        "src",
                        "RetroDownfall.Arcanum.Infrastructure",
                        "Data",
                        "Schema"),
                    "*.sql",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .First();

            File.Delete(requiredSql);

            ProcessResult missingSqlResult = await RunProcessAsync(
                host,
                ["--smoke", "--source-root", sourceRoot],
                workingDirectory);

            Assert.Equal(2, missingSqlResult.ExitCode);
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

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

    [SkippableTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(130, false)]
    [InlineData(0, true)]
    public async Task Smoke_script_publishes_once_runs_directly_and_bounds_or_preserves_exit(
        int hostExitCode,
        bool forceParentDeadline)
    {
        Skip.If(OperatingSystem.IsWindows(), "Requires a POSIX shell.");

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

            if (forceParentDeadline)
            {
                string hangingHost = Path.Combine(fixture, "hanging-host");

                await WriteExecutableAsync(
                    hangingHost,
                    "#!/bin/sh\n" +
                    "printf 'host:%s home:%s\\n' \"$*\" \"${ARCANUM_TEST_HOME-unset}\" >> \"$BENCHMARK_FIXTURE_LOG\"\n" +
                    "trap '' TERM\n" +
                    "while :; do :; done\n");

                await WriteExecutableAsync(
                    fakeDotnet,
                    "#!/bin/sh\n" +
                    "printf 'dotnet:%s\\n' \"$*\" >> \"$BENCHMARK_FIXTURE_LOG\"\n" +
                    "output=''\n" +
                    "previous=''\n" +
                    "for argument in \"$@\"; do if [ \"$previous\" = '-o' ]; then output=$argument; fi; previous=$argument; done\n" +
                    "mkdir -p \"$output\"\n" +
                    "cp \"$BENCHMARK_HANGING_HOST\" \"$output/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks\"\n");

                await WriteExecutableAsync(Path.Combine(fakeBin, "sleep"), "#!/bin/sh\nexit 0\n");
            }

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

            start.Environment["BENCHMARK_HANGING_HOST"] = Path.Combine(fixture, "hanging-host");

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            string calls = await File.ReadAllTextAsync(log);

            Assert.Equal(forceParentDeadline ? 2 : hostExitCode, process.ExitCode);

            Assert.Equal(1, calls.Split('\n').Count(static line => line.StartsWith("dotnet:publish ", StringComparison.Ordinal)));

            Assert.Contains("-c Release", calls, StringComparison.Ordinal);

            Assert.Contains("-p:RestoreLockedMode=true", calls, StringComparison.Ordinal);

            if (!forceParentDeadline)
            {
                Assert.Contains(
                    "host:--smoke --source-root " + root + " home:unset",
                    calls,
                    StringComparison.Ordinal);
            }

            Assert.DoesNotContain("dotnet:run", calls, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Script_rejects_unknown_mode_before_publishing()
    {
        Skip.If(OperatingSystem.IsWindows(), "Requires a POSIX shell.");

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

    [SkippableTheory]
    [InlineData(0, "none", 0)]
    [InlineData(130, "none", 130)]
    [InlineData(0, "ancestry", 2)]
    [InlineData(0, "catalog-extra-csharp", 2)]
    [InlineData(0, "catalog-extra-sql", 2)]
    [InlineData(0, "bytes-baseline", 2)]
    [InlineData(0, "bytes-candidate", 2)]
    [InlineData(0, "bytes-caller", 2)]
    public async Task Qualification_uses_two_immutable_publishes_and_preserves_measurement_cancellation(
        int hostExitCode,
        string breakName,
        int expectedExitCode)
    {
        Skip.If(OperatingSystem.IsWindows(), "Requires a POSIX shell.");

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
                "exit \"${BENCHMARK_FIXTURE_EXIT:-0}\"\n");

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

            string harness = new('9', 40);

            string baseline = new('a', 40);

            string candidate = new('b', 40);

            string controlledRevision = Path.Combine(fixture, "revision");

            Directory.CreateDirectory(controlledRevision);

            await CopyCatalogInputsAsync(root, controlledRevision);

            string controlledCatalog = Path.Combine(
                controlledRevision,
                "tests",
                "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
                "grimoire-admission-input-catalog-v1.txt");

            string[] controlledTree = (await File.ReadAllLinesAsync(controlledCatalog))
                .Select(static line => line.Split('\t')[1])
                .ToArray();

            await File.WriteAllLinesAsync(Path.Combine(controlledRevision, "tree.txt"), controlledTree);

            await WriteExecutableAsync(
                Path.Combine(fakeBin, "git"),
                "#!/bin/sh\n" +
                "case \"$3\" in\n" +
                "  status) exit 0;;\n" +
                "  rev-parse) case \"$4\" in 9*) printf '%s\\n' '" + harness + "';; a*) printf '%s\\n' '" + baseline + "';; *) printf '%s\\n' '" + candidate + "';; esac; exit 0;;\n" +
                "  merge-base) [ \"${BENCHMARK_QUALIFICATION_BREAK:-none}\" != 'ancestry' ]; exit $?;;\n" +
                "  ls-tree)\n" +
                "    cat \"$BENCHMARK_QUALIFICATION_ROOT/revision/tree.txt\" || exit 2\n" +
                "    if [ \"$6\" = '" + candidate + "' ]; then\n" +
                "      case \"${BENCHMARK_QUALIFICATION_BREAK:-none}\" in\n" +
                "        catalog-extra-csharp) printf '%s\\n' 'src/RetroDownfall.Arcanum.Infrastructure/ExtraQualificationFixture.cs';;\n" +
                "        catalog-extra-sql) printf '%s\\n' 'src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/ExtraQualificationFixture.sql';;\n" +
                "      esac\n" +
                "    fi\n" +
                "    exit 0;;\n" +
                "  show)\n" +
                "    git_fixture__spec=$4\n" +
                "    git_fixture__revision=${git_fixture__spec%%:*}\n" +
                "    git_fixture__path=${git_fixture__spec#*:}\n" +
                "    git_fixture__file=$BENCHMARK_QUALIFICATION_ROOT/revision/$git_fixture__path\n" +
                "    [ -f \"$git_fixture__file\" ] || exit 2\n" +
                "    cat \"$git_fixture__file\" || exit 2\n" +
                "    if [ \"$git_fixture__path\" = 'Directory.Build.props' ]; then\n" +
                "      case \"${BENCHMARK_QUALIFICATION_BREAK:-none}:$git_fixture__revision\" in\n" +
                "        bytes-baseline:a*|bytes-candidate:b*|bytes-caller:*) printf '%s\\n' 'controlled byte drift';;\n" +
                "      esac\n" +
                "    fi\n" +
                "    exit 0;;\n" +
                "  archive) exit 0;;\n" +
                "esac\n" +
                "exit 2\n");

            await WriteExecutableAsync(Path.Combine(fakeBin, "tar"), "#!/bin/sh\nexit 0\n");

            await WriteExecutableAsync(Path.Combine(fakeBin, "uuidgen"), "#!/bin/sh\nprintf '00000000-0000-0000-0000-000000000001\\n'\n");

            string output = Path.Combine(fixture, "evidence");

            ProcessStartInfo start = new(
                "/bin/sh",
                Path.Combine(root, "scripts", "benchmark-grimoire-admission.sh")
                    + " --qualify --harness " + harness
                    + " --base " + baseline
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

            start.Environment["BENCHMARK_FIXTURE_EXIT"] = hostExitCode.ToString(
                global::System.Globalization.CultureInfo.InvariantCulture);

            start.Environment["BENCHMARK_QUALIFICATION_BREAK"] = breakName;

            start.Environment["BENCHMARK_QUALIFICATION_ROOT"] = fixture;

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            Assert.Equal(expectedExitCode, process.ExitCode);

            if (breakName != "none")
            {
                return;
            }

            string calls = await File.ReadAllTextAsync(log);

            Assert.Equal(2, calls.Split('\n').Count(static line => line.StartsWith("dotnet:publish ", StringComparison.Ordinal)));

            int expectedHostCalls = hostExitCode == 0 ? 13 : 1;

            Assert.Equal(expectedHostCalls, calls.Split('\n').Count(static line => line.StartsWith("host:", StringComparison.Ordinal)));

            if (hostExitCode == 130)
            {
                return;
            }

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

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Calibration_keeps_the_result_and_refuses_failed_toolchain_capture(
        int toolchainExitCode)
    {
        Skip.If(OperatingSystem.IsWindows(), "Requires a POSIX shell.");

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
                "case \"$1\" in --version) printf '10.0.400\\n'; exit 0;; --info) [ \"$BENCHMARK_TOOLCHAIN_EXIT\" -eq 0 ] || exit \"$BENCHMARK_TOOLCHAIN_EXIT\"; printf 'fake toolchain\\n'; exit 0;; esac\n" +
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

            start.Environment["BENCHMARK_TOOLCHAIN_EXIT"] = toolchainExitCode.ToString(
                global::System.Globalization.CultureInfo.InvariantCulture);

            start.Environment.Remove("ARCANUM_TEST_HOME");

            using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

            await process.WaitForExitAsync();

            Assert.Equal(toolchainExitCode == 0 ? 0 : 2, process.ExitCode);

            if (toolchainExitCode != 0)
            {
                Assert.False(File.Exists(result));

                return;
            }

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

    private static async Task CopyCatalogInputsAsync(string root, string destination)
    {
        string catalogRelative = Path.Combine(
            "tests",
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
            "grimoire-admission-input-catalog-v1.txt");

        foreach (string line in await File.ReadAllLinesAsync(Path.Combine(root, catalogRelative)))
        {
            string[] parts = line.Split('\t');

            string relative = parts[1].Replace('/', Path.DirectorySeparatorChar);

            string source = Path.Combine(root, relative);

            if (!File.Exists(source))
            {
                Assert.Equal("O", parts[0]);

                continue;
            }

            string target = Path.Combine(destination, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            File.Copy(source, target);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("ARCANUM_TEST_HOME");

        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                if (value is null)
                {
                    start.Environment.Remove(key);
                }
                else
                {
                    start.Environment[key] = value;
                }
            }
        }

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(5));

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);

        Task<string> standardError = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);

            return new(process.ExitCode, await standardOutput, await standardError);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync();
            }

            throw;
        }
    }

    private static string Property(XDocument document, string name) =>
        document.Descendants(name).Single().Value;

    private static string FindRepositoryRoot() =>
        global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
