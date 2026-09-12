using System.Data.Common;

using System.Net;

using System.Net.Http.Json;

using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Events;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

[Trait("Category", "Integration")]
public sealed partial class GrimoireMaintenanceAdmissionTests
{
    private sealed record CommitStream(MaintenanceSseResponse Response, PostAdmissionEndpointProbe Probe, string[] InitialFrames);

    private static async Task<CommitStream[]> StartCommitStreamsAsync(GrimoireMaintenanceAdmissionHarness harness)
    {
        MaintenanceSseResponse daemon = await harness.OpenSseAsync("/api/events/daemon");

        MaintenanceSseResponse mcp = await harness.OpenSseAsync("/api/events/mcp");

        MaintenanceSseResponse logs = await harness.OpenSseAsync("/api/events/logs");

        MaintenanceSseResponse session = await harness.OpenSseAsync($"/api/sessions/{harness.SessionId}/stream");

        MaintenanceSseResponse chronicle = await harness.OpenSseAsync($"/api/apprentices/{harness.ApprenticeId}/chronicle");

        string daemonFrame = "data: " + JsonSerializer.Serialize(new DaemonEvent(DateTimeOffset.UnixEpoch,
            Guid.Parse("25700000-0000-0000-0000-000000000001"), "controlled-daemon", "controlled-spell", DaemonEventType.Started),
            ArcanumJsonContext.Default.DaemonEvent);

        string mcpFrame = "data: " + JsonSerializer.Serialize(new McpServerEvent(DateTimeOffset.UnixEpoch)
            { ServerName = "controlled-mcp", State = McpServerState.Running }, ArcanumJsonContext.Default.McpServerEvent);

        string logFrame = "data: " + JsonSerializer.Serialize(new RetroDownfall.Arcanum.Core.Logging.LogEntry(
            257, DateTimeOffset.UnixEpoch, RetroDownfall.Arcanum.Core.Logging.LogLevel.Information,
            "MaintenanceTests", "controlled-log", null, null, null, []), ArcanumJsonContext.Default.LogEntry);

        Assert.Equal(daemonFrame, await daemon.ReadDataFrameAsync());

        Assert.Equal(mcpFrame, await mcp.ReadDataFrameAsync());

        Assert.Equal(logFrame, await logs.ReadDataFrameAsync());

        string entryFrame = await session.ReadDataFrameAsync();

        var entry = JsonSerializer.Deserialize(entryFrame[6..], ArcanumJsonContext.Default.EntryDto);

        Assert.NotNull(entry);

        Assert.Equal(harness.EntryId, entry.Id);

        Assert.Equal(harness.SessionId, entry.SessionId);

        Assert.Equal("controlled-entry", entry.Content);

        Assert.Equal("user", entry.Role);

        Assert.Null(entry.ToolCallId);

        Assert.Null(entry.ToolName);

        Assert.False(entry.IsPinned);

        // Consume the real replay/live sentinel before revocation so it cannot be mistaken
        // for a newly started data frame after the gate has revoked this request.
        Assert.Equal("data: {\"type\":\"live\"}", await session.ReadDataFrameAsync());

        string chronicleFrame = await chronicle.ReadDataFrameAsync();

        using JsonDocument plan = JsonDocument.Parse(chronicleFrame[6..]);

        Assert.Equal(new[] { "type", "apprenticeId", "timestamp", "plan" }, plan.RootElement.EnumerateObject().Select(property => property.Name));

        Assert.Equal("planGenerated", plan.RootElement.GetProperty("type").GetString());

        Assert.Equal(harness.ApprenticeId, plan.RootElement.GetProperty("apprenticeId").GetGuid());

        Assert.True(plan.RootElement.GetProperty("timestamp").TryGetDateTimeOffset(out _));

        JsonElement stepElement = Assert.Single(plan.RootElement.GetProperty("plan").EnumerateArray());

        var step = stepElement.Deserialize(ArcanumJsonContext.Default.PlanStep);

        Assert.NotNull(step);

        Assert.Equal(1, step.Index);

        Assert.Equal("controlled-plan", step.Description);

        PostAdmissionEndpointProbe[] probes = harness.SseProbes.ToArray();

        Assert.Equal(5, probes.Length);

        Assert.All(probes, probe =>
        {
            Assert.Equal(1, probe.Invocations);

            Assert.NotNull(probe.Scope);

            Assert.NotNull(probe.ObservedLease);

            Assert.True(probe.IsExecuting);
        });

        Assert.Equal(5, probes.Select(probe => probe.Scope).Distinct().Count());

        await harness.Events.Daemon.Checkpoint.WaitUntilReachedAsync();

        await harness.Events.Mcp.Checkpoint.WaitUntilReachedAsync();

        await harness.Logs.Frames.Checkpoint.WaitUntilReachedAsync();

        MaintenanceSseResponse[] responses = [daemon, mcp, logs, session, chronicle];

        return responses.Select((response, index) => new CommitStream(response, probes[index], response.Frames.ToArray())).ToArray();
    }

    private static async Task FinishCommitStreamsAsync(GrimoireMaintenanceAdmissionHarness harness,
        CommitStream[] streams, Task<HttpResponseMessage> reset)
    {
        await harness.Events.Daemon.WaitUntilCancelledAsync();

        await harness.Events.Mcp.WaitUntilCancelledAsync();

        await harness.Logs.Frames.WaitUntilCancelledAsync();

        for (int index = 0; index < streams.Length; index++)
        {
            CommitStream stream = streams[index];

            // Release the three held enumerators individually; the two hub streams quiesce
            // directly on revocation. Finite work still keeps the real stage-one drain pending.
            switch (index)
            {
                case 0:
                    harness.Events.Daemon.Checkpoint.Release();
                    break;

                case 1:
                    harness.Events.Mcp.Checkpoint.Release();
                    break;

                case 2:
                    harness.Logs.Frames.Checkpoint.Release();
                    break;
            }

            Assert.True(stream.Probe.ObservedLease!.MaintenanceRevocation.IsCancellationRequested, stream.Probe.Path);

            Assert.True(await stream.Response.ReadRevocationTerminalToEndAsync() == 1,
                $"{stream.Probe.Path} must emit exactly one complete terminal frame.");

            Assert.True(stream.InitialFrames.Concat(["data: [DONE]"]).SequenceEqual(stream.Response.Frames),
                $"{stream.Probe.Path} must retain its exact complete frames with no later data frame.");

            await stream.Probe.Scope!.WaitUntilDisposedAsync();

            Assert.False(stream.Probe.IsExecuting, stream.Probe.Path);

            await stream.Response.DisposeAsync();

            AssertStageOnePending(harness, reset);
        }

        await harness.Events.Daemon.WaitUntilDisposedAsync();

        await harness.Events.Mcp.WaitUntilDisposedAsync();

        await harness.Logs.Frames.WaitUntilDisposedAsync();

        Assert.Equal(0, harness.Factory.Services.GetRequiredService<SessionEventHub>().GetSubscriberCount(harness.SessionId));

        Assert.Equal(0, harness.Factory.Services.GetRequiredService<ChronicleHub>().TrackedApprenticeCount);
    }

    private static async Task AssertCommitRefusalsAsync(GrimoireMaintenanceAdmissionHarness harness, CancellationToken cancellationToken)
    {
        const string message = "The Grimoire is temporarily unavailable while maintenance owns connection admission.";

        int tickets = harness.Admission.Tickets.Count;

        using HttpRequestMessage stats = harness.CreateObservedRequest(HttpMethod.Get, "/api/grimoire/stats");

        using HttpResponseMessage api = await harness.Client.SendAsync(stats, cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);

        var body = await api.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseString, cancellationToken);

        Assert.False(body!.IsSuccess);

        Assert.Null(body.Data);

        Assert.Equal("Grimoire.MaintenanceUnavailable", body.Error!.Value.Code);

        Assert.Equal(message, body.Error.Value.Message);

        Assert.Equal(1, harness.StatsProbe.Invocations);

        Assert.Equal(1, harness.Stats.Callbacks);

        using HttpRequestMessage models = harness.CreateObservedRequest(HttpMethod.Get, "/v1/models");

        using HttpResponseMessage v1 = await harness.Client.SendAsync(models, cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, v1.StatusCode);

        var openAi = await v1.Content.ReadFromJsonAsync(ArcanumJsonContext.Default.OpenAiErrorResponse, cancellationToken);

        Assert.Equal("service_unavailable", openAi!.Error.Type);

        Assert.Equal("grimoire_maintenance", openAi.Error.Code);

        Assert.Equal(message, openAi.Error.Message);

        Assert.Null(openAi.Error.Param);

        Assert.Equal(0, harness.ProbeForPath("/v1/models").Invocations);

        Assert.Null(harness.ProbeForPath("/v1/models").Scope);

        Assert.Equal(tickets, harness.Admission.Tickets.Count);

        Assert.Equal(2, harness.Admission.RequestAttempts.Count(attempt => !attempt.Acquired));
    }

    private static async Task<string[]> OrdinaryIndexingSnapshotAsync(GrimoireMaintenanceAdmissionHarness harness, Guid attachmentId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await using AsyncServiceScope scope = harness.Factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync(timeout.Token);

        return await IndexingSnapshotAsync(db.Database.GetDbConnection(), attachmentId);
    }

    private static async Task<string[]> ClosedIndexingSnapshotAsync(GrimoireMaintenanceAdmissionHarness harness, Guid attachmentId, CancellationToken cancellationToken)
    {
        IGrimoireExclusiveClosedLease closed = Assert.IsAssignableFrom<IGrimoireExclusiveClosedLease>(harness.Admission.LastClosedLease);

        await using AsyncServiceScope inspection = harness.Factory.Services.CreateAsyncScope();

        ICovenantClosedPeriodLedgerConnection ledger = inspection.ServiceProvider.GetRequiredService<ICovenantClosedPeriodLedgerConnection>();

        await using IGrimoireMaintenanceIoLane lane = (await closed.AcquireMaintenanceIoLaneAsync(
            (owner, generation, _) => ValueTask.FromResult(owner == closed.Owner && generation == closed.Generation),
            cancellationToken)).Value;

        await using IGrimoireScopedConnectionPermit permit = closed.AcquireScopedConnectionPermit(ledger.Connection).Value;

        await using IGrimoireTrackedMaintenanceHandle handle = permit.AcquireOpen(
            ledger.Connection, closed.Owner, closed.Generation, lane).Value;

        Assert.True(handle.ReportOpenStarted().IsSuccess);

        try
        {
            await ledger.OpenAsync(cancellationToken);

            return await IndexingSnapshotAsync(ledger.Connection, attachmentId);
        }
        finally
        {
            await ledger.Connection.CloseAsync();

            Assert.True(harness.Drain.ClearExactPoolAfterClose((Microsoft.Data.Sqlite.SqliteConnection)ledger.Connection).IsSuccess);

            Assert.True(handle.ReportPhysicallyClosed().IsSuccess);
        }
    }

    private static async Task<string[]> IndexingSnapshotAsync(DbConnection connection, Guid attachmentId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        List<string> rows = [];

        foreach (string table in new[] { "SessionAttachments", "session_attachment_index_state", "session_attachment_chunks" })
        {
            await using DbCommand command = connection.CreateCommand();

            string key = table == "SessionAttachments" ? "Id" : "AttachmentId";

            command.CommandText = $"SELECT * FROM {table} WHERE {key} = @id ORDER BY 1";

            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = "@id";

            parameter.Value = attachmentId.ToString().ToUpperInvariant();

            command.Parameters.Add(parameter);

            await using DbDataReader reader = await command.ExecuteReaderAsync(timeout.Token);

            int count = 0;

            while (await reader.ReadAsync(timeout.Token))
            {
                string[] values = Enumerable.Range(0, reader.FieldCount).Select(index =>
                {
                    if (reader.IsDBNull(index))
                    {
                        return "null";
                    }

                    string value = Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)!;

                    return $"{reader.GetName(index)}:{value.Length}:{value}";
                }).ToArray();

                rows.Add($"{table}:{string.Join('|', values)}");

                count++;
            }

            rows.Add($"{table}:count:{count}");
        }

        return rows.ToArray();
    }


}
