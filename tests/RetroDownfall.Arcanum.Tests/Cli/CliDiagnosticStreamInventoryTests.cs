using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The automation contract splits the two streams: stdout carries the payload and stderr carries the
/// diagnostics, so <c>arcanum ... &gt; data.json</c> captures data and <c>--json</c> puts exactly one
/// document on stdout.
/// </summary>
/// <remarks>
/// An inventory assertion rather than a behavior test, because the failure it prevents is a new call
/// site. The global <see cref="Spectre.Console.AnsiConsole"/> is built over <c>Console.Out</c>, so a
/// command that renders a failure through it writes the failure onto the payload stream — the
/// diagnostic lands in the data file and stderr stays empty. There are nearly two hundred of these
/// sites across the command tree and no behavior test reaches most of them, so the rule is pinned
/// where a new one is written rather than where it would eventually be noticed.
///
/// <para>Scoped to themed <em>error</em> renders. Tables, panels and the ordinary informational lines
/// a command prints are payload and belong exactly where they are.</para>
/// </remarks>
public sealed class CliDiagnosticStreamInventoryTests
{

    /// <summary>
    /// <c>AnsiConsole.MarkupLine(</c> followed — on the same line or the next one — by a themed error
    /// markup call. Both spellings of the palette parameter appear across the command tree.
    /// </summary>
    private static readonly Regex ErrorOnPayloadStream = new(
        @"AnsiConsole\.MarkupLine\(\s*(themePalette|palette|_palette)\.(ErrorMarkup|ErrorLabelMarkup)\b",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void No_command_renders_a_themed_error_through_the_payload_stream_console()
    {

        List<string> offenders = [];

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {

            if (!source.RelativePath.Contains(
                    Path.Combine("RetroDownfall.Arcanum.Cli", "Commands"),
                    StringComparison.Ordinal))
            {

                continue;

            }

            foreach (Match match in ErrorOnPayloadStream.Matches(source.Text))
            {

                offenders.Add($"{source.RelativePath}: {match.Value}");

            }

        }

        Assert.True(
            offenders.Count == 0,
            "Route these through CliErrorOutput.WriteMarkupLine so the diagnostic reaches stderr:\n"
            + string.Join('\n', offenders));

    }

}
