using System.Runtime.CompilerServices;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class ShippingPublishVerificationScriptTests
{
    [Fact]
    public void Gate_publishes_the_supported_shape_and_executes_the_published_product()
    {
        string root = FindRepositoryRoot();
        string scriptPath = Path.Combine(root, "scripts", "verify-shipping-publish.sh");
        string apphostGatePath = Path.Combine(root, "scripts", "verify-published-apphost.sh");

        Assert.True(File.Exists(scriptPath), $"Missing shipping verification gate: {scriptPath}");
        Assert.True(File.Exists(apphostGatePath), $"Missing published-apphost gate: {apphostGatePath}");

        string script = File.ReadAllText(scriptPath);
        string apphostGate = File.ReadAllText(apphostGatePath);

        Assert.Contains("PublishAot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishReadyToRun", script, StringComparison.Ordinal);
        Assert.Contains("rg --no-config", script, StringComparison.Ordinal);
        Assert.Contains("verify-published-apphost.sh", script, StringComparison.Ordinal);
        Assert.Contains("otool -L", script, StringComparison.Ordinal);
        Assert.Contains("otool -D", script, StringComparison.Ordinal);
        Assert.Contains("otool -l", script, StringComparison.Ordinal);
        Assert.Contains("LC_RPATH", script, StringComparison.Ordinal);
        Assert.Contains("find \"$PUBLISH_DIR\" -type f -print0", script, StringComparison.Ordinal);
        Assert.Contains("non-portable macOS dependency", script, StringComparison.Ordinal);
        Assert.Contains("/opt/homebrew/", script, StringComparison.Ordinal);
        Assert.Contains("/usr/local/", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/usr/lib/* | /System/Library/* | @rpath/*",
            script,
            StringComparison.Ordinal);
        Assert.Contains("--artifacts-path", apphostGate, StringComparison.Ordinal);
        Assert.Contains("rg --no-config", apphostGate, StringComparison.Ordinal);
        Assert.Contains("ARCANUM_PUBLISHED_EXECUTABLE", apphostGate, StringComparison.Ordinal);
        Assert.Contains("PublishedArcanumSessionSmokeTests", apphostGate, StringComparison.Ordinal);
        Assert.Contains("cygpath -w", apphostGate, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", apphostGate, StringComparison.Ordinal);
        Assert.Contains("for attempt in 1 2 3", script, StringComparison.Ordinal);
        Assert.Contains("local original_status=$?", script, StringComparison.Ordinal);
        Assert.Contains("local final_status=\"$original_status\"", script, StringComparison.Ordinal);
        Assert.Contains("trap - EXIT", script, StringComparison.Ordinal);
        Assert.Contains("exit \"$final_status\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "could not remove shipping verification temporary directory after three attempts",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Continuous_integration_gate_is_a_local_stub_contract_not_real_model_inference()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "verify-published-apphost.sh"));
        string smoke = File.ReadAllText(
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedArcanumSessionSmokeTests.cs"));
        string workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "ci.yml"));
        string windowsRelease = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "build-windows.yml"));

        Assert.Contains("FakeOpenAiProvider", smoke, StringComparison.Ordinal);
        Assert.Contains("deterministic first-session provider-contract test", script, StringComparison.Ordinal);
        Assert.Contains("provider-contract smoke", workflow, StringComparison.Ordinal);
        Assert.Contains("provider-contract smoke", windowsRelease, StringComparison.Ordinal);
        Assert.DoesNotContain("OLLAMA_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("11434", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Published_smoke_proves_the_exact_durable_first_turn_and_reports_sanitized_cleanup_failures()
    {
        string root = FindRepositoryRoot();
        string smoke = File.ReadAllText(
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedArcanumSessionSmokeTests.cs"));
        string isolation = File.ReadAllText(
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedTestProcessIsolation.cs"));

        Assert.Contains("Assert.Single(summaries)", smoke, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(2, entries.Length)", smoke, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(ExpectedPrompt, userEntry", smoke, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(ExpectedReply, assistantEntry", smoke, StringComparison.Ordinal);
        Assert.Contains("RunPublishedCliAsync", smoke, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(0, cli.ExitCode)", smoke, StringComparison.Ordinal);
        Assert.Contains("ExpectedReply + global::System.Environment.NewLine", smoke, StringComparison.Ordinal);
        Assert.Contains("\"Mage is generating response...\" + global::System.Environment.NewLine", smoke, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(ExpectedPrompt, providerRequest.Prompt)", smoke, StringComparison.Ordinal);
        Assert.Contains("Assert.True(providerRequest.Streaming)", smoke, StringComparison.Ordinal);
        Assert.Contains("metadataData.GetProperty(\"grimoireDirectory\")", smoke, StringComparison.Ordinal);
        Assert.Contains("metadataData.GetProperty(\"configPath\")", smoke, StringComparison.Ordinal);
        Assert.Contains("metadataData.GetProperty(\"port\")", smoke, StringComparison.Ordinal);
        Assert.Contains("metadataData.GetProperty(\"listenAny\")", smoke, StringComparison.Ordinal);
        Assert.Contains("text/event-stream", smoke, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", smoke, StringComparison.Ordinal);
        Assert.Contains("\"run\",", smoke, StringComparison.Ordinal);
        Assert.Contains("\"--new\",", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("client.PostAsync(\n                \"api/intelligence/ping\"", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanup.StandardOutput.Trim()", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("cleanup.StandardError.Trim()", smoke, StringComparison.Ordinal);
        Assert.Contains("LocalOllamaQualificationGuards.CreateSanitizedFailure", smoke, StringComparison.Ordinal);
        Assert.Contains("PublishedTestProcessIsolation.Create", smoke, StringComparison.Ordinal);
        Assert.Contains("isolation.Delete()", smoke, StringComparison.Ordinal);
        Assert.Contains(
            "LocalOllamaQualificationGuards.IsNativeBinary(executable)",
            smoke,
            StringComparison.Ordinal);
        Assert.Contains("DeleteDirectoryWithRetries", isolation, StringComparison.Ordinal);
        Assert.Contains("ApplyTemporaryEnvironment", isolation, StringComparison.Ordinal);
        Assert.Contains("TestRootDirectoryName", isolation, StringComparison.Ordinal);
        Assert.Contains("new AggregateException", smoke, StringComparison.Ordinal);
        Assert.Contains("DOTNET_CLI_HOME", smoke, StringComparison.Ordinal);
        Assert.Contains("XDG_CONFIG_HOME", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(testHome, \"tmp\")", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (TimeoutException)", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void Published_smoke_gate_requires_a_success_receipt_from_the_selected_test()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "verify-published-apphost.sh"));
        string smoke = File.ReadAllText(
            Path.Combine(
                root,
                "tests",
                "RetroDownfall.Arcanum.Tests",
                "Packaging",
                "PublishedArcanumSessionSmokeTests.cs"));

        Assert.Contains("ARCANUM_PUBLISHED_SMOKE_RECEIPT", script, StringComparison.Ordinal);
        Assert.Contains("ARCANUM_PUBLISHED_SMOKE_RECEIPT", smoke, StringComparison.Ordinal);
        Assert.Contains("published-session-smoke:v1", script, StringComparison.Ordinal);
        Assert.Contains("published-session-smoke:v1", smoke, StringComparison.Ordinal);
        Assert.Contains(
            "SuccessReceipt + global::System.Environment.NewLine",
            smoke,
            StringComparison.Ordinal);
        Assert.Contains("require_exact_receipt", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipping_rid_runs_an_exact_published_executable_gate_on_its_native_runner()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "ci.yml"));
        string windowsRelease = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "build-windows.yml"));

        Assert.Contains(
            "./scripts/verify-shipping-publish.sh --rid osx-arm64",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "./scripts/verify-shipping-publish.sh --rid win-x64",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "./scripts/verify-shipping-publish.sh --rid win-arm64",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "verify-published-apphost.sh --executable $executable",
            windowsRelease,
            StringComparison.Ordinal);
        Assert.DoesNotContain("verify-shipping-publish.sh", windowsRelease, StringComparison.Ordinal);
    }

    [Fact]
    public void Platform_packagers_require_native_aot_and_reject_managed_fallbacks()
    {
        string root = FindRepositoryRoot();
        string macOs = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "build-arcanum.sh"));
        string windows = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "windows", "package-windows.ps1"));
        string linux = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "linux", "package-linux.sh"));
        string macOsEntitlements = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "entitlements.cli.plist"));

        Assert.Contains("Publishing Arcanum Native AOT", macOs, StringComparison.Ordinal);
        Assert.Contains("managed assemblies", macOs, StringComparison.Ordinal);
        Assert.Contains("rg --no-config", macOs, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreCLR fallback publish", macOs, StringComparison.Ordinal);
        Assert.DoesNotContain("hardened runtime + JIT entitlements", macOs, StringComparison.Ordinal);
        Assert.DoesNotContain("com.apple.security.cs.allow-jit", macOsEntitlements, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "com.apple.security.cs.allow-unsigned-executable-memory",
            macOsEntitlements,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "com.apple.security.cs.disable-library-validation",
            macOsEntitlements,
            StringComparison.Ordinal);

        Assert.Contains("Publishing Arcanum Native AOT", windows, StringComparison.Ordinal);
        Assert.Contains("hostfxr.dll", windows, StringComparison.Ordinal);
        Assert.Contains("publishWarnings", windows, StringComparison.Ordinal);
        Assert.Contains("Assert-NativeAotPublish", windows, StringComparison.Ordinal);

        Assert.Contains("Publishing Arcanum Native AOT", linux, StringComparison.Ordinal);
        Assert.Contains("rg --no-config", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishReadyToRun", linux, StringComparison.Ordinal);
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
}
