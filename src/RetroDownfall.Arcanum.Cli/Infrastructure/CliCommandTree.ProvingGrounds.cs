using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands.ProvingGrounds;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildTrial(IServiceProvider sp)
    {
        TrialCommands handler = sp.GetRequiredService<TrialCommands>();
        Command trial = new("trial", "Run Trials against spells, prompts, or Apprentice goals (requires arcanum serve).");

        Command run = new("run", "Run a Trial with Inquisitors.");
        Option<string?> runTarget = new("--target") { Description = "Trial target kind: spell, prompt, apprenticeGoal." };
        Option<string?> runTargetValue = new("--target-value") { Description = "Spell name, prompt GUID, or apprentice goal text." };
        Option<string?> runModel = new("--model") { Description = "Model override for the Trial." };
        Option<string?> runWorkspace = new("--workspace") { Description = "Workspace root to scope the Trial." };
        Option<string?> runName = new("--name") { Description = "Trial display name; defaults to '{targetKind}:{target}'." };
        Option<string[]> runInquisitor = new("--inquisitor") { AllowMultipleArgumentsPerToken = true, Description = "Inquisitor spec: inline JSON, or @filename. Pass multiple times for several inquisitors." };
        Option<string[]> runVar = new("--var") { AllowMultipleArgumentsPerToken = true, Description = "Trial variable as key=value; pass multiple times for several variables." };
        run.Add(runTarget); run.Add(runTargetValue); run.Add(runModel); run.Add(runWorkspace);
        run.Add(runName); run.Add(runInquisitor); run.Add(runVar);
        run.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Run(
                pr.GetValue(runTarget),
                pr.GetValue(runTargetValue),
                ActiveModel(sp, pr.GetValue(runModel)),
                ActiveWorkspace(sp, pr.GetValue(runWorkspace)),
                pr.GetValue(runName),
                pr.GetValue(runInquisitor),
                pr.GetValue(runVar),
                ct).ConfigureAwait(false));
        trial.Add(run);

        return trial;
    }

}
