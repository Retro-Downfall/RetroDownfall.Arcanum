using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class ContinuousIntegrationWorkflowTests
{

    /// <summary>
    /// One job of a workflow under <c>.github/workflows</c>: the runner it asks for, whether a
    /// job-level <c>if:</c> gates it, and every line of its body.
    /// </summary>
    private sealed record WorkflowJob(string Id, string RunsOn, bool IsConditional, string Body);

    /// <summary>
    /// The hermetic SQLCipher targets raise ARCSQLC002 before <c>CopyFilesToOutputDirectory</c>
    /// whenever the RID being built has no checked-in asset — deliberately, with no probe and no
    /// fallback. A lane that sets .NET up on such a runner therefore dies at the first
    /// <c>dotnet build</c> and never reaches a single test, so it has to be gated on the manifest
    /// rather than presented as a check that merely happens to be red.
    /// </summary>
    /// <remarks>
    /// Every workflow, not only <c>ci.yml</c>. A <c>workflow_dispatch:</c> release pipeline fails
    /// no pull request, which is exactly what makes it worse: the breakage is invisible until an
    /// operator reaches for it to cut a build, and then it presents as a packaging failure at the
    /// moment a release is wanted rather than as a missing prerequisite. Gating on the manifest
    /// states the prerequisite up front and restores the lane by itself once the asset lands.
    /// </remarks>
    [Fact]
    public void No_workflow_builds_on_a_runner_whose_native_sqlcipher_asset_is_missing()
    {

        string repositoryRoot = FindRepositoryRoot();

        List<string> offenders = [];

        foreach (string workflow in WorkflowFiles(repositoryRoot))
        {

            foreach (WorkflowJob job in JobsIn(workflow))
            {

                if (!job.Body.Contains("actions/setup-dotnet", StringComparison.Ordinal))
                {

                    continue;

                }

                string rid = RuntimeIdentifierFor(job.RunsOn);

                if (HasVerifiedNativeSqlCipherAsset(repositoryRoot, rid) || job.IsConditional)
                {

                    continue;

                }

                offenders.Add(
                    $"{Path.GetFileName(workflow)}: {job.Id} (runs-on: {job.RunsOn}, needs a "
                    + $"verified {rid} asset)");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A workflow job builds .NET on a runtime identifier whose hermetic SQLCipher asset is "
            + "not checked in and verified, so it fails with ARCSQLC002 before any test runs. Gate "
            + "the job on the manifest status so it returns automatically once the asset lands:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// A test project the solution carries but no lane executes is a suite whose failures nobody
    /// ever sees. Whole-project exclusions are invisible in a way per-test <c>Skip</c> is not: they
    /// leave no trace in any test report, so nothing distinguishes a quarantined suite from one that
    /// passes.
    /// </summary>
    [Fact]
    public void Ci_runs_every_test_project_the_solution_carries()
    {

        string repositoryRoot = FindRepositoryRoot();

        IReadOnlySet<string> executed = ProjectsCiRunsTestsFor(repositoryRoot);

        List<string> offenders = [];

        foreach (XElement project in
            XDocument.Load(Path.Combine(repositoryRoot, "RetroDownfall.Arcanum.slnx"))
                .Descendants("Project"))
        {

            string path = (project.Attribute("Path")?.Value ?? string.Empty).Replace('\\', '/');

            if (!path.StartsWith("tests/", StringComparison.Ordinal))
            {

                continue;

            }

            if (!executed.Contains(path))
            {

                offenders.Add(path);

            }

        }

        Assert.True(
            offenders.Count == 0,
            "A test project in RetroDownfall.Arcanum.slnx is never executed by "
            + ".github/workflows/ci.yml, so every assertion it makes is dark on every lane:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// Projects named as the target of a <c>dotnet test</c> invocation in the workflow. Line
    /// continuations are folded first because both the bash (<c>\</c>) and pwsh (<c>`</c>) steps put
    /// the project and its flags on separate physical lines.
    /// </summary>
    private static IReadOnlySet<string> ProjectsCiRunsTestsFor(string repositoryRoot)
    {

        string workflow = File
            .ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("`\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);

        HashSet<string> tested = new(StringComparer.Ordinal);

        foreach (string line in workflow.Split('\n'))
        {

            int start = line.IndexOf("dotnet test", StringComparison.Ordinal);

            if (start < 0)
            {

                continue;

            }

            foreach (string token in
                line[start..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {

                if (token.EndsWith(".csproj", StringComparison.Ordinal))
                {

                    _ = tested.Add(token.Replace('\\', '/'));

                }

            }

        }

        return tested;

    }

    /// <summary>
    /// A Linux package manager inside a macOS job is a stale ubuntu-to-macOS migration, and it wedges
    /// the job at that step before it reaches the work it exists to do. The failure is loud, which is
    /// exactly why it gets normalised: the lane is red on every single run, so nobody reads it.
    /// </summary>
    [Theory]

    [InlineData("apt-get")]

    [InlineData("apt install")]

    [InlineData("yum ")]

    public void Macos_lanes_never_install_tools_with_a_linux_package_manager(string packageManager)
    {

        List<string> offenders = [];

        foreach (WorkflowJob job in ContinuousIntegrationJobs(FindRepositoryRoot()))
        {

            if (!job.RunsOn.StartsWith("macos", StringComparison.Ordinal))
            {

                continue;

            }

            if (job.Body.Contains(packageManager, StringComparison.Ordinal))
            {

                offenders.Add($"{job.Id} (runs-on: {job.RunsOn})");

            }

        }

        Assert.True(
            offenders.Count == 0,
            $"A macOS CI job installs a tool with '{packageManager}', which does not exist on a "
            + "macOS runner, so the step fails and the job never reaches the work it exists to do. "
            + "Use `brew install` instead:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// Both tools the AOT IL gate depends on degrade it rather than stopping it. Without ripgrep
    /// <c>verify-aot-il-warnings.sh</c> fails closed, and without <c>ld64.lld</c> the CLI csproj
    /// cannot turn Native AOT on at all, so the gate falls back to Roslyn analyzer diagnostics with
    /// no ILC whole-program analysis — a weaker gate that still reports success.
    /// </summary>
    [Theory]

    [InlineData("brew install ripgrep")]

    [InlineData("ld64.lld")]

    public void Every_lane_running_the_aot_il_gate_installs_what_the_gate_needs(string requirement)
    {

        List<string> offenders = [];

        foreach (WorkflowJob job in ContinuousIntegrationJobs(FindRepositoryRoot()))
        {

            if (!job.Body.Contains("verify-aot-il-warnings.sh", StringComparison.Ordinal))
            {

                continue;

            }

            if (!job.Body.Contains(requirement, StringComparison.Ordinal))
            {

                offenders.Add(job.Id);

            }

        }

        Assert.True(
            offenders.Count == 0,
            $"A CI job runs the AOT IL warning gate without '{requirement}'. Without ripgrep the "
            + "gate refuses to run; without ld64.lld it silently degrades to analyzer diagnostics "
            + "and reports success having performed no ILC closure analysis:"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

    }

    /// <summary>
    /// The runtime identifier a runner label builds for. Only the RID family matters here: the
    /// hosted runners are x64 for Windows and Linux and arm64 for macOS.
    /// </summary>
    private static string RuntimeIdentifierFor(string runsOn) =>
        runsOn switch
        {
            _ when runsOn.StartsWith("windows", StringComparison.Ordinal) => "win-x64",
            _ when runsOn.StartsWith("macos", StringComparison.Ordinal) => "osx-arm64",
            _ => "linux-x64",
        };

    private static bool HasVerifiedNativeSqlCipherAsset(string repositoryRoot, string rid)
    {

        string packageRoot = Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.NativeSqlCipher");

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(packageRoot, "native-source-manifest.json")));

        foreach (JsonElement asset in manifest.RootElement.GetProperty("assets").EnumerateArray())
        {

            if (asset.GetProperty("rid").GetString() != rid)
            {

                continue;

            }

            return asset.GetProperty("status").GetString() == "verified"
                && File.Exists(FullPath(packageRoot, asset.GetProperty("path").GetString()!));

        }

        return false;

    }

    /// <summary>
    /// The one step that runs the Covenant release gate, and the artifact it has to leave behind.
    /// </summary>
    /// <remarks>
    /// Nothing else pins this step. The benchmark project is deliberately outside the solution so the
    /// every-test-project rule does not demand a <c>dotnet test</c> line for a console host, and that
    /// removes the only other way the lane could be held in place: the manifest tests parse the
    /// workload off disk and survive this step's deletion intact. Deleting the run line, or dropping
    /// <c>--gate</c> from it, would leave a repository that measures nothing and reports success.
    /// </remarks>
    [Fact]
    public void Ci_runs_the_covenant_benchmark_gate_and_keeps_the_run_it_measured()
    {

        string repositoryRoot = FindRepositoryRoot();

        Assert.True(
            File.Exists(Path.Combine(repositoryRoot, "scripts", "benchmark-covenant.sh")),
            "The Covenant benchmark script is missing, so the gate step in ci.yml cannot run.");

        Assert.True(
            File.Exists(FullPath(
                repositoryRoot,
                "tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj")),
            "The Covenant benchmark host is missing, so the gate step in ci.yml cannot publish it.");

        IReadOnlyList<WorkflowJob> jobs = JobsIn(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

        WorkflowJob gate = Assert.Single(
            jobs,
            static job => job.Body.Contains("benchmark-covenant.sh", StringComparison.Ordinal));

        Assert.Contains("./scripts/benchmark-covenant.sh --gate", gate.Body, StringComparison.Ordinal);

        // Recorded as well as gated. Without the run JSON the lane produces no fingerprint for a
        // release checklist to match and no observed distribution to justify the ceilings' headroom
        // against, so the comparative half of the gate never executes outside a hand run.
        Assert.Contains("--record covenant-benchmark-run.json", gate.Body, StringComparison.Ordinal);

        Assert.Contains("actions/upload-artifact", gate.Body, StringComparison.Ordinal);

        Assert.Contains("path: covenant-benchmark-run.json", gate.Body, StringComparison.Ordinal);

        Assert.Contains("retention-days:", gate.Body, StringComparison.Ordinal);

        // The gate step exits 1 on a ceiling breach, and the breaching run is the one whose observed
        // distribution most needs reading. A step with no condition defaults to `if: success()`, so
        // the artifact would be dropped on exactly the run that justified collecting it. Matched at
        // step-key indentation rather than as a bare substring, because the prose above the step in
        // ci.yml names the condition and would otherwise satisfy this on its own.
        Assert.Contains(
            $"{System.Environment.NewLine}        if: always(){System.Environment.NewLine}",
            gate.Body,
            StringComparison.Ordinal);

        // A conditional job is a gate that can be turned off by editing a condition rather than by
        // deleting a step, and a job-level condition here would also exempt this job from the
        // native-SQLCipher runner check above. The step-level condition asserted immediately above
        // must not count as one: JobsIn discards every line that is not at job-level indentation
        // before it reads a key, so these two assertions together pin that a step-level `if:` leaves
        // IsConditional false.
        Assert.False(
            gate.IsConditional,
            "The Covenant benchmark lane is conditional, so it can report success without running.");

    }

    private static IReadOnlyList<string> WorkflowFiles(string repositoryRoot)
    {

        string directory = Path.Combine(repositoryRoot, ".github", "workflows");

        string[] files = Directory.GetFiles(directory, "*.yml");

        Assert.NotEmpty(files);

        return files;

    }

    private static IReadOnlyList<WorkflowJob> ContinuousIntegrationJobs(string repositoryRoot) =>
        JobsIn(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

    /// <summary>
    /// Splits a workflow file into its jobs. Job keys are the only two-space-indented mapping keys
    /// under <c>jobs:</c>, and <c>runs-on:</c>/<c>if:</c> at four spaces are the only job-level
    /// occurrences of those keys, so the shape can be read without a YAML parser.
    /// </summary>
    private static IReadOnlyList<WorkflowJob> JobsIn(string workflowPath)
    {

        string[] lines = File.ReadAllLines(workflowPath);

        List<WorkflowJob> jobs = [];

        string? id = null;

        string runsOn = string.Empty;

        bool conditional = false;

        StringBuilder body = new();

        bool insideJobs = false;

        foreach (string line in lines)
        {

            string trimmed = line.Trim();

            if (!insideJobs)
            {

                insideJobs = line.StartsWith("jobs:", StringComparison.Ordinal);

                continue;

            }

            if (WorkflowIndentOf(line) == 2
                && !trimmed.StartsWith('#')
                && trimmed.EndsWith(':'))
            {

                if (id is not null)
                {

                    jobs.Add(new WorkflowJob(id, runsOn, conditional, body.ToString()));

                }

                id = trimmed[..^1];

                runsOn = string.Empty;

                conditional = false;

                body.Clear();

                continue;

            }

            if (id is null)
            {

                continue;

            }

            body.AppendLine(line);

            if (WorkflowIndentOf(line) != 4)
            {

                continue;

            }

            if (trimmed.StartsWith("runs-on:", StringComparison.Ordinal))
            {

                runsOn = trimmed["runs-on:".Length..].Trim();

            }

            if (trimmed.StartsWith("if:", StringComparison.Ordinal))
            {

                conditional = true;

            }

        }

        if (id is not null)
        {

            jobs.Add(new WorkflowJob(id, runsOn, conditional, body.ToString()));

        }

        Assert.NotEmpty(jobs);

        return jobs;

    }

    private static int WorkflowIndentOf(string line)
    {

        int indent = 0;

        while (indent < line.Length && line[indent] == ' ')
        {

            indent++;

        }

        return indent;

    }

    [Theory]

    [InlineData("src/RetroDownfall.TheForge.Core/RetroDownfall.TheForge.Core.csproj")]

    [InlineData("src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj")]

    [InlineData("src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj")]

    [InlineData("src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj")]

    // Not shipped, but in the same position: the benchmark host is outside the solution, so no
    // build of RetroDownfall.Arcanum.slnx and no dotnet test compiles it. Without a step that names
    // it, a change to a Covenant service that broke the host merges green and surfaces only when
    // somebody reaches for the gate to qualify a release.
    [InlineData("tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj")]

    public void Ci_compiles_every_project_the_release_workflows_ship(string relativeProjectPath)
    {

        string repositoryRoot = FindRepositoryRoot();

        IReadOnlySet<string> compiled = CompiledProjectClosure(repositoryRoot);

        string expected = FullPath(repositoryRoot, relativeProjectPath);

        Assert.True(
            compiled.Contains(expected),
            $"{relativeProjectPath} is shipped by the release workflows but is never compiled by "
            + ".github/workflows/ci.yml, so a compile break in it merges green.");

    }

    [Fact]
    public void Ci_runs_on_a_push_to_every_branch_the_project_integrates_on()
    {

        IReadOnlyList<string> branches = PushTriggerBranches(FindRepositoryRoot());

        // main, and whatever else the project integrates on at the time. long-term-memory was named
        // here for as long as it was the branch every workstream landed on; it was merged into main
        // and deleted, and a rule that keeps asserting a branch nobody can push to is asserting the
        // past. Add the next integration branch here when there is one -- the reason has not changed:
        // a direct push to a branch this list omits runs no gate at all, so coverage, the Windows
        // suite, and the AOT IL closure analysis would execute only on pull requests.
        Assert.True(
            branches.Contains("main", StringComparer.Ordinal),
            "A direct push to main runs no CI at all: .github/workflows/ci.yml lists on.push.branches "
            + $"as [{string.Join(", ", branches)}].");

    }

    [Theory]

    [InlineData(
        "scripts/align_csharp_blanklines.py --repo . --check",
        "the C# blank-line formatter the repository ships")]

    [InlineData(
        "shellcheck -x -P SCRIPTDIR",
        "shellcheck over every packaging and gate script")]

    public void Ci_enforces_the_formatting_and_shell_validation_tools_the_repository_ships(
        string invocation,
        string description)
    {

        string workflow = WorkflowText(FindRepositoryRoot());

        Assert.True(
            workflow.Contains(invocation, StringComparison.Ordinal),
            $"ci.yml never invokes {description} (`{invocation}`), so the repository ships a "
            + "checker no lane runs and violations accumulate unnoticed.");

    }

    [Fact]
    public void Ci_uploads_exactly_the_release_evidence_artifacts_the_readme_lists()
    {

        string repositoryRoot = FindRepositoryRoot();

        IReadOnlySet<string> produced = UploadedArtifactNames(repositoryRoot);

        IReadOnlySet<string> documented = DocumentedReleaseEvidenceArtifacts(repositoryRoot);

        Assert.True(
            produced.SetEquals(documented),
            "The release-qualification evidence list in README.md and the upload-artifact steps in "
            + $".github/workflows/ci.yml disagree. README lists [{string.Join(", ", documented)}]; "
            + $"the workflow produces [{string.Join(", ", produced)}]. A documented artifact with no "
            + "producer cannot be cited as evidence, and an undocumented one is evidence nobody "
            + "knows to collect.");

    }

    private static IReadOnlyList<string> PushTriggerBranches(string repositoryRoot)
    {

        string[] lines = WorkflowLines(repositoryRoot);

        int push = Array.FindIndex(lines, line => line == "  push:");

        Assert.True(push >= 0, "ci.yml has no top-level on.push trigger.");

        int branches = -1;

        for (int index = push + 1; index < lines.Length; index++)
        {

            string line = lines[index];

            // A line at the `push:` indent or shallower has left the trigger block; anything
            // deeper is a comment or a sibling key such as `paths-ignore`.
            if (line.Length > 0 && !line.StartsWith("    ", StringComparison.Ordinal))
            {

                break;

            }

            if (line.Trim() == "branches:")
            {

                branches = index;

                break;

            }

        }

        Assert.True(branches >= 0, "ci.yml's on.push trigger names no branch list.");

        List<string> named = [];

        for (int index = branches + 1; index < lines.Length; index++)
        {

            string trimmed = lines[index].Trim();

            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {

                break;

            }

            named.Add(trimmed[2..].Trim());

        }

        return named;

    }

    private static IReadOnlySet<string> UploadedArtifactNames(string repositoryRoot)
    {

        string[] lines = WorkflowLines(repositoryRoot);

        HashSet<string> names = new(StringComparer.Ordinal);

        for (int index = 0; index < lines.Length; index++)
        {

            if (!lines[index].Contains("uses: actions/upload-artifact", StringComparison.Ordinal))
            {

                continue;

            }

            for (int candidate = index + 1; candidate < lines.Length; candidate++)
            {

                string trimmed = lines[candidate].Trim();

                // `- name:` starts the next step, so the `with:` block ended without naming the
                // artifact and GitHub would upload it under its default name.
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {

                    break;

                }

                if (trimmed.StartsWith("name:", StringComparison.Ordinal))
                {

                    names.Add(trimmed["name:".Length..].Trim());

                    break;

                }

            }

        }

        Assert.NotEmpty(names);

        return names;

    }

    private static IReadOnlySet<string> DocumentedReleaseEvidenceArtifacts(string repositoryRoot)
    {

        string[] lines = File.ReadAllLines(Path.Combine(repositoryRoot, "README.md"));

        int heading = Array.FindIndex(
            lines,
            line => line.Trim() == "### Release-qualification evidence");

        Assert.True(
            heading >= 0,
            "README.md carries no release-qualification evidence section, so nothing states which "
            + "artifacts a qualified release must be able to produce.");

        int fence = Array.FindIndex(lines, heading, line => line.Trim() == "```text");

        Assert.True(fence >= 0, "The release-qualification evidence section lists no artifacts.");

        HashSet<string> documented = new(StringComparer.Ordinal);

        for (int index = fence + 1; index < lines.Length && lines[index].Trim() != "```"; index++)
        {

            string trimmed = lines[index].Trim();

            if (trimmed.Length > 0)
            {

                documented.Add(trimmed);

            }

        }

        Assert.NotEmpty(documented);

        return documented;

    }

    private static string[] WorkflowLines(string repositoryRoot) =>
        WorkflowText(repositoryRoot).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string WorkflowText(string repositoryRoot) =>
        File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

    private static IReadOnlySet<string> CompiledProjectClosure(string repositoryRoot)
    {

        string workflow = WorkflowText(repositoryRoot);

        HashSet<string> closure = new(StringComparer.Ordinal);

        Queue<string> pending = new();

        foreach (string token in workflow.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {

            if (!token.EndsWith(".csproj", StringComparison.Ordinal))
            {

                continue;

            }

            string projectPath = FullPath(repositoryRoot, token);

            if (File.Exists(projectPath) && closure.Add(projectPath))
            {

                pending.Enqueue(projectPath);

            }

        }

        Assert.NotEmpty(closure);

        while (pending.Count > 0)
        {

            string projectPath = pending.Dequeue();

            string projectDirectory = Path.GetDirectoryName(projectPath)!;

            foreach (XElement element in XDocument.Load(projectPath).Descendants("ProjectReference"))
            {

                string? include = element.Attribute("Include")?.Value;

                if (include is null)
                {

                    continue;

                }

                string referencePath = FullPath(projectDirectory, include);

                if (File.Exists(referencePath) && closure.Add(referencePath))
                {

                    pending.Enqueue(referencePath);

                }

            }

        }

        return closure;

    }

    private static string FullPath(string baseDirectory, string relativePath) =>
        Path.GetFullPath(
            Path.Combine(
                baseDirectory,
                relativePath.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));

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
