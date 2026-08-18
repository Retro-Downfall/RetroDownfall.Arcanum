using System.Text;

namespace RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

internal static class CliCompletionShells
{

    public const string Bash = "bash";

    public const string Zsh = "zsh";

    public const string Fish = "fish";

    public const string PowerShell = "powershell";

    public static readonly string[] Names = [Bash, Zsh, Fish, PowerShell];

    public static bool IsSupported(string? shell) =>
        shell is not null && Names.Contains(shell, StringComparer.Ordinal);

}

/// <summary>
/// Emits static completion scripts from <see cref="CliSurfaceMap"/>.
///
/// The scripts are pure data plus dispatch: every command path, option, and closed value set is
/// baked in as a literal, so completion works with the host stopped and costs one shell function
/// call. Nothing machine-, account-, or endpoint-specific is written, and generation is
/// deterministic — the same tree produces byte-identical output on any machine, so the result can
/// be committed, diffed, and snapshot-tested.
///
/// Live resources (models, Campaigns, Sessions, …) are not baked in. Where a symbol has a dynamic
/// source, the script shells out to <c>arcanum completion resolve</c>, which fails silently and
/// falls back to no suggestions when the host is unavailable.
/// </summary>
internal static class CliCompletionScriptWriter
{

    public static string Write(string shell, CliSurfaceMap map)
    {

        ArgumentNullException.ThrowIfNull(map);

        return shell switch
        {
            CliCompletionShells.Bash => WriteBash(map),
            CliCompletionShells.Zsh => WriteZsh(map),
            CliCompletionShells.Fish => WriteFish(map),
            CliCompletionShells.PowerShell => WritePowerShell(map),
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unsupported shell."),
        };

    }

    /// <summary>
    /// Every command path in canonical order, each with the subcommands and option spellings valid
    /// at that path. This is the one place the tree is flattened; each shell writer renders it.
    /// </summary>
    private static IReadOnlyList<CompletionNode> Flatten(CliSurfaceMap map)
    {

        List<CompletionNode> nodes =
        [
            new(
                string.Empty,
                [.. map.Commands.Select(static command => command.Name)],
                [.. GlobalSpellings(map).Where(IsDashPrefixed)],
                null,
                map.GlobalOptions),
        ];

        foreach (CliSurfaceCommand command in map.Commands.SelectMany(CliSurfaceBuilder.Flatten))
        {

            nodes.Add(
                new CompletionNode(
                    command.Path,
                    [
                        // A positional with a closed value set is completed exactly like a
                        // subcommand: after `arcanum completion `, `bash` is as valid a next word
                        // as `install`, and offering only one of them is the wrong half.
                        .. command.Commands
                            .Select(static child => child.Name)
                            .Concat(command.Arguments.SelectMany(static argument => argument.Values))
                            .Distinct(StringComparer.Ordinal),
                    ],
                    [
                        .. command.Options
                            .SelectMany(static option => option.Aliases.Append(option.Name))
                            .Concat(GlobalSpellings(map))
                            .Where(IsDashPrefixed)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(static spelling => spelling, StringComparer.Ordinal),
                    ],
                    command,
                    map.GlobalOptions));

        }

        return nodes;

    }

    /// <summary>
    /// System.CommandLine also accepts Windows-style <c>/?</c> and <c>/h</c> for help. Emitting a
    /// slash-prefixed token into a shell completion list is noise on POSIX and reads as a path, so
    /// only dash-prefixed spellings are offered. The parser still accepts the others.
    /// </summary>
    private static bool IsDashPrefixed(string spelling) =>
        spelling.StartsWith('-');

    private static IEnumerable<string> GlobalSpellings(CliSurfaceMap map) =>
        map.GlobalOptions
            .Where(static option => option.Recursive)
            .SelectMany(static option => option.Aliases.Append(option.Name));

    /// <summary>
    /// Options at a path that take a closed value set or a dynamic provider, for the shell to look
    /// up after the option token. Every accepted spelling is yielded, not just the canonical one:
    /// <c>-m</c> is as valid as <c>--model</c> and an operator who types the published short flag
    /// is owed the same suggestions.
    /// </summary>
    private static IEnumerable<(string Option, string Values, string? Provider)> ValueSources(
        CompletionNode node)
    {

        // Recursive root options are valid at every path but are not repeated in each command's own
        // option list, so their closed sets have to be contributed here or `--output-format <TAB>`
        // would offer nothing anywhere below the root.
        IEnumerable<CliSurfaceOption> options = node.Command is null
            ? node.GlobalOptions
            : node.Command.Options.Concat(node.GlobalOptions.Where(static option => option.Recursive));

        foreach (CliSurfaceOption option in options)
        {

            (string values, string? provider) = option.Values.Count > 0
                ? (string.Join(' ', option.Values), null)
                : (string.Empty, option.Completion);

            if (values.Length == 0 && provider is null)
            {

                continue;

            }

            foreach (string spelling in Spellings(option))
            {

                yield return (spelling, values, provider);

            }

        }

    }

    /// <summary>
    /// Every dash-prefixed spelling the parser accepts for an option, canonical name included, in
    /// the same order the option lists use.
    /// </summary>
    private static IEnumerable<string> Spellings(CliSurfaceOption option) =>
        option.Aliases
            .Append(option.Name)
            .Where(IsDashPrefixed)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static spelling => spelling, StringComparer.Ordinal);

    /// <summary>
    /// The dynamic source for a command's own positional, if it names a live resource. Keyed on the
    /// command path alone, because a positional has no preceding option token to key on — which is
    /// also why the shell has to know whether a positional has already been supplied before
    /// offering it.
    /// </summary>
    private static string? ArgumentProvider(CompletionNode node) =>
        node.Command?.Arguments
            .Select(static argument => argument.Completion)
            .FirstOrDefault(static completion => completion is not null);

    private static string WriteBash(CliSurfaceMap map)
    {

        StringBuilder builder = new();

        builder.Append(
            """
            # arcanum bash completion. Generated from the canonical command tree.
            # Install: arcanum completion install bash
            _arcanum_complete() {
              local cur prev path i words_seen extra
              COMPREPLY=()
              cur="${COMP_WORDS[COMP_CWORD]}"
              prev="${COMP_WORDS[COMP_CWORD-1]}"
              path=""
              extra=0
              for (( i=1; i < COMP_CWORD; i++ )); do
                case "${COMP_WORDS[i]}" in
                  -*) continue ;;
                esac
                if [[ -z "$path" ]]; then words_seen="${COMP_WORDS[i]}"; else words_seen="$path ${COMP_WORDS[i]}"; fi
                if [[ $extra -eq 0 && -n "$(_arcanum_children "$words_seen")$(_arcanum_options "$words_seen")" ]]; then
                  path="$words_seen"
                else
                  extra=$(( extra + 1 ))
                fi
              done

              local provider
              provider="$(_arcanum_value_provider "$path" "$prev")"
              if [[ -n "$provider" ]]; then
                local dynamic
                dynamic="$(_arcanum_dynamic "$provider")"
                COMPREPLY=( $(compgen -W "$dynamic" -- "$cur") )
                return 0
              fi

              local values
              values="$(_arcanum_values "$path" "$prev")"
              if [[ -n "$values" ]]; then
                COMPREPLY=( $(compgen -W "$values" -- "$cur") )
                return 0
              fi

              # A resource positional is offered alongside this path's own words, because an option
              # is as valid a next token as the name. `extra` is the count of words the walk could
              # not fold into the path, so a non-zero count means the positional is already typed.
              local argument_provider argument_values
              argument_values=""
              if [[ $extra -eq 0 ]]; then
                argument_provider="$(_arcanum_argument_provider "$path")"
                if [[ -n "$argument_provider" ]]; then
                  argument_values="$(_arcanum_dynamic "$argument_provider")"
                fi
              fi

              COMPREPLY=( $(compgen -W "$(_arcanum_children "$path") $(_arcanum_options "$path") $argument_values" -- "$cur") )
            }

            # Dynamic resources come from the running host only. A stopped or slow host produces no
            # suggestions and no shell noise, and never starts the host.

            """);

        builder.Append(
            """
            _arcanum_dynamic() {
              arcanum completion resolve "$1" 2>/dev/null || true
            }

            """);

        AppendBashLookup(builder, "_arcanum_children", Flatten(map).Select(static node => (node.Path, string.Join(' ', node.Children))));

        AppendBashLookup(builder, "_arcanum_options", Flatten(map).Select(static node => (node.Path, string.Join(' ', node.Options))));

        AppendBashValueLookup(builder, "_arcanum_values", Flatten(map), dynamicSource: false);

        AppendBashValueLookup(builder, "_arcanum_value_provider", Flatten(map), dynamicSource: true);

        AppendBashLookup(
            builder,
            "_arcanum_argument_provider",
            Flatten(map).Select(static node => (node.Path, ArgumentProvider(node) ?? string.Empty)));

        builder.Append("complete -F _arcanum_complete arcanum\n");

        return builder.ToString();

    }

    private static void AppendBashLookup(
        StringBuilder builder,
        string function,
        IEnumerable<(string Path, string Values)> entries)
    {

        builder.Append(function).Append("() {\n  case \"$1\" in\n");

        foreach ((string path, string values) in entries)
        {

            if (values.Length == 0)
            {

                continue;

            }

            builder
                .Append("    ")
                .Append(BashPattern(path))
                .Append(") echo \"")
                .Append(values)
                .Append("\" ;;\n");

        }

        builder.Append("  esac\n}\n\n");

    }

    private static void AppendBashValueLookup(
        StringBuilder builder,
        string function,
        IReadOnlyList<CompletionNode> nodes,
        bool dynamicSource)
    {

        builder.Append(function).Append("() {\n  case \"$1|$2\" in\n");

        foreach (CompletionNode node in nodes)
        {

            foreach ((string option, string values, string? provider) in ValueSources(node))
            {

                string payload = dynamicSource ? provider ?? string.Empty : values;

                if (payload.Length == 0)
                {

                    continue;

                }

                builder
                    .Append("    \"")
                    .Append(node.Path)
                    .Append('|')
                    .Append(option)
                    .Append("\") echo \"")
                    .Append(payload)
                    .Append("\" ;;\n");

            }

        }

        builder.Append("  esac\n}\n\n");

    }

    private static string BashPattern(string path) =>
        path.Length == 0 ? "\"\"" : $"\"{path}\"";

    private static string WriteZsh(CliSurfaceMap map)
    {

        StringBuilder builder = new();

        builder.Append(
            """
            #compdef arcanum
            # arcanum zsh completion. Generated from the canonical command tree.
            # Install: arcanum completion install zsh

            _arcanum() {
              local -a path_words argument_values
              local path="" word provider values argument_provider
              integer i extra=0
              for (( i = 2; i < CURRENT; i++ )); do
                word="${words[i]}"
                [[ "$word" == -* ]] && continue
                if [[ -z "$path" ]]; then path_words="$word"; else path_words="$path $word"; fi
                if (( extra == 0 )) && [[ -n "${_arcanum_children[$path_words]}${_arcanum_options[$path_words]}" ]]; then
                  path="$path_words"
                else
                  (( extra += 1 ))
                fi
              done

              provider="${_arcanum_providers[$path|${words[CURRENT-1]}]}"
              if [[ -n "$provider" ]]; then
                # Silent on an unavailable host; static completion still works.
                compadd -- ${=$(arcanum completion resolve "$provider" 2>/dev/null)}
                return
              fi

              values="${_arcanum_values[$path|${words[CURRENT-1]}]}"
              if [[ -n "$values" ]]; then
                compadd -- ${=values}
                return
              fi

              # A resource positional is offered alongside this path's own words: an option is as
              # valid a next token as the name. `extra` counts the words the walk could not fold
              # into the path, so a non-zero count means the positional is already supplied.
              if (( extra == 0 )); then
                argument_provider="${_arcanum_arguments[$path]}"
                if [[ -n "$argument_provider" ]]; then
                  argument_values=( ${=$(arcanum completion resolve "$argument_provider" 2>/dev/null)} )
                fi
              fi

              compadd -- ${=_arcanum_children[$path]} ${=_arcanum_options[$path]} $argument_values
            }


            """);

        IReadOnlyList<CompletionNode> nodes = Flatten(map);

        AppendZshMap(builder, "_arcanum_children", nodes.Select(static node => (node.Path, string.Join(' ', node.Children))));

        AppendZshMap(builder, "_arcanum_options", nodes.Select(static node => (node.Path, string.Join(' ', node.Options))));

        AppendZshValueMap(builder, "_arcanum_values", nodes, dynamicSource: false);

        AppendZshValueMap(builder, "_arcanum_providers", nodes, dynamicSource: true);

        AppendZshMap(
            builder,
            "_arcanum_arguments",
            nodes.Select(static node => (node.Path, ArgumentProvider(node) ?? string.Empty)));

        // Installed as `~/.zfunc/_arcanum` and autoloaded, so the whole file is the body of
        // `_arcanum`: the maps and this line sit after the definition, which is exactly what rules
        // out zsh's ksh-style "source the file, then call the function" shortcut. Without the
        // self-call the invocation that triggered the autoload would define the function, add no
        // candidates, and return — a dead first TAB in every new shell. `compdef` is the right call
        // for the sourced install instead, and only exists once compinit has run.
        builder.Append(
            """
            if [ "$funcstack[1]" = "_arcanum" ]; then
              _arcanum "$@"
            else
              (( $+functions[compdef] )) && compdef _arcanum arcanum
            fi

            """);

        return builder.ToString();

    }

    private static void AppendZshMap(
        StringBuilder builder,
        string name,
        IEnumerable<(string Path, string Values)> entries)
    {

        builder.Append("typeset -gA ").Append(name).Append("\n").Append(name).Append("=(\n");

        foreach ((string path, string values) in entries)
        {

            if (values.Length == 0)
            {

                continue;

            }

            builder
                .Append("  ['")
                .Append(path)
                .Append("']='")
                .Append(values)
                .Append("'\n");

        }

        builder.Append(")\n\n");

    }

    private static void AppendZshValueMap(
        StringBuilder builder,
        string name,
        IReadOnlyList<CompletionNode> nodes,
        bool dynamicSource)
    {

        builder.Append("typeset -gA ").Append(name).Append("\n").Append(name).Append("=(\n");

        foreach (CompletionNode node in nodes)
        {

            foreach ((string option, string values, string? provider) in ValueSources(node))
            {

                string payload = dynamicSource ? provider ?? string.Empty : values;

                if (payload.Length == 0)
                {

                    continue;

                }

                builder
                    .Append("  ['")
                    .Append(node.Path)
                    .Append('|')
                    .Append(option)
                    .Append("']='")
                    .Append(payload)
                    .Append("'\n");

            }

        }

        builder.Append(")\n\n");

    }

    private static string WriteFish(CliSurfaceMap map)
    {

        StringBuilder builder = new();

        IReadOnlyList<CompletionNode> nodes = Flatten(map);

        builder.Append(
            """
            # arcanum fish completion. Generated from the canonical command tree.
            # Install: arcanum completion install fish

            # Every command path in the tree. The walk below stops at the first word that is not one
            # of them, the way bash, zsh and PowerShell all stop: concatenating every non-dash token
            # instead lets a positional value — or any option's value — build a path no generated
            # condition matches, and since every `complete` line carries `-f`, fish then offers
            # filenames where the command's own flags belong.
            set -g __arcanum_paths

            """);

        foreach (CompletionNode node in nodes.Where(static node => node.Path.Length > 0))
        {

            builder.Append("set -a __arcanum_paths '").Append(node.Path).Append("'\n");

        }

        builder.Append(
            """

            function __arcanum_walk
              set -l parts (commandline -opc)
              set -l path ""
              set -l extra 0
              for word in $parts[2..-1]
                string match -q -- '-*' $word; and continue
                set -l candidate $word
                if test -n "$path"
                  set candidate "$path $word"
                end
                if test $extra -eq 0; and contains -- $candidate $__arcanum_paths
                  set path $candidate
                else
                  set extra (math $extra + 1)
                end
              end
              if test "$argv[1]" = "extra"
                echo $extra
              else
                echo $path
              end
            end

            function __arcanum_path
              __arcanum_walk path
            end

            # True until this command's positional has been supplied. A resource name is offered
            # once and then gives way to the options that may follow it.
            function __arcanum_positional_open
              test (__arcanum_walk extra) -eq 0
            end

            # Dynamic resources are read from the running host only; an unavailable host yields
            # nothing and never triggers a host start.
            function __arcanum_resolve
              arcanum completion resolve $argv[1] 2>/dev/null
            end


            """);

        foreach (CompletionNode node in nodes)
        {

            // fish substitutes a command only outside double quotes or through the `$(…)` form
            // (3.4+). Written as `test "(__arcanum_path)" = "…"` the left side is the literal text
            // `(__arcanum_path)`, so every condition below the root is false and the installed
            // script silently offers nothing. Quoting is not optional either: a path is a
            // multi-word string, and an unquoted substitution of an empty path leaves `test` with
            // no left operand at all.
            string condition = node.Path.Length == 0
                ? "test -z (__arcanum_path)"
                : $"test \"$(__arcanum_path)\" = \"{node.Path}\"";

            foreach (string child in node.Children)
            {

                builder
                    .Append("complete -c arcanum -f -n '")
                    .Append(condition)
                    .Append("' -a '")
                    .Append(child)
                    .Append("'\n");

            }

            if (ArgumentProvider(node) is { } argumentProvider)
            {

                // A positional has no preceding option token to key on, so the condition is the
                // command path plus "the positional is still unsupplied" — otherwise the resource
                // list would be offered again in the position after it.
                builder
                    .Append("complete -c arcanum -f -n '")
                    .Append(condition)
                    .Append("; and __arcanum_positional_open' -a '(__arcanum_resolve ")
                    .Append(argumentProvider)
                    .Append(")'\n");

            }

            List<(string Option, string Values, string? Provider)> valueSources = [.. ValueSources(node)];

            HashSet<string> carriesValues = [.. valueSources.Select(static source => source.Option)];

            foreach (string option in node.Options.Where(static option => option.StartsWith("--", StringComparison.Ordinal)))
            {

                // Registered once. An option declared both as a flag here and as taking a parameter
                // below is a contradiction, and the parameter declaration is the true one.
                if (carriesValues.Contains(option))
                {

                    continue;

                }

                builder
                    .Append("complete -c arcanum -f -n '")
                    .Append(condition)
                    .Append("' -l '")
                    .Append(option.TrimStart('-'))
                    .Append("'\n");

            }

            foreach ((string option, string values, string? provider) in valueSources)
            {

                // `-x` is `-r -f`: fish routes an `-a` list to the token after an option only when
                // the option is declared as requiring a parameter. Without it the list is
                // unreachable by any spelling, including `--option=value`.
                if (!option.StartsWith("--", StringComparison.Ordinal))
                {

                    continue;

                }

                builder
                    .Append("complete -c arcanum -x -n '")
                    .Append(condition)
                    .Append("' -l '")
                    .Append(option.TrimStart('-'))
                    .Append("' -a '")
                    .Append(provider is null ? values : $"(__arcanum_resolve {provider})")
                    .Append("'\n");

            }

        }

        return builder.ToString();

    }

    private static string WritePowerShell(CliSurfaceMap map)
    {

        StringBuilder builder = new();

        builder.Append(
            """
            # arcanum PowerShell completion. Generated from the canonical command tree.
            # Install: arcanum completion install powershell

            $script:ArcanumChildren = @{}
            $script:ArcanumOptions = @{}
            $script:ArcanumValues = @{}
            $script:ArcanumProviders = @{}
            $script:ArcanumArguments = @{}


            """);

        IReadOnlyList<CompletionNode> nodes = Flatten(map);

        AppendPowerShellMap(builder, "ArcanumChildren", nodes.Select(static node => (node.Path, string.Join(' ', node.Children))));

        AppendPowerShellMap(builder, "ArcanumOptions", nodes.Select(static node => (node.Path, string.Join(' ', node.Options))));

        AppendPowerShellValueMap(builder, "ArcanumValues", nodes, dynamicSource: false);

        AppendPowerShellValueMap(builder, "ArcanumProviders", nodes, dynamicSource: true);

        AppendPowerShellMap(
            builder,
            "ArcanumArguments",
            nodes.Select(static node => (node.Path, ArgumentProvider(node) ?? string.Empty)));

        builder.Append(
            """

            Register-ArgumentCompleter -Native -CommandName arcanum -ScriptBlock {
              param($wordToComplete, $commandAst, $cursorPosition)

              # A quoted element keeps its quotes in the extent text, and the map keys are unquoted.
              $tokens = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $_.Value } else { $_.Extent.Text }
              })

              # CommandElements includes the word under the cursor, unlike bash's COMP_WORDS[COMP_CWORD-1]
              # and zsh's words[CURRENT-1]. Walking it unfiltered makes a partially typed value its own
              # preceding token, so `--model gp<TAB>` looks up `run|gp` and resolves nothing.
              $walk = if ($wordToComplete) { @($tokens | Select-Object -First ([Math]::Max(0, $tokens.Count - 1))) } else { $tokens }

              $path = ''
              $extra = 0
              foreach ($token in $walk) {
                if ($token.StartsWith('-')) { continue }
                $candidate = if ($path) { "$path $token" } else { $token }
                if ($extra -eq 0 -and ($script:ArcanumChildren.ContainsKey($candidate) -or $script:ArcanumOptions.ContainsKey($candidate))) {
                  $path = $candidate
                } else {
                  $extra++
                }
              }

              $previous = if ($walk.Count -ge 1) { $walk[$walk.Count - 1] } else { '' }
              $key = "$path|$previous"

              if ($script:ArcanumProviders.ContainsKey($key)) {
                # Silent when the host is unavailable; never starts it.
                $resolved = & arcanum completion resolve $script:ArcanumProviders[$key] 2>$null
                if ($LASTEXITCODE -eq 0 -and $resolved) {
                  return $resolved -split '\s+' |
                    Where-Object { $_ -like "$wordToComplete*" } |
                    ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
                }
                return @()
              }

              if ($script:ArcanumValues.ContainsKey($key)) {
                return $script:ArcanumValues[$key] -split ' ' |
                  Where-Object { $_ -like "$wordToComplete*" } |
                  ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
              }

              $candidates = @()
              if ($script:ArcanumChildren.ContainsKey($path)) { $candidates += $script:ArcanumChildren[$path] -split ' ' }
              if ($script:ArcanumOptions.ContainsKey($path)) { $candidates += $script:ArcanumOptions[$path] -split ' ' }

              # A resource positional is offered alongside this path's own words, and only while it
              # is still unsupplied: $extra counts the words the walk could not fold into the path.
              if ($extra -eq 0 -and $script:ArcanumArguments.ContainsKey($path)) {
                $resolved = & arcanum completion resolve $script:ArcanumArguments[$path] 2>$null
                if ($LASTEXITCODE -eq 0 -and $resolved) { $candidates += $resolved -split '\s+' }
              }

              $candidates |
                Where-Object { $_ -and $_ -like "$wordToComplete*" } |
                ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_) }
            }

            """);

        return builder.ToString();

    }

    private static void AppendPowerShellMap(
        StringBuilder builder,
        string name,
        IEnumerable<(string Path, string Values)> entries)
    {

        foreach ((string path, string values) in entries)
        {

            if (values.Length == 0)
            {

                continue;

            }

            builder
                .Append("$script:")
                .Append(name)
                .Append("['")
                .Append(path)
                .Append("'] = '")
                .Append(values)
                .Append("'\n");

        }

        builder.Append('\n');

    }

    private static void AppendPowerShellValueMap(
        StringBuilder builder,
        string name,
        IReadOnlyList<CompletionNode> nodes,
        bool dynamicSource)
    {

        foreach (CompletionNode node in nodes)
        {

            foreach ((string option, string values, string? provider) in ValueSources(node))
            {

                string payload = dynamicSource ? provider ?? string.Empty : values;

                if (payload.Length == 0)
                {

                    continue;

                }

                builder
                    .Append("$script:")
                    .Append(name)
                    .Append("['")
                    .Append(node.Path)
                    .Append('|')
                    .Append(option)
                    .Append("'] = '")
                    .Append(payload)
                    .Append("'\n");

            }

        }

        builder.Append('\n');

    }

    private sealed record CompletionNode(
        string Path,
        IReadOnlyList<string> Children,
        IReadOnlyList<string> Options,
        CliSurfaceCommand? Command,
        IReadOnlyList<CliSurfaceOption> GlobalOptions);

}
