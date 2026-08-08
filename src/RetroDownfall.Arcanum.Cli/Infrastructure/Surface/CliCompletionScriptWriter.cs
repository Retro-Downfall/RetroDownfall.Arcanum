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
    /// Option spellings at a path that take a closed value set or a dynamic provider, rendered as
    /// <c>option=values</c> pairs the shell can look up after <c>--option </c>.
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

            if (option.Values.Count > 0)
            {

                yield return (option.Name, string.Join(' ', option.Values), null);

            }
            else if (option.Completion is not null)
            {

                yield return (option.Name, string.Empty, option.Completion);

            }

        }

    }

    private static string WriteBash(CliSurfaceMap map)
    {

        StringBuilder builder = new();

        builder.Append(
            """
            # arcanum bash completion. Generated from the canonical command tree.
            # Install: arcanum completion install bash
            _arcanum_complete() {
              local cur prev path i words_seen
              COMPREPLY=()
              cur="${COMP_WORDS[COMP_CWORD]}"
              prev="${COMP_WORDS[COMP_CWORD-1]}"
              path=""
              for (( i=1; i < COMP_CWORD; i++ )); do
                case "${COMP_WORDS[i]}" in
                  -*) continue ;;
                esac
                if [[ -z "$path" ]]; then words_seen="${COMP_WORDS[i]}"; else words_seen="$path ${COMP_WORDS[i]}"; fi
                case "$(_arcanum_children "$words_seen")$(_arcanum_options "$words_seen")" in
                  "") break ;;
                  *) path="$words_seen" ;;
                esac
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

              COMPREPLY=( $(compgen -W "$(_arcanum_children "$path") $(_arcanum_options "$path")" -- "$cur") )
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
              local -a path_words
              local path="" word provider values
              integer i
              for (( i = 2; i < CURRENT; i++ )); do
                word="${words[i]}"
                [[ "$word" == -* ]] && continue
                if [[ -z "$path" ]]; then path_words="$word"; else path_words="$path $word"; fi
                if [[ -n "${_arcanum_children[$path_words]}${_arcanum_options[$path_words]}" ]]; then
                  path="$path_words"
                else
                  break
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

              compadd -- ${=_arcanum_children[$path]} ${=_arcanum_options[$path]}
            }


            """);

        IReadOnlyList<CompletionNode> nodes = Flatten(map);

        AppendZshMap(builder, "_arcanum_children", nodes.Select(static node => (node.Path, string.Join(' ', node.Children))));

        AppendZshMap(builder, "_arcanum_options", nodes.Select(static node => (node.Path, string.Join(' ', node.Options))));

        AppendZshValueMap(builder, "_arcanum_values", nodes, dynamicSource: false);

        AppendZshValueMap(builder, "_arcanum_providers", nodes, dynamicSource: true);

        builder.Append("compdef _arcanum arcanum\n");

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

        builder.Append(
            """
            # arcanum fish completion. Generated from the canonical command tree.
            # Install: arcanum completion install fish

            function __arcanum_path
              set -l parts (commandline -opc)
              set -l path ""
              for word in $parts[2..-1]
                string match -q -- '-*' $word; and continue
                if test -z "$path"
                  set path $word
                else
                  set path "$path $word"
                end
              end
              echo $path
            end

            # Dynamic resources are read from the running host only; an unavailable host yields
            # nothing and never triggers a host start.
            function __arcanum_resolve
              arcanum completion resolve $argv[1] 2>/dev/null
            end


            """);

        foreach (CompletionNode node in Flatten(map))
        {

            string condition = node.Path.Length == 0
                ? "test -z (__arcanum_path)"
                : $"test \"(__arcanum_path)\" = \"{node.Path}\"";

            foreach (string child in node.Children)
            {

                builder
                    .Append("complete -c arcanum -f -n '")
                    .Append(condition)
                    .Append("' -a '")
                    .Append(child)
                    .Append("'\n");

            }

            foreach (string option in node.Options.Where(static option => option.StartsWith("--", StringComparison.Ordinal)))
            {

                builder
                    .Append("complete -c arcanum -f -n '")
                    .Append(condition)
                    .Append("' -l '")
                    .Append(option.TrimStart('-'))
                    .Append("'\n");

            }

            foreach ((string option, string values, string? provider) in ValueSources(node))
            {

                builder
                    .Append("complete -c arcanum -f -n '")
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


            """);

        IReadOnlyList<CompletionNode> nodes = Flatten(map);

        AppendPowerShellMap(builder, "ArcanumChildren", nodes.Select(static node => (node.Path, string.Join(' ', node.Children))));

        AppendPowerShellMap(builder, "ArcanumOptions", nodes.Select(static node => (node.Path, string.Join(' ', node.Options))));

        AppendPowerShellValueMap(builder, "ArcanumValues", nodes, dynamicSource: false);

        AppendPowerShellValueMap(builder, "ArcanumProviders", nodes, dynamicSource: true);

        builder.Append(
            """

            Register-ArgumentCompleter -Native -CommandName arcanum -ScriptBlock {
              param($wordToComplete, $commandAst, $cursorPosition)

              $tokens = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.ToString() })
              $path = ''
              foreach ($token in $tokens) {
                if ($token.StartsWith('-')) { continue }
                $candidate = if ($path) { "$path $token" } else { $token }
                if ($script:ArcanumChildren.ContainsKey($candidate) -or $script:ArcanumOptions.ContainsKey($candidate)) {
                  $path = $candidate
                } else {
                  break
                }
              }

              $previous = if ($tokens.Count -ge 1) { $tokens[$tokens.Count - 1] } else { '' }
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
