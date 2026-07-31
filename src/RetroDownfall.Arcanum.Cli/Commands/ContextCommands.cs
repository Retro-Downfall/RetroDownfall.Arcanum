using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ContextCommands(
    ICliContextService context,
    IConsoleDispatcher dispatcher)
{

    public async Task<int> Use(
        CliContextScope scope,
        string identifier,
        CancellationToken cancellationToken)
    {

        CliContextMutationResult result = await context
            .SelectAsync(scope, identifier, cancellationToken)
            .ConfigureAwait(false);

        WriteMutation(result);

        return (int)result.ExitCode;

    }

    public int Clear(CliContextScope scope)
    {

        CliContextMutationResult result = context.Clear(scope);

        WriteMutation(result);

        return (int)result.ExitCode;

    }

    public int InvalidClearScope(string? scope)
    {

        CliContextMutationResult result = CliContextMutationResult.Failure(
            $"Unknown context scope '{scope}'. Expected campaign, workspace, model, or session.");

        WriteMutation(result);

        return (int)result.ExitCode;

    }

    public async Task<int> Current(CancellationToken cancellationToken)
    {

        CliContextStatusPayload status = await context
            .GetCurrentAsync(
                CliInvocationContext.Current.NoContext,
                cancellationToken)
            .ConfigureAwait(false);

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                status,
                CliJsonContext.Default.CliContextStatusPayload);

            return (int)CliExitCode.Success;

        }

        dispatcher.WritePayload(
            $"Campaign:  {status.Campaign.Value} ({status.Campaign.Source})");

        dispatcher.WritePayload(
            $"Workspace: {status.Workspace.Value} ({status.Workspace.Source})");

        dispatcher.WritePayload(
            $"Model:     {status.Model.Value} ({status.Model.Source})");

        dispatcher.WritePayload(
            $"Session:   {status.Session.Value} ({status.Session.Source})");

        dispatcher.WritePayload($"State:     {status.StateFile}");

        foreach (string warning in status.Warnings)
        {

            dispatcher.WriteDiagnostic("Warning: " + warning);

        }

        return (int)CliExitCode.Success;

    }

    private void WriteMutation(CliContextMutationResult result)
    {

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                result,
                CliJsonContext.Default.CliContextMutationResult);

            return;

        }

        if (result.IsSuccess)
        {

            dispatcher.WritePayload(result.Message);

        }
        else
        {

            dispatcher.WriteDiagnostic(result.Message);

        }

    }

}
