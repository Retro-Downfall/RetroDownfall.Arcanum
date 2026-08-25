using System.CommandLine;

using System.CommandLine.Parsing;

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

        Option<CovenantLane?> listLane = LaneOption(
            "Confirmed (operator-authored) or Proposed (agent-suggested). Omit for both.");

        Option<CovenantLifecycle?> listLifecycle = new("--lifecycle")
        {
            Description = "Set (the default), Retired, or Any.",
            CustomParser = static result => Parse<CovenantLifecycle>(result, "Covenant lifecycle"),
        };

        list.Add(campaign);

        list.Add(allScopes);

        list.Add(listLane);

        list.Add(listLifecycle);

        list.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.List(
                    pr.GetValue(campaign),
                    pr.GetValue(allScopes),
                    pr.GetValue(listLane),
                    pr.GetValue(listLifecycle) ?? CovenantLifecycle.Set,
                    ct).ConfigureAwait(false));

        Command show = new("show", "Show both lane heads for one preference key.");

        Argument<string> showKey = new("key") { Description = "The preference key." };

        Option<bool> history = new("--history")
        {
            Description = "Also print each lane's version history, newest revision first.",
        };

        show.Add(showKey);

        show.Add(campaign);

        show.Add(history);

        show.SetAction(
            async (ParseResult pr, CancellationToken ct) =>
                await handler.Show(
                    pr.GetValue(showKey)!,
                    pr.GetValue(campaign),
                    pr.GetValue(history),
                    ct).ConfigureAwait(false));

        Command retire = new("retire", "Retire one preference so it is honored on no later turn.");

        Argument<string> retireKey = new("key") { Description = "The preference key." };

        Option<CovenantLane?> retireLane = LaneOption("Confirmed or Proposed. Defaults to Confirmed.");

        Option<long> retireRevision = new("--expected-revision")
        {
            Description = "The exact lane revision being retired.",

            // Required, because zero is not a value a retirement can ever mean. A live head is never
            // at revision zero, so an omitted flag would send the one number guaranteed to be refused
            // — after the operator had already approved the screen describing the write.
            Required = true,
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
                    pr.GetValue(retireLane) ?? CovenantLane.Confirmed,
                    pr.GetValue(retireRevision),
                    ct).ConfigureAwait(false));

        covenant.Add(set);

        covenant.Add(list);

        covenant.Add(show);

        covenant.Add(retire);

        return covenant;

    }

    private static Option<CovenantLane?> LaneOption(string description) =>
        new("--lane")
        {
            Description = description,
            CustomParser = static result => Parse<CovenantLane>(result, "Covenant lane"),
        };

    /// <summary>
    /// Reads one enum-valued option, failing the command on a value the vocabulary does not contain.
    /// </summary>
    /// <remarks>
    /// An unrecognized value used to become absence, and absence is a different instruction on every
    /// verb that reads one: <c>list</c> treats it as no filter, and <c>retire</c> coalesced it to the
    /// operator-authored Confirmed lane — so <c>retire my.key --lane propsed</c> sent a well-formed
    /// request that retired the wrong lane and reported success. Refusing here puts no second copy of
    /// the vocabulary in the CLI, because the names are read off the enum itself.
    ///
    /// <para>Absence still means absence: an option nobody typed carries no tokens, and the caller's
    /// own default applies.</para>
    /// </remarks>
    private static TValue? Parse<TValue>(ArgumentResult result, string subject)
        where TValue : struct, Enum
    {

        if (result.Tokens.Count == 0)
        {

            return null;

        }

        string value = result.Tokens[0].Value;

        // Enum.TryParse accepts any numeric string, including one naming no member at all, so the
        // defined check is what makes this a vocabulary rather than a cast.
        if (Enum.TryParse(value, ignoreCase: true, out TValue parsed) && Enum.IsDefined(parsed))
        {

            return parsed;

        }

        result.AddError(
            $"'{value}' is not a {subject}. Valid values: {string.Join(", ", Enum.GetNames<TValue>())}.");

        return null;

    }

}
