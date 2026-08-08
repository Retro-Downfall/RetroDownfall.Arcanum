using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Generated completion scripts are a projection of the live command tree. They must be stable
/// enough to commit, free of anything machine- or account-specific, and silent when the host is not
/// running.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliCompletionTests
{

    public static TheoryData<string> Shells =>
        [.. CliCompletionShells.Names];

    [Theory]
    [MemberData(nameof(Shells))]
    public void Completion_emits_a_script_on_stdout(string shell)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "completion", shell);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.False(string.IsNullOrWhiteSpace(result.Output));

        Assert.Empty(result.Error);

    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_are_byte_for_byte_stable(string shell)
    {

        CliSurfaceMap map = CliSurfaceTests.BuildMap();

        Assert.Equal(
            CliCompletionScriptWriter.Write(shell, map),
            CliCompletionScriptWriter.Write(shell, map));

    }

    /// <summary>
    /// A committed or shell-sourced script must not carry the generating machine's identity, the
    /// operator's paths, or any credential-shaped value. The username is checked as a path segment
    /// rather than a bare substring — a short username like <c>mat</c> legitimately occurs inside
    /// ordinary option names such as <c>--output-format</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_contain_no_machine_or_secret_specific_values(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        string user = global::System.Environment.UserName;

        Assert.DoesNotContain($"/{user}/", script, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain($"\\{user}\\", script, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.UserProfile,
                global::System.Environment.SpecialFolderOption.DoNotVerify),
            script,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("/Users/", script, StringComparison.Ordinal);

        Assert.DoesNotContain("/home/", script, StringComparison.Ordinal);

        Assert.DoesNotContain("localhost:", script, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);

        // Credential-shaped values, not the word "key": `--restore-master-api-key` is an option
        // name operators must be able to complete, and refusing to emit it would be the wrong fix.
        Assert.DoesNotContain("sk-", script, StringComparison.Ordinal);

        Assert.DoesNotContain("Bearer ", script, StringComparison.Ordinal);

        Assert.DoesNotContain("ARCANUM_PROVIDER_", script, StringComparison.Ordinal);

    }

    /// <summary>
    /// The strongest guarantee available: generation reads the command tree and nothing else, so
    /// changing the ambient environment cannot change a single byte of the output. This is what
    /// makes the scripts safe to commit and share.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generation_is_independent_of_the_ambient_environment(string shell)
    {

        CliSurfaceMap map = CliSurfaceTests.BuildMap();

        string baseline = CliCompletionScriptWriter.Write(shell, map);

        string? previousHome = global::System.Environment.GetEnvironmentVariable("HOME");

        string? previousUser = global::System.Environment.GetEnvironmentVariable("USER");

        try
        {

            global::System.Environment.SetEnvironmentVariable("HOME", "/tmp/arcanum-completion-probe");

            global::System.Environment.SetEnvironmentVariable("USER", "probe-user");

            Assert.Equal(baseline, CliCompletionScriptWriter.Write(shell, map));

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable("HOME", previousHome);

            global::System.Environment.SetEnvironmentVariable("USER", previousUser);

        }

    }

    /// <summary>
    /// System.CommandLine carries Windows-style <c>/?</c> and <c>/h</c> help aliases. They are real
    /// spellings, but offering them as completion words in a POSIX shell is noise at best and a
    /// path-looking token at worst, so the generator emits only dash-prefixed options.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_offer_only_dash_prefixed_option_spellings(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        Assert.DoesNotContain("/?", script, StringComparison.Ordinal);

        Assert.DoesNotContain(" /h ", script, StringComparison.Ordinal);

    }

    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_offer_the_canonical_top_level_commands(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        Assert.Contains("run", script, StringComparison.Ordinal);

        Assert.Contains("doctor", script, StringComparison.Ordinal);

        Assert.Contains("completion", script, StringComparison.Ordinal);

    }

    /// <summary>
    /// Removed spellings must not reappear through the generated script — that would resurrect the
    /// alias surface the whole change set exists to delete.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_never_offer_a_removed_spelling(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        foreach (string removed in (string[])["--tool-name", "--campaignId", "--sessionId", "--agentUrl"])
        {

            Assert.DoesNotContain(removed, script, StringComparison.Ordinal);

        }

    }

    /// <summary>
    /// A positional with a closed value set is completed exactly like a subcommand: after
    /// <c>arcanum completion </c>, <c>bash</c> is as valid a next word as <c>install</c>, and
    /// offering only one of them is the wrong half.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_offer_positional_enum_values(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        foreach (string name in CliCompletionShells.Names)
        {

            Assert.Contains(name, script, StringComparison.Ordinal);

        }

        Assert.Contains("sessions", script, StringComparison.Ordinal);

        Assert.Contains("automation", script, StringComparison.Ordinal);

    }

    /// <summary>
    /// Recursive root options are valid at every path but are not repeated in each command's own
    /// option list, so their closed sets must be contributed explicitly or
    /// <c>--output-format &lt;TAB&gt;</c> would offer nothing anywhere below the root.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_offer_recursive_global_option_values_at_nested_paths(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        // Each shell binds an option to its values in its own idiom: bash/zsh/PowerShell key a map
        // by "path|option", while fish emits one `complete` line carrying both the path condition
        // and the values.
        string expected = shell == CliCompletionShells.Fish
            ? "\"session list\"' -l 'output-format' -a 'json text'"
            : "session list|--output-format";

        Assert.Contains(expected, script, StringComparison.Ordinal);

        Assert.Contains("json text", script, StringComparison.Ordinal);

    }

    [Fact]
    public void An_unsupported_shell_is_a_command_line_error()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "completion", "tcsh");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

    }

    /// <summary>
    /// Installing writes to the operator's shell configuration, so it names the exact target and
    /// refuses to proceed non-interactively without <c>--yes</c>.
    /// </summary>
    [Fact]
    public void Completion_install_fails_closed_without_confirmation()
    {

        CliTestResult result = CliTestHarness.Run(
            CreateServices(),
            "completion",
            "install",
            "bash",
            "--target",
            Path.Combine(Path.GetTempPath(), "arcanum-completion-refused.bash"));

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.False(
            File.Exists(Path.Combine(Path.GetTempPath(), "arcanum-completion-refused.bash")),
            "A refused install must not write the file.");

    }

    [Fact]
    public void Completion_install_writes_the_script_and_reports_the_target()
    {

        string target = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-completion-{Guid.NewGuid():N}.bash");

        try
        {

            CliTestResult result = CliTestHarness.Run(
                CreateServices(),
                "completion",
                "install",
                "bash",
                "--target",
                target,
                "--yes");

            Assert.Equal((int)CliExitCode.Success, result.ExitCode);

            Assert.True(File.Exists(target));

            Assert.Equal(
                CliCompletionScriptWriter.Write("bash", CliSurfaceTests.BuildMap()),
                File.ReadAllText(target));

            Assert.Contains(target, result.Error, StringComparison.Ordinal);

        }
        finally
        {

            File.Delete(target);

        }

    }

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

}
