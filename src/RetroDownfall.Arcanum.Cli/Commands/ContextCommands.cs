using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ContextCommands(
    ICliContextService context,
    IConsoleDispatcher dispatcher,
    ArcanumApiClient apiClient,
    ICliInferenceContextResolver contextResolver)
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

    public async Task<int> Preview(

        ContextPreviewView view,

        string prompt,

        bool showContent,

        bool noRetrieval,

        string? campaign,

        string? workspace,

        string? model,

        string? session,

        CancellationToken cancellationToken)

    {

        string invocationDirectory = Environment.CurrentDirectory;

        CliInferenceContextResult resolved = await contextResolver

            .ResolveAsync(

                new CliInferenceContextRequest(

                    campaign,

                    workspace,

                    model,

                    session,

                    invocationDirectory,

                    CliInvocationContext.Current.NoContext,

                    NewSession: false),

                cancellationToken)

            .ConfigureAwait(false);

        if (!resolved.IsSuccess)

        {

            if (resolved.IsCancelled)

            {

                return (int)CliExitCode.Success;

            }

            dispatcher.WriteDiagnostic(

                resolved.Error ?? "CLI context could not be resolved.");

            return (int)CliExitCode.ConfigurationError;

        }

        foreach (string warning in resolved.Warnings)

        {

            dispatcher.WriteDiagnostic("Warning: " + warning);

        }

        CliEffectiveContext effective = resolved.Context!;

        ContextPreviewRequest request = new(

            prompt,

            effective.Model.Value,

            effective.Workspace.Value ?? invocationDirectory,

            effective.Session.Value,

            effective.Campaign.Value,

            showContent,

            noRetrieval);

        Result<ContextPreviewResult> response = await apiClient

            .PreviewContextAsync(request, cancellationToken)

            .ConfigureAwait(false);

        if (response.IsFailure)

        {

            dispatcher.WriteDiagnostic(

                $"{response.Error.Code}: {response.Error.Message}");

            return (int)CliExitCode.GenericError;

        }

        ContextPreviewResult preview = response.Value;

        if (CliInvocationContext.Current.Json)

        {

            dispatcher.WriteJson(

                preview,

                ArcanumJsonContext.Default.ContextPreviewResult);

            return (int)CliExitCode.Success;

        }

        switch (view)

        {

            case ContextPreviewView.Tools:

                WriteTools(preview);

                break;

            case ContextPreviewView.Sources:

                WriteSources(preview);

                break;

            case ContextPreviewView.Mana:

                WriteMana(preview);

                break;

            default:

                WriteInspect(preview);

                break;

        }

        return (int)CliExitCode.Success;

    }

    private void WriteInspect(ContextPreviewResult preview)

    {

        dispatcher.WritePayload($"Provider:       {preview.Provider}");

        dispatcher.WritePayload($"Model:          {preview.Model}");

        dispatcher.WritePayload($"Context window: {preview.ContextWindowLimit:N0} tokens");

        dispatcher.WritePayload($"Spell:          {preview.SelectedSpell ?? "[None]"}");

        dispatcher.WritePayload($"Routing:        {preview.RoutingMode}");

        dispatcher.WritePayload($"Spell reason:   {preview.SpellReason}");

        dispatcher.WritePayload(

            $"Compression:    {(preview.Compression.Applied ? "applied" : "not applied")} — {preview.Compression.Reason}");

        WriteMana(preview);

        if (preview.ResonantDependencies.Count > 0)

        {

            dispatcher.WritePayload(

                "Resonant:       " + string.Join(", ", preview.ResonantDependencies));

        }

        dispatcher.WritePayload("Sources:");

        WriteSources(preview);

        dispatcher.WritePayload("Tools:");

        WriteTools(preview);

        foreach (ContextPreviewAuxiliaryCall call in preview.AuxiliaryCalls)

        {

            dispatcher.WritePayload(

                $"Auxiliary:     {call.Purpose}: {call.Reason}");

        }

        if (preview.Content is not null)

        {

            dispatcher.WritePayload("Content:");

            dispatcher.WritePayload(preview.Content.SystemPrompt);

            foreach (CoreChatMessage message in preview.Content.Messages)

            {

                dispatcher.WritePayload($"[{message.Role}] {message.Content}");

            }

        }

    }

    private void WriteMana(ContextPreviewResult preview)

    {

        dispatcher.WritePayload(

            $"Mana:           {preview.Tokens.TotalTokens:N0} / {preview.ContextWindowLimit:N0} tokens ({preview.Tokens.Classification})");

        dispatcher.WritePayload(

            $"Reserved output: {preview.Tokens.ReservedOutputTokens:N0} tokens");

    }

    private void WriteTools(ContextPreviewResult preview)

    {

        foreach (ContextPreviewTool tool in preview.Tools)

        {

            dispatcher.WritePayload(

                $"{(tool.Included ? "+" : "-")} {tool.Name} [{tool.Source}] — {tool.Reason}");

        }

    }

    private void WriteSources(ContextPreviewResult preview)

    {

        foreach (ContextPreviewSource source in preview.Sources)

        {

            dispatcher.WritePayload(

                $"{source.Source}: {source.TokenCount:N0} tokens ({source.Classification}) — {source.Reason}");

            if (!string.IsNullOrEmpty(source.Content))

            {

                dispatcher.WritePayload(source.Content);

            }

        }

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

public enum ContextPreviewView

{

    Inspect,

    Tools,

    Sources,

    Mana,

}
