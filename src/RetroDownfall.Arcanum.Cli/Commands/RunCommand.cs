using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed record RunCommandRequest(

    string[] Prompt,

    string[] EscapedArguments,

    bool Research,

    string? Spell,

    string[] With,

    bool DryRun,

    bool ShowContent,

    string? Model,

    bool NewSession,

    bool Unattended,

    string? Campaign,

    string? Workspace,

    string? Session,

    string? Temperature,

    string? TopP,

    string? MaxTokens,

    string? Seed,

    string[] Stop,

    string? ResponseFormat,

    string? PresencePenalty,

    string? FrequencyPenalty,

    int? SourceTarget,

    int TokenBudget,

    decimal? CostBudget);

internal sealed class RunCommand(

    IRunInputReader inputReader,

    IRunAttachmentStager attachmentStager,

    ICliInferenceContextResolver contextResolver,

    IRunExecutionDispatcher executionDispatcher,

    IGrimoireCliInitialization grimoireBootstrapper,

    IArcanumServeLauncher serveLauncher,

    IConsoleDispatcher dispatcher)
{

    private const string AttachmentOnlyPrompt =
        "Analyze the attached context.";

    public async Task<int> RunAsync(
        RunCommandRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        if (request.Research
            && request.Spell is not null)
        {

            return Fail(
                "--research and --spell select different routes and cannot be used together.");

        }

        if (request.Spell is not null
            && string.IsNullOrWhiteSpace(request.Spell))
        {

            return Fail("--spell requires a named Spell.");

        }

        string positionalInstruction = AskCommand.BuildPrompt(
            request.Prompt,
            request.EscapedArguments);

        RunInputReadResult input = await inputReader
            .ReadAsync(
                positionalInstruction,
                cancellationToken,
                hasExplicitFileContext: request.With.Length > 0)
            .ConfigureAwait(false);

        WriteDiagnostics(input.Diagnostics);

        if (!input.IsSuccess)
        {

            return Fail(
                input.Error ?? "Standard input could not be read.");

        }

        if (string.IsNullOrWhiteSpace(input.Instruction)
            && input.PipedContent is null
            && request.With.Length == 0)
        {

            return Fail(
                "Prompt, redirected standard input, or --with @path context is required.");

        }

        try
        {

            await grimoireBootstrapper
                .EnsureInitializedAsync(cancellationToken)
                .ConfigureAwait(false);

        }
        catch (MissingMasterApiKeyException exception)
        {

            return Fail(
                exception.Message,
                CliExitCode.GenericError);

        }

        _ = await serveLauncher
            .EnsureRunningAsync(cancellationToken)
            .ConfigureAwait(false);

        string invocationDirectory = Environment.CurrentDirectory;

        CliInferenceContextResult contextResult = await contextResolver
            .ResolveAsync(
                new CliInferenceContextRequest(
                    request.Campaign,
                    request.Workspace,
                    request.Model,
                    request.NewSession
                        ? null
                        : request.Session,
                    invocationDirectory,
                    CliInvocationContext.Current.NoContext,
                    request.NewSession),
                cancellationToken)
            .ConfigureAwait(false);

        if (!contextResult.IsSuccess)
        {

            if (contextResult.IsCancelled)
            {

                return (int)CliExitCode.Success;

            }

            return Fail(
                contextResult.Error ?? "CLI context could not be resolved.");

        }

        WriteDiagnostics(
            contextResult.Warnings,
            prefix: "Warning: ");

        CliEffectiveContext context = contextResult.Context!;

        string workingDirectory = context.Workspace.Value
            ?? invocationDirectory;

        CliEffectiveContext executionContext = context.Workspace.Value is null
            ? context with
            {

                Workspace = new CliContextValue<string?>(
                    workingDirectory,
                    CliContextSource.CurrentDirectory),

            }
            : context;

        RunAttachmentStageResult staged = await attachmentStager
            .StageAsync(
                request.With,
                workingDirectory,
                input.PipedContent,
                cancellationToken)
            .ConfigureAwait(false);

        WriteDiagnostics(staged.Diagnostics);

        if (!staged.IsSuccess)
        {

            return Fail(
                staged.Error ?? "Turn-scoped context could not be staged.");

        }

        bool hasStagedContext = staged.AttachedFiles.Count > 0
            || staged.ScryingFoci.Count > 0;

        string instruction = input.Instruction.Trim();

        if (instruction.Length == 0
            && hasStagedContext)
        {

            instruction = AttachmentOnlyPrompt;

        }

        if (instruction.Length == 0)
        {

            return Fail(
                "Prompt or non-empty staged context is required.");

        }

        RunRoute route = request.Research
            ? RunRoute.Research
            : request.Spell is not null
                ? RunRoute.Spell
                : RunRoute.Agent;

        return await executionDispatcher
            .ExecuteAsync(
                new RunExecutionRequest(
                    request,
                    route,
                    instruction,
                    executionContext,
                    staged.AttachedFiles,
                    staged.ScryingFoci),
                cancellationToken)
            .ConfigureAwait(false);

    }

    private int Fail(
        string message,
        CliExitCode exitCode = CliExitCode.ConfigurationError)
    {

        dispatcher.WriteDiagnostic(message);

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                new CliErrorPayload(
                    message,
                    (int)exitCode),
                CliJsonContext.Default.CliErrorPayload);

        }

        return (int)exitCode;

    }

    private void WriteDiagnostics(
        IEnumerable<string> diagnostics,
        string prefix = "")
    {

        foreach (string diagnostic in diagnostics)
        {

            if (!string.IsNullOrWhiteSpace(diagnostic))
            {

                dispatcher.WriteDiagnostic(prefix + diagnostic);

            }

        }

    }

}
