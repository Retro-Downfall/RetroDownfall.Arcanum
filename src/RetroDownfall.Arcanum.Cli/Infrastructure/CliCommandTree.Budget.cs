using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildBudget(IServiceProvider serviceProvider)
    {
        DeferredHandler<BudgetCommands> handler = new(serviceProvider);

        Command budget = new(
            "budget",
            "Show today's spend against the daily budget, separating local from delegated (A2A) cost.");

        budget.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Value.Show(cancellationToken).ConfigureAwait(false));

        return budget;
    }
}
