using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed record RunCommandRequest(

    string[] Prompt,

    bool Research,

    string? Spell,

    string[] With,

    string[] Attachment,

    bool DryRun,

    bool ShowContent,

    string? Model,

    bool NewSession,

    bool Unattended,

    bool Continue,

    bool Resume,

    string? ResumeTarget,

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

    CliSessionManager sessionManager,

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

        // A malformed reference is invalid input, so it is rejected here rather than deep in the
        // inference path — which would report it as a generic error after starting the host.
        if (!AttachmentReferenceInput.TryParse(
                request.Attachment,
                out _,
                out string? attachmentError))
        {

            return Fail(attachmentError!);

        }

        if (!TryResolveSessionSelector(
                request,
                out string? sessionSelector,
                out bool sessionPicker,
                out string? selectorError))
        {

            return Fail(selectorError!);

        }

        string positionalInstruction = AskCommand.BuildPrompt(request.Prompt);

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
                        : sessionSelector,
                    invocationDirectory,
                    CliInvocationContext.Current.NoContext,
                    request.NewSession,
                    sessionPicker && !request.NewSession),
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

    /// <summary>
    /// <c>--session</c>, <c>--continue</c>, and <c>--resume</c> all fill the same slot, so supplying
    /// more than one is a contradiction rather than a precedence question. <c>--new</c> keeps its
    /// documented behavior of winning over an explicit selector instead of adding a second conflict.
    /// </summary>
    private bool TryResolveSessionSelector(
        RunCommandRequest request,
        out string? selector,
        out bool picker,
        out string? error)
    {

        selector = request.Session;

        picker = false;

        error = null;

        int selectorCount = (string.IsNullOrWhiteSpace(request.Session) ? 0 : 1)
            + (request.Continue ? 1 : 0)
            + (request.Resume ? 1 : 0);

        if (selectorCount > 1)
        {

            error = "--session, --continue, and --resume each select a session; supply exactly one.";

            return false;

        }

        // --new wins over a selector rather than adding a second conflict, so nothing is resolved:
        // resolving --continue here would fail a fresh install that explicitly asked for a new
        // session, and would announce a continuation the caller discards moments later.
        if (request.NewSession)
        {

            selector = null;

            return true;

        }

        if (request.Continue)
        {

            Guid? previous = sessionManager.GetLastSessionId(quiet: true);

            if (previous is null)
            {

                error = "No previous session to continue. Start one with: arcanum run \"<prompt>\"";

                return false;

            }

            selector = previous.Value.ToString("D");

            dispatcher.WriteVerbose($"Continuing session {selector}.");

            return true;

        }

        if (request.Resume)
        {

            if (string.IsNullOrWhiteSpace(request.ResumeTarget))
            {

                picker = true;

                selector = null;

                return true;

            }

            selector = request.ResumeTarget;

        }

        return true;

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
