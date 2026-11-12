using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Cli.Commands.Guild;

internal sealed partial class GuildCommands
{
    private async Task<int> WatchAsync(string path, GuildCommandInput input, CancellationToken cancellationToken)
    {
        bool tree = path == "watch tree";

        Guid id = await Select(tree ? "task" : "cohort", input.Argument(), cancellationToken).ConfigureAwait(false);

        string route = tree ? $"api/guild/quests/{id:D}/events" : $"api/guild/cohorts/{id:D}/events";

        string? after = input.Text("after");

        bool finite = CliInvocationContext.Current.Json;

        bool ndjson = input.Flag("ndjson");

        if (ndjson) dispatcher.BeginJsonStream();

        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        ConsoleCancelEventHandler interrupt = (_, args) =>
        {
            args.Cancel = true;

            watching.Cancel();
        };

        Console.CancelKeyPress += interrupt;

        try
        {
            while (true)
            {
                bool resnapshot = false;

                await foreach (WatchSseFrame frame in apiClient.WatchSseAsync(
                    ArcanumApiClient.GuildQuery(route, ("after", after)), watching.Token).ConfigureAwait(false))
                {
                    if (frame.Type == WatchSseFrameType.Heartbeat)
                    {
                        if (!string.IsNullOrEmpty(frame.Diagnostic)) dispatcher.WriteDiagnostic(frame.Diagnostic);

                        continue;
                    }

                    if (frame.Type == WatchSseFrameType.Error)
                        return frame.Error is { } error ? Failure(error) : Failure("Guild stream failed.", 1);

                    if (frame.Type is WatchSseFrameType.UnexpectedEof or WatchSseFrameType.Done)
                        return Failure("Guild stream disconnected; request a fresh --json snapshot or resume with --after and the last cursor.", 3);

                    if (frame.Data is not { } data) return Failure("The Guild stream did not contain a JSON event body.", 1);

                    string eventName = frame.EventName ?? string.Empty;

                    if (finite)
                    {
                        if (eventName != "snapshot") return Failure("The Guild stream did not begin with its required snapshot; request a fresh snapshot.", 1);

                        if (tree)
                        {
                            GuildTreeSnapshotDto snapshot = data.Deserialize(Data<GuildTreeSnapshotDto>())
                                ?? throw new JsonException("Missing tree snapshot.");

                            return Output(snapshot);
                        }

                        GuildCohortSnapshotDto cohort = data.Deserialize(Data<GuildCohortSnapshotDto>())
                            ?? throw new JsonException("Missing Cohort snapshot.");

                        return Output(cohort);
                    }

                    // Validate the closed, generated source payload before publishing a machine frame.
                    ValidateEvent(eventName, data, tree);

                    if (ndjson)
                    {
                        dispatcher.WriteJson(new GuildWatchRecord(eventName, frame.EventId, data), CliJsonContext.Default.GuildWatchRecord);
                    }
                    else
                    {
                        dispatcher.WritePayload($"{eventName} [{frame.EventId ?? "no cursor"}] {data.GetRawText()}");
                    }

                    if (frame.EventId is not null) after = frame.EventId;

                    if (eventName == "resync")
                    {
                        dispatcher.WriteDiagnostic("Guild projection requires resync; request a fresh --json snapshot, then resume live output with --after and its resume cursor.");

                        if (ndjson) return 1;

                        // Disposing this enumerator closes the old subscription before a new snapshot.
                        after = null;

                        resnapshot = true;

                        break;
                    }

                    if (eventName == "terminal") return 0;
                }

                if (!resnapshot) return Failure("Guild stream ended without a terminal frame; obtain a fresh snapshot or resume from the last cursor.", 3);
            }
        }
        catch (JsonException)
        {
            return Failure("The Guild stream contained an invalid or unknown event payload; request a fresh snapshot before resuming.", 1);
        }
        catch (OperationCanceledException) when (watching.IsCancellationRequested)
        {
            dispatcher.WriteDiagnostic("Guild watch cancelled.");

            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
        }
    }

    private static void ValidateEvent(string name, JsonElement data, bool tree)
    {
        switch (name)
        {
            case "snapshot":
                if (tree) _ = data.Deserialize(Data<GuildTreeSnapshotDto>()) ?? throw new JsonException("Missing tree snapshot.");
                else _ = data.Deserialize(Data<GuildCohortSnapshotDto>()) ?? throw new JsonException("Missing Cohort snapshot.");

                break;

            case "change": _ = data.Deserialize(Data<GuildChangeEventDto>()) ?? throw new JsonException("Missing change event."); break;

            case "resync": _ = data.Deserialize(Data<GuildResyncEventDto>()) ?? throw new JsonException("Missing resync event."); break;

            case "terminal": _ = data.Deserialize(Data<GuildTerminalEventDto>()) ?? throw new JsonException("Missing terminal event."); break;

            default: throw new JsonException("Unknown Guild event kind.");
        }
    }
}
