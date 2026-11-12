using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Tree_stream_starts_with_authenticated_snapshot_and_opaque_resume_id()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Stream root", "Inspect stream",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Guid sessionId = accepted.RootElement.GetProperty("data").GetProperty("sessionId").GetGuid();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        using HttpRequestMessage conflicting = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events?after=first");

        conflicting.Headers.TryAddWithoutValidation("Last-Event-ID", "second");

        using HttpResponseMessage refused = await client.SendAsync(conflicting, timeout.Token);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using HttpRequestMessage watch = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events");

        using HttpResponseMessage response = await client.SendAsync(watch, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);

        using StreamReader reader = new(stream);

        string? eventName = null;

        string? id = null;

        string? data = null;

        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];

            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];

            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }

        Assert.Equal("snapshot", eventName);

        Assert.False(string.IsNullOrWhiteSpace(id));

        Assert.NotNull(data);

        using JsonDocument snapshot = JsonDocument.Parse(data);

        Assert.Equal(questId, snapshot.RootElement.GetProperty("summary").GetProperty("id").GetGuid());

        Assert.Equal(id, snapshot.RootElement.GetProperty("resumeCursor").GetString());

        using HttpResponseMessage wrongScope = await client.GetAsync(
            $"/api/guild/quests/{Guid.NewGuid():D}/events?after={Uri.EscapeDataString(id!)}", timeout.Token);

        Assert.Equal(HttpStatusCode.BadRequest, wrongScope.StatusCode);

        using HttpResponseMessage missingCohort = await client.GetAsync($"/api/guild/cohorts/{Guid.NewGuid():D}/events", timeout.Token);

        Assert.Equal(HttpStatusCode.NotFound, missingCohort.StatusCode);

        IGrimoireOrdinaryConnectionFactory connections = factory.Services.GetRequiredService<IGrimoireOrdinaryConnectionFactory>();

        Result<IGrimoireOrdinaryConnectionLease> opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, timeout.Token);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using (IGrimoireOrdinaryConnectionLease lease = opened.Value)
        {
            using CovenantSqliteAuthorizationScope deletion = CovenantSqliteConnectionInitializer.Instance.Authorize(
                lease.Connection, CovenantSqliteAuthorizationKind.SessionRetention);

            await using var command = lease.Connection.CreateCommand();

            command.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            command.Parameters.AddWithValue("$id", sessionId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await command.ExecuteNonQueryAsync(timeout.Token));
        }

        using HttpResponseMessage erasedCursor = await client.GetAsync(
            $"/api/guild/quests/{questId:D}/events?after={Uri.EscapeDataString(id!)}", timeout.Token);

        Assert.Equal(HttpStatusCode.Gone, erasedCursor.StatusCode);

        using HttpResponseMessage missingOwner = await client.GetAsync($"/api/guild/quests/{questId:D}/events", timeout.Token);

        Assert.Equal(HttpStatusCode.NotFound, missingOwner.StatusCode);
    }
    [SkippableFact]
    public async Task Concurrent_tree_streams_obey_the_shared_guild_connection_limit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Execution.MaxSseConnectionsPerType = 1;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), null, "Stream root", "Inspect streams",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        using HttpRequestMessage firstRequest = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events");

        using HttpResponseMessage first = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpRequestMessage secondRequest = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events");

        using HttpResponseMessage second = await client.SendAsync(secondRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tree_stream_sends_resync_control_when_history_or_visibility_changes(bool changeVisibility)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), null, "Stream root", "Inspect stream gaps",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        using HttpRequestMessage watch = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events");

        using HttpResponseMessage response = await client.SendAsync(watch, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);

        using StreamReader reader = new(body);

        string? initialName = null;

        string? initialId = null;

        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) initialName = line[7..];

            if (line.StartsWith("id: ", StringComparison.Ordinal)) initialId = line[4..];
        }

        Assert.Equal("snapshot", initialName);

        Assert.False(string.IsNullOrWhiteSpace(initialId));

        SignetAuthority authority = factory.Services.GetRequiredService<SignetAuthority>();

        Guid source = Guid.NewGuid();

        Result<long> gap = await authority.InQuestTransactionAsync<long>(questId, async (connection, transaction, _, token) =>
        {
            if (changeVisibility)
            {
                await using var clock = connection.CreateCommand();

                clock.Transaction = transaction;

                clock.CommandText = "INSERT INTO artifact_sensitivity_clock(Id,Revision) VALUES(1,1) ON CONFLICT(Id) DO UPDATE SET Revision=Revision+1";

                await clock.ExecuteNonQueryAsync(token);

                return Result<long>.Success(1);
            }

            Result<long> appended = await GuildPortalEvents.AppendWithinAsync(connection, transaction, questId, questId,
                GuildEventKind.QuestChanged, source, 1, null, DateTimeOffset.UtcNow, token);

            if (appended.IsFailure) return appended.Error;

            await using var command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "DELETE FROM guild_events WHERE RootQuestId=$root AND EventSequence=$sequence";

            command.Parameters.AddWithValue("$root", questId.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$sequence", appended.Value);

            await command.ExecuteNonQueryAsync(token);

            return appended.Value;
        }, timeout.Token);

        Assert.True(gap.IsSuccess, gap.Error.Message);

        string? eventName = null;

        string? id = null;

        string? data = null;

        while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];

            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];

            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }

        Assert.Equal("resync", eventName);

        Assert.False(string.IsNullOrWhiteSpace(id));

        Assert.NotNull(data);

        using JsonDocument control = JsonDocument.Parse(data);

        Assert.Equal("Guild.CursorGone", control.RootElement.GetProperty("reasonCode").GetString());

        Assert.Equal($"/api/guild/quests/{questId:D}/tree", control.RootElement.GetProperty("snapshotRoute").GetString());

        Assert.Equal(id, control.RootElement.GetProperty("resumeCursor").GetString());

        Assert.Null(await reader.ReadLineAsync(timeout.Token));

        using HttpResponseMessage expired = await client.GetAsync(
            $"/api/guild/quests/{questId:D}/events?after={Uri.EscapeDataString(initialId!)}", timeout.Token);

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [SkippableFact]
    public async Task Guild_stream_respects_global_slot_held_by_chronicle()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Execution.MaxSseConnections = 1;

                settings.Execution.MaxSseConnectionsPerType = 2;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), null, "Global stream root", "Inspect global admission",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        SseConnectionGate gate = factory.Services.GetRequiredService<SseConnectionGate>();

        Assert.True(gate.TryAcquire(SseEventTypes.Chronicle, out SseConnectionLease? chronicle, out _));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        using HttpResponseMessage denied = await client.GetAsync($"/api/guild/quests/{questId:D}/events", timeout.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);

        chronicle!.Dispose();

        using HttpRequestMessage watch = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events");

        using HttpResponseMessage admitted = await client.SendAsync(watch, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
    }

    [SkippableFact]
    public async Task Tree_and_cohort_streams_share_one_counter_distinct_from_chronicle()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Execution.MaxSseConnections = 3;

                settings.Execution.MaxSseConnectionsPerType = 1;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), null, "Issuer", "Coordinate streams",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)),
                [GuildDelegationKind.Local, GuildDelegationKind.Cohort], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        CreateCohortRequest request = new(Guid.NewGuid(), questId, "Watchers", new(GuildConvergenceKind.All, null), new("text/plain"), null);

        using HttpResponseMessage created = await client.PostAsync("/api/guild/cohorts",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.CreateCohortRequest)));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using JsonDocument cohort = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        Guid cohortId = cohort.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        SseConnectionGate gate = factory.Services.GetRequiredService<SseConnectionGate>();

        Assert.True(gate.TryAcquire(SseEventTypes.Chronicle, out SseConnectionLease? chronicle, out _));

        using (chronicle)
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15)))
        using (HttpRequestMessage cohortRequest = new(HttpMethod.Get, $"/api/guild/cohorts/{cohortId:D}/events"))
        using (HttpResponseMessage stream = await client.SendAsync(cohortRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);

            await using Stream body = await stream.Content.ReadAsStreamAsync(timeout.Token);

            using StreamReader reader = new(body);

            string? eventName = null;

            string? data = null;

            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];

                if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
            }

            Assert.Equal("snapshot", eventName);

            Assert.NotNull(data);

            using JsonDocument snapshot = JsonDocument.Parse(data);

            Assert.Equal(cohortId, snapshot.RootElement.GetProperty("summary").GetProperty("id").GetGuid());

            using HttpResponseMessage blocked = await client.GetAsync($"/api/guild/quests/{questId:D}/events", timeout.Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        }
    }



}
