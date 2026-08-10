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

    /// <summary>
    /// fish runs a command substitution only outside double quotes or through the <c>$(…)</c> form,
    /// so a path condition written as <c>test "(__arcanum_path)" = "session list"</c> compares the
    /// literal text <c>(__arcanum_path)</c> and can never hold — the script installs cleanly and
    /// then silently offers nothing below the root. Asserted against a model of fish's substitution
    /// and <c>test</c> semantics rather than the condition's wording, so a future rewrite of the
    /// idiom is judged on what it does.
    /// </summary>
    [Theory]
    [InlineData("", "session")]
    [InlineData("session", "list")]
    [InlineData("session list", "output-format")]
    [InlineData("session list", "json text")]
    public void Fish_completes_the_path_the_operator_has_already_typed(string path, string expected)
    {

        Assert.Contains(expected, FishOffers(path), StringComparer.Ordinal);

    }

    /// <summary>
    /// The other half of the same guarantee: a condition that always holds would "fix" the dead
    /// completions by offering every path's words at every path.
    /// </summary>
    [Fact]
    public void Fish_does_not_offer_a_root_command_below_the_root()
    {

        Assert.DoesNotContain("doctor", FishOffers("session list"), StringComparer.Ordinal);

    }

    /// <summary>
    /// The words a fish shell would offer for <c>arcanum &lt;path&gt; &lt;TAB&gt;</c>: the payload
    /// of every generated <c>complete</c> line whose condition holds for that command path.
    /// </summary>
    private static IReadOnlyList<string> FishOffers(string path)
    {

        string script = CliCompletionScriptWriter.Write(
            CliCompletionShells.Fish,
            CliSurfaceTests.BuildMap());

        List<string> offers = [];

        foreach (string line in script.Split('\n'))
        {

            if (!line.StartsWith("complete ", StringComparison.Ordinal))
            {

                continue;

            }

            if (!FishConditionHolds(Quoted(line, " -n "), path))
            {

                continue;

            }

            foreach (string flag in (string[])[" -a ", " -l "])
            {

                if (line.Contains(flag, StringComparison.Ordinal))
                {

                    offers.Add(Quoted(line, flag));

                }

            }

        }

        return offers;

    }

    /// <summary>
    /// The single-quoted value that follows <paramref name="flag"/> on a generated
    /// <c>complete</c> line. Nothing the generator emits contains a single quote.
    /// </summary>
    private static string Quoted(string line, string flag)
    {

        int start = line.IndexOf(flag, StringComparison.Ordinal) + flag.Length + 1;

        return line[start..line.IndexOf('\'', start)];

    }

    /// <summary>
    /// Evaluates one generated condition the way fish would, including the rule these tests exist
    /// for: <c>(cmd)</c> and <c>$(cmd)</c> run the command, while <c>"(cmd)"</c> inside double
    /// quotes is literal text. An unquoted substitution that produced nothing contributes no word,
    /// which is why the root's <c>test -z (__arcanum_path)</c> holds on an empty path.
    /// </summary>
    private static bool FishConditionHolds(string condition, string path)
    {

        List<string> words = [];

        foreach ((string text, bool quoted) in FishWords(condition))
        {

            string substituted = text.Replace("$(__arcanum_path)", path, StringComparison.Ordinal);

            if (!quoted)
            {

                substituted = substituted.Replace("(__arcanum_path)", path, StringComparison.Ordinal);

            }

            if (!quoted && substituted.Length == 0 && text.Length > 0)
            {

                continue;

            }

            words.Add(substituted);

        }

        return words switch
        {
            ["test", "-z", string subject] => subject.Length == 0,
            ["test", "-z"] => true,
            ["test", string left, "=", string right] => string.Equals(left, right, StringComparison.Ordinal),
            _ => throw new InvalidOperationException($"Unmodelled generated fish condition: {condition}"),
        };

    }

    /// <summary>
    /// Splits a condition into shell words, recording whether each was written inside double
    /// quotes — the distinction the substitution rule turns on.
    /// </summary>
    private static IReadOnlyList<(string Text, bool Quoted)> FishWords(string condition)
    {

        List<(string Text, bool Quoted)> words = [];

        System.Text.StringBuilder current = new();

        bool inQuotes = false;

        bool quoted = false;

        foreach (char character in condition)
        {

            if (character == '"')
            {

                inQuotes = !inQuotes;

                quoted = true;

                continue;

            }

            if (character == ' ' && !inQuotes)
            {

                if (current.Length > 0 || quoted)
                {

                    words.Add((current.ToString(), quoted));

                    current.Clear();

                    quoted = false;

                }

                continue;

            }

            current.Append(character);

        }

        if (current.Length > 0 || quoted)
        {

            words.Add((current.ToString(), quoted));

        }

        return words;

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
