using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands.Tower;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    /// <summary>
    /// The <c>memory covenant</c> subgroup: the operator's own standing agreement.
    /// </summary>
    /// <remarks>
    /// A subgroup of <c>memory</c> rather than a top-level verb, because the Covenant is one memory
    /// source among several and an operator looking for "what does Arcanum remember" should find it
    /// where the others already are.
    ///
    /// <para><c>set</c> takes no content argument. Authored content arrives through <c>--file</c> or
    /// piped standard input, so a preference never lands in shell history or in the process list of a
    /// shared machine.</para>
    /// </remarks>
    private static Command BuildCovenant(IServiceProvider sp)
    {

        CovenantCommands handler = sp.GetRequiredService<CovenantCommands>();

        Command covenant = new(
            "covenant",
            "Read and write the standing agreement between this operator and the agent.");

        Option<Guid?> campaign = new("--campaign", "-C")
        {
            Description = "Scope to one Campaign by identity. Omit for the installation-wide Global scope.",
        };

        Command set = new("set", "Write one standing preference the agent will honor on later turns.");

        Argument<string> setKey = new("key") { Description = "The preference key, for example preference.builds." };

        Option<string?> file = new("--file", "-f")
        {
            Description = "Read the preference text from this file. Omit to read from piped standard input.",
        };

        Option<long> expectedRevision = new("--expected-revision")
        {
            Description = "The revision this write expects to replace. Zero creates.",
        };

        Option<bool> reactivate = new("--reactivate")
        {
            Description = "Reinstate a key that was previously retired.",
        };

        set.Add(setKey);

        set.Add(campaign);

        set.Add(file);

        set.Add(expectedRevision);

        set.Add(reactivate);

        set.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Set(
                    pr.GetValue(setKey)!,
                    pr.GetValue(campaign),
                    pr.GetValue(file),
                    pr.GetValue(expectedRevision),
                    pr.GetValue(reactivate),
                    ct).ConfigureAwait(false));

        Command list = new("list", "List the standing preferences in one scope.");

        Option<bool> allScopes = new("--all-scopes")
        {
            Description = "List Global and every Campaign scope together.",
        };

        Option<string?> listLane = new("--lane")
        {
            Description = "Confirmed (operator-authored) or Proposed (agent-suggested).",
        };

        list.Add(campaign);

        list.Add(allScopes);

        list.Add(listLane);

        list.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.List(
                    pr.GetValue(campaign),
                    pr.GetValue(allScopes),
                    ParseLane(pr.GetValue(listLane)),
                    ct).ConfigureAwait(false));

        Command show = new("show", "Show both lane heads for one preference key.");

        Argument<string> showKey = new("key") { Description = "The preference key." };

        show.Add(showKey);

        show.Add(campaign);

        show.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Show(
                    pr.GetValue(showKey)!,
                    pr.GetValue(campaign),
                    ct).ConfigureAwait(false));

        Command retire = new("retire", "Retire one preference so it is honored on no later turn.");

        Argument<string> retireKey = new("key") { Description = "The preference key." };

        Option<string?> retireLane = new("--lane")
        {
            Description = "Confirmed or Proposed. Defaults to Confirmed.",
        };

        Option<long> retireRevision = new("--expected-revision")
        {
            Description = "The exact lane revision being retired.",
        };

        retire.Add(retireKey);

        retire.Add(campaign);

        retire.Add(retireLane);

        retire.Add(retireRevision);

        retire.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Retire(
                    pr.GetValue(retireKey)!,
                    pr.GetValue(campaign),
                    ParseLane(pr.GetValue(retireLane)) ?? CovenantLane.Confirmed,
                    pr.GetValue(retireRevision),
                    ct).ConfigureAwait(false));

        covenant.Add(set);

        covenant.Add(list);

        covenant.Add(show);

        covenant.Add(retire);

        return covenant;

    }

    /// <summary>
    /// Reads a lane name, treating anything unrecognized as unspecified.
    /// </summary>
    /// <remarks>
    /// A misspelled lane becomes "no lane filter" rather than an error here, because the server
    /// validates the request it actually receives; refusing locally would put a second, drifting copy
    /// of the lane vocabulary in the CLI.
    /// </remarks>
    private static CovenantLane? ParseLane(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out CovenantLane lane) ? lane : null;

}
