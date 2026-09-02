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
    /// A symbol whose values are a live resource catalog is completed from the running host, and
    /// the provider name is the entire contract between a generated script and
    /// <c>completion resolve</c>. Nothing pinned this pipeline before, for options or positionals.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_bind_an_options_dynamic_source(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        string expected = shell == CliCompletionShells.Fish
            ? "\"run\"' -l 'model' -a '(__arcanum_resolve model)'"
            : ProviderBinding(shell, "run|--model", "model");

        Assert.Contains(expected, script, StringComparison.Ordinal);

    }

    /// <summary>
    /// Several commands name their resource with a positional and have no option for it at all —
    /// <c>campaign show</c>, <c>session show</c> and <c>workspace unregister</c> carry no options
    /// whatsoever. A binding that reaches the command map and no shell leaves those positions
    /// offering nothing in every shell.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shells))]
    public void Generated_scripts_bind_a_positionals_dynamic_source(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        string expected = shell == CliCompletionShells.Fish
            ? "\"campaign show\"; and __arcanum_positional_open' -a '(__arcanum_resolve campaign)'"
            : ArgumentBinding(shell, "campaign show", "campaign");

        Assert.Contains(expected, script, StringComparison.Ordinal);

    }

    /// <summary>
    /// A resource positional is offered until it has been supplied and not after: once
    /// <c>arcanum campaign show Arcanum </c> is typed, the next word is an option, not a second
    /// campaign name.
    /// </summary>
    [Fact]
    public void Fish_stops_offering_a_positional_once_it_has_been_supplied()
    {

        Assert.Contains(
            "(__arcanum_resolve campaign)",
            FishOffers("campaign show"),
            StringComparer.Ordinal);

        Assert.DoesNotContain(
            "(__arcanum_resolve campaign)",
            FishOffers("campaign show Arcanum"),
            StringComparer.Ordinal);

    }

    /// <summary>
    /// The map is keyed by the token the operator actually typed, and <c>-m</c> is as valid a
    /// spelling of <c>--model</c> as the long form. Keying only the canonical name leaves every
    /// published short flag resolving nothing.
    /// </summary>
    [Theory]
    [InlineData(CliCompletionShells.Bash)]
    [InlineData(CliCompletionShells.Zsh)]
    [InlineData(CliCompletionShells.PowerShell)]
    public void Generated_scripts_bind_a_dynamic_source_to_every_accepted_spelling(string shell)
    {

        string script = CliCompletionScriptWriter.Write(shell, CliSurfaceTests.BuildMap());

        Assert.Contains(ProviderBinding(shell, "run|-m", "model"), script, StringComparison.Ordinal);

    }

    /// <summary>
    /// The zsh script installs to <c>~/.zfunc/_arcanum</c> and is autoloaded, so the whole file is
    /// the body of <c>_arcanum</c> — the maps and the trailing <c>compdef</c> after the definition
    /// are exactly what rules out zsh's ksh-style "source then call" shortcut. Without a self-call
    /// the invocation that triggered the autoload defines the function, adds no candidates, and
    /// returns: the first TAB of every new shell is dead. The guard is correct for the sourced
    /// install too, where <c>compdef</c> is the right call and only exists once compinit has run.
    /// </summary>
    [Fact]
    public void Zsh_script_calls_the_completion_function_when_it_is_autoloaded()
    {

        string script = CliCompletionScriptWriter.Write(
            CliCompletionShells.Zsh,
            CliSurfaceTests.BuildMap());

        Assert.Contains("if [ \"$funcstack[1]\" = \"_arcanum\" ]; then", script, StringComparison.Ordinal);

        Assert.Contains("_arcanum \"$@\"", script, StringComparison.Ordinal);

        Assert.Contains("(( $+functions[compdef] )) && compdef _arcanum arcanum", script, StringComparison.Ordinal);

    }

    /// <summary>
    /// <c>$commandAst.CommandElements</c> includes the word under the cursor, unlike bash's
    /// <c>COMP_WORDS[COMP_CWORD-1]</c> and zsh's <c>words[CURRENT-1]</c>. Deriving the path and the
    /// preceding token from it unfiltered makes a partially typed value its own predecessor, so
    /// <c>arcanum run --model gp&lt;TAB&gt;</c> looks up <c>run|gp</c> and offers nothing where
    /// bash and zsh complete the model names. Asserted against the emitted text: no PowerShell host
    /// is available to the test run, and the derivation is the defect.
    /// </summary>
    [Fact]
    public void PowerShell_completer_excludes_the_word_under_the_cursor()
    {

        string script = CliCompletionScriptWriter.Write(
            CliCompletionShells.PowerShell,
            CliSurfaceTests.BuildMap());

        Assert.DoesNotContain("$tokens[$tokens.Count - 1]", script, StringComparison.Ordinal);

        Assert.Contains("foreach ($token in $walk)", script, StringComparison.Ordinal);

        Assert.Contains(
            "$previous = if ($walk.Count -ge 1) { $walk[$walk.Count - 1] } else { '' }",
            script,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// How one shell writes "after <paramref name="key"/>, resolve <paramref name="provider"/>".
    /// </summary>
    private static string ProviderBinding(string shell, string key, string provider) =>
        shell switch
        {
            CliCompletionShells.Bash => $"\"{key}\") echo \"{provider}\" ;;",
            CliCompletionShells.Zsh => $"['{key}']='{provider}'",
            CliCompletionShells.PowerShell => $"$script:ArcanumProviders['{key}'] = '{provider}'",
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unmodelled shell."),
        };

    /// <summary>
    /// How one shell writes "at <paramref name="path"/>, the positional resolves
    /// <paramref name="provider"/>". Keyed on the path alone, because a positional has no
    /// preceding option token to key on.
    /// </summary>
    private static string ArgumentBinding(string shell, string path, string provider) =>
        shell switch
        {
            CliCompletionShells.Bash => $"\"{path}\") echo \"{provider}\" ;;",
            CliCompletionShells.Zsh => $"['{path}']='{provider}'",
            CliCompletionShells.PowerShell => $"$script:ArcanumArguments['{path}'] = '{provider}'",
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unmodelled shell."),
        };

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
    [InlineData("session list", "--output-format")]
    [InlineData("session list --output-format", "json")]
    [InlineData("session list --output-format", "text")]
    public void Fish_completes_the_position_the_operator_has_typed_their_way_to(
        string typed,
        string expected)
    {

        Assert.Contains(expected, FishOffers(typed), StringComparer.Ordinal);

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
    /// An option's values belong after the option and nowhere else. Offering <c>json</c> and
    /// <c>text</c> at the bare <c>session list</c> path would be a suggestion the parser rejects.
    /// </summary>
    [Fact]
    public void Fish_does_not_offer_an_option_value_at_the_bare_path()
    {

        Assert.DoesNotContain("json", FishOffers("session list"), StringComparer.Ordinal);

    }

    /// <summary>
    /// The path walk must stop at the first word that is not a command, exactly as bash, zsh and
    /// PowerShell stop. Concatenating every non-dash token instead — a positional value, or any
    /// option's value — builds a path no generated condition matches, and because every
    /// <c>complete</c> line carries <c>-f</c> the operator is then offered filenames where the
    /// command's own flags belong.
    /// </summary>
    [Theory]
    [InlineData("campaign show Arcanum")]
    [InlineData("session list --limit 10")]
    public void Fish_still_completes_once_a_value_has_been_typed(string typed)
    {

        IReadOnlyList<string> offers = FishOffers(typed);

        Assert.Contains("--json", offers, StringComparer.Ordinal);

        Assert.DoesNotContain("doctor", offers, StringComparer.Ordinal);

    }

    /// <summary>
    /// The model below walks the command path against the list the generated script declares, so
    /// this pins that the generated <c>__arcanum_path</c> walks it the same way — the one part of
    /// the script the model cannot read out of the emitted text.
    /// </summary>
    [Fact]
    public void Fish_walks_the_command_path_against_the_declared_paths()
    {

        string script = CliCompletionScriptWriter.Write(
            CliCompletionShells.Fish,
            CliSurfaceTests.BuildMap());

        Assert.Contains("contains -- $candidate $__arcanum_paths", script, StringComparison.Ordinal);

        Assert.Contains("set -a __arcanum_paths 'session list'", script, StringComparison.Ordinal);

    }

    /// <summary>
    /// The words a fish shell would offer for <c>arcanum &lt;typed&gt; &lt;TAB&gt;</c>.
    ///
    /// The command path is walked against the path list the script itself declares, so a script
    /// that declares none — or an incomplete one — is judged on that rather than on the model's
    /// own idea of the tree. Option values are routed the way fish routes them: an <c>-a</c> list
    /// attached to an option is reachable only after that option, and only when the option is
    /// declared as taking a parameter (<c>-x</c>, which is <c>-r -f</c>). Without that declaration
    /// fish treats the option as a flag and the list is unreachable by any spelling.
    /// </summary>
    private static IReadOnlyList<string> FishOffers(string typed)
    {

        string script = CliCompletionScriptWriter.Write(
            CliCompletionShells.Fish,
            CliSurfaceTests.BuildMap());

        string[] words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        HashSet<string> declared = [.. FishDeclaredPaths(script)];

        (string path, int extra) = FishWalk(words, declared);

        string previous = words.Length == 0 ? string.Empty : words[^1];

        List<FishCompletion> applicable =
        [
            .. FishCompletions(script).Where(completion =>
                FishConditionHolds(completion.Condition, path, extra)),
        ];

        FishCompletion? awaiting = applicable.FirstOrDefault(completion =>
            completion.TakesParameter
            && completion.Option is not null
            && string.Equals($"--{completion.Option}", previous, StringComparison.Ordinal));

        if (awaiting is not null)
        {

            return [.. FishValues(awaiting.Values ?? string.Empty)];

        }

        List<string> offers = [];

        foreach (FishCompletion completion in applicable)
        {

            if (completion.Option is not null)
            {

                offers.Add($"--{completion.Option}");

                continue;

            }

            offers.AddRange(FishValues(completion.Values ?? string.Empty));

        }

        return offers;

    }

    /// <summary>
    /// Walks the typed words into a command path the way the generated <c>__arcanum_path</c> does:
    /// dash-led words are skipped, and the walk stops extending at the first word that is not a
    /// declared path. <c>Extra</c> counts the words that fell outside it — a positional value or an
    /// option's value — which is what tells a resource positional it has already been supplied.
    /// </summary>
    private static (string Path, int Extra) FishWalk(
        IReadOnlyList<string> words,
        IReadOnlySet<string> declared)
    {

        string path = string.Empty;

        int extra = 0;

        foreach (string word in words)
        {

            if (word.StartsWith('-'))
            {

                continue;

            }

            string candidate = path.Length == 0 ? word : $"{path} {word}";

            if (extra == 0 && declared.Contains(candidate))
            {

                path = candidate;

            }
            else
            {

                extra++;

            }

        }

        return (path, extra);

    }

    private static IEnumerable<string> FishDeclaredPaths(string script) =>
        script
            .Split('\n')
            .Where(static line => line.StartsWith("set -a __arcanum_paths ", StringComparison.Ordinal))
            .Select(static line => Quoted(line, "__arcanum_paths "));

    private static IEnumerable<FishCompletion> FishCompletions(string script) =>
        script
            .Split('\n')
            .Where(static line => line.StartsWith("complete ", StringComparison.Ordinal))
            .Select(static line => new FishCompletion(
                Quoted(line, " -n "),
                line.Contains(" -l ", StringComparison.Ordinal) ? Quoted(line, " -l ") : null,
                line.Contains(" -a ", StringComparison.Ordinal) ? Quoted(line, " -a ") : null,
                line.Contains(" -x ", StringComparison.Ordinal)
                    || line.Contains(" -r ", StringComparison.Ordinal)));

    /// <summary>
    /// A command substitution stays one candidate: it is the dynamic list, not a word list the
    /// generator baked in.
    /// </summary>
    private static IEnumerable<string> FishValues(string values) =>
        values.StartsWith('(')
            ? [values]
            : values.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private sealed record FishCompletion(
        string Condition,
        string? Option,
        string? Values,
        bool TakesParameter);

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
    private static bool FishConditionHolds(string condition, string path, int extra)
    {

        int separator = condition.IndexOf("; and ", StringComparison.Ordinal);

        if (separator >= 0)
        {

            // The only compound condition the generator emits guards a resource positional that has
            // not been supplied yet.
            Assert.Equal(
                "__arcanum_positional_open",
                condition[(separator + "; and ".Length)..]);

            return extra == 0 && FishConditionHolds(condition[..separator], path, extra);

        }

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

    /// <summary>
    /// <c>--target</c> is an operator-supplied path Arcanum does not own — a system-wide
    /// <c>site-functions</c> directory, or a home directory. A completion script is not a secret, so
    /// installing one must never silently re-permission the directory it lands in.
    /// </summary>
    [SkippableFact]
    public void Completion_install_leaves_an_existing_target_directory_permissions_alone()
    {

        Skip.If(OperatingSystem.IsWindows(), "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-completion-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        const UnixFileMode shared =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        File.SetUnixFileMode(directory, shared);

        string target = Path.Combine(directory, "arcanum");

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

            Assert.Equal(shared, File.GetUnixFileMode(directory));

        }
        finally
        {

            Directory.Delete(directory, recursive: true);

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
