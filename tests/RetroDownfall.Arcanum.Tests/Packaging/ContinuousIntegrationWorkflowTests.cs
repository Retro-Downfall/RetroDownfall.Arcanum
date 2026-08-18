using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class ContinuousIntegrationWorkflowTests
{

    /// <summary>
    /// One job of <c>.github/workflows/ci.yml</c>: the runner it asks for, whether a job-level
    /// <c>if:</c> gates it, and every line of its body.
    /// </summary>
    private sealed record WorkflowJob(string Id, string RunsOn, bool IsConditional, string Body);

    /// <summary>
    /// The hermetic SQLCipher targets raise ARCSQLC002 before <c>CopyFilesToOutputDirectory</c>
    /// whenever the RID being built has no checked-in asset — deliberately, with no probe and no
    /// fallback. A lane that sets .NET up on such a runner therefore dies at the first
    /// <c>dotnet build</c> and never reaches a single test, so it has to be gated on the manifest
    /// rather than presented as a check that merely happens to be red.
    /// </summary>
    [Fact]
    public void Ci_never_builds_on_a_runner_whose_native_sqlcipher_asset_is_missing()
    {

        string repositoryRoot = FindRepositoryRoot();

        List<string> offenders = [];

        foreach (WorkflowJob job in ContinuousIntegrationJobs(repositoryRoot))
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

            offenders.Add($"{job.Id} (runs-on: {job.RunsOn}, needs a verified {rid} asset)");

        }

        Assert.True(
            offenders.Count == 0,
            "A CI job builds .NET on a runtime identifier whose hermetic SQLCipher asset is not "
            + "checked in and verified, so it fails with ARCSQLC002 before any test runs. Gate the "
            + "job on the manifest status so it returns automatically once the asset lands:"
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
    /// Splits <c>.github/workflows/ci.yml</c> into its jobs. Job keys are the only two-space-indented
    /// mapping keys under <c>jobs:</c>, and <c>runs-on:</c>/<c>if:</c> at four spaces are the only
    /// job-level occurrences of those keys, so the shape can be read without a YAML parser.
    /// </summary>
    private static IReadOnlyList<WorkflowJob> ContinuousIntegrationJobs(string repositoryRoot)
    {

        string[] lines = File.ReadAllLines(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

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

    private static IReadOnlySet<string> CompiledProjectClosure(string repositoryRoot)
    {

        string workflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

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
