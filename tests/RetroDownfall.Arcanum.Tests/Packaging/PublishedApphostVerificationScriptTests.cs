using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class PublishedApphostVerificationScriptTests
{
    private const string PublishedFilter =
        "FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumSessionSmokeTests."
        + "Published_executable_creates_initial_session_and_completes_provider_contract_exchange";

    private static void RequirePosixScriptFixture() =>
        Skip.If(
            OperatingSystem.IsWindows(),
            "The Bash behavior fixture requires native POSIX process and filesystem semantics.");

    [SkippableFact]
    public async Task Gate_fails_closed_when_the_test_runner_reports_success_without_a_receipt()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new();

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-published-apphost.sh"),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "selected smoke test did not execute successfully",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Gate_runs_only_the_exact_published_smoke_and_accepts_its_receipt()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-published-apphost.sh"),
            "--executable",
            Path.GetFileName(fixture.Executable));

        Assert.Equal(0, result.ExitCode);
        string dotnetLog = File.ReadAllText(fixture.DotnetLog);

        Assert.Contains($"--filter {PublishedFilter}", dotnetLog, StringComparison.Ordinal);
        string publishedExecutable = ReadLoggedValue(dotnetLog, "published-executable=");

        Assert.True(Path.IsPathFullyQualified(publishedExecutable));
        Assert.Equal(Path.GetFileName(fixture.Executable), Path.GetFileName(publishedExecutable));
    }

    [SkippableFact]
    public async Task Gate_accepts_an_exact_windows_style_success_receipt()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published-crlf");

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-published-apphost.sh"),
            "--executable",
            fixture.Executable);

        Assert.Equal(0, result.ExitCode);
    }

    [SkippableFact]
    public async Task Gate_fails_when_its_private_temporary_directory_cannot_be_removed()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.FailRecursiveRemovalFor("arcanum-apphost-verify.");
        fixture.CaptureBackoffDelays();
        fixture.DisableErrexitBeforeSuccessfulExit("Exact published apphost is warning-free");

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-published-apphost.sh"),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "could not remove apphost verification temporary directory after three attempts",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Equal(["0.1", "0.2"], fixture.ReadBackoffDelays());
    }

    [SkippableFact]
    public async Task Gate_fails_when_its_warning_scan_cannot_run()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.FailSearches();

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-published-apphost.sh"),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("published-apphost warning scan failed", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("published")]
    [InlineData("published-crlf")]
    public async Task Gate_rejects_receipt_bytes_after_the_required_line(
        string receiptKind)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind);
        fixture.AppendReceiptBytesWithoutANewline();

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-published-apphost.sh"),
            "--executable",
            fixture.Executable);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("invalid success receipt", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Shipping_gate_fails_when_its_private_temporary_directory_cannot_be_removed()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        fixture.FailRecursiveRemovalFor("arcanum-shipping-verify.");
        fixture.CaptureBackoffDelays();
        fixture.DisableErrexitBeforeSuccessfulExit("Shipping publish is Native AOT");

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-shipping-publish.sh"),
            "--rid",
            "osx-arm64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "could not remove shipping verification temporary directory after three attempts",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains("Exact published apphost is warning-free", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(["0.1", "0.2"], fixture.ReadBackoffDelays());
    }

    [SkippableFact]
    public async Task Shipping_gate_fails_when_its_warning_scan_cannot_run()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.FailSearches();

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-shipping-publish.sh"),
            "--rid",
            "osx-arm64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("shipping warning scan failed", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Shipping_gate_fails_when_its_forbidden_runtime_scan_cannot_run()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.FailFileSearches();

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-shipping-publish.sh"),
            "--rid",
            "osx-arm64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not inspect the Native AOT publish shape", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Shipping_gate_fails_when_its_macho_file_inventory_cannot_run()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.FailRegularFileSearches();

        ScriptResult result = await fixture.RunAsync(
            Path.Combine(fixture.RepositoryRoot, "scripts", "verify-shipping-publish.sh"),
            "--rid",
            "osx-arm64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "could not inspect the Native AOT publish shape for Mach-O files",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Shipping_success_without_retention_removes_both_private_trees()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();

        ScriptResult result = await fixture.RunShippingAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Shipping publish is Native AOT", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "retained")));
        Assert.Single(File.ReadAllLines(fixture.DotnetLog), line => line.StartsWith("publish ", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shipping_retains_the_exact_whole_closure_after_original_apphost_smoke(bool relative)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        string destination = Path.Combine(fixture.Root, "retained publish");

        ScriptResult result = await fixture.RunShippingAsync(
            "--retain-publish-dir", relative ? "retained publish" : destination);

        Assert.Equal(0, result.ExitCode);
        string retainedPath = ReadLoggedValue(result.StandardOutput, "==> Retained qualified publish: ");

        Assert.True(Path.IsPathFullyQualified(retainedPath));
        Assert.True(Directory.Exists(retainedPath));
        Assert.EndsWith("/retained publish", retainedPath, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
        Assert.Equal("#!/usr/bin/env bash\nexit 0\n", File.ReadAllText(Path.Combine(destination, "RetroDownfall.Arcanum.Cli")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
                File.GetUnixFileMode(Path.Combine(destination, "RetroDownfall.Arcanum.Cli")));
        }

        Assert.Equal("native sibling\n", File.ReadAllText(Path.Combine(destination, "libfixture.dylib")));
        Assert.Equal("nested payload\n", File.ReadAllText(Path.Combine(destination, "nested", "payload")));
        Assert.Equal("hidden payload\n", File.ReadAllText(Path.Combine(destination, ".closure")));
        Assert.Equal(4, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        string[] dotnetLog = File.ReadAllLines(fixture.DotnetLog);

        Assert.Single(dotnetLog, line => line.StartsWith("publish ", StringComparison.Ordinal));
        string smokeExecutable = ReadLoggedValue(string.Join("\n", dotnetLog), "published-executable=");

        Assert.Contains(Path.GetFileName(fixture.Root) + "/process-temp/arcanum-shipping-verify.", smokeExecutable, StringComparison.Ordinal);
        Assert.EndsWith("/publish/RetroDownfall.Arcanum.Cli", smokeExecutable, StringComparison.Ordinal);
        Assert.DoesNotContain(destination, smokeExecutable, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("directory")]
    [InlineData("empty-directory")]
    [InlineData("file")]
    [InlineData("dangling-link")]
    public async Task Shipping_refuses_existing_retention_leaves_without_modifying_them(string kind)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        string destination = Path.Combine(fixture.Root, "retained");

        switch (kind)
        {
            case "directory":
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "sentinel"), "unrelated bytes");
                break;
            case "empty-directory":
                Directory.CreateDirectory(destination);
                break;
            case "file":
                File.WriteAllText(destination, "unrelated bytes");
                break;
            default:
                File.CreateSymbolicLink(destination, "missing-target");
                break;
        }

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", destination);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DotnetLog));

        switch (kind)
        {
            case "directory":
                Assert.Equal("unrelated bytes", File.ReadAllText(Path.Combine(destination, "sentinel")));
                Assert.Single(Directory.GetFileSystemEntries(destination));
                break;
            case "empty-directory":
                Assert.True(Directory.Exists(destination));
                Assert.Empty(Directory.GetFileSystemEntries(destination));
                break;
            case "file":
                Assert.Equal("unrelated bytes", File.ReadAllText(destination));
                break;
            default:
                Assert.Equal("missing-target", new FileInfo(destination).LinkTarget);
                break;
        }
    }

    [SkippableTheory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("missing-parent")]
    [InlineData("dot")]
    [InlineData("private-tree")]
    public async Task Shipping_refuses_invalid_retention_arguments_before_publishing(string kind)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        string destination = kind switch
        {
            "empty" => "",
            "missing-parent" => "missing-parent/retained",
            "dot" => ".",
            "private-tree" => fixture.UsePredictableShippingTemporaryDirectory(),
            _ => "unused",
        };
        string[] arguments = kind == "missing" ? ["--retain-publish-dir"] : ["--retain-publish-dir", destination];

        ScriptResult result = await fixture.RunShippingAsync(arguments);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.DotnetLog));
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
    }

    [SkippableTheory]
    [InlineData("publish-failure")]
    [InlineData("publish-warning")]
    [InlineData("scan-failure")]
    [InlineData("runner-failure")]
    [InlineData("missing-receipt")]
    [InlineData("malformed-receipt")]
    public async Task Shipping_never_retains_output_from_a_failed_gate(string failure)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: failure == "missing-receipt" ? null : "published");
        fixture.EnableShippingSuccess();
        string destination = Path.Combine(fixture.Root, "retained");

        switch (failure)
        {
            case "publish-failure":
                fixture.EnvironmentOverrides["FAKE_PUBLISH_EXIT"] = "17";
                fixture.DisableErrexitBeforeSuccessfulExit("dotnet publish");
                break;
            case "publish-warning":
                fixture.EmitPublishWarningOutput();
                break;
            case "scan-failure":
                fixture.FailSearches();
                break;
            case "runner-failure":
                fixture.EnvironmentOverrides["FAKE_TEST_EXIT"] = "19";
                fixture.DisableErrexitBeforeSuccessfulExit("verify-published-apphost.sh");
                break;
            case "malformed-receipt":
                fixture.AppendReceiptBytesWithoutANewline();
                break;
        }

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", destination);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(destination));
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
        Assert.Single(File.ReadAllLines(fixture.DotnetLog), line => line.StartsWith("publish ", StringComparison.Ordinal));

        if (failure == "publish-failure")
        {
            Assert.Equal(17, result.ExitCode);
            Assert.DoesNotContain("published-executable=", File.ReadAllText(fixture.DotnetLog), StringComparison.Ordinal);
        }
        else if (failure == "runner-failure")
        {
            Assert.Equal(19, result.ExitCode);
            Assert.Contains("published-executable=", File.ReadAllText(fixture.DotnetLog), StringComparison.Ordinal);
        }
        else
        {
            string diagnostic = failure switch
            {
                "publish-warning" => "warning-free shipping verification found warning output",
                "scan-failure" => "shipping warning scan failed",
                "missing-receipt" => "success receipt was not created",
                _ => "invalid success receipt",
            };

            Assert.Contains(diagnostic, result.StandardError, StringComparison.Ordinal);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shipping_partial_copy_failure_removes_only_owned_output_and_reports_blocked_removal(bool blockRemoval)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        fixture.FailRetentionCopy();
        fixture.DisableErrexitBeforeSuccessfulExit("cp -a");
        fixture.CaptureBackoffDelays();
        string destination = Path.Combine(fixture.Root, "retained");
        string sentinel = Path.Combine(fixture.Root, "sentinel");
        File.WriteAllText(sentinel, "unrelated bytes");

        if (blockRemoval)
        {
            fixture.FailRecursiveRemovalFor("/retained");
        }

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", destination);

        Assert.Equal(23, result.ExitCode);
        Assert.Contains("Exact published apphost is warning-free", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("unrelated bytes", File.ReadAllText(sentinel));
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
        Assert.Equal(blockRemoval, Directory.Exists(destination));

        if (blockRemoval)
        {
            Assert.Equal("#!/usr/bin/env bash\nexit 0\n", File.ReadAllText(Path.Combine(destination, "RetroDownfall.Arcanum.Cli")));
            Assert.Contains(destination, result.StandardError, StringComparison.Ordinal);
            Assert.Contains("unusable failed-run artifact", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(["0.1", "0.2"], fixture.ReadBackoffDelays());
            Assert.Equal(3, fixture.ReadRemovalAttempts().Length);
        }
    }

    [SkippableFact]
    public async Task Shipping_cleanup_failure_after_complete_copy_removes_retained_output()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        fixture.FailRecursiveRemovalFor("arcanum-shipping-verify.");
        fixture.CaptureBackoffDelays();
        fixture.DisableErrexitBeforeSuccessfulExit("cp -a");
        fixture.CaptureCompletedRetentionCopy();
        string destination = Path.Combine(fixture.Root, "retained");

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", destination);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Exact published apphost is warning-free", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("fixture completed whole copy", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("could not remove shipping verification temporary directory after three attempts", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(["0.1", "0.2"], fixture.ReadBackoffDelays());
        Assert.Single(Directory.GetDirectories(fixture.ProcessTemporaryDirectory));
        Assert.Equal(3, fixture.ReadRemovalAttempts().Length);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shipping_atomic_destination_creation_failure_does_not_take_ownership(bool race)
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        fixture.FailRetentionDirectoryCreation(race);
        string destination = Path.Combine(fixture.Root, "retained");

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", destination);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Exact published apphost is warning-free", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
        Assert.Equal(race, Directory.Exists(destination));

        if (race)
        {
            Assert.Equal("race owner", File.ReadAllText(Path.Combine(destination, "sentinel")));
        }
    }

    [SkippableFact]
    public async Task Shipping_retention_resolves_a_symlink_parent_to_its_physical_directory()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        string actualParent = Path.Combine(fixture.Root, "actual parent");
        Directory.CreateDirectory(actualParent);
        string linkParent = Path.Combine(fixture.Root, "linked parent");
        Directory.CreateSymbolicLink(linkParent, actualParent);

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", "linked parent/retained");

        Assert.Equal(0, result.ExitCode);
        string retainedPath = ReadLoggedValue(result.StandardOutput, "==> Retained qualified publish: ");

        Assert.EndsWith("/actual parent/retained", retainedPath, StringComparison.Ordinal);
        Assert.DoesNotContain("linked parent", retainedPath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(actualParent, "retained", "RetroDownfall.Arcanum.Cli")));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.ProcessTemporaryDirectory));
    }

    [SkippableFact]
    public async Task Shipping_preserves_publish_failure_status_when_private_cleanup_also_fails_without_errexit()
    {
        RequirePosixScriptFixture();

        using ScriptFixture fixture = new(receiptKind: "published");
        fixture.EnableShippingSuccess();
        fixture.EnvironmentOverrides["FAKE_PUBLISH_EXIT"] = "17";
        fixture.FailRecursiveRemovalFor("arcanum-shipping-verify.");
        fixture.CaptureBackoffDelays();
        fixture.DisableErrexitBeforeSuccessfulExit("dotnet publish");
        string destination = Path.Combine(fixture.Root, "retained");

        ScriptResult result = await fixture.RunShippingAsync("--retain-publish-dir", destination);

        Assert.Equal(17, result.ExitCode);
        Assert.Contains("original status: 17", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.DoesNotContain("Retained qualified publish", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(["0.1", "0.2"], fixture.ReadBackoffDelays());
        Assert.Single(Directory.GetDirectories(fixture.ProcessTemporaryDirectory));
        Assert.Equal(3, fixture.ReadRemovalAttempts().Length);
    }

    [Fact]
    public void Shipping_publish_delegates_the_exact_output_to_the_reusable_non_publishing_gate()
    {
        string root = FindRepositoryRoot();
        string gate = File.ReadAllText(
            Path.Combine(root, "scripts", "verify-published-apphost.sh"));
        string publisher = File.ReadAllText(
            Path.Combine(root, "scripts", "verify-shipping-publish.sh"));

        Assert.DoesNotContain("dotnet publish", gate, StringComparison.Ordinal);
        Assert.Contains("--executable \"$EXECUTABLE\"", publisher, StringComparison.Ordinal);
        Assert.Contains("verify-published-apphost.sh", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_workflow_packaging_an_executable_archive_extracts_and_smokes_it_before_upload()
    {
        string root = FindRepositoryRoot();
        string workflowDirectory = Path.Combine(root, ".github", "workflows");
        int packagedArchives = 0;

        foreach (string workflowPath in Directory.GetFiles(workflowDirectory, "*.yml"))
        {
            string workflow = File.ReadAllText(workflowPath);
            packagedArchives += AssertPackagedArchivesAreGated(
                workflowPath,
                workflow,
                "./scripts/packaging/macos/build-arcanum.sh",
                [
                    "ARCANUM_ARCHIVE: dist/arcanum-osx-arm64.zip",
                    "ditto -x -k \"$ARCANUM_ARCHIVE\" \"$SMOKE_DIR\"",
                    "--executable \"$SMOKE_DIR/arcanum-osx-arm64/arcanum\"",
                ]);
            packagedArchives += AssertPackagedArchivesAreGated(
                workflowPath,
                workflow,
                @".\scripts\packaging\windows\package-windows.ps1",
                [
                    "$archive = \"dist\\windows\\arcanum-$env:RID.zip\"",
                    "Expand-Archive -LiteralPath $archive -DestinationPath $smokeRoot",
                    @"arcanum-$env:RID\arcanum.exe",
                    "verify-published-apphost.sh --executable $executable",
                ]);
        }

        // The macOS release and the Windows build are the two workflows that package an Arcanum
        // archive. A third copy lived in private-beta-release.yml until that second release path was
        // deleted; losing either remaining one is what this count exists to catch.
        Assert.True(
            packagedArchives >= 2,
            $"Expected both shipping archive workflows, but found {packagedArchives}.");
    }

    private static int AssertPackagedArchivesAreGated(
        string workflowPath,
        string workflow,
        string packageCommand,
        IReadOnlyList<string> orderedGateEvidence)
    {
        int count = 0;
        int searchFrom = 0;

        while (true)
        {
            int package = workflow.IndexOf(packageCommand, searchFrom, StringComparison.Ordinal);

            if (package < 0)
            {
                return count;
            }

            count++;
            int upload = workflow.IndexOf("uses: actions/upload-artifact@", package, StringComparison.Ordinal);

            Assert.True(
                upload > package,
                $"{Path.GetFileName(workflowPath)} packages an Arcanum executable archive but never uploads it.");

            string beforeUpload = workflow[package..upload];
            int previous = -1;

            foreach (string evidence in orderedGateEvidence)
            {
                int current = beforeUpload.IndexOf(evidence, previous + 1, StringComparison.Ordinal);

                Assert.True(
                    current > previous,
                    $"{Path.GetFileName(workflowPath)} must extract and smoke the exact packaged archive "
                    + $"before upload; missing or out of order: {evidence}");
                previous = current;
            }

            Assert.DoesNotContain("verify-shipping-publish.sh", beforeUpload, StringComparison.Ordinal);
            searchFrom = package + packageCommand.Length;
        }
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(sourceFile)!);

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

    internal static string ReadLoggedValue(
        string log,
        string prefix)
    {
        string line = Assert.Single(
            log.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));

        return line[prefix.Length..].TrimEnd('\r');
    }

    internal sealed class ScriptFixture : IDisposable
    {
        private const string ExpectedImageSha256 =
            "88d2994d07000a2c3bb31f307c536c6a9c45731d9d002c33bd8407817f269cb0";

        private readonly string _root;

        private readonly string _processTemporaryDirectory;

        private string? _recursiveRemovalFailureFragment;

        private string? _successfulExitMarker;

        private bool _appendReceiptBytesWithoutANewline;

        private string? _backoffLog;

        private bool _emitWarning;

        public ScriptFixture(string? receiptKind = null)
        {
            RepositoryRoot = FindRepositoryRoot();
            _root = Path.Combine(
                Path.GetTempPath(),
                $"arcanum-published-apphost-script-{Guid.NewGuid():N}");
            string fakeBin = Path.Combine(_root, "bin");

            _processTemporaryDirectory = Path.Combine(_root, "process-temp");

            Directory.CreateDirectory(fakeBin);
            Directory.CreateDirectory(_processTemporaryDirectory);

            Executable = Path.Combine(_root, "arcanum");
            Image = Path.Combine(_root, "qualified-image.jpg");
            DotnetLog = Path.Combine(_root, "dotnet.log");
            ReceiptKind = receiptKind;
            FakeBin = fakeBin;

            WriteExecutable(
                Executable,
                """
                #!/usr/bin/env bash
                exit 0
                """);
            File.WriteAllBytes(Image, [0xFF, 0xD8, 0xFF, 0xD9]);
            WriteExecutable(
                Path.Combine(fakeBin, "rg"),
                """
                #!/usr/bin/env bash
                exit 1
                """);
            WriteExecutable(
                Path.Combine(fakeBin, "shasum"),
                """
                #!/usr/bin/env bash
                printf '%s  %s\n' '__EXPECTED_IMAGE_SHA256__' "${@: -1}"
                """.Replace(
                    "__EXPECTED_IMAGE_SHA256__",
                    ExpectedImageSha256,
                    StringComparison.Ordinal));
            WriteExecutable(
                Path.Combine(fakeBin, "dotnet"),
                """
                #!/usr/bin/env bash
                set -euo pipefail
                printf '%s\n' "$*" >> "${FAKE_DOTNET_LOG:?}"

                if [[ "${1:-}" == build && "${FAKE_EMIT_WARNING:-0}" == 1 ]]; then
                  printf '%s\n' 'warning: simulated build warning'
                fi

                case "${1:-}" in
                  msbuild)
                    property=''

                    for argument in "$@"; do
                      case "$argument" in
                        -getProperty:*)
                          property="${argument#-getProperty:}"
                          ;;
                      esac
                    done

                    if [[ "$property" == 'PublishSingleFile' ]]; then
                      printf '%s\n' 'false'
                    else
                      printf '%s\n' 'true'
                    fi

                    exit 0
                    ;;
                  publish)
                    output=''
                    rid=''

                    while (($#)); do
                      case "$1" in
                        -o)
                          output="${2:?}"
                          shift 2
                          ;;
                        -r)
                          rid="${2:?}"
                          shift 2
                          ;;
                        *)
                          shift
                          ;;
                      esac
                    done

                    mkdir -p "$output"
                    executable="$output/RetroDownfall.Arcanum.Cli"

                    if [[ "$rid" == win-* ]]; then
                      executable="$executable.exe"
                    fi

                    printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$executable"
                    chmod +x "$executable"
                    if [[ "${FAKE_SHIPPING_SUCCESS:-0}" == 1 ]]; then
                      chmod 751 "$executable"
                      printf '%s\n' 'native sibling' > "$output/libfixture.dylib"
                      mkdir -p "$output/nested"
                      printf '%s\n' 'nested payload' > "$output/nested/payload"
                      printf '%s\n' 'hidden payload' > "$output/.closure"
                    fi

                    if [[ "${FAKE_EMIT_PUBLISH_WARNING:-0}" == 1 ]]; then
                      printf '%s\n' 'warning: simulated publish warning'
                    fi

                    exit "${FAKE_PUBLISH_EXIT:-0}"
                    ;;
                  test)
                    ;;
                  *)
                    exit 0
                    ;;
                esac

                printf 'published-executable=%s\n' "${ARCANUM_PUBLISHED_EXECUTABLE:-}" >> "${FAKE_DOTNET_LOG:?}"
                printf 'ollama-image=%s\n' "${ARCANUM_OLLAMA_QUALIFICATION_IMAGE:-}" >> "${FAKE_DOTNET_LOG:?}"

                printf 'perplexity-mode=%s\n' "${ARCANUM_OLLAMA_QUALIFICATION_PERPLEXITY:-}" >> "${FAKE_DOTNET_LOG:?}"
                printf 'perplexity-reference=%s\n' "${ARCANUM_OLLAMA_QUALIFICATION_PERPLEXITY_KEY_VARIABLE:-}" >> "${FAKE_DOTNET_LOG:?}"

                expected_published='FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumSessionSmokeTests.Published_executable_creates_initial_session_and_completes_provider_contract_exchange'
                expected_ollama='FullyQualifiedName=RetroDownfall.Arcanum.Tests.Packaging.PublishedArcanumOllamaQualificationTests.Published_native_aot_preserves_corrected_file_context_and_runs_vision_across_restart'
                selected=''

                while (($#)); do
                  if [[ "$1" == '--filter' ]]; then
                    selected="${2:-}"
                    break
                  fi

                  shift
                done

                case "${FAKE_DOTNET_RECEIPT_KIND:-}" in
                  published)
                    if [[ "$selected" == "$expected_published" ]]; then
                      printf '%s\n' 'published-session-smoke:v1' > "${ARCANUM_PUBLISHED_SMOKE_RECEIPT:?}"

                      if [[ "${FAKE_APPEND_RECEIPT_BYTES_WITHOUT_NEWLINE:-0}" == 1 ]]; then
                        printf '%s' 'trailing-bytes' >> "${ARCANUM_PUBLISHED_SMOKE_RECEIPT:?}"
                      fi
                    fi
                    ;;
                  published-crlf)
                    if [[ "$selected" == "$expected_published" ]]; then
                      printf '%s\r\n' 'published-session-smoke:v1' > "${ARCANUM_PUBLISHED_SMOKE_RECEIPT:?}"

                      if [[ "${FAKE_APPEND_RECEIPT_BYTES_WITHOUT_NEWLINE:-0}" == 1 ]]; then
                        printf '%s' 'trailing-bytes' >> "${ARCANUM_PUBLISHED_SMOKE_RECEIPT:?}"
                      fi
                    fi
                    ;;
                  ollama-perplexity | ollama-perplexity-crlf)
                    if [[ "$selected" == "$expected_ollama" ]]; then
                      if [[ "${FAKE_DOTNET_RECEIPT_KIND}" == ollama-perplexity-crlf ]]; then
                        printf '%s\r\n' 'local-ollama-aot-perplexity-qualification:v1' > "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"
                      else
                        printf '%s\n' 'local-ollama-aot-perplexity-qualification:v1' > "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"
                      fi

                      if [[ "${FAKE_APPEND_RECEIPT_BYTES_WITHOUT_NEWLINE:-0}" == 1 ]]; then
                        printf '%s' 'trailing-bytes' >> "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"
                      fi
                    fi
                    ;;
                  ollama)
                    if [[ "$selected" == "$expected_ollama" ]]; then
                      printf '%s\n' 'local-ollama-aot-qualification:v1' > "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"

                      if [[ "${FAKE_APPEND_RECEIPT_BYTES_WITHOUT_NEWLINE:-0}" == 1 ]]; then
                        printf '%s' 'trailing-bytes' >> "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"
                      fi
                    fi
                    ;;
                  ollama-crlf)
                    if [[ "$selected" == "$expected_ollama" ]]; then
                      printf '%s\r\n' 'local-ollama-aot-qualification:v1' > "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"

                      if [[ "${FAKE_APPEND_RECEIPT_BYTES_WITHOUT_NEWLINE:-0}" == 1 ]]; then
                        printf '%s' 'trailing-bytes' >> "${ARCANUM_OLLAMA_QUALIFICATION_RECEIPT:?}"
                      fi
                    fi
                    ;;
                esac

                exit "${FAKE_TEST_EXIT:-0}"
                """);
        }

        public string Root => _root;

        public string ProcessTemporaryDirectory => _processTemporaryDirectory;

        public string RepositoryRoot { get; }

        public string Executable { get; }

        public string Image { get; }

        public string DotnetLog { get; }

        public Dictionary<string, string?> EnvironmentOverrides { get; } = [];

        private string FakeBin { get; }

        private string? ReceiptKind { get; }

        public Task<ScriptResult> RunShippingAsync(params string[] arguments) =>
            RunAsync(
                Path.Combine(RepositoryRoot, "scripts", "verify-shipping-publish.sh"),
                ["--rid", "osx-arm64", .. arguments]);

        public void EnableShippingSuccess()
        {
            EnvironmentOverrides["FAKE_SHIPPING_SUCCESS"] = "1";
            WriteExecutable(Path.Combine(FakeBin, "file"),
                """
                #!/usr/bin/env bash
                case "${@: -1}" in
                  */RetroDownfall.Arcanum.Cli | *.dylib) printf '%s\n' 'Mach-O 64-bit arm64' ;;
                  *) printf '%s\n' 'ASCII text' ;;
                esac
                """);
            WriteExecutable(Path.Combine(FakeBin, "otool"),
                """
                #!/usr/bin/env bash
                printf '%s:\n' "${2:?}"
                if [[ "$1" == '-L' ]]; then
                  printf '%s\n' ' /usr/lib/libSystem.B.dylib (compatibility version 1.0.0)'
                fi
                """);
        }

        public void EmitPublishWarningOutput()
        {
            EmitWarningOutput();
            _emitWarning = false;
            EnvironmentOverrides["FAKE_EMIT_PUBLISH_WARNING"] = "1";
        }

        public void CaptureCompletedRetentionCopy()
        {
            WriteExecutable(Path.Combine(FakeBin, "cp"),
                """
                #!/usr/bin/env bash
                set -euo pipefail
                /bin/cp "$@"
                printf '%s\n' 'fixture completed whole copy'
                """);
        }

        public void FailRetentionCopy()
        {
            WriteExecutable(Path.Combine(FakeBin, "cp"),
                """
                #!/usr/bin/env bash
                /bin/cp "${2%/.}/RetroDownfall.Arcanum.Cli" "${3:?}/RetroDownfall.Arcanum.Cli"
                printf '%s\n' 'simulated partial copy failure' >&2
                exit 23
                """);
        }

        public void FailRetentionDirectoryCreation(bool race)
        {
            EnvironmentOverrides["FAKE_MKDIR_RACE"] = race ? "1" : "0";
            WriteExecutable(Path.Combine(FakeBin, "mkdir"),
                """
                #!/usr/bin/env bash
                if [[ "${1:-}" == */retained ]]; then
                  if [[ "${FAKE_MKDIR_RACE:-0}" == 1 ]]; then
                    /bin/mkdir "$1"
                    printf '%s' 'race owner' > "$1/sentinel"
                  fi
                  exit 1
                fi
                exec /bin/mkdir "$@"
                """);
        }

        public string UsePredictableShippingTemporaryDirectory()
        {
            string path = Path.Combine(_processTemporaryDirectory, "arcanum-shipping-verify.owned");
            Directory.CreateDirectory(path);
            EnvironmentOverrides["FAKE_SHIPPING_WORK"] = path;
            WriteExecutable(Path.Combine(FakeBin, "mktemp"),
                """
                #!/usr/bin/env bash
                printf '%s\n' "$FAKE_SHIPPING_WORK"
                """);

            return Path.Combine(path, "retained");
        }

        public string[] ReadRemovalAttempts() => File.ReadAllLines(Path.Combine(_root, "removal.log"));

        public void FailRecursiveRemovalFor(string pathFragment)
        {
            _recursiveRemovalFailureFragment = pathFragment;
            EnvironmentOverrides["FAKE_RM_LOG"] = Path.Combine(_root, "removal.log");
            WriteExecutable(
                Path.Combine(FakeBin, "rm"),
                """
                #!/usr/bin/env bash
                set -euo pipefail

                if [[ "${1:-}" == '-rf' && "${2:-}" == *"${FAKE_RM_FAIL_SUBSTRING:?}"* ]]; then
                  printf '%s\n' "$2" >> "${FAKE_RM_LOG:?}"
                  exit 1
                fi

                exec /bin/rm "$@"
                """);
        }

        public void EmitWarningOutput()
        {
            _emitWarning = true;

            string realRg = (global::System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, "rg"))
                .First(File.Exists);

            WriteExecutable(Path.Combine(FakeBin, "rg"),
                "#!/usr/bin/env bash\nexec '" + realRg.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "' \"$@\"");
        }

        public void FailSearches()
        {
            WriteExecutable(
                Path.Combine(FakeBin, "rg"),
                """
                #!/usr/bin/env bash
                printf '%s\n' 'simulated ripgrep failure' >&2
                exit 2
                """);
        }

        public void CaptureBackoffDelays()
        {
            _backoffLog = Path.Combine(_root, "backoff.log");
            WriteExecutable(
                Path.Combine(FakeBin, "sleep"),
                """
                #!/usr/bin/env bash
                printf '%s\n' "$*" >> "${FAKE_SLEEP_LOG:?}"
                """);
        }

        public string[] ReadBackoffDelays() =>
            File.ReadAllLines(_backoffLog ?? throw new InvalidOperationException("Backoff capture was not enabled."));

        public void FailFileSearches()
        {
            WriteExecutable(
                Path.Combine(FakeBin, "find"),
                """
                #!/usr/bin/env bash
                printf '%s\n' 'simulated find failure' >&2
                exit 2
                """);
        }

        public void FailRegularFileSearches()
        {
            WriteExecutable(
                Path.Combine(FakeBin, "find"),
                """
                #!/usr/bin/env bash
                set -euo pipefail

                if [[ " $* " == *' -type f '* ]]; then
                  printf '%s\n' 'simulated regular-file find failure' >&2
                  exit 2
                fi

                exec /usr/bin/find "$@"
                """);
        }

        public void DisableErrexitBeforeSuccessfulExit(string marker)
        {
            _successfulExitMarker = marker;
            string bashEnvironment = Path.Combine(_root, "bash-environment");

            File.WriteAllText(
                bashEnvironment,
                """
                trap 'if [[ "$BASH_COMMAND" == *"${FAKE_DISABLE_ERREXIT_MARKER:?}"* ]]; then set +e; fi' DEBUG
                """ + "\n");
        }

        public void AppendReceiptBytesWithoutANewline()
        {
            _appendReceiptBytesWithoutANewline = true;
        }

        public async Task<ScriptResult> RunAsync(
            string script,
            params string[] arguments)
        {
            ProcessStartInfo start = new()
            {
                FileName = OperatingSystem.IsWindows() ? "bash.exe" : "/bin/bash",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = _root,
            };

            start.Environment.Clear();

            foreach (string name in new[] { "SystemRoot", "ComSpec" })
            {
                string? value = global::System.Environment.GetEnvironmentVariable(name);

                if (!string.IsNullOrEmpty(value))
                {
                    start.Environment[name] = value;
                }
            }

            start.ArgumentList.Add(script);

            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["FAKE_DOTNET_LOG"] = DotnetLog;
            start.Environment["TMPDIR"] = _processTemporaryDirectory;
            start.Environment["TMP"] = _processTemporaryDirectory;
            start.Environment["TEMP"] = _processTemporaryDirectory;
            start.Environment["PATH"] = FakeBin
                + Path.PathSeparator
                + global::System.Environment.GetEnvironmentVariable("PATH");
            start.Environment.Remove("GITHUB_ACTIONS");

            if (ReceiptKind is not null)
            {
                start.Environment["FAKE_DOTNET_RECEIPT_KIND"] = ReceiptKind;
            }

            if (_recursiveRemovalFailureFragment is not null)
            {
                start.Environment["FAKE_RM_FAIL_SUBSTRING"] = _recursiveRemovalFailureFragment;
            }

            if (_successfulExitMarker is not null)
            {
                start.Environment["BASH_ENV"] = Path.Combine(_root, "bash-environment");
                start.Environment["FAKE_DISABLE_ERREXIT_MARKER"] = _successfulExitMarker;
            }

            if (_appendReceiptBytesWithoutANewline)
            {
                start.Environment["FAKE_APPEND_RECEIPT_BYTES_WITHOUT_NEWLINE"] = "1";
            }

            if (_emitWarning)
            {
                start.Environment["FAKE_EMIT_WARNING"] = "1";
            }

            if (_backoffLog is not null)
            {
                start.Environment["FAKE_SLEEP_LOG"] = _backoffLog;
            }

            foreach ((string name, string? value) in EnvironmentOverrides)
            {
                start.Environment[name] = value;
            }

            using DiagnosticsProcess process = DiagnosticsProcess.Start(start)
                ?? throw new InvalidOperationException("Could not start bash behavior fixture.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            return new ScriptResult(
                process.ExitCode,
                await standardOutput,
                await standardError);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void WriteExecutable(
            string path,
            string contents)
        {
            File.WriteAllText(
                path,
                contents.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }
    }

    internal sealed record ScriptResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
