using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Guards the release/packaging pipeline against the failure modes that only show up on a
/// signing runner: Actions script injection, credentials on a command line, and archives whose
/// signature covers less code than the operator believes.
/// </summary>
public sealed class ReleasePipelineTests
{
    /// <summary>
    /// Expression contexts an outside party can influence. Expanded by the Actions templating
    /// engine into the generated script's source text, so they may never appear inside a
    /// <c>run:</c> block — the value has to arrive through <c>env:</c> instead.
    /// </summary>
    private static readonly string[] UntrustedExpressionContexts =
    [
        "inputs.",

        "github.event.",

        "github.head_ref",
    ];

    [Fact]
    public void Workflow_run_blocks_never_interpolate_untrusted_expressions()
    {
        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles())
        {
            foreach ((int number, string text) in ShellScriptLines(File.ReadAllLines(workflow)))
            {
                foreach (string context in UntrustedExpressionContexts)
                {
                    if (ContainsExpression(text, context))
                    {
                        offenders.Add($"{Path.GetFileName(workflow)}:{number}: {text.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Untrusted workflow expressions are expanded into shell source before any validation "
            + "runs (Actions script injection). Pass them through the step's env: mapping and read "
            + "the environment variable instead:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// Credentials that arrive from a repository secret. A process command line is world-readable
    /// on the build host (CWE-214), and log masking does not reach the process table, so these
    /// may never be handed to a tool as an argv element — feed them on stdin or via env instead.
    /// <c>KEYCHAIN_PASSWORD</c> is deliberately absent: it is derived from the public run id and
    /// guards a keychain that is created and deleted inside a single job.
    /// </summary>
    private static readonly string[] SecretVariables =
    [
        "APPLE_APP_SPECIFIC_PASSWORD",

        "APPLE_CERTIFICATE_PASSWORD",

        "WINDOWS_CERT_PASSWORD",
    ];

    [Fact]
    public void Packaging_never_passes_a_secret_as_a_command_line_argument()
    {
        List<string> offenders = [];

        foreach ((string file, int number, string text) in PackagingShellLines())
        {
            foreach (string secret in SecretVariables)
            {
                Regex option = new(
                    "(^|\\s)-{1,2}[A-Za-z][A-Za-z0-9-]*[=\\s]+\"?\\$(env:|\\{)?"
                    + Regex.Escape(secret)
                    + "\\b",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5));

                if (option.IsMatch(text))
                {
                    offenders.Add($"{file}:{number}: {text.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A repository secret is passed to a tool on its command line, where the whole process "
            + "table can read it. Feed it on stdin (both notarytool and security prompt for the "
            + "value when the option is omitted) or hand it over through the environment:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// Packaging flags that produce an artifact no one outside the build host can trust:
    /// <c>--skip-sign</c> leaves it unsigned, and <c>--local-sign</c> signs it with whatever
    /// certificate the operator has in Keychain Access and deliberately skips notarization.
    /// Both are local-verification tools. Either one in a release workflow uploads a draft-release
    /// asset that installs cleanly on the machine that built it and is refused by Gatekeeper
    /// everywhere else — a failure the release job itself cannot observe.
    /// </summary>
    private static readonly string[] NonReleaseSigningFlags =
    [
        "--skip-sign",

        "--local-sign",
    ];

    [Fact]
    public void Release_workflows_never_pass_a_non_release_signing_flag()
    {
        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles())
        {
            foreach ((int number, string text) in ShellScriptLines(File.ReadAllLines(workflow)))
            {
                foreach (string flag in NonReleaseSigningFlags)
                {
                    if (text.Contains(flag, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(workflow)}:{number}: {text.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A workflow passes a packaging flag that suppresses release signing or notarization. "
            + "The artifact it produces is trusted only on the build host, and nothing later in "
            + "the release job can tell the difference:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// Local signing exists so the privilege-free Native AOT hardened-runtime signature can be
    /// exercised on a development certificate, which Apple will not notarize. If a notarization or
    /// stapling step were reachable from that path it would fail at the Apple end, late, and only on
    /// the one machine that runs it — so every call site stays behind an explicit
    /// <c>LOCAL_SIGN</c> guard.
    /// </summary>
    [Fact]
    public void MacOs_local_signing_never_reaches_notarization()
    {
        string[] notarizationCalls =
        [
            "notarize_submit",

            "staple_item",

            "spctl --assess",

            "verify_notarized_cli",
        ];

        foreach (string script in MacOsPackagingBuildScripts())
        {
            string text = File.ReadAllText(script);

            string name = Path.GetFileName(script);

            Assert.True(
                text.Contains("require_local_signing_identity", StringComparison.Ordinal),
                $"{name} does not offer local signing; it must resolve the identity through "
                + "require_local_signing_identity so the keychain is the only source.");

            foreach (string call in notarizationCalls)
            {
                foreach (string block in GuardedBlocksContaining(text, call))
                {
                    Assert.True(
                        block.Contains("LOCAL_SIGN", StringComparison.Ordinal),
                        $"{name} reaches '{call}' from a branch that does not exclude "
                        + "--local-sign. Apple rejects a development certificate, so this fails "
                        + "at submission time rather than at the flag.");
                }
            }
        }
    }

    /// <summary>
    /// The <c>if</c>/<c>elif</c> conditions governing every line that contains
    /// <paramref name="needle"/>, taken as the condition text of each enclosing shell branch. A
    /// script with no occurrence yields nothing, which is a pass — the point is that a reachable
    /// call is guarded, not that one exists.
    /// </summary>
    private static IReadOnlyList<string> GuardedBlocksContaining(string script, string needle)
    {
        List<string> conditions = [];

        string[] lines = script.Split('\n');

        List<string> open = [];

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("if ", StringComparison.Ordinal))
            {
                open.Add(trimmed);
            }
            else if (trimmed.StartsWith("elif ", StringComparison.Ordinal) && open.Count > 0)
            {
                open[^1] = open[^1] + " " + trimmed;
            }
            else if (trimmed == "fi" && open.Count > 0)
            {
                open.RemoveAt(open.Count - 1);
            }

            if (!line.Contains(needle, StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            conditions.Add(open.Count == 0 ? string.Empty : string.Join(" ", open));
        }

        return conditions;
    }

    private static IReadOnlyList<string> MacOsPackagingBuildScripts()
    {
        string directory = Path.Combine(
            RepositoryRoot(),
            "scripts",
            "packaging",
            "macos");

        Assert.True(Directory.Exists(directory), $"Missing macOS packaging directory: {directory}");

        string[] scripts =
        [
            Path.Combine(directory, "build-arcanum.sh"),

            Path.Combine(directory, "build-app-dmg.sh"),
        ];

        foreach (string script in scripts)
        {
            Assert.True(File.Exists(script), $"Missing macOS packaging script: {script}");
        }

        return scripts;
    }

    [Fact]
    public void Windows_packaging_signs_every_portable_executable_it_ships()
    {
        string script = File.ReadAllText(WindowsPackagingScript());

        // Every product that gets staged and archived must be signed as a whole tree. Signing an
        // individually named file leaves the first-party managed assemblies and the
        // SQLCipher/oniguruma native libraries unsigned inside a zip that looks signed, so
        // WDAC/AppLocker publisher rules cannot cover the code that actually executes.
        string[] publishers = ["Publish-Cli", "Publish-Gui"];

        foreach (string publisher in publishers)
        {
            string body = Assert.Single(BracedBlocksAfter(script, $"function {publisher}"));

            Assert.True(
                body.Contains("Invoke-StageAuthenticodeSign", StringComparison.Ordinal),
                $"{publisher} archives a stage directory without a full-tree Authenticode pass "
                + "over it.");

            Assert.False(
                body.Contains("Invoke-AuthenticodeSign ", StringComparison.Ordinal),
                $"{publisher} signs an individually named file; sign the staged tree instead so "
                + "every shipped .exe and .dll is covered.");
        }

        string helper = Assert.Single(BracedBlocksAfter(script, "function Invoke-StageAuthenticodeSign"));

        Assert.Contains("-Recurse", helper, StringComparison.Ordinal);

        Assert.Contains(".exe", helper, StringComparison.Ordinal);

        Assert.Contains(".dll", helper, StringComparison.Ordinal);

        Assert.Contains("signtool verify", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_gui_publish_captures_output_and_rejects_warnings()
    {
        string root = RepositoryRoot();

        string windows = File.ReadAllText(WindowsPackagingScript());

        string windowsGui = Assert.Single(BracedBlocksAfter(windows, "function Publish-Gui"));

        Assert.Contains("$publishOutput = @(& dotnet publish", windowsGui, StringComparison.Ordinal);

        Assert.Contains("2>&1)", windowsGui, StringComparison.Ordinal);

        Assert.Contains("$publishWarnings", windowsGui, StringComparison.Ordinal);

        Assert.Contains("Where-Object", windowsGui, StringComparison.Ordinal);

        Assert.Contains("warning output", windowsGui, StringComparison.Ordinal);

        string macOs = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "build-app-dmg.sh"));

        int macOsPublish = macOs.IndexOf("dotnet \"${PUBLISH_ARGS[@]}\"", StringComparison.Ordinal);

        int macOsCapture = macOs.IndexOf("2>&1 | tee \"$PUBLISH_LOG\"", macOsPublish, StringComparison.Ordinal);

        int macOsReject = macOs.IndexOf("GUI publish emitted warning output", macOsCapture, StringComparison.Ordinal);

        int macOsScanFailure = macOs.IndexOf("could not scan GUI publish output", macOsCapture, StringComparison.Ordinal);

        Assert.True(
            macOsPublish >= 0
            && macOsCapture > macOsPublish
            && macOsReject > macOsCapture
            && macOsScanFailure > macOsCapture,
            "The macOS GUI publish must capture the real dotnet output and reject warnings before packaging.");

        string linux = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "linux", "package-linux.sh"));

        string linuxGui = Assert.Single(BracedBlocksAfter(linux, "publish_gui()"));

        int linuxPublish = linuxGui.IndexOf("dotnet publish", StringComparison.Ordinal);

        int linuxCapture = linuxGui.IndexOf("2>&1 | tee \"$publish_log\"", linuxPublish, StringComparison.Ordinal);

        int linuxReject = linuxGui.IndexOf("GUI publish emitted warning output", linuxCapture, StringComparison.Ordinal);

        int linuxScanFailure = linuxGui.IndexOf("could not scan GUI publish output", linuxCapture, StringComparison.Ordinal);

        Assert.True(
            linuxPublish >= 0
            && linuxCapture > linuxPublish
            && linuxReject > linuxCapture
            && linuxScanFailure > linuxCapture,
            "The dormant Linux GUI publish must remain warning-free when Linux shipping is restored.");
    }

    [Fact]
    public void Every_ripgrep_publish_warning_scan_fails_when_the_scan_itself_errors()
    {
        string root = RepositoryRoot();

        string macOsCli = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "build-arcanum.sh"));

        Assert.Contains("could not scan Arcanum publish output", macOsCli, StringComparison.Ordinal);

        Assert.Contains("could not inspect Native AOT package", macOsCli, StringComparison.Ordinal);

        Assert.DoesNotContain("-print -quit | rg --no-config -q", macOsCli, StringComparison.Ordinal);

        string linux = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "linux", "package-linux.sh"));

        string linuxCli = Assert.Single(BracedBlocksAfter(linux, "publish_cli()"));

        Assert.Contains("could not scan Arcanum publish output", linuxCli, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_credentials_and_packaging_workspaces_are_cleaned_fail_closed()
    {
        string root = RepositoryRoot();

        string windows = File.ReadAllText(WindowsPackagingScript());

        Assert.Contains("ArcanumPackaging-", windows, StringComparison.Ordinal);

        Assert.Contains("$script:SigningStoreName", windows, StringComparison.Ordinal);

        Assert.Contains("$script:SigningStorePath", windows, StringComparison.Ordinal);

        Assert.Contains("function Remove-OwnedSigningStore", windows, StringComparison.Ordinal);

        Assert.Contains("/s $script:SigningStoreName", windows, StringComparison.Ordinal);

        Assert.DoesNotContain("Cert:\\CurrentUser\\My", windows, StringComparison.Ordinal);

        Assert.Contains("function Remove-PackagingWorkDirectory", windows, StringComparison.Ordinal);

        Assert.Contains("for ($attempt = 1; $attempt -le 3; $attempt++)", windows, StringComparison.Ordinal);

        Assert.Contains("Start-Sleep -Milliseconds (50 * $attempt)", windows, StringComparison.Ordinal);

        Assert.Contains("-DeleteKey", windows, StringComparison.Ordinal);

        Assert.True(
            Regex.Matches(
                windows,
                "SetEnvironmentVariable\\(\\s*\\\"WINDOWS_CERT_PASSWORD\\\",\\s*\\$null",
                RegexOptions.CultureInvariant).Count >= 2,
            "The signing password must be cleared immediately after capture and again in finally.");

        Assert.Contains("[System.AggregateException]", windows, StringComparison.Ordinal);

        string windowsFinally = windows[windows.LastIndexOf("finally {", StringComparison.Ordinal)..];

        Assert.DoesNotContain("SilentlyContinue", windowsFinally, StringComparison.Ordinal);

        string common = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "common.sh"));

        Assert.Contains("notary_keychain_is_absent", common, StringComparison.Ordinal);

        Assert.Contains("for attempt in 1 2 3", common, StringComparison.Ordinal);

        Assert.Contains("packaging_cleanup_exit", common, StringComparison.Ordinal);

        Assert.DoesNotContain("delete-keychain \"$NOTARY_KEYCHAIN\" >/dev/null 2>&1 || true", common, StringComparison.Ordinal);

        foreach (string packagerName in new[] { "build-arcanum.sh", "build-app-dmg.sh" })
        {
            string packager = File.ReadAllText(
                Path.Combine(root, "scripts", "packaging", "macos", packagerName));

            Assert.Contains("local original_status=$?", packager, StringComparison.Ordinal);

            Assert.Contains("packaging_cleanup_exit \"$original_status\" \"$WORK\"", packager, StringComparison.Ordinal);
        }

        string release = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "release-macos-arm64.yml"));

        Assert.DoesNotContain("security delete-keychain \"$KEYCHAIN_PATH\" || true", release, StringComparison.Ordinal);
    }

    [Fact]
    public void MacOs_packagers_apply_artifact_appropriate_post_notarization_validation()
    {
        string root = RepositoryRoot();

        string cli = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "build-arcanum.sh"));

        Assert.Contains("notarize_submit \"$ZIP_PATH\"", cli, StringComparison.Ordinal);

        Assert.Contains(
            "verify_notarized_cli \"$BINARY\"",
            cli,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "spctl --assess --type execute",
            cli,
            StringComparison.Ordinal);

        Assert.Contains("\"$BINARY\" --version", cli, StringComparison.Ordinal);

        string gui = File.ReadAllText(
            Path.Combine(root, "scripts", "packaging", "macos", "build-app-dmg.sh"));

        Assert.Contains(
            "spctl --assess --type open --context context:primary-signature --verbose=4 \"$DMG_PATH\"",
            gui,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "spctl --assess --type open --context context:primary-signature --verbose=4 \"$DMG_PATH\" ||",
            gui,
            StringComparison.Ordinal);

        Assert.DoesNotContain("warning: spctl", gui, StringComparison.Ordinal);
    }

    /// <summary>
    /// A release dispatch must never replace the assets of a release that is already public, and two
    /// dispatches must not interleave their uploads. <c>gh release upload --clobber</c> succeeds on
    /// any existing release, so without a check an explicit version turns a notarized public
    /// download into a mutable one.
    /// </summary>
    [Fact]
    public void Release_refuses_to_clobber_a_published_release()
    {
        string root = RepositoryRoot();

        string release = File
            .ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int create = release.IndexOf("- name: Create or update the draft GitHub Release", StringComparison.Ordinal);

        Assert.True(create > 0, "release.yml lost its draft-release assembly step.");

        string beforeCreate = release[..create];

        Assert.Contains("isDraft", beforeCreate, StringComparison.Ordinal);

        int jobs = release.IndexOf("\njobs:\n", StringComparison.Ordinal);

        Assert.True(jobs > 0, "release.yml declares no jobs.");

        string topLevel = release[..jobs];

        int concurrency = topLevel.IndexOf("\nconcurrency:\n", StringComparison.Ordinal);

        Assert.True(
            concurrency >= 0,
            "release.yml needs a top-level concurrency group so two dispatches cannot interleave per-file uploads.");

        string concurrencyBlock = topLevel[concurrency..];

        Assert.Contains("cancel-in-progress: false", concurrencyBlock, StringComparison.Ordinal);

        Assert.DoesNotContain("cancel-in-progress: true", release, StringComparison.Ordinal);

        // Never on a job: this workflow is also workflow_call, and a called job's group would
        // serialize against the caller's.
        Assert.Equal(
            1,
            release.Split('\n').Count(static line => line.TrimStart().StartsWith("concurrency:", StringComparison.Ordinal)));

        string macOs = File
            .ReadAllText(Path.Combine(root, ".github", "workflows", "release-macos-arm64.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int macOsCreate = macOs.IndexOf("- name: Create or update draft GitHub Release", StringComparison.Ordinal);

        Assert.True(macOsCreate > 0, "release-macos-arm64.yml lost its draft-release step.");

        Assert.Contains("isDraft", macOs[..macOsCreate], StringComparison.Ordinal);
    }

    /// <summary>
    /// With <c>cancel-in-progress: false</c> GitHub runs one release at a time and keeps only one
    /// pending run per group: a third dispatch cancels the one that was waiting. The group therefore
    /// protects a run in progress and the draft it is assembling, and it does not queue every
    /// dispatch. The workflow's comment and both documents must say what it does.
    /// </summary>
    [Fact]
    public void The_release_concurrency_documentation_says_a_waiting_dispatch_is_superseded_not_queued()
    {
        string root = RepositoryRoot();

        string release = File
            .ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("Queue, never cancel", release, StringComparison.Ordinal);

        int concurrency = release.IndexOf("\nconcurrency:\n", StringComparison.Ordinal);

        Assert.True(concurrency > 0, "release.yml lost its top-level concurrency group.");

        string comment = release[..concurrency];

        Assert.Contains("supersede", comment, StringComparison.OrdinalIgnoreCase);

        foreach (string document in new[] { "Arcanum.DESIGN.md", "Arcanum.Engineering.md" })
        {
            string text = File
                .ReadAllText(Path.Combine(root, "docs", document))
                .Replace("\r\n", "\n", StringComparison.Ordinal);

            int group = text.IndexOf("release-<version or auto>", StringComparison.Ordinal);

            Assert.True(group >= 0, $"{document} no longer documents the release concurrency group.");

            string paragraph = text[group..Math.Min(text.Length, group + 700)];

            Assert.Contains("supersede", paragraph, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("queue instead", paragraph, StringComparison.Ordinal);

            Assert.DoesNotContain("queues rather than cancels", text[Math.Max(0, group - 400)..group], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A draft release holds a tag name and a target commit and writes neither into the repository
    /// until it is published. Created without <c>--target</c> the draft points at the default
    /// branch's head at that moment, so publishing it later tags whatever landed after the build
    /// instead of the commit whose binaries the release carries.
    /// </summary>
    [Fact]
    public void Every_gh_release_create_names_its_target_commit()
    {
        List<string> offenders = [];

        int creates = 0;

        foreach (string workflow in WorkflowFiles())
        {
            string folded = string.Join(
                '\n',
                ShellScriptLines(File.ReadAllLines(workflow)).Select(static line => line.Text))
                .Replace("\\\n", " ", StringComparison.Ordinal);

            foreach (string command in folded.Split('\n'))
            {
                if (!command.Contains("gh release create", StringComparison.Ordinal))
                {
                    continue;
                }

                creates++;

                if (!command.Contains("--target \"$GITHUB_SHA\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(workflow)}: {command.Trim()}");
                }
            }
        }

        Assert.True(creates >= 2, "Expected both release workflows to create a draft release.");

        Assert.True(
            offenders.Count == 0,
            "A gh release create does not pass --target \"$GITHUB_SHA\", so publishing the draft would "
            + "tag the default branch's head instead of the built commit:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    /// <summary>
    /// The release workflow's CI report card used to say CI is "dispatch-only" while ci.yml runs on
    /// every pull request. The operator reading "this is expected unless you ran it" would conclude a
    /// merged commit had never been tested, when the truth is subtler: CI ran on the pull request's
    /// head, and a squash or merge commit has no run of its own.
    /// </summary>
    [Fact]
    public void Release_does_not_call_ci_dispatch_only_while_ci_runs_on_pull_requests()
    {
        string root = RepositoryRoot();

        string ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Matches(@"(?m)^  pull_request:\s*$", ci);

        string release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        Assert.DoesNotContain("dispatch-only", release, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("pull requests", release, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>notarytool submit --wait</c> blocks for as long as Apple takes to answer. With no bound a
    /// stalled submission holds the signing keychain and the macOS runner until the job ceiling, and
    /// reads as a slow release rather than a failed one.
    /// </summary>
    [Fact]
    public void Notarization_submit_bounds_its_wait()
    {
        string common = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "scripts", "packaging", "macos", "common.sh"));

        int submit = common.IndexOf("xcrun notarytool submit", StringComparison.Ordinal);

        Assert.True(submit >= 0, "common.sh no longer submits to notarytool.");

        string call = common[submit..common.IndexOf('}', submit)];

        Assert.Contains("--wait", call, StringComparison.Ordinal);

        Assert.Contains("--timeout 30m", call, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hung test host otherwise sits until the job-level ceiling. The hang detector kills it with a
    /// named test, and <c>--blame-hang-dump-type none</c> keeps the kill from writing a multi-gigabyte
    /// process dump on a hosted runner.
    /// </summary>
    [Fact]
    public void Every_dotnet_test_invocation_detects_a_hung_test()
    {
        string root = RepositoryRoot();

        // The minimum number of invocations each file is known to hold. A rewrite of one of them
        // into a shape the scan no longer recognises would otherwise leave it unchecked, and the
        // test green, so the floor is what proves the scan still sees every lane.
        Dictionary<string, int> expectedMinimums = new(StringComparer.Ordinal)
        {
            ["ci.yml"] = 8,

            ["coverage.sh"] = 1,

            ["verify-published-apphost.sh"] = 1,

            ["verify-local-ollama-aot.sh"] = 1,
        };

        string[] files =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.yml"),
            .. Directory
                .EnumerateFiles(Path.Combine(root, "scripts"), "*.sh")
                .Where(static path => !path.EndsWith("_test.sh", StringComparison.Ordinal)),
        ];

        foreach (string file in files)
        {
            string[] invocations = [.. DotnetTestInvocations(File.ReadAllText(file))];

            string name = Path.GetFileName(file);

            if (expectedMinimums.TryGetValue(name, out int minimum))
            {
                Assert.True(
                    invocations.Length >= minimum,
                    $"{name} should hold at least {minimum} `dotnet test` invocations but the scan found {invocations.Length}.");

                expectedMinimums.Remove(name);
            }

            // 15 minutes everywhere except the local Ollama qualification, whose single test legitimately
            // spends longer than that in cold model turns and carries its own 30 minute bound.
            int allowedMinutes = name == "verify-local-ollama-aot.sh" ? 30 : 15;

            foreach (string invocation in invocations)
            {
                Match timeout = HangTimeout.Match(invocation);

                Assert.True(
                    timeout.Success
                    && int.Parse(timeout.Groups["minutes"].Value, System.Globalization.CultureInfo.InvariantCulture) <= allowedMinutes
                    && invocation.Contains("--blame-hang-dump-type none", StringComparison.Ordinal),
                    $"{name} runs a test project without a hang bound of at most {allowedMinutes}m (and no dump), so a hung test host is bounded only by the job ceiling: {invocation[..Math.Min(invocation.Length, 140)]}");
            }
        }

        Assert.True(
            expectedMinimums.Count == 0,
            "The scan never opened: " + string.Join(", ", expectedMinimums.Keys));
    }

    /// <summary>
    /// xunit reports a test that is still running past <c>longRunningTestSeconds</c> as a diagnostic
    /// message, and the runner prints diagnostic messages only when <c>diagnosticMessages</c> is on.
    /// Without it the setting would configure a notice nobody can read.
    /// </summary>
    [Fact]
    public void The_long_running_test_notice_is_visible_because_diagnostic_messages_are_enabled()
    {
        string path = Path.Combine(RepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests", "xunit.runner.json");

        using System.Text.Json.JsonDocument runner = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        Assert.True(
            runner.RootElement.TryGetProperty("longRunningTestSeconds", out System.Text.Json.JsonElement seconds)
            && seconds.GetInt32() > 0,
            "xunit.runner.json no longer sets longRunningTestSeconds, so a slow test is never named.");

        Assert.True(
            runner.RootElement.TryGetProperty("diagnosticMessages", out System.Text.Json.JsonElement diagnostics)
            && diagnostics.ValueKind == System.Text.Json.JsonValueKind.True,
            "xunit.runner.json sets longRunningTestSeconds without diagnosticMessages, so the notice is never printed.");
    }

    /// <summary>
    /// The text from each <c>dotnet test</c> of a test project to the end of its logical line, with
    /// shell (<c>\</c>) and PowerShell (<c>`</c>) line continuations folded first.
    /// </summary>
    private static IEnumerable<string> DotnetTestInvocations(string text)
    {
        string folded = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("`\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);

        foreach (Match invocation in DotnetTestInvocation.Matches(folded))
        {
            yield return invocation.Value;
        }
    }

    private static readonly Regex HangTimeout = new(
        @"--blame-hang-timeout (?<minutes>\d+)m(?=\s|$)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex DotnetTestInvocation = new(
        @"dotnet test (?:""\$TEST_PROJECT""|tests/)[^\n]*",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Bodies of every brace-delimited block that follows <paramref name="header"/>, brace-matched
    /// so a nested block does not terminate its parent.
    /// </summary>
    private static IReadOnlyList<string> BracedBlocksAfter(string script, string header)
    {
        List<string> blocks = [];

        int search = 0;

        while (true)
        {
            int start = script.IndexOf(header, search, StringComparison.Ordinal);

            if (start < 0)
            {
                return blocks;
            }

            search = start + header.Length;

            int open = script.IndexOf('{', search);

            if (open < 0)
            {
                return blocks;
            }

            int depth = 0;

            for (int i = open; i < script.Length; i++)
            {
                if (script[i] == '{')
                {
                    depth++;
                }
                else if (script[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                    {
                        blocks.Add(script[(open + 1)..i]);

                        search = i;

                        break;
                    }
                }
            }
        }
    }

    private static string WindowsPackagingScript()
    {
        string path = Path.Combine(
            RepositoryRoot(),
            "scripts",
            "packaging",
            "windows",
            "package-windows.ps1");

        Assert.True(File.Exists(path), $"Missing Windows packaging script: {path}");

        return path;
    }

    /// <summary>
    /// Every line of shell the packaging pipeline executes: the packaging scripts themselves plus
    /// the inline scripts the workflows run.
    /// </summary>
    private static IEnumerable<(string File, int Number, string Text)> PackagingShellLines()
    {
        string root = RepositoryRoot();

        string scripts = Path.Combine(root, "scripts");

        foreach (string file in Directory.EnumerateFiles(scripts, "*.*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is not (".sh" or ".ps1"))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                yield return (Path.GetRelativePath(root, file), i + 1, lines[i]);
            }
        }

        foreach (string workflow in WorkflowFiles())
        {
            foreach ((int number, string text) in ShellScriptLines(File.ReadAllLines(workflow)))
            {
                yield return (Path.GetRelativePath(root, workflow), number, text);
            }
        }
    }

    private static bool ContainsExpression(string line, string context)
    {
        int index = 0;

        while (true)
        {
            index = line.IndexOf("${{", index, StringComparison.Ordinal);

            if (index < 0)
            {
                return false;
            }

            index += 3;

            int end = line.IndexOf("}}", index, StringComparison.Ordinal);

            if (end < 0)
            {
                return false;
            }

            if (line[index..end].TrimStart().StartsWith(context, StringComparison.Ordinal))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Yields every line that ends up inside a step's shell script, whether it was written as a
    /// block scalar (<c>run: |</c>) or as an inline value (<c>run: echo hi</c>).
    /// </summary>
    private static IEnumerable<(int Number, string Text)> ShellScriptLines(string[] lines)
    {
        int blockIndent = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (blockIndent >= 0)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                if (IndentOf(line) > blockIndent)
                {
                    yield return (i + 1, line);

                    continue;
                }

                blockIndent = -1;
            }

            int indent = IndentOf(line);

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                indent += 2;

                trimmed = trimmed[2..].TrimStart();
            }

            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            string value = trimmed[4..].Trim();

            if (value.Length == 0 || value[0] is '|' or '>')
            {
                blockIndent = indent;

                continue;
            }

            yield return (i + 1, line);
        }
    }

    private static int IndentOf(string line)
    {
        int indent = 0;

        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        return indent;
    }

    private static IReadOnlyList<string> WorkflowFiles()
    {
        string directory = Path.Combine(RepositoryRoot(), ".github", "workflows");

        Assert.True(Directory.Exists(directory), $"Missing workflow directory: {directory}");

        string[] files = Directory.GetFiles(directory, "*.yml");

        Assert.NotEmpty(files);

        return files;
    }

    private static string RepositoryRoot(
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

    /// <summary>
    /// A <c>uses:</c> reference to a tag is a mutable ref: whoever controls the upstream repository
    /// decides what code it resolves to on the next run (the tj-actions/changed-files class of
    /// attack). In a workflow that decodes the Developer ID Application private key into a keychain
    /// and holds <c>contents: write</c>, that is a signing key an upstream compromise can walk off
    /// with — and a key that signs anything Gatekeeper then trusts on every operator's machine. A
    /// commit SHA is immutable, so the reference means one specific tree forever.
    /// </summary>
    /// <remarks>
    /// Every workflow, not only the ones that read <c>secrets.</c>. A holder of no secret still
    /// checks out this repository's source and runs a third-party binary over it on a runner that
    /// reaches the network, so an upstream compromise gets arbitrary code execution against the
    /// tree the release is cut from — and the workflow one commit later may be the one that gains
    /// a secret. The single-maintainer third-party action is the reference the tj-actions threat
    /// model applies to literally, and it lives in the workflow that builds the native runtime.
    /// </remarks>
    [Fact]
    public void Workflows_pin_every_third_party_action_to_a_commit()
    {
        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles())
        {
            string[] lines = File.ReadAllLines(workflow);

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();

                if (!trimmed.StartsWith("uses:", StringComparison.Ordinal))
                {
                    continue;
                }

                string reference = trimmed["uses:".Length..].Trim();

                // A local action is this repository's own reviewed code, not an upstream ref.
                if (reference.StartsWith("./", StringComparison.Ordinal))
                {
                    continue;
                }

                int at = reference.LastIndexOf('@');

                if (at >= 0 && IsCommitSha(reference[(at + 1)..]))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(workflow)}:{i + 1}: {reference}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A workflow runs an action from a mutable tag, so upstream decides what code executes "
            + "against this repository's source on the next run. Pin the full 40-character commit "
            + "SHA and keep the version in a trailing comment:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));
    }

    private static bool IsCommitSha(string reference)
    {
        string candidate = reference.Split('#')[0].Trim();

        return candidate.Length == 40
            && candidate.All(static character =>
                character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }
}
