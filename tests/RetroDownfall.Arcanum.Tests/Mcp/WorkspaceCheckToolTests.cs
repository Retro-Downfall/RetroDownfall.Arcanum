using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("ProcessEnvironment")]
public sealed class WorkspaceCheckToolTests : IDisposable
{
    private readonly string? _originalHome =
        global::System.Environment.GetEnvironmentVariable("HOME");

    private readonly string? _originalUserProfile =
        global::System.Environment.GetEnvironmentVariable("USERPROFILE");

    public WorkspaceCheckToolTests()
    {
        global::System.Environment.SetEnvironmentVariable(
            "HOME",
            TestProcessPaths.OriginalUserProfile);
        global::System.Environment.SetEnvironmentVariable(
            "USERPROFILE",
            TestProcessPaths.OriginalUserProfile);
    }

    public void Dispose()
    {
        global::System.Environment.SetEnvironmentVariable("HOME", _originalHome);
        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);
    }

    [Fact]
    public void Built_in_profiles_render_only_closed_server_owned_arguments()
    {

        WorkspaceCheckProfileCatalog catalog = WorkspaceCheckProfileCatalog.Create(
            new WorkspaceCheckSettings());

        WorkspaceCheckProfileResolution build = catalog.Resolve(
            WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["configuration"] = "release",
            });

        WorkspaceCheckProfileResolution test = catalog.Resolve(
            WorkspaceCheckCatalogDefaults.DotNetTestProfileId,
            options: null);

        WorkspaceCheckProfileResolution lint = catalog.Resolve(
            WorkspaceCheckCatalogDefaults.DotNetLintProfileId,
            options: null);

        Assert.True(build.Success, build.Message);
        Assert.Equal(
            ["build", "--no-restore", "--configuration", "Release"],
            build.Profile!.Arguments);
        Assert.Equal(["test", "--no-restore"], test.Profile!.Arguments);
        Assert.Equal(
            ["format", "--verify-no-changes", "--no-restore"],
            lint.Profile!.Arguments);
        Assert.All(
            [build.Profile, test.Profile, lint.Profile],
            static profile => Assert.Equal(
                WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                profile.ExecutableId));
    }

    [Fact]
    public void Operator_profile_uses_exact_allowlisted_rendering_and_rejects_unknown_values()
    {

        WorkspaceCheckSettings settings = new()
        {
            CustomProfiles = new Dictionary<string, WorkspaceCheckProfileSettings>
            {
                ["operator-build"] = new()
                {
                    ExecutableId = WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                    Kind = WorkspaceCheckKind.Build,
                    Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                    Target = "src/Checked/Checked.csproj",
                    FixedArguments = ["build", "--no-restore", "--nologo"],
                    Options = new Dictionary<string, WorkspaceCheckProfileOptionSettings>
                    {
                        ["configuration"] = new()
                        {
                            AllowedValues = new Dictionary<string, string[]>
                            {
                                ["checked"] = ["--configuration", "Checked"],
                            },
                        },
                    },
                },
            },
        };
        WorkspaceCheckProfileCatalog catalog = WorkspaceCheckProfileCatalog.Create(settings);

        WorkspaceCheckProfileResolution accepted = catalog.Resolve(
            "operator-build",
            new Dictionary<string, string> { ["configuration"] = "checked" });
        WorkspaceCheckProfileResolution rejected = catalog.Resolve(
            "operator-build",
            new Dictionary<string, string> { ["configuration"] = "--;rm" });

        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal(
            ["build", "--no-restore", "--nologo", "--configuration", "Checked"],
            accepted.Profile!.Arguments);
        Assert.Equal(
            "src/Checked/Checked.csproj",
            accepted.Profile.TargetRelativePath);
        Assert.False(rejected.Success);
        Assert.Equal("invalid_option_value", rejected.Code);
    }

    [Fact]
    public void Configured_profile_target_bypasses_ambiguous_root_discovery_but_stays_contained()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        _ = tree.CreateNativeExecutable("workspace/First.csproj");
        _ = tree.CreateNativeExecutable("workspace/Second.csproj");
        string projectDirectory =
            tree.CreateDirectory("workspace/src/Checked");
        string project = Path.Combine(
            projectDirectory,
            "Checked.csproj");
        File.WriteAllText(project, "<Project />");

        WorkspaceCheckTargetResolution automatic =
            WorkspaceCheckTargetResolver.Resolve(workspace);
        WorkspaceCheckTargetResolution configured =
            WorkspaceCheckTargetResolver.Resolve(
                workspace,
                "src/Checked/Checked.csproj");
        WorkspaceCheckTargetResolution traversal =
            WorkspaceCheckTargetResolver.Resolve(
                workspace,
                "../outside.csproj");
        WorkspaceCheckTargetResolution? symlinkEscape = null;

        if (!OperatingSystem.IsWindows())
        {
            string outside = tree.CreateDirectory("outside");
            string outsideProject = Path.Combine(
                outside,
                "Outside.csproj");
            File.WriteAllText(outsideProject, "<Project />");
            File.CreateSymbolicLink(
                Path.Combine(
                    projectDirectory,
                    "Escape.csproj"),
                outsideProject);
            symlinkEscape = WorkspaceCheckTargetResolver.Resolve(
                workspace,
                "src/Checked/Escape.csproj");
        }

        Assert.False(automatic.Success);
        Assert.Equal("ambiguous_workspace_target", automatic.Code);
        Assert.True(configured.Success, configured.Message);
        Assert.True(File.Exists(configured.TargetPath));
        Assert.Equal(
            Path.GetFileName(project),
            Path.GetFileName(configured.TargetPath));
        Assert.False(traversal.Success);
        Assert.Equal("invalid_workspace_target", traversal.Code);
        Assert.False(symlinkEscape?.Success ?? false);
    }

    [Theory]
    [InlineData("--restore")]
    [InlineData("-t:Restore")]
    [InlineData("-p:BaseOutputPath=/workspace/bin")]
    [InlineData("-p:OutDir=/workspace/bin")]
    [InlineData("-p:MSBuildProjectExtensionsPath=/workspace/obj")]
    [InlineData("-p:ArtifactsPath=/workspace/artifacts")]
    [InlineData("-p:UseArtifactsOutput=true")]
    [InlineData("-p:NuGetPackages=/workspace/packages")]
    [InlineData("--output")]
    [InlineData("-o")]
    [InlineData("--results-directory")]
    [InlineData("--artifacts-path")]
    [InlineData("--packages")]
    [InlineData("--interactive")]
    [InlineData("--force")]
    [InlineData("--force-evaluate")]
    [InlineData("--logger")]
    [InlineData("--binarylog")]
    [InlineData("--report")]
    [InlineData("--output=/workspace/bin")]
    [InlineData("--results-directory=/workspace/results")]
    [InlineData("--artifacts-path=/workspace/artifacts")]
    [InlineData("--packages=/workspace/packages")]
    [InlineData("-bl:/workspace/build.binlog")]
    [InlineData("/bl:/workspace/build.binlog")]
    public void Operator_profiles_cannot_override_no_restore_or_trusted_output_roots(
        string forbiddenToken)
    {

        WorkspaceCheckSettings settings = new()
        {
            CustomProfiles = new Dictionary<string, WorkspaceCheckProfileSettings>
            {
                ["unsafe-build"] = new()
                {
                    ExecutableId = WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                    Kind = WorkspaceCheckKind.Build,
                    Parser = WorkspaceCheckDiagnosticParserKind.MsBuild,
                    FixedArguments = ["build", forbiddenToken],
                },
            },
        };
        WorkspaceCheckProfileCatalog catalog =
            WorkspaceCheckProfileCatalog.Create(settings);

        WorkspaceCheckProfileResolution resolution = catalog.Resolve(
            "unsafe-build",
            options: null);

        Assert.False(resolution.Success);
        Assert.Equal("invalid_profile", resolution.Code);
    }

    [Fact]
    public void Workspace_check_argument_policy_allows_no_restore_and_ordinary_fixed_arguments()
    {
        Assert.False(WorkspaceCheckArgumentPolicy.IsRuntimeReservedToken(string.Empty));
        Assert.False(WorkspaceCheckArgumentPolicy.IsRuntimeReservedToken(" --no-restore "));
        Assert.False(WorkspaceCheckArgumentPolicy.IsRuntimeReservedToken("--configuration"));
        Assert.False(WorkspaceCheckArgumentPolicy.IsRuntimeReservedToken("Release"));
    }

    [Theory]
    [InlineData("macOS", true, true, true, true)]
    [InlineData("macOS", false, true, true, false)]
    [InlineData("macOS", true, false, true, false)]
    [InlineData("macOS", true, true, false, false)]
    [InlineData("Linux", true, true, true, false)]
    [InlineData("Windows", true, true, true, false)]
    [InlineData("FreeBSD", true, true, true, false)]
    public void Execution_policy_is_distinct_and_requires_active_macos_jail(
        string platform,
        bool enabled,
        bool executableValid,
        bool jailAvailable,
        bool expectedEligible)
    {

        WorkspaceCheckExecutionStatus status = WorkspaceCheckExecutionPolicy.Resolve(
            platform,
            enabled,
            executableValid,
            jailAvailable);

        Assert.Equal(expectedEligible, status.IsEligible);
        Assert.Equal(!expectedEligible && enabled, status.IsHealthDegraded);
        Assert.False(HostProcessToolPolicy.IsHostProcessTool(
            ToolRiskClassifier.WorkspaceCheckToolName));

        if (!expectedEligible)
        {
            Assert.False(string.IsNullOrWhiteSpace(status.Reason));
        }
        else if (string.Equals(platform, "macOS", StringComparison.Ordinal))
        {
            Assert.Contains(
                "detached descendant",
                status.Reason,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "network",
                status.Reason,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Workspace_check_environment_starts_empty_and_excludes_secrets_proxies_and_hijacks()
    {

        WorkspaceCheckEnvironmentPaths paths = new(
            DotNetRoot: "/trusted/dotnet",
            Home: "/runs/home",
            DotNetCliHome: "/runs/cli",
            NuGetHttpCache: "/runs/http",
            Temp: "/runs/tmp",
            GlobalPackages: "/trusted/packages");
        Dictionary<string, string?> inherited = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ARCANUM_Arcanum__Providers__0__ApiKey"] = "secret",
            ["AWS_SECRET_ACCESS_KEY"] = "secret",
            ["HTTPS_PROXY"] = "http://attacker.invalid",
            ["LD_PRELOAD"] = "/tmp/evil.dylib",
            ["DOTNET_STARTUP_HOOKS"] = "/tmp/hook.dll",
            ["PATH"] = "/workspace/bin",
            ["HOME"] = "/Users/operator",
            ["LANG"] = "en_US.UTF-8",
        };

        IReadOnlyDictionary<string, string> environment =
            WorkspaceCheckEnvironmentBuilder.Build(paths, inherited);

        Assert.Equal("/trusted/dotnet", environment["DOTNET_ROOT"]);
        Assert.Equal("/runs/home", environment["HOME"]);
        Assert.Equal("/runs/cli", environment["DOTNET_CLI_HOME"]);
        Assert.Equal("/runs/http", environment["NUGET_HTTP_CACHE_PATH"]);
        Assert.Equal("/runs/tmp", environment["TMPDIR"]);
        Assert.Equal("/runs/tmp", environment["TEMP"]);
        Assert.Equal("/runs/tmp", environment["TMP"]);
        Assert.Equal("/trusted/packages", environment["NUGET_PACKAGES"]);
        Assert.Equal("1", environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", environment["DOTNET_NOLOGO"]);
        Assert.Equal("1", environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("LANG", environment.Keys);
        }
        else
        {
            Assert.Equal("en_US.UTF-8", environment["LANG"]);
        }
        Assert.DoesNotContain(
            environment.Keys,
            static key => key.StartsWith("ARCANUM_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", environment.Keys);
        Assert.DoesNotContain("HTTPS_PROXY", environment.Keys);
        Assert.DoesNotContain("LD_PRELOAD", environment.Keys);
        Assert.DoesNotContain("DOTNET_STARTUP_HOOKS", environment.Keys);
        Assert.Contains("/trusted/dotnet", environment["PATH"], StringComparison.Ordinal);
        Assert.DoesNotContain("/workspace", environment["PATH"], StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_check_environment_honors_explicit_host_and_rejects_control_characters()
    {
        WorkspaceCheckEnvironmentPaths paths = new(
            DotNetRoot: "/trusted/dotnet",
            Home: "/runs/home",
            DotNetCliHome: "/runs/cli",
            NuGetHttpCache: "/runs/http",
            Temp: "/runs/tmp",
            GlobalPackages: "/trusted/packages",
            SandboxApplicationGroupId: "group.example",
            DotNetHostPath: "/trusted/host/dotnet");
        Dictionary<string, string?> inherited = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LANG"] = "line\nbreak",
            ["LC_ALL"] = "nul\0value",
            ["LC_CTYPE"] = "carriage\rreturn",
            ["TZ"] = "   ",
            ["SystemRoot"] = "line\nbreak",
            ["WINDIR"] = "nul\0value",
            ["COMSPEC"] = "carriage\rreturn",
            ["PATHEXT"] = "   ",
        };

        IReadOnlyDictionary<string, string> environment =
            WorkspaceCheckEnvironmentBuilder.Build(paths, inherited);

        Assert.Equal("/trusted/host/dotnet", environment["DOTNET_HOST_PATH"]);
        Assert.Equal(
            "group.example",
            environment["NETCOREAPP_SANDBOX_APPLICATION_GROUP_ID"]);
        Assert.Equal(
            "group.example",
            environment["DOTNET_SANDBOX_APPLICATION_GROUP_ID"]);
        Assert.DoesNotContain("LANG", environment.Keys);
        Assert.DoesNotContain("LC_ALL", environment.Keys);
        Assert.DoesNotContain("LC_CTYPE", environment.Keys);
        Assert.DoesNotContain("TZ", environment.Keys);
        Assert.DoesNotContain("SystemRoot", environment.Keys);
        Assert.DoesNotContain("WINDIR", environment.Keys);
        Assert.DoesNotContain("COMSPEC", environment.Keys);
        Assert.DoesNotContain("PATHEXT", environment.Keys);
    }

    [Fact]
    public void Workspace_check_sandbox_keeps_source_and_packages_read_only()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("source/workspace");
        string packages = tree.CreateDirectory("trusted/packages");
        string sdk = tree.CreateDirectory("trusted/dotnet");
        string output = tree.CreateDirectory("runs/output");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForWorkspaceCheck(
            workspace,
            [packages, sdk],
            [output]);

        Assert.Contains(request.ReadOnlyRoots, root => PathsEqual(root, workspace));
        Assert.Contains(request.ReadOnlyRoots, root => PathsEqual(root, packages));
        Assert.Contains(request.ReadExecuteRoots, root => PathsEqual(root, sdk));
        Assert.Contains(request.ReadWriteRoots, root => PathsEqual(root, output));
        Assert.DoesNotContain(request.ReadWriteRoots, root => PathsEqual(root, workspace));
        Assert.False(request.AllowUnsandboxed);
        Assert.True(request.RequireAppliedFilesystemJail);
    }

    [Fact]
    public void Workspace_check_sandbox_rejects_canonical_writable_overlap()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string packages = tree.CreateDirectory("packages");
        string sdk = tree.CreateDirectory("sdk");
        string overlappingOutput = tree.CreateDirectory("workspace/output");

        Assert.Throws<InvalidOperationException>(() =>
            ChildProcessSandboxRoots.ForWorkspaceCheck(
                workspace,
                [packages, sdk],
                [overlappingOutput]));

    }

    [SkippableFact]
    public void Workspace_check_run_roots_are_owner_only()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mode ownership is covered on Unix hosts.");

        if (OperatingSystem.IsWindows())
        {

            return;
        }

        using TestTree tree = new();
        string root = tree.CreateDirectory("run");

        WorkspaceCheckRunDirectories directories =
            WorkspaceCheckRunDirectories.CreateUnder(root);

        Assert.DoesNotContain(
            directories.Root,
            directories.WritableRoots);
        UnixFileMode forbidden =
            UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        Assert.All(
            directories.WritableRoots,
            path => Assert.Equal(
                0,
#pragma warning disable CA1416 // The test returns above on Windows.
                (int)(File.GetUnixFileMode(path) & forbidden)));
#pragma warning restore CA1416

    }

    [Fact]
    public void Diagnostic_parser_extracts_mixed_msbuild_severities_and_caps_results()
    {

        string workspace = Path.GetFullPath("/workspace");
        string stdout =
            "/workspace/src/App.cs(12,7): error CS1002: ; expected [/workspace/App.csproj]\n"
            + "/workspace/src/Other.cs(3,2): warning CA1822: Mark member static [/workspace/App.csproj]\n";

        WorkspaceCheckDiagnosticParseResult parsed = WorkspaceCheckDiagnosticParser.Parse(
            WorkspaceCheckDiagnosticParserKind.MsBuild,
            stdout,
            standardError: string.Empty,
            workspace,
            maxDiagnostics: 1);

        WorkspaceCheckToolResultItem diagnostic = Assert.Single(parsed.Diagnostics);

        Assert.Equal("src/App.cs", diagnostic.File);
        Assert.Equal(12, diagnostic.Line);
        Assert.Equal(7, diagnostic.Column);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("CS1002", diagnostic.Code);
        Assert.Equal("; expected", diagnostic.Message);
        Assert.Equal(2, parsed.TotalDiagnosticCount);
        Assert.Equal(1, parsed.ErrorCount);
        Assert.Equal(1, parsed.WarningCount);
        Assert.True(parsed.Truncated);
    }

    [Fact]
    public void Diagnostic_parser_kind_selects_structured_vstest_failures_and_counts()
    {

        string workspace = Path.GetFullPath("/workspace");
        const string output =
            "  Failed App.Tests.WidgetTests.Does_work [12 ms]\n"
            + "Failed!  - Failed: 1, Passed: 2, Skipped: 0, Total: 3, Duration: 20 ms\n";

        WorkspaceCheckDiagnosticParseResult msbuild =
            WorkspaceCheckDiagnosticParser.Parse(
                WorkspaceCheckDiagnosticParserKind.MsBuild,
                output,
                string.Empty,
                workspace,
                maxDiagnostics: 10);
        WorkspaceCheckDiagnosticParseResult vstest =
            WorkspaceCheckDiagnosticParser.Parse(
                WorkspaceCheckDiagnosticParserKind.VsTest,
                output,
                string.Empty,
                workspace,
                maxDiagnostics: 10);

        Assert.Empty(msbuild.Diagnostics);
        WorkspaceCheckToolResultItem failure =
            Assert.Single(vstest.Diagnostics);
        Assert.Equal("error", failure.Severity);
        Assert.Equal("VSTEST_FAIL", failure.Code);
        Assert.Contains("WidgetTests.Does_work", failure.Message);
        Assert.Equal(1, vstest.ErrorCount);
        Assert.Equal(1, vstest.TotalDiagnosticCount);
        Assert.Equal(3, vstest.TotalTestCount);
        Assert.Equal(2, vstest.PassedTestCount);
        Assert.Equal(1, vstest.FailedTestCount);
        Assert.Equal(0, vstest.SkippedTestCount);

    }

    [Fact]
    public void Trx_results_merge_console_compiler_diagnostics_without_replacing_authoritative_counts()
    {
        WorkspaceCheckDiagnosticParseResult console =
            WorkspaceCheckDiagnosticParser.Parse(
                WorkspaceCheckDiagnosticParserKind.VsTest,
                "/workspace/App.cs(4,2): error CS1002: ; expected [/workspace/App.csproj]\n"
                + "  Failed App.Tests.WidgetTests.Does_work [12 ms]\n"
                + "Failed!  - Failed: 1, Passed: 2, Skipped: 0, Total: 3, Duration: 20 ms\n",
                "/workspace/App.cs(4,2): error CS1002: ; expected [/workspace/App.csproj]\n"
                + "/workspace/App.cs(8,1): warning CA1822: Mark member static [/workspace/App.csproj]\n",
                "/workspace",
                maxDiagnostics: 10,
                includeVsTestFailures: false);
        WorkspaceCheckTrxParseResult trx = new(
            [
                new WorkspaceCheckToolResultItem(
                    null,
                    null,
                    null,
                    "error",
                    "VSTEST_FAIL",
                    "App.Tests.WidgetTests.Does_work: expected true"),
            ],
            TotalDiagnosticCount: 1,
            TotalTestCount: 3,
            PassedTestCount: 2,
            FailedTestCount: 1,
            SkippedTestCount: 0,
            ParsedAny: true,
            Truncated: false);

        WorkspaceCheckDiagnosticParseResult merged =
            WorkspaceCheckDiagnosticParser.MergeAuthoritativeTrx(
                console,
                trx,
                maxDiagnostics: 2);

        Assert.Collection(
            merged.Diagnostics,
            diagnostic => Assert.Equal("CS1002", diagnostic.Code),
            diagnostic => Assert.Equal("CA1822", diagnostic.Code));
        Assert.Equal(3, merged.TotalDiagnosticCount);
        Assert.Equal(2, merged.ErrorCount);
        Assert.Equal(1, merged.WarningCount);
        Assert.Equal(3, merged.TotalTestCount);
        Assert.Equal(2, merged.PassedTestCount);
        Assert.Equal(1, merged.FailedTestCount);
        Assert.Equal(0, merged.SkippedTestCount);
        Assert.True(merged.Truncated);
    }

    [Theory]
    [InlineData((int)WorkspaceCheckDiagnosticParserKind.MsBuild)]
    [InlineData((int)WorkspaceCheckDiagnosticParserKind.VsTest)]
    [InlineData((int)WorkspaceCheckDiagnosticParserKind.DotNetFormat)]
    public void Every_check_parser_returns_typed_diagnostics(
        int parserValue)
    {

        WorkspaceCheckDiagnosticParseResult parsed =
            WorkspaceCheckDiagnosticParser.Parse(
                (WorkspaceCheckDiagnosticParserKind)parserValue,
                "/workspace/Code.cs(8,4): warning TEST1000: typed message [/workspace/App.csproj]",
                string.Empty,
                "/workspace",
                maxDiagnostics: 10);
        WorkspaceCheckToolResultItem diagnostic = Assert.Single(
            parsed.Diagnostics);

        Assert.Equal("Code.cs", diagnostic.File);
        Assert.Equal(8, diagnostic.Line);
        Assert.Equal(4, diagnostic.Column);
        Assert.Equal("warning", diagnostic.Severity);
        Assert.Equal("TEST1000", diagnostic.Code);
        Assert.Equal("typed message", diagnostic.Message);
        Assert.Equal(1, parsed.WarningCount);
    }

    [Fact]
    public void Unparsed_check_output_remains_available_for_structured_fallback()
    {

        WorkspaceCheckDiagnosticParseResult parsed =
            WorkspaceCheckDiagnosticParser.Parse(
                WorkspaceCheckDiagnosticParserKind.DotNetFormat,
                "tool-specific output without diagnostic shape",
                "tool-specific stderr",
                "/workspace",
                maxDiagnostics: 10);

        Assert.False(parsed.ParsedAny);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(0, parsed.TotalDiagnosticCount);
    }

    [Fact]
    public void Diagnostic_parser_skips_individually_oversized_lines()
    {

        string oversized =
            "/workspace/Code.cs(8,4): error TEST1000: "
            + new string('x', 256 * 1024);

        WorkspaceCheckDiagnosticParseResult parsed =
            WorkspaceCheckDiagnosticParser.Parse(
                WorkspaceCheckDiagnosticParserKind.MsBuild,
                oversized,
                string.Empty,
                "/workspace",
                maxDiagnostics: 10);

        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(0, parsed.TotalDiagnosticCount);
        Assert.True(parsed.Truncated);
        Assert.False(parsed.ParsedAny);

    }

    [Fact]
    public void Structured_result_reports_selected_sdk_and_retains_valid_capped_shape()
    {

        WorkspaceCheckToolResultEnvelope result = new()
        {
            Status = "failed",
            ProfileId = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
            SelectedSdkVersion = "10.0.100",
            Diagnostics =
            [
                new WorkspaceCheckToolResultItem(
                    "a.cs",
                    1,
                    2,
                    "error",
                    "CS0001",
                    "first"),
                new WorkspaceCheckToolResultItem(
                    "b.cs",
                    3,
                    4,
                    "warning",
                    "CA0001",
                    "second"),
            ],
            TotalDiagnosticCount = 2,
        };

        WorkspaceCheckToolResultEnvelope retained = result.RetainLeadingItems(1);

        Assert.Equal("dotnet-build", retained.ProfileId);
        Assert.Equal("10.0.100", retained.SelectedSdkVersion);
        Assert.Single(retained.Diagnostics);
        Assert.Equal(1, retained.OmittedDiagnosticCount);
        Assert.True(retained.Truncated);
    }

    [Fact]
    public void Executable_identity_is_captured_and_revalidated_before_spawn()
    {

        using TestTree tree = new();
        string trustedRoot = tree.CreateDirectory("trusted-dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("trusted-dotnet", NativeDotNetFileName));
        WorkspaceCheckExecutableRuntimePolicy policy =
            WorkspaceCheckExecutableRuntimePolicy.ForTrustedRoots([trustedRoot]);

        WorkspaceCheckExecutableCapture captured = policy.Capture(
            executable,
            workspace);

        Assert.True(captured.Success, captured.Message);
        Assert.True(policy.Revalidate(captured.Snapshot!, workspace).Success);

        File.Delete(executable);
        tree.CreateNativeExecutable(Path.Combine("trusted-dotnet", NativeDotNetFileName));

        WorkspaceCheckExecutableRevalidation changed = policy.Revalidate(
            captured.Snapshot!,
            workspace);

        Assert.False(changed.Success);
        Assert.Equal("executable_changed", changed.Code);
    }

    [SkippableFact]
    public void Executable_policy_rejects_user_writable_trusted_installation_roots()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix installation ownership policy is covered on Unix hosts.");
        using TestTree tree = new();
        string trustedRoot = tree.CreateDirectory("user-writable-dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("user-writable-dotnet", NativeDotNetFileName));
        WorkspaceCheckExecutableRuntimePolicy policy =
            WorkspaceCheckExecutableRuntimePolicy.ForTrustedRoots(
                [trustedRoot],
                requireImmutableOwnership: true);

        WorkspaceCheckExecutableCapture captured =
            policy.Capture(executable, workspace);

        Assert.False(captured.Success);
        Assert.Equal("untrusted_executable", captured.Code);

    }

    [SkippableFact]
    public void Launch_chain_policy_captures_and_revalidates_root_owned_helpers()
    {

        Skip.If(
            !OperatingSystem.IsMacOS(),
            "The mandatory workspace-check launch chain is currently macOS-only.");

        WorkspaceCheckLaunchChainSnapshot? snapshot =
            WorkspaceCheckLaunchChainPolicy.Capture();

        Assert.NotNull(snapshot);
        Assert.True(
            WorkspaceCheckLaunchChainPolicy.Revalidate(snapshot!));

    }

    [Fact]
    public void Runtime_rejects_a_stale_settings_bound_invocation_surface()
    {

        WorkspaceCheckSettings original = new();
        WorkspaceCheckSettings current = new()
        {
            MaxDiagnostics = original.MaxDiagnostics + 1,
        };
        using ServiceProvider services =
            new ServiceCollection().BuildServiceProvider();
        WorkspaceCheckRuntime runtime = new(
            original,
            services.GetRequiredService<IServiceScopeFactory>(),
            currentSettingsProvider: () => current);

        WorkspaceCheckExecutionStatus status =
            runtime.GetStatus(Path.GetTempPath());

        Assert.False(status.IsEligible);
        Assert.Contains(
            "configuration changed",
            status.Reason,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Runtime_keeps_semantically_equivalent_rebound_settings_surface_valid()
    {

        WorkspaceCheckSettings original = new();
        WorkspaceCheckSettings current = original with
        {
            ExecutableCatalog =
                new WorkspaceCheckExecutableCatalogSettings
                {
                    DotNet =
                        new WorkspaceCheckExecutableSettings
                        {
                            Path = original.ExecutableCatalog
                                .DotNet.Path,
                        },
                },
            CustomProfiles =
                new Dictionary<
                    string,
                    WorkspaceCheckProfileSettings>(
                    original.CustomProfiles,
                    StringComparer.OrdinalIgnoreCase),
        };
        using ServiceProvider services =
            new ServiceCollection().BuildServiceProvider();
        WorkspaceCheckRuntime runtime = new(
            original,
            services.GetRequiredService<IServiceScopeFactory>(),
            currentSettingsProvider: () => current);

        WorkspaceCheckExecutionStatus status =
            runtime.GetStatus(Path.GetTempPath());

        Assert.DoesNotContain(
            "configuration changed",
            status.Reason,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Global_json_selects_only_an_installed_sdk_and_runtime_under_the_trusted_dotnet_root()
    {

        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(dotnetRoot, "10.0.100", "10.0.1");
        tree.CreateSdk(dotnetRoot, "10.0.200", "10.0.2");
        File.WriteAllText(
            Path.Combine(workspace, "global.json"),
            """{"sdk":{"version":"10.0.100","rollForward":"disable"}}""");
        WorkspaceCheckExecutableRuntimePolicy executablePolicy =
            WorkspaceCheckExecutableRuntimePolicy.ForTrustedRoots([dotnetRoot]);
        WorkspaceCheckExecutableSnapshot host = executablePolicy
            .Capture(executable, workspace)
            .Snapshot!;

        WorkspaceCheckSdkResolution selected = WorkspaceCheckSdkResolver.Resolve(
            workspace,
            host,
            (_, _) => new WorkspaceCheckMuxerSdkSelection(
                "10.0.100",
                Path.Combine(dotnetRoot, "sdk", "10.0.100")));

        Assert.True(selected.Success, selected.Message);
        Assert.Equal("10.0.100", selected.Snapshot!.Version);
        Assert.EndsWith(
            Path.Combine("sdk", "10.0.100"),
            selected.Snapshot.SdkPath,
            StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine("shared", "Microsoft.NETCore.App", "10.0.1"),
            selected.Snapshot.RuntimePath,
            StringComparison.Ordinal);
        Assert.True(WorkspaceCheckSdkResolver.Revalidate(selected.Snapshot).Success);
        Assert.EndsWith(
            Path.Combine("sdk", "10.0.100", "dotnet.dll"),
            selected.Snapshot.SdkEntryPointPath,
            StringComparison.Ordinal);
        File.Delete(selected.Snapshot.SdkEntryPointPath);
        File.WriteAllText(
            selected.Snapshot.SdkEntryPointPath,
            "replaced");
        Assert.False(
            WorkspaceCheckSdkResolver.Revalidate(
                selected.Snapshot).Success);
    }

    [Fact]
    public void Unavailable_global_json_selection_fails_closed_without_falling_back()
    {

        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(dotnetRoot, "10.0.200", "10.0.2");
        File.WriteAllText(
            Path.Combine(workspace, "global.json"),
            """{"sdk":{"version":"9.0.100","rollForward":"disable"}}""");
        WorkspaceCheckExecutableSnapshot host =
            WorkspaceCheckExecutableRuntimePolicy
                .ForTrustedRoots([dotnetRoot])
                .Capture(executable, workspace)
                .Snapshot!;

        WorkspaceCheckSdkResolution selected = WorkspaceCheckSdkResolver.Resolve(
            workspace,
            host,
            (_, _) => null);

        Assert.False(selected.Success);
        Assert.Equal("sdk_unavailable", selected.Code);
    }

    [Fact]
    public void Sdk_resolver_honors_nearest_applicable_global_json_and_feature_roll_forward()
    {

        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string workspaceParent = tree.CreateDirectory("repo");
        string workspace = tree.CreateDirectory("repo/src/App");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(dotnetRoot, "10.0.200", "10.0.2");
        tree.CreateSdk(dotnetRoot, "10.0.201", "10.0.2");
        tree.CreateSdk(dotnetRoot, "10.0.300", "10.0.3");
        File.WriteAllText(
            Path.Combine(workspaceParent, "global.json"),
            """{"sdk":{"version":"10.0.150","rollForward":"feature","allowPrerelease":false}}""");
        WorkspaceCheckExecutableSnapshot host =
            WorkspaceCheckExecutableRuntimePolicy
                .ForTrustedRoots([dotnetRoot])
                .Capture(executable, workspace)
                .Snapshot!;

        WorkspaceCheckSdkResolution selected =
            WorkspaceCheckSdkResolver.Resolve(
                workspace,
                host,
                (_, _) => new WorkspaceCheckMuxerSdkSelection(
                    "10.0.201",
                    Path.Combine(
                        dotnetRoot,
                        "sdk",
                        "10.0.201")));

        Assert.True(selected.Success, selected.Message);
        Assert.Equal("10.0.201", selected.Snapshot!.Version);

    }

    [Theory]
    [InlineData(
        """{"sdk":{"version":"10.0.100","rollForward":"patch"}}""",
        "10.0.101")]
    [InlineData(
        """{"sdk":{"version":"10.0.100","rollForward":"latestPatch"}}""",
        "10.0.199")]
    [InlineData(
        """{"sdk":{"version":"10.0.100-preview.1"}}""",
        "10.0.100-preview.2")]
    public void Sdk_resolver_defers_roll_forward_and_default_prerelease_to_muxer(
        string globalJson,
        string authoritativeVersion)
    {
        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(
            dotnetRoot,
            authoritativeVersion,
            "10.0.2");
        File.WriteAllText(
            Path.Combine(workspace, "global.json"),
            globalJson);
        WorkspaceCheckExecutableSnapshot host =
            WorkspaceCheckExecutableRuntimePolicy
                .ForTrustedRoots([dotnetRoot])
                .Capture(executable, workspace)
                .Snapshot!;

        WorkspaceCheckSdkResolution selected =
            WorkspaceCheckSdkResolver.Resolve(
                workspace,
                host,
                (_, _) => new WorkspaceCheckMuxerSdkSelection(
                    authoritativeVersion,
                    Path.Combine(
                        dotnetRoot,
                        "sdk",
                        authoritativeVersion)));

        Assert.True(selected.Success, selected.Message);
        Assert.Equal(
            authoritativeVersion,
            selected.Snapshot!.Version);
    }

    [Fact]
    public void Sdk_resolver_honors_trusted_paths_and_rejects_workspace_sdk_paths()
    {

        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(dotnetRoot, "10.0.100", "10.0.1");
        WorkspaceCheckExecutableSnapshot host =
            WorkspaceCheckExecutableRuntimePolicy
                .ForTrustedRoots([dotnetRoot])
                .Capture(executable, workspace)
                .Snapshot!;
        File.WriteAllText(
            Path.Combine(workspace, "global.json"),
            JsonSerializer.Serialize(new
            {
                sdk = new
                {
                    version = "10.0.100",
                    rollForward = "disable",
                    paths = new[] { dotnetRoot },
                },
            }));

        WorkspaceCheckSdkResolution trusted =
            WorkspaceCheckSdkResolver.Resolve(
                workspace,
                host,
                (_, _) => new WorkspaceCheckMuxerSdkSelection(
                    "10.0.100",
                    Path.Combine(
                        dotnetRoot,
                        "sdk",
                        "10.0.100")));

        Assert.True(trusted.Success, trusted.Message);

        string workspaceSdkRoot = tree.CreateDirectory("workspace/local-dotnet");
        tree.CreateSdk(workspaceSdkRoot, "10.0.100", "10.0.1");
        File.WriteAllText(
            Path.Combine(workspace, "global.json"),
            """{"sdk":{"version":"10.0.100","rollForward":"disable","paths":["local-dotnet"]}}""");

        WorkspaceCheckSdkResolution untrusted =
            WorkspaceCheckSdkResolver.Resolve(
                workspace,
                host,
                (_, _) => throw new InvalidOperationException(
                    "Untrusted paths must fail before muxer resolution."));

        Assert.False(untrusted.Success);
        Assert.Equal("invalid_global_json", untrusted.Code);

    }

    [Fact]
    public void Sdk_resolver_accepts_muxer_selected_sdk_from_ordered_trusted_paths()
    {
        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string first = tree.CreateDirectory("dotnet/first");
        string second = tree.CreateDirectory("dotnet/second");
        string workspace = tree.CreateDirectory("workspace");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(dotnetRoot, "10.0.100", "10.0.1");
        tree.CreateSdk(second, "10.0.100", "10.0.1");
        File.WriteAllText(
            Path.Combine(workspace, "global.json"),
            JsonSerializer.Serialize(new
            {
                sdk = new
                {
                    version = "10.0.100",
                    paths = new[] { first, second },
                },
            }));
        WorkspaceCheckExecutableSnapshot host =
            WorkspaceCheckExecutableRuntimePolicy
                .ForTrustedRoots([dotnetRoot])
                .Capture(executable, workspace)
                .Snapshot!;

        WorkspaceCheckSdkResolution selected =
            WorkspaceCheckSdkResolver.Resolve(
                workspace,
                host,
                (_, _) => new WorkspaceCheckMuxerSdkSelection(
                    "10.0.100",
                    Path.Combine(
                        second,
                        "sdk",
                        "10.0.100")));

        Assert.True(selected.Success, selected.Message);
        Assert.EndsWith(
            Path.Combine(
                "second",
                "sdk",
                "10.0.100"),
            selected.Snapshot!.SdkPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Sdk_resolver_uses_sanitized_server_snapshot_when_workspace_global_json_is_replaced()
    {
        using TestTree tree = new();
        string dotnetRoot = tree.CreateDirectory("dotnet");
        string workspace = tree.CreateDirectory("workspace");
        string resolverRoot = tree.CreateDirectory("run/resolver");
        string executable = tree.CreateNativeExecutable(
            Path.Combine("dotnet", NativeDotNetFileName));
        tree.CreateSdk(dotnetRoot, "10.0.100", "10.0.1");
        string globalJson = Path.Combine(workspace, "global.json");
        File.WriteAllText(
            globalJson,
            """{"sdk":{"version":"10.0.100","rollForward":"disable","paths":["$host$"]}}""");
        WorkspaceCheckExecutableSnapshot host =
            WorkspaceCheckExecutableRuntimePolicy
                .ForTrustedRoots([dotnetRoot])
                .Capture(executable, workspace)
                .Snapshot!;
        bool muxerInvoked = false;

        WorkspaceCheckSdkResolution selected =
            WorkspaceCheckSdkResolver.Resolve(
                workspace,
                host,
                (workingRoot, _) =>
                {
                    muxerInvoked = true;
                    Assert.EndsWith(
                        Path.Combine("run", "resolver"),
                        workingRoot,
                        StringComparison.Ordinal);
                    File.WriteAllText(
                        globalJson,
                        """{"sdk":{"version":"99.0.100","paths":["local-sdk"]}}""");
                    string sanitized = File.ReadAllText(
                        Path.Combine(
                            resolverRoot,
                            "global.json"));
                    Assert.Contains(
                        "10.0.100",
                        sanitized,
                        StringComparison.Ordinal);
                    using JsonDocument sanitizedJson = JsonDocument.Parse(sanitized);
                    string[] sanitizedPaths = sanitizedJson.RootElement
                        .GetProperty("sdk")
                        .GetProperty("paths")
                        .EnumerateArray()
                        .Select(static path => path.GetString()!)
                        .ToArray();
                    Assert.Contains(
                        sanitizedPaths,
                        path => PathsEqual(path, host.DotNetRoot));
                    Assert.DoesNotContain(
                        "$host$",
                        sanitized,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        "local-sdk",
                        sanitized,
                        StringComparison.Ordinal);
                    return new WorkspaceCheckMuxerSdkSelection(
                        "10.0.100",
                        Path.Combine(
                            dotnetRoot,
                            "sdk",
                            "10.0.100"));
                },
                resolverRoot);

        Assert.True(muxerInvoked);
        Assert.True(selected.Success, selected.Message);
        Assert.Equal("10.0.100", selected.Snapshot!.Version);
    }

    [Fact]
    public async Task Restore_artifacts_seed_into_the_dotnet_10_artifacts_project_layout()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/src/App");
        string project = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string obj = tree.CreateDirectory("workspace/src/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");
        string artifactsRoot = tree.CreateDirectory("run/artifacts");

        WorkspaceCheckRestoreSeedResult seeded =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                artifactsRoot,
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(seeded.Success, seeded.Message);
        Assert.Equal(1, seeded.ProjectCount);
        Assert.Equal(5, seeded.FileCount);
        string destination = WorkspaceCheckArtifactsLayout.ProjectIntermediateRoot(
            artifactsRoot,
            project);
        Assert.True(File.Exists(Path.Combine(destination, "project.assets.json")));
        Assert.True(File.Exists(Path.Combine(destination, "App.csproj.nuget.g.props")));
        Assert.True(File.Exists(Path.Combine(destination, "App.csproj.nuget.g.targets")));
        Assert.True(File.Exists(Path.Combine(destination, "App.csproj.nuget.dgspec.json")));
        Assert.True(File.Exists(Path.Combine(destination, "project.nuget.cache")));
    }

    [Fact]
    public async Task Restore_seed_default_continues_beyond_former_project_and_artifact_caps()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");

        for (int index = 0; index < 129; index++)
        {

            string projectName = $"Project{index:D3}";
            string projectDirectory =
                tree.CreateDirectory($"workspace/{projectName}");
            File.WriteAllText(
                Path.Combine(projectDirectory, $"{projectName}.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            WriteRestoreArtifacts(
                tree.CreateDirectory($"workspace/{projectName}/obj"),
                $"{projectName}.csproj");

        }

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/many-projects"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(129, result.ProjectCount);
        Assert.Equal(645, result.FileCount);
        Assert.Equal(129, result.InputManifest!.ProjectCount);

    }

    [Fact]
    public async Task Restore_seed_rejects_artifacts_project_name_collisions_without_global_tracking()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");

        foreach (string directory in new[] { "First", "Second" })
        {

            string projectDirectory =
                tree.CreateDirectory($"workspace/{directory}");
            File.WriteAllText(
                Path.Combine(projectDirectory, "Shared.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            WriteRestoreArtifacts(
                tree.CreateDirectory($"workspace/{directory}/obj"),
                "Shared.csproj");

        }

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/name-collision"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("restore_required", result.Code);
        Assert.Contains(
            "map to the same .NET artifacts project name 'Shared'",
            result.Message,
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task Restore_seed_default_continues_beyond_former_per_project_and_global_input_caps()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        string inputDirectory = tree.CreateDirectory("workspace/App/inputs");

        foreach (int index in Enumerable.Range(0, 300))
        {

            File.WriteAllText(
                Path.Combine(inputDirectory, $"input-{index:D3}.props"),
                "<Project />");

        }

        string imports = string.Join(
            System.Environment.NewLine,
            Enumerable.Range(0, 300).Select(
                index =>
                    $"  <Import Project=\"inputs/input-{index:D3}.props\" />"));
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\">{System.Environment.NewLine}{imports}{System.Environment.NewLine}</Project>");
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/App/obj"),
            "App.csproj");

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/many-inputs"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.ProjectCount);
        Assert.Equal(5, result.FileCount);
        Assert.True(result.InputManifest!.RecordCount > 256);

    }

    [Fact]
    public async Task Restore_seed_streams_restore_input_xml_beyond_former_eight_megabyte_parser_cap()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project>"
            + new string(' ', (8 * 1024 * 1024) + 1)
            + "</Project>");
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/App/obj"),
            "App.csproj");

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/large-input"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(result.Success, result.Message);

    }

    [Fact]
    public async Task Restore_seed_honors_cancellation_without_count_caps()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/App/obj"),
            "App.csproj");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/cancelled"),
                WorkspaceCheckRestoreSeedOptions.Default,
                cancellation.Token));

    }

    [Fact]
    public async Task Missing_and_stale_restore_artifacts_fail_closed()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string firstProjectDirectory = tree.CreateDirectory("workspace/First");
        string firstProject = Path.Combine(firstProjectDirectory, "First.csproj");
        File.WriteAllText(firstProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string firstObj = tree.CreateDirectory("workspace/First/obj");
        WriteRestoreArtifacts(firstObj, "First.csproj");
        File.SetLastWriteTimeUtc(firstProject, DateTime.UtcNow.AddMinutes(1));
        string secondProjectDirectory = tree.CreateDirectory("workspace/Second");
        File.WriteAllText(
            Path.Combine(secondProjectDirectory, "Second.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string secondObj = tree.CreateDirectory("workspace/Second/obj");
        WriteRestoreArtifacts(secondObj, "Second.csproj");

        WorkspaceCheckRestoreSeedResult stale =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/stale"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(stale.Success);
        Assert.Equal("restore_required", stale.Code);
    }

    [SkippableFact]
    public async Task Restore_seed_rejects_symlink_escape_and_file_byte_caps()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "File symlink creation requires elevation on Windows.");
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        string project = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string obj = tree.CreateDirectory("workspace/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");
        WorkspaceCheckRestoreSeedResult byteCapped =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/byte-cap"),
                WorkspaceCheckRestoreSeedOptions.Default with
                {
                    MaxBytes = 1,
                },
                CancellationToken.None);
        string outside = Path.Combine(
            tree.CreateDirectory("outside"),
            "project.assets.json");
        File.WriteAllText(outside, "{}");
        string assets = Path.Combine(obj, "project.assets.json");
        File.Delete(assets);
        File.CreateSymbolicLink(assets, outside);
        WorkspaceCheckRestoreSeedResult escaping =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/escaping"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(byteCapped.Success);
        Assert.Equal("seed_cap_exceeded", byteCapped.Code);
        Assert.Contains(
            "MaxFileWriteMb",
            byteCapped.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "explicitly raising",
            byteCapped.Message,
            StringComparison.Ordinal);
        Assert.False(escaping.Success);
        Assert.Equal("restore_required", escaping.Code);
    }

    [SkippableFact]
    public async Task Restore_seed_rejects_symlinked_directory_components_outside_workspace()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Directory symlink creation requires elevation on Windows.");
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string outsideObj = tree.CreateDirectory("outside/obj");
        WriteRestoreArtifacts(outsideObj, "App.csproj");
        Directory.CreateSymbolicLink(
            Path.Combine(projectDirectory, "obj"),
            outsideObj);

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/symlink-component"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("restore_required", result.Code);

    }

    [Fact]
    public async Task Restore_seed_applies_streaming_byte_cap_before_complex_artifact_parse()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string obj = tree.CreateDirectory("workspace/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");
        File.WriteAllText(
            Path.Combine(obj, "project.assets.json"),
            new string('[', 4096));

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/parse-cap"),
                WorkspaceCheckRestoreSeedOptions.Default with
                {
                    MaxBytes = 1024,
                },
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("seed_cap_exceeded", result.Code);

    }

    [Fact]
    public async Task Restore_seed_rejects_cross_workspace_import_inputs()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string outside = tree.CreateDirectory("outside");
        string imported = Path.Combine(outside, "Shared.props");
        File.WriteAllText(imported, "<Project />");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        string project = Path.Combine(
            projectDirectory,
            "App.csproj");
        File.WriteAllText(
            project,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <Import Project="{imported}" />
             </Project>
             """);
        string obj = tree.CreateDirectory("workspace/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/import"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("restore_required", result.Code);

    }

    [Fact]
    public async Task Restore_seed_recursively_tracks_supported_property_expanded_nested_imports()
    {
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        string buildDirectory = tree.CreateDirectory("workspace/App/build");
        string nested = Path.Combine(buildDirectory, "nested.targets");
        File.WriteAllText(nested, "<Project />");
        File.WriteAllText(
            Path.Combine(buildDirectory, "common.props"),
            """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)nested.targets" />
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <BuildRoot>build</BuildRoot>
              </PropertyGroup>
              <Import Project="$(BuildRoot)/common.props" />
            </Project>
            """);
        string obj = tree.CreateDirectory("workspace/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");

        WorkspaceCheckRestoreSeedResult fresh =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/fresh-nested"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(fresh.Success, fresh.Message);

        File.SetLastWriteTimeUtc(
            nested,
            DateTime.UtcNow.AddMinutes(1));
        WorkspaceCheckRestoreSeedResult stale =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/stale-nested"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(stale.Success);
        Assert.Equal("restore_required", stale.Code);
    }

    [Fact]
    public async Task Restore_seed_rejects_dynamic_nested_imports()
    {
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        string buildDirectory = tree.CreateDirectory("workspace/App/build");
        File.WriteAllText(
            Path.Combine(buildDirectory, "common.props"),
            """
            <Project>
              <Import Project="$(UnprovenDynamicRoot)/nested.targets" />
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="build/common.props" />
            </Project>
            """);
        string obj = tree.CreateDirectory("workspace/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/dynamic-nested"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("restore_required", result.Code);
    }

    [Fact]
    public async Task Restore_seed_recursively_tracks_ancestor_imports()
    {
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string build = tree.CreateDirectory("workspace/build");
        string nested = Path.Combine(build, "nested.props");
        File.WriteAllText(nested, "<Project />");
        File.WriteAllText(
            Path.Combine(workspace, "Directory.Build.props"),
            """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)build/nested.props" />
            </Project>
            """);
        string projectDirectory = tree.CreateDirectory("workspace/App");
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string obj = tree.CreateDirectory("workspace/App/obj");
        WriteRestoreArtifacts(obj, "App.csproj");

        WorkspaceCheckRestoreSeedResult fresh =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/ancestor-fresh"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(fresh.Success, fresh.Message);
        File.SetLastWriteTimeUtc(
            nested,
            DateTime.UtcNow.AddMinutes(1));

        WorkspaceCheckRestoreSeedResult stale =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/ancestor-stale"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(stale.Success);
        Assert.Equal("restore_required", stale.Code);
    }

    [Fact]
    public async Task Restore_seed_recurses_through_project_references()
    {
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string app = tree.CreateDirectory("workspace/App");
        string library = tree.CreateDirectory("workspace/Library");
        File.WriteAllText(
            Path.Combine(app, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../Library/Library.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(library, "Library.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$(UnprovenReferenceImport)/shared.props" />
            </Project>
            """);
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/App/obj"),
            "App.csproj");
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/Library/obj"),
            "Library.csproj");

        WorkspaceCheckRestoreSeedResult result =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/reference"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("restore_required", result.Code);
    }

    [Fact]
    public async Task Restore_seed_fingerprints_reject_post_seed_input_mutation()
    {
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string projectDirectory = tree.CreateDirectory("workspace/App");
        string project = Path.Combine(
            projectDirectory,
            "App.csproj");
        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/App/obj"),
            "App.csproj");

        WorkspaceCheckRestoreSeedResult seeded =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/fingerprint"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);

        Assert.True(seeded.Success, seeded.Message);
        WorkspaceCheckRestoreInputManifest manifest =
            Assert.IsType<WorkspaceCheckRestoreInputManifest>(
                seeded.InputManifest);
        Assert.True(File.Exists(manifest.Path));

        if (!OperatingSystem.IsWindows())
        {

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(manifest.Path));
        }

        Assert.True(
            WorkspaceCheckRestoreArtifactSeeder
                .RevalidateManifest(
                    workspace,
                    manifest,
                    WorkspaceCheckRestoreSeedOptions.Default,
                    CancellationToken.None));
        File.AppendAllText(
            project,
            System.Environment.NewLine);

        Assert.False(
            WorkspaceCheckRestoreArtifactSeeder
                .RevalidateManifest(
                    workspace,
                    manifest,
                    WorkspaceCheckRestoreSeedOptions.Default,
                    CancellationToken.None));
    }

    [Theory]
    [InlineData("Directory.Packages.props")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("NuGet.Config")]
    [InlineData("packages.lock.json")]
    [InlineData("global.json")]
    [InlineData("Added.csproj")]
    public async Task Restore_manifest_rejects_new_restore_input_after_seed(
        string addedName)
    {
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(
            workspace,
            "App.csproj");
        File.WriteAllText(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteRestoreArtifacts(
            tree.CreateDirectory("workspace/obj"),
            "App.csproj");
        WorkspaceCheckRestoreSeedResult seeded =
            await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                workspace,
                tree.CreateDirectory("run/manifest"),
                WorkspaceCheckRestoreSeedOptions.Default,
                CancellationToken.None);
        Assert.True(seeded.Success, seeded.Message);

        string content = addedName switch
        {
            "NuGet.Config" => "<configuration />",
            "packages.lock.json" or "global.json" => "{}",
            _ => "<Project />",
        };
        File.WriteAllText(
            Path.Combine(workspace, addedName),
            content);

        Assert.False(
            WorkspaceCheckRestoreArtifactSeeder
                .RevalidateManifest(
                    workspace,
                    seeded.InputManifest!,
                    WorkspaceCheckRestoreSeedOptions.Default,
                    CancellationToken.None));
    }

    [Fact]
    public void Process_start_info_uses_argument_list_artifacts_path_and_test_results_root()
    {

        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string run = tree.CreateDirectory("run");
        WorkspaceCheckRunDirectories directories =
            WorkspaceCheckRunDirectories.CreateUnder(run);
        WorkspaceCheckResolvedProfile profile = new(
            WorkspaceCheckCatalogDefaults.DotNetTestProfileId,
            WorkspaceCheckCatalogDefaults.DotNetExecutableId,
            WorkspaceCheckKind.Test,
            WorkspaceCheckDiagnosticParserKind.VsTest,
            ["test", "--no-restore"]);
        WorkspaceCheckEnvironmentPaths environment = new(
            "/trusted/dotnet",
            directories.Home,
            directories.CliHome,
            directories.HttpCache,
            directories.Temp,
            "/trusted/packages");
        directories.PinSelectedSdk("10.0.100", "/trusted/dotnet");
        string workspaceTarget = Path.Combine(workspace, "App.csproj");

        System.Diagnostics.ProcessStartInfo startInfo =
            WorkspaceCheckProcessStartInfoFactory.Create(
                "/trusted/dotnet/dotnet",
                "/trusted/dotnet/sdk/10.0.100/dotnet.dll",
                workspace,
                profile,
                directories,
                environment,
                workspaceTarget);

        Assert.Equal("/trusted/dotnet/dotnet", startInfo.FileName);
        Assert.Equal(
            "/trusted/dotnet/sdk/10.0.100/dotnet.dll",
            startInfo.ArgumentList[0]);
        Assert.Equal(directories.Root, startInfo.WorkingDirectory);
        Assert.Contains(workspaceTarget, startInfo.ArgumentList);
        Assert.Contains(
            "\"version\": \"10.0.100\"",
            File.ReadAllText(Path.Combine(directories.Root, "global.json")),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"rollForward\": \"disable\"",
            File.ReadAllText(Path.Combine(directories.Root, "global.json")),
            StringComparison.Ordinal);
        Assert.Contains("--artifacts-path", startInfo.ArgumentList);
        Assert.Contains(directories.Artifacts, startInfo.ArgumentList);
        Assert.Contains("--results-directory", startInfo.ArgumentList);
        Assert.Contains(directories.TestResults, startInfo.ArgumentList);
        Assert.DoesNotContain("-c", startInfo.ArgumentList);
        Assert.DoesNotContain("--", startInfo.ArgumentList);
        Assert.Equal("/trusted/packages", startInfo.Environment["NUGET_PACKAGES"]);
        Assert.Contains(
            "/trusted/dotnet",
            startInfo.Environment["PATH"],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            workspace,
            startInfo.Environment["PATH"],
            StringComparison.Ordinal);

        WorkspaceCheckResolvedProfile lintProfile = new(
            WorkspaceCheckCatalogDefaults.DotNetLintProfileId,
            WorkspaceCheckCatalogDefaults.DotNetExecutableId,
            WorkspaceCheckKind.Lint,
            WorkspaceCheckDiagnosticParserKind.DotNetFormat,
            ["format", "--verify-no-changes", "--no-restore"]);
        System.Diagnostics.ProcessStartInfo lint =
            WorkspaceCheckProcessStartInfoFactory.Create(
                "/trusted/dotnet/dotnet",
                "/trusted/dotnet/sdk/10.0.100/dotnet.dll",
                workspace,
                lintProfile,
                directories,
                environment,
                Path.Combine(workspace, "App.csproj"));

        Assert.Equal(
            directories.Artifacts,
            lint.Environment["ArtifactsPath"]);
        Assert.Equal("true", lint.Environment["UseArtifactsOutput"]);
        Assert.Contains("--binarylog", lint.ArgumentList);
        Assert.Contains(
            Path.Combine(directories.Artifacts, "format.binlog"),
            lint.ArgumentList);
        Assert.Contains("--report", lint.ArgumentList);
        Assert.Contains(directories.TestResults, lint.ArgumentList);
        Assert.Equal(directories.Root, lint.WorkingDirectory);
    }

    [SkippableFact]
    public void Mandatory_jail_probe_timeout_kills_and_reaps_probe_process()
    {

        Skip.If(
            OperatingSystem.IsWindows(),
            "Probe process regression uses the Unix shell.");
        using TestTree tree = new();
        string pidFile = Path.Combine(
            tree.CreateDirectory("probe"),
            "probe.pid");

        bool available = WorkspaceCheckExecutionPolicy.ProbeProcess(
            "/bin/sh",
            ["-c", $"echo $$ > \"{pidFile}\"; sleep 60"],
            TimeSpan.FromMilliseconds(200));

        Assert.False(available);
        int pid = int.Parse(
            File.ReadAllText(pidFile).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() =>
            global::System.Diagnostics.Process.GetProcessById(pid));

    }

    [Fact]
    public void Macos_ci_requires_real_workspace_check_production_surface()
    {
        if (!string.Equals(
                global::System.Environment.GetEnvironmentVariable(
                    "ARCANUM_REQUIRE_MACOS_WORKSPACE_CHECK"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.True(
            CanRunMacOsWorkspaceCheck(),
            "The macOS CI lane requires a runnable production sandbox-exec workspace_check surface.");
        Assert.NotNull(
            WorkspaceCheckLaunchChainPolicy.Capture());
    }

    [SkippableFact]
    public async Task Runtime_rejects_new_restore_input_added_after_seed_before_spawn()
    {
        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "App.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(workspace, "Program.cs"),
            "public sealed class AppMarker { }");
        await RestoreAsync(dotnet, project);
        bool mutated = false;
        await using ServiceProvider services =
            CreateRuntimeServices(
                () =>
                {
                    File.WriteAllText(
                        Path.Combine(
                            workspace,
                            "Directory.Packages.props"),
                        "<Project />");
                    mutated = true;
                });
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>());

        WorkspaceCheckToolResultEnvelope result =
            await runtime.RunAsync(
                RuntimeRequest(workspace),
                CancellationToken.None);

        Assert.True(mutated);
        Assert.Equal("restore_required", result.Status);
        Assert.Equal("restore_inputs_changed", result.Code);
        Assert.Null(result.ExitCode);
    }

    [SkippableFact]
    public async Task Real_dotnet_build_uses_seeded_assets_read_only_source_and_split_writable_caches()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "App.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="ReportIsolation" BeforeTargets="BeforeBuild">
                <Message Importance="High" Text="CHECK_PATHS|$(BaseOutputPath)|$(BaseIntermediateOutputPath)|$(MSBuildProjectExtensionsPath)|$(NUGET_PACKAGES)|$(NUGET_HTTP_CACHE_PATH)|$(DOTNET_CLI_HOME)|$(TMPDIR)" />
              </Target>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(workspace, "Program.cs"),
            "public sealed class AppMarker { }");
        await RestoreAsync(dotnet, project);
        string[] before = SnapshotWorkspaceFiles(workspace);
        WorkspaceCheckSettings settings = EnabledRuntimeSettings(dotnet);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            settings,
            services.GetRequiredService<IServiceScopeFactory>());

        WorkspaceCheckToolResultEnvelope result = await runtime.RunAsync(
            RuntimeRequest(workspace),
            CancellationToken.None);

        Assert.True(
            result.Status == "ok",
            $"{result.Status}/{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(before, SnapshotWorkspaceFiles(workspace));
        Assert.False(Directory.Exists(Path.Combine(workspace, "bin")));
        Assert.Contains("CHECK_PATHS|", result.StandardOutput, StringComparison.Ordinal);
        string pathLine = result.StandardOutput!
            .Split('\n')
            .Single(static line => line.Contains("CHECK_PATHS|", StringComparison.Ordinal));
        string[] paths = pathLine.Trim().Split('|');
        Assert.Equal(8, paths.Length);
        Assert.All(
            paths[1..4],
            path => Assert.False(IsWithinOrEqual(path, workspace), path));
        Assert.Equal(
            CanonicalGlobalPackages(),
            Path.TrimEndingDirectorySeparator(paths[4]));
        Assert.All(
            paths[5..],
            path => Assert.False(IsWithinOrEqual(path, workspace), path));
        Assert.Equal(3, paths[5..].Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task Real_package_project_builds_from_seeded_assets_without_restore()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string packageRoot = CanonicalGlobalPackages();
        string packageDirectory = Path.Combine(
            packageRoot,
            "microsoft.bcl.memory");
        Skip.IfNot(
            Directory.Exists(packageDirectory),
            "The pre-existing Microsoft.Bcl.Memory package is required.");
        string version = Directory.EnumerateDirectories(packageDirectory)
            .Select(Path.GetFileName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(static value => value, StringComparer.Ordinal)
            .First()!;
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "PackageApp.csproj");
        File.WriteAllText(
            project,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
               </PropertyGroup>
               <ItemGroup>
                 <PackageReference Include="Microsoft.Bcl.Memory" Version="{version}" />
               </ItemGroup>
             </Project>
             """);
        File.WriteAllText(
            Path.Combine(workspace, "Program.cs"),
            "public sealed class PackageMarker { }");
        await RestoreAsync(dotnet, project);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>());

        WorkspaceCheckToolResultEnvelope result = await runtime.RunAsync(
            RuntimeRequest(workspace),
            CancellationToken.None);

        Assert.True(
            result.Status == "ok",
            $"{result.Status}/{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(
            "restore",
            result.StandardError ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Real_dotnet_lint_uses_seeded_project_state_without_source_writes()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "LintApp.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(workspace, "Marker.cs"),
            """
            namespace Sample;

            public sealed class Marker
            {
            }

            """);
        await RestoreAsync(dotnet, project);
        string[] before = SnapshotWorkspaceFiles(workspace);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>());

        WorkspaceCheckToolResultEnvelope result = await runtime.RunAsync(
            RuntimeRequest(
                workspace,
                WorkspaceCheckCatalogDefaults.DotNetLintProfileId),
            CancellationToken.None);

        Assert.True(
            result.Status == "ok",
            $"{result.Status}/{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.Equal(before, SnapshotWorkspaceFiles(workspace));
    }

    [SkippableFact]
    public async Task Real_dotnet_test_uses_seeded_assets_and_external_result_root()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string testSdkVersion = FindInstalledPackageVersion(
            "microsoft.net.test.sdk");
        string xunitVersion = FindInstalledPackageVersion("xunit");
        string runnerVersion = FindInstalledPackageVersion(
            "xunit.runner.visualstudio");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "CheckedTests.csproj");
        File.WriteAllText(
            project,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <IsTestProject>true</IsTestProject>
                 <IsPackable>false</IsPackable>
               </PropertyGroup>
               <ItemGroup>
                 <PackageReference Include="Microsoft.NET.Test.Sdk" Version="{testSdkVersion}" />
                 <PackageReference Include="xunit" Version="{xunitVersion}" />
                 <PackageReference Include="xunit.runner.visualstudio" Version="{runnerVersion}" PrivateAssets="all" />
               </ItemGroup>
             </Project>
             """);
        File.WriteAllText(
            Path.Combine(workspace, "CheckedTest.cs"),
            """
            using Xunit;

            public sealed class CheckedTest
            {
                [Fact]
                public void Passes() => Assert.True(true);
            }

            """);
        await RestoreAsync(dotnet, project);
        string[] before = SnapshotWorkspaceFiles(workspace);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>());

        WorkspaceCheckToolResultEnvelope result = await runtime.RunAsync(
            RuntimeRequest(
                workspace,
                WorkspaceCheckCatalogDefaults.DotNetTestProfileId),
            CancellationToken.None);

        Assert.True(
            result.Status == "ok",
            $"{result.Status}/{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Passed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, result.TotalTestCount);
        Assert.Equal(1, result.PassedTestCount);
        Assert.Equal(0, result.FailedTestCount);
        Assert.Equal(0, result.SkippedTestCount);
        Assert.Equal(before, SnapshotWorkspaceFiles(workspace));
    }

    [SkippableFact]
    public async Task Malicious_msbuild_source_write_is_denied_and_reported_structurally()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "Malicious.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="AttemptSourceWrite" BeforeTargets="BeforeBuild">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/pwned.txt" Lines="owned" Overwrite="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(workspace, "Program.cs"),
            "public sealed class SafeMarker { }");
        await RestoreAsync(dotnet, project);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>());

        WorkspaceCheckToolResultEnvelope result = await runtime.RunAsync(
            RuntimeRequest(workspace),
            CancellationToken.None);

        Assert.True(
            result.Status == "failed",
            $"{result.Status}/{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.True(
            result.Code == "source_write_denied",
            $"{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.False(File.Exists(Path.Combine(workspace, "pwned.txt")));
        WorkspaceCheckToolResultItem diagnostic = Assert.Single(
            result.Diagnostics);
        Assert.Equal("Malicious.csproj", diagnostic.File);
        Assert.Equal(6, diagnostic.Line);
        Assert.Equal(5, diagnostic.Column);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("MSB3491", diagnostic.Code);
    }

    [SkippableFact]
    public async Task Check_owned_timeout_kills_tree_and_returns_normal_timed_out_outcome()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "Slow.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <UsingTask TaskName="SlowTask" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <Task>
                  <Code Type="Fragment" Language="cs">System.Threading.Thread.Sleep(20000);</Code>
                </Task>
              </UsingTask>
              <Target Name="SlowTask" BeforeTargets="BeforeBuild">
                <SlowTask />
              </Target>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(workspace, "Code.cs"),
            "public sealed class SlowMarker { }");
        await RestoreAsync(dotnet, project);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>(),
            processTimeoutOverride: TimeSpan.FromMilliseconds(500));

        WorkspaceCheckToolResultEnvelope result = await runtime.RunAsync(
            RuntimeRequest(workspace),
            CancellationToken.None);

        Assert.True(
            result.Status == "timed_out",
            $"{result.Status}/{result.Code}: {result.Message}\n{result.StandardOutput}\n{result.StandardError}");
        Assert.Equal("timed_out", result.Code);
        Assert.Null(result.ExitCode);
    }

    [SkippableFact]
    public async Task Caller_cancellation_kills_check_cleans_and_propagates()
    {

        Skip.IfNot(
            CanRunMacOsWorkspaceCheck(),
            "Requires a runnable macOS sandbox-exec filesystem jail.");
        string dotnet = ResolveInstalledDotNet();
        using TestTree tree = new();
        string workspace = tree.CreateDirectory("workspace");
        string project = Path.Combine(workspace, "Cancel.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <UsingTask TaskName="SlowTask" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <Task>
                  <Code Type="Fragment" Language="cs">System.Threading.Thread.Sleep(20000);</Code>
                </Task>
              </UsingTask>
              <Target Name="SlowTask" BeforeTargets="BeforeBuild">
                <SlowTask />
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/after-cancel.txt" Lines="bad" />
              </Target>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(workspace, "Code.cs"),
            "public sealed class CancelMarker { }");
        await RestoreAsync(dotnet, project);
        await using ServiceProvider services = CreateRuntimeServices();
        WorkspaceCheckRuntime runtime = new(
            EnabledRuntimeSettings(dotnet),
            services.GetRequiredService<IServiceScopeFactory>());
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(750));

        WorkspaceCheckToolResultEnvelope? completed = null;
        Exception? failure = await Record.ExceptionAsync(
            async () =>
            {
                completed = await runtime.RunAsync(
                    RuntimeRequest(workspace),
                    cancellation.Token);
            });

        Assert.True(
            failure is OperationCanceledException,
            completed is null
                ? failure?.ToString()
                : $"{completed.Status}/{completed.Code}: {completed.Message}\n{completed.StandardOutput}\n{completed.StandardError}");

        Assert.False(
            File.Exists(Path.Combine(workspace, "after-cancel.txt")));
    }

    private static WorkspaceCheckSettings EnabledRuntimeSettings(
        string dotnet) =>
        new()
        {
            Enabled = true,
            MaxDiagnostics = 100,
            MaxOutputBytes = 1024 * 1024,
            ExecutableCatalog = new WorkspaceCheckExecutableCatalogSettings
            {
                DotNet = new WorkspaceCheckExecutableSettings
                {
                    Path = dotnet,
                },
            },
        };

    private static WorkspaceCheckRuntimeRequest RuntimeRequest(
        string workspace,
        string profileId = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId)
    {

        return new WorkspaceCheckRuntimeRequest(
            workspace,
            profileId,
            new Dictionary<string, string>());
    }

    private static ServiceProvider CreateRuntimeServices(
        Action? beforeLimitApply = null)
    {

        ServiceCollection services = new();
        services.AddSingleton<ISanctumGuard>(
            new PermissiveSanctumGuard());
        services.AddSingleton<IProcessResourceLimiter>(
            beforeLimitApply is null
                ? new ProcessResourceLimiter()
                : new CallbackProcessResourceLimiter(
                    beforeLimitApply));
        return services.BuildServiceProvider();
    }

    private static string ResolveInstalledDotNet()
    {

        string[] candidates =
        [
            "/opt/dotnet/dotnet",
            "/usr/local/share/dotnet/dotnet",
            "/usr/share/dotnet/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/usr/local/bin/dotnet",
        ];

        string? found = candidates.FirstOrDefault(File.Exists);

        return found
            ?? throw new InvalidOperationException(
                "No trusted installed dotnet host was found.");
    }

    private static string CanonicalGlobalPackages()
    {

        string path = Path.Combine(
            TestProcessPaths.OriginalUserProfile,
            ".nuget",
            "packages");
        DirectoryInfo directory = new(path);

        return Path.TrimEndingDirectorySeparator(
            directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? directory.FullName);
    }

    private static string FindInstalledPackageVersion(
        string packageId)
    {

        string root = Path.Combine(
            CanonicalGlobalPackages(),
            packageId);

        Skip.IfNot(
            Directory.Exists(root),
            $"The pre-existing {packageId} package is required.");

        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(
                static value => value,
                StringComparer.Ordinal)
            .First()!;
    }

    private static async Task RestoreAsync(
        string dotnet,
        string project)
    {

        System.Diagnostics.ProcessStartInfo startInfo = new()
        {
            FileName = dotnet,
            WorkingDirectory = Path.GetDirectoryName(project)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("--packages");
        startInfo.ArgumentList.Add(CanonicalGlobalPackages());
        startInfo.ArgumentList.Add("--ignore-failed-sources");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using System.Diagnostics.Process process = new()
        {
            StartInfo = startInfo,
        };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout;
        string error = await stderr;

        Assert.True(
            process.ExitCode == 0,
            $"Restore setup failed with {process.ExitCode}."
            + global::System.Environment.NewLine
            + output
            + global::System.Environment.NewLine
            + error);
    }

    private static string[] SnapshotWorkspaceFiles(string workspace) =>
        Directory.EnumerateFiles(
                workspace,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(workspace, path))
            .OrderBy(
                static path => path,
                StringComparer.Ordinal)
            .ToArray();

    private static bool IsWithinOrEqual(
        string candidate,
        string root)
    {

        string relative = Path.GetRelativePath(
            Path.GetFullPath(root),
            Path.GetFullPath(candidate.Trim()));

        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    private static bool CanRunMacOsWorkspaceCheck()
    {

        if (!OperatingSystem.IsMacOS()
            || !File.Exists("/usr/bin/sandbox-exec"))
        {

            return false;
        }

        try
        {

            using System.Diagnostics.Process process = new();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/sandbox-exec",
                ArgumentList =
                {
                    "-p",
                    "(version 1)(allow default)",
                    "/usr/bin/true",
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            return process.Start()
                && process.WaitForExit(5_000)
                && process.ExitCode == 0;
        }
        catch
        {

            return false;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {

        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private static void WriteRestoreArtifacts(string obj, string projectFileName)
    {

        File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");
        File.WriteAllText(
            Path.Combine(obj, projectFileName + ".nuget.g.props"),
            "<Project />");
        File.WriteAllText(
            Path.Combine(obj, projectFileName + ".nuget.g.targets"),
            "<Project />");
        File.WriteAllText(
            Path.Combine(obj, projectFileName + ".nuget.dgspec.json"),
            "{}");
        File.WriteAllText(Path.Combine(obj, "project.nuget.cache"), "{}");
    }

    private static string NativeDotNetFileName =>
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private sealed class PermissiveSanctumGuard : ISanctumGuard
    {
        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?>
            GetChildProcessBoundaryForWorkspaceAsync(
                string? workspaceRoot,
                CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class CallbackProcessResourceLimiter(
        Action beforeApply) : IProcessResourceLimiter
    {

        private readonly ProcessResourceLimiter _inner = new();

        public ProcessResourceLimiterResult Apply(
            System.Diagnostics.ProcessStartInfo startInfo,
            ResourceLimits limits)
        {

            beforeApply();
            return _inner.Apply(startInfo, limits);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed class TestTree : IDisposable
    {

        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-workspace-check-{Guid.NewGuid():N}");

        public TestTree() => Directory.CreateDirectory(_root);

        public string CreateDirectory(string relativePath)
        {

            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateNativeExecutable(string relativePath)
        {

            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] nativeHeader = OperatingSystem.IsWindows()
                ? [(byte)'M', (byte)'Z', 0x00, 0x00]
                : OperatingSystem.IsLinux()
                    ? [0x7F, (byte)'E', (byte)'L', (byte)'F']
                    : [0xCF, 0xFA, 0xED, 0xFE];
            File.WriteAllBytes(path, nativeHeader);

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            return path;
        }

        public void CreateSdk(
            string dotnetRoot,
            string sdkVersion,
            string runtimeVersion)
        {

            string sdk = Path.Combine(dotnetRoot, "sdk", sdkVersion);
            Directory.CreateDirectory(sdk);
            File.WriteAllText(
                Path.Combine(sdk, ".version"),
                "test-commit"
                + global::System.Environment.NewLine
                + runtimeVersion
                + global::System.Environment.NewLine);
            File.WriteAllText(
                Path.Combine(sdk, "dotnet.dll"),
                "test-sdk-entrypoint");
            Directory.CreateDirectory(
                Path.Combine(
                    dotnetRoot,
                    "shared",
                    "Microsoft.NETCore.App",
                    runtimeVersion));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
