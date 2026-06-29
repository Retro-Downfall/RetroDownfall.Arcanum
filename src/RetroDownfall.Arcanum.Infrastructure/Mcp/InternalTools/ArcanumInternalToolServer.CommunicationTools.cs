using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;


namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{


    private async Task<McpToolsCallResultWire> ExecuteAskHumanAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        AskHumanParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.AskHumanParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "ask_human argument deserialization failed.");

            return ToolError("Invalid arguments for ask_human.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Question) || string.IsNullOrWhiteSpace(args.PromptId))
        {
            return ToolError("ask_human requires non-empty 'question' and 'promptId'.");
        }

        try
        {
            string answer = await _humanPrompts
                .WaitForResponseAsync(args.PromptId.Trim(), cancellationToken)
                .ConfigureAwait(false);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = answer },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogError(ex, "ask_human registration failed.");

            return ToolError("ask_human: an internal error occurred.");
        }
    }

    private Task<McpToolsCallResultWire> ExecuteAdjustInitiativeAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        AdjustInitiativeArgs? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.AdjustInitiativeArgs);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "adjust_initiative argument deserialization failed.");

            return Task.FromResult(ToolError("Invalid arguments for adjust_initiative."));
        }

        if (args is null || string.IsNullOrWhiteSpace(args.JobName))
        {
            return Task.FromResult(ToolError("adjust_initiative requires a non-empty 'job_name'."));
        }

        string jobName = args.JobName.Trim();

        int clamped = ArcanumSettingClamps.UnseenServantIntervalMinutes(args.IntervalMinutes);

        _pacer.SetDynamicInterval(jobName, args.IntervalMinutes);

        string text =
            $"Unseen Servant job '{jobName}' polling interval set to {clamped} minutes (clamped to allowed range).";

        return Task.FromResult(
            new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = text },
                ],
                IsError = false,
            });
    }

    private async Task<McpToolsCallResultWire> ExecuteUseCommlinkAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        UseCommlinkParams? args;

        try
        {

            args = JsonSerializer.Deserialize(arguments, _json.UseCommlinkParams);

        }
        catch (JsonException ex)
        {

            _logger?.LogError(ex, "use_commlink argument deserialization failed.");

            return ToolError("Invalid arguments for use_commlink.");

        }

        if (args is null
            || string.IsNullOrWhiteSpace(args.Title)
            || string.IsNullOrWhiteSpace(args.Body)
            || string.IsNullOrWhiteSpace(args.Severity))
        {

            return ToolError("use_commlink requires non-empty 'title', 'body', and 'severity'.");

        }

        if (!Enum.TryParse(args.Severity.Trim(), ignoreCase: true, out CommLinkSeverity severity))
        {

            severity = CommLinkSeverity.Info;

        }

        string source = string.IsNullOrWhiteSpace(args.Source) ? "use_commlink" : args.Source.Trim();

        CommLinkMessage message = new(args.Title.Trim(), args.Body.Trim(), severity, source);

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ICommLinkDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<ICommLinkDispatcher>();

            Result r = await dispatcher
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (r.IsFailure)
            {

                return ToolError($"use_commlink failed: {r.Error.Message}");

            }

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = "Comm Link alert dispatched successfully." },
                ],
                IsError = false,
            };

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            _logger?.LogError(ex, "use_commlink dispatch failed.");

            return ToolError("An internal error occurred during use_commlink.");

        }

    }

    private async Task<McpToolsCallResultWire> ExecutePetitionDungeonMasterAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        PetitionDungeonMasterParams? args;

        try
        {

            args = JsonSerializer.Deserialize(arguments, _json.PetitionDungeonMasterParams);

        }
        catch (JsonException ex)
        {

            _logger?.LogError(ex, "petition_dungeon_master argument deserialization failed.");

            return ToolError("Invalid arguments for petition_dungeon_master.");

        }

        if (args is null || string.IsNullOrWhiteSpace(args.Reason))
        {

            return ToolError("petition_dungeon_master requires a non-empty 'reason'.");

        }

        string reason = args.Reason.Trim();

        string source = string.IsNullOrWhiteSpace(args.Source) ? "petition_dungeon_master" : args.Source.Trim();

        CommLinkMessage message = new(
            "Apprentice petitions the Dungeon Master",
            reason,
            CommLinkSeverity.Critical,
            source);

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            ICommLinkDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<ICommLinkDispatcher>();

            Result r = await dispatcher
                .DispatchAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (r.IsFailure)
            {

                return ToolError($"petition_dungeon_master failed: {r.Error.Message}");

            }

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire
                    {
                        Text = "Petition sent to the Dungeon Master. The Apprentice awaits Divine Intervention.",
                    },
                ],
                IsError = false,
            };

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            _logger?.LogError(ex, "petition_dungeon_master dispatch failed.");

            return ToolError("An internal error occurred during petition_dungeon_master.");

        }

    }

    private async Task<McpToolsCallResultWire> ExecuteCastSendingAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {

        if (!_conclaveEnabled)
        {
            return ToolError("The Conclave is disabled; cross-Apprentice delegation is not available.");
        }

        if (string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            return ToolError(WorkspaceNotConfiguredMessage);
        }

        CastSendingParams? args;

        try
        {
            args = JsonSerializer.Deserialize(arguments, _json.CastSendingParams);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "cast_sending argument deserialization failed.");

            return ToolError("Invalid arguments for cast_sending.");
        }

        if (args is null || string.IsNullOrWhiteSpace(args.Goal))
        {
            return ToolError("cast_sending requires a non-empty 'goal'.");
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            IConclaveArchmage archmage = scope.ServiceProvider.GetRequiredService<IConclaveArchmage>();

            Result<Apprentice> result = await archmage
                .CastAsync(
                    new ConclaveCastRequest(args.Goal.Trim(), args.Name, _workspaceRoot!),
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsFailure)
            {
                return ToolError($"cast_sending failed: {result.Error.Message}");
            }

            CastSendingResultWire payload = new() { ChildApprenticeId = result.Value!.Id };

            string json = JsonSerializer.Serialize(payload, _json.CastSendingResultWire);

            return new McpToolsCallResultWire
            {
                Content =
                [
                    new McpToolContentTextWire { Text = json },
                ],
                IsError = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "cast_sending failed.");

            return ToolError("An internal error occurred during cast_sending.");
        }
    }

}
