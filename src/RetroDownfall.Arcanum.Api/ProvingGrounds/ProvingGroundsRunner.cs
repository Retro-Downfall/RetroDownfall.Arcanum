using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds;

public sealed partial class ProvingGroundsRunner(
    ISpellRepository spellRepository,
    IPromptRepository promptRepository,
    IArcanumIntelligenceProvider intelligence,
    IProvingGroundsArbiter arbiter,
    PromptRenderer promptRenderer,
    SpellWorkspaceResolver workspaceResolver,
    IOptionsSnapshot<ArcanumSettings> options)
{

    public async Task<Result<TrialResult>> RunAsync(Trial trial, CancellationToken cancellationToken = default)
    {
        if (trial is null)
        {
            return Result<TrialResult>.Failure(new Error(ErrorCodes.ProvingGrounds.InvalidTrial, "Trial payload is required."));
        }

        if (string.IsNullOrWhiteSpace(trial.Target))
        {
            return Result<TrialResult>.Failure(new Error(ErrorCodes.ProvingGrounds.InvalidTrial, "Trial target is required."));
        }

        if (trial.Inquisitors is null || trial.Inquisitors.Count == 0)
        {
            return Result<TrialResult>.Failure(
                new Error(ErrorCodes.ProvingGrounds.InvalidTrial, "At least one Inquisitor is required."));
        }

        ArcanumSettings arc = options.Value;

        Result<string> workspaceResult = workspaceResolver.ResolveRequired(trial.Workspace);

        if (workspaceResult.IsFailure)
        {
            return Result<TrialResult>.Failure(
                new Error(ErrorCodes.ProvingGrounds.WorkspaceNotAllowed, workspaceResult.Error.Message));
        }

        string resolvedWorkspace = workspaceResult.Value!;

        Result<PingRequest> pingResult = await ResolvePingRequestAsync(trial, resolvedWorkspace, cancellationToken).ConfigureAwait(false);

        if (pingResult.IsFailure)
        {
            return Result<TrialResult>.Failure(pingResult.Error);
        }

        Result<PromptTurnResult> turn = await intelligence
            .ExecutePromptAsync(pingResult.Value!, cancellationToken)
            .ConfigureAwait(false);

        if (turn.IsFailure)
        {
            return Result<TrialResult>.Failure(
                new Error(
                    ErrorCodes.ProvingGrounds.InferenceFailed,
                    $"Trial inference failed: {turn.Error.Message}"));
        }

        string output = turn.Value!.Text;

        string? judgeModel = null;

        if (!string.IsNullOrWhiteSpace(arc.FastModel))
        {
            judgeModel = arc.FastModel.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(arc.DefaultModel))
        {
            judgeModel = arc.DefaultModel.Trim();
        }

        IReadOnlyList<InquisitorVerdict> verdicts = await arbiter
            .AdjudicateAsync(output, trial.Inquisitors, judgeModel, cancellationToken)
            .ConfigureAwait(false);

        int passedCount = verdicts.Count(static v => v.Passed);

        string trialName = string.IsNullOrWhiteSpace(trial.Name)
            ? $"{trial.TargetKind}:{trial.Target}"
            : trial.Name.Trim();

        TrialResult result = new(
            trialName,
            trial.TargetKind,
            trial.Target,
            verdicts.All(static v => v.Passed),
            output,
            verdicts,
            passedCount,
            verdicts.Count,
            turn.Value.Usage);

        return Result<TrialResult>.Success(result);
    }

    private async Task<Result<PingRequest>> ResolvePingRequestAsync(
        Trial trial,
        string resolvedWorkspace,
        CancellationToken cancellationToken)
    {
        return trial.TargetKind switch
        {
            TrialTargetKind.Spell => await ResolveSpellPingAsync(trial, resolvedWorkspace, cancellationToken).ConfigureAwait(false),
            TrialTargetKind.Prompt => await ResolvePromptPingAsync(trial, resolvedWorkspace, cancellationToken).ConfigureAwait(false),
            TrialTargetKind.ApprenticeGoal => ResolveApprenticeGoalPing(trial, resolvedWorkspace),
            _ => Result<PingRequest>.Failure(new Error(ErrorCodes.ProvingGrounds.InvalidTrial, $"Unknown target kind '{trial.TargetKind}'.")),
        };
    }

    private async Task<Result<PingRequest>> ResolveSpellPingAsync(
        Trial trial,
        string resolvedWorkspace,
        CancellationToken cancellationToken)
    {
        string spellName = trial.Target.Trim();

        SpellDetail? spell = await spellRepository
            .GetAsync(spellName, resolvedWorkspace, cancellationToken)
            .ConfigureAwait(false);

        if (spell is null)
        {
            return Result<PingRequest>.Failure(
                new Error(ErrorCodes.ProvingGrounds.SpellNotFound, $"Spell '{spellName}' was not found in the resolved workspace."));
        }

        string userPrompt = ResolveVariable(trial.Variables, "input") ?? string.Empty;

        PingRequest ping = new(
            Prompt: userPrompt,
            Model: trial.Model,
            WorkingDirectory: resolvedWorkspace,
            OverrideSpellName: spellName,
            SkipSpellRouting: false,
            UnattendedMode: true);

        return Result<PingRequest>.Success(ping);
    }

    private async Task<Result<PingRequest>> ResolvePromptPingAsync(
        Trial trial,
        string resolvedWorkspace,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(trial.Target.Trim(), out Guid promptId))
        {
            return Result<PingRequest>.Failure(
                new Error(ErrorCodes.ProvingGrounds.InvalidTrial, "Prompt target must be a valid prompt GUID."));
        }

        Prompt? prompt = await promptRepository
            .GetByIdAsync(promptId, cancellationToken)
            .ConfigureAwait(false);

        if (prompt is null)
        {
            return Result<PingRequest>.Failure(
                new Error(ErrorCodes.ProvingGrounds.PromptNotFound, $"Prompt '{promptId}' was not found."));
        }

        Result<PromptRenderResultDto> renderResult = promptRenderer.Render(prompt, trial.Variables);

        if (renderResult.IsFailure)
        {
            return Result<PingRequest>.Failure(renderResult.Error);
        }

        string userMessage = ResolveVariable(trial.Variables, "input") ?? string.Empty;

        PingRequest ping = new(
            Prompt: userMessage,
            Model: trial.Model ?? prompt.Model,
            WorkingDirectory: resolvedWorkspace,
            SkipSpellRouting: true,
            UnattendedMode: true,
            DisableMcpTools: true,
            AdditionalSystemPrompt: renderResult.Value!.RenderedText);

        return Result<PingRequest>.Success(ping);
    }

    private static Result<PingRequest> ResolveApprenticeGoalPing(Trial trial, string resolvedWorkspace)
    {
        string goal = SubstituteGoalVariables(trial.Target.Trim(), trial.Variables);

        Apprentice apprentice = new()
        {
            Name = string.IsNullOrWhiteSpace(trial.Name) ? "Trial" : trial.Name.Trim(),
            Goal = goal,
        };

        string planPrompt = ApprenticePromptBuilder.BuildPlanGenerationPrompt(apprentice);

        PingRequest ping = new(
            Prompt: planPrompt,
            Model: trial.Model,
            WorkingDirectory: resolvedWorkspace,
            UnattendedMode: true,
            DisableMcpTools: true,
            SkipSpellRouting: true);

        return Result<PingRequest>.Success(ping);
    }

    private static string? ResolveVariable(Dictionary<string, string>? variables, string key)
    {
        if (variables is null)
        {
            return null;
        }

        return variables.TryGetValue(key, out string? value) ? value : null;
    }

    private static string SubstituteGoalVariables(string goal, Dictionary<string, string>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return goal;
        }

        return GoalPlaceholderRegex().Replace(goal, match =>
        {
            string key = match.Groups[1].Value;

            return variables.TryGetValue(key, out string? value) ? value : match.Value;
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex GoalPlaceholderRegex();

}
