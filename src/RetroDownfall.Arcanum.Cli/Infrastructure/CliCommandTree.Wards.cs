using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.Wards;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildWard(IServiceProvider sp)
    {
        WardCommands handler = sp.GetRequiredService<WardCommands>();
        Command ward = new("ward", "Retained Ward record compatibility API (requires arcanum serve).");

        Command list = new("list", "List active compatibility wards.");
        list.SetAction(async (ParseResult pr, CancellationToken ct) => await handler.List(ct).ConfigureAwait(false));
        ward.Add(list);

        Command show = new("show", "Show compatibility ward detail.");
        Argument<string> showId = new("id") { Description = "Ward ID." };
        show.Add(showId);
        show.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(showId)!, ct).ConfigureAwait(false));
        ward.Add(show);

        Command resolve = new("resolve", "Resolve a compatibility ward.");
        Argument<string> resolveId = new("id") { Description = "Ward ID." };
        Option<bool> resolveAllow = new("--allow") { Description = "Allow the warded tool call to proceed." };
        Option<bool> resolveDeny = new("--deny") { Description = "Deny the warded tool call." };
        Option<string?> resolveReason = new("--reason") { Description = "Optional reason recorded with the resolution." };
        resolve.Add(resolveId); resolve.Add(resolveAllow); resolve.Add(resolveDeny); resolve.Add(resolveReason);
        resolve.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Resolve(
                pr.GetValue(resolveId)!,
                pr.GetValue(resolveAllow),
                pr.GetValue(resolveDeny),
                pr.GetValue(resolveReason),
                ct).ConfigureAwait(false));
        ward.Add(resolve);

        return ward;
    }

}
