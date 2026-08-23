using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Commands.Conclave;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{

    private static Command BuildConclave(IServiceProvider serviceProvider)
    {

        ConclaveCommands handler = serviceProvider.GetRequiredService<ConclaveCommands>();

        Command conclave = new("conclave", "The Conclave and its A2A surface (requires arcanum serve).");

        Command status = new("status", "Show whether A2A is disabled, configured, degraded, or healthy.");

        status.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Status(cancellationToken).ConfigureAwait(false));

        conclave.Add(status);

        Command dispatch = new("dispatch", "Dispatch a Sending to a remote A2A agent and wait for its result.");

        Option<string?> agentUrl = new("--agent-url")
        {
            Description = "Remote agent base URL or Agent Card URL.",
        };

        Option<string?> goal = new("--goal") { Description = "Goal text delegated to the remote agent." };

        Option<string?> name = new("--name") { Description = "Optional display name for the Sending." };

        Option<bool> continuable = new("--continuable")
        {
            Description =
                "Return a continuation task id when the remote agent asks for more input or authentication, "
                + "instead of ending the Sending. Answer it with 'arcanum conclave continue'.",
        };

        Option<string?> skill = new("--skill")
        {
            Description =
                "Agent Card skill id to target. The Sending fails before the remote task is created if "
                + "the peer advertises no such skill.",
        };

        Option<string[]?> accept = new("--accept")
        {
            Description =
                "Media type to accept back (repeatable, e.g. --accept text/plain). Omit to accept "
                + "whatever this instance can consume.",
            AllowMultipleArgumentsPerToken = true,
        };

        Option<bool> callback = new("--callback")
        {
            Description =
                "Ask the remote agent to report back when it finishes instead of holding one of this "
                + "instance's concurrent-Sending slots for the whole remote run. Requires "
                + "Arcanum:Integrations:A2A:PushNotifications and a reachable PushCallbackBaseUrl; "
                + "falls back to the ordinary wait when the peer cannot accept a callback.",
        };

        dispatch.Add(agentUrl);
        dispatch.Add(goal);
        dispatch.Add(name);
        dispatch.Add(continuable);
        dispatch.Add(skill);
        dispatch.Add(accept);
        dispatch.Add(callback);

        dispatch.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Dispatch(
                result.GetValue(agentUrl),
                result.GetValue(goal),
                result.GetValue(name),
                result.GetValue(continuable),
                result.GetValue(skill),
                result.GetValue(accept),
                result.GetValue(callback),
                cancellationToken).ConfigureAwait(false));

        conclave.Add(dispatch);

        Command continueSending = new(
            "continue",
            "Answer a Sending the remote agent parked at input-required or auth-required.");

        Argument<string?> taskId = new("task-id")
        {
            Description = "Remote A2A task id reported by 'arcanum conclave dispatch --continuable'.",
        };

        Option<string?> continueAgentUrl = new("--agent-url")
        {
            Description = "Remote agent base URL or Agent Card URL — the same one the Sending was dispatched to.",
        };

        Option<string?> message = new("--message")
        {
            Description = "The input or credential the remote agent asked for.",
        };

        Option<bool> stayContinuable = new("--continuable")
        {
            Description = "Keep returning a continuation if the remote asks for something again.",
        };

        Option<string?> continueSkill = new("--skill")
        {
            Description = "Agent Card skill id to target, validated against the peer's card before sending.",
        };

        Option<string[]?> continueAccept = new("--accept")
        {
            Description =
                "Media type to accept back (repeatable). Omit to accept whatever this instance can consume.",
            AllowMultipleArgumentsPerToken = true,
        };

        continueSending.Add(taskId);
        continueSending.Add(continueAgentUrl);
        continueSending.Add(message);
        continueSending.Add(stayContinuable);
        continueSending.Add(continueSkill);
        continueSending.Add(continueAccept);

        continueSending.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Continue(
                result.GetValue(taskId),
                result.GetValue(continueAgentUrl),
                result.GetValue(message),
                result.GetValue(stayContinuable),
                result.GetValue(continueSkill),
                result.GetValue(continueAccept),
                cancellationToken).ConfigureAwait(false));

        conclave.Add(continueSending);

        return conclave;

    }

    private static Command BuildApprentice(IServiceProvider sp)
    {
        ApprenticeCommands handler = sp.GetRequiredService<ApprenticeCommands>();
        Command apprentice = new("apprentice", "Apprentice orchestration (requires arcanum serve).");

        Command list = new("list", "List Apprentices.");
        Option<string?> listCampaignId = new("--campaign-id") { Description = "Filter by campaign GUID." };
        Option<string?> listStatus = new("--status") { Description = "Filter by status." };
        Option<int?> listLimit = new("--limit") { Description = "Maximum number of Apprentices to return." };
        list.Add(listCampaignId); list.Add(listStatus); list.Add(listLimit);
        list.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.List(ActiveCampaign(sp, pr.GetValue(listCampaignId)), pr.GetValue(listStatus), pr.GetValue(listLimit), ct).ConfigureAwait(false));
        apprentice.Add(list);

        Command show = new("show", "Show Apprentice detail.");
        Argument<string?> showId = new("apprentice")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Optional Apprentice GUID, exact name, or unique name prefix; omit for an interactive picker.",
        };
        show.Add(showId);
        show.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Get(pr.GetValue(showId), ct).ConfigureAwait(false));
        apprentice.Add(show);

        Command create = new("create", "Create an Apprentice.");
        Option<string?> createGoal = new("--goal") { Description = "Apprentice goal: inline text, or @filename to read from a file." };
        Option<string?> createName = new("--name") { Description = "Display name; defaults to a truncated form of the goal." };
        Option<string?> createCampaignId = new("--campaign-id") { Description = "Campaign GUID to associate with." };
        Option<string?> createWorkspace = new("--workspace") { Description = "Workspace root to scope the Apprentice." };
        create.Add(createGoal); create.Add(createName); create.Add(createCampaignId); create.Add(createWorkspace);
        create.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Create(
                pr.GetValue(createGoal),
                pr.GetValue(createName),
                ActiveCampaign(sp, pr.GetValue(createCampaignId)),
                ActiveWorkspace(sp, pr.GetValue(createWorkspace)),
                ct).ConfigureAwait(false));
        apprentice.Add(create);

        Command delete = new("delete", "Delete a terminal Apprentice.");
        Argument<string?> deleteId = OptionalResourceArgument("id", "Apprentice GUID or name");
        delete.Add(deleteId);
        delete.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Delete(pr.GetValue(deleteId), ct).ConfigureAwait(false));
        apprentice.Add(delete);

        Command start = new("start", "Start plan generation and execution.");
        Argument<string?> startId = OptionalResourceArgument("id", "Apprentice GUID or name");
        start.Add(startId);
        start.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Start(pr.GetValue(startId), ct).ConfigureAwait(false));
        apprentice.Add(start);

        Command pause = new("pause", "Pause at the next step boundary.");
        Argument<string?> pauseId = OptionalResourceArgument("id", "Apprentice GUID or name");
        pause.Add(pauseId);
        pause.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Pause(pr.GetValue(pauseId), ct).ConfigureAwait(false));
        apprentice.Add(pause);

        Command resume = new("resume", "Resume from checkpoint.");
        Argument<string?> resumeId = OptionalResourceArgument("id", "Apprentice GUID or name");
        resume.Add(resumeId);
        resume.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Resume(pr.GetValue(resumeId), ct).ConfigureAwait(false));
        apprentice.Add(resume);

        Command cancel = new("cancel", "Cancel execution.");
        Argument<string?> cancelId = OptionalResourceArgument("id", "Apprentice GUID or name");
        cancel.Add(cancelId);
        cancel.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Cancel(pr.GetValue(cancelId), ct).ConfigureAwait(false));
        apprentice.Add(cancel);

        Command reweave = new("reweave", "Replace the remaining plan steps.");
        Argument<string?> reweaveId = OptionalResourceArgument("id", "Apprentice GUID or name");
        Option<string?> reweavePlan = new("--plan") { Description = "JSON array of plan steps: inline text, or @filename to read from a file." };
        reweave.Add(reweaveId); reweave.Add(reweavePlan);
        reweave.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Reweave(pr.GetValue(reweaveId), pr.GetValue(reweavePlan), ct).ConfigureAwait(false));
        apprentice.Add(reweave);

        Command intervene = new("intervene", "Provide Divine Intervention guidance to an escalated Apprentice.");
        Argument<string?> interveneId = OptionalResourceArgument("id", "Apprentice GUID or name");
        Option<string?> interveneGuidance = new("--guidance") { Description = "Guidance text for the escalated Apprentice." };
        intervene.Add(interveneId); intervene.Add(interveneGuidance);
        intervene.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Intervene(pr.GetValue(interveneId), pr.GetValue(interveneGuidance), ct).ConfigureAwait(false));
        apprentice.Add(intervene);

        Command cast = new("cast", "Delegate a child Apprentice via The Conclave.");
        Argument<string?> castId = OptionalResourceArgument("id", "Apprentice GUID or name");
        Option<string?> castGoal = new("--goal") { Description = "Child Apprentice goal text." };
        Option<string?> castName = new("--name") { Description = "Display name for the child Apprentice." };
        cast.Add(castId); cast.Add(castGoal); cast.Add(castName);
        cast.SetAction(async (ParseResult pr, CancellationToken ct) =>
            await handler.Cast(pr.GetValue(castId), pr.GetValue(castGoal), pr.GetValue(castName), ct).ConfigureAwait(false));
        apprentice.Add(cast);

        return apprentice;
    }

}
