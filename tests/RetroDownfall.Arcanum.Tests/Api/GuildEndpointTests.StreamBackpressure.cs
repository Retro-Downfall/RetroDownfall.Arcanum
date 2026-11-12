using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stalled_tree_response_replays_retained_history_or_signals_a_removed_event(bool removeEvent)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        ResponseWriteBarrier barrier = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Execution.MaxSseConnections = 2;

                settings.Execution.MaxSseConnectionsPerType = 2;

                return settings;
            },
            ServiceOverrides = services =>
            {
                HoldExecution(services);

                services.AddSingleton<IStartupFilter>(new StallTreeResponseFilter(barrier));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), null, "Stream root", "Verify stalled replay",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));

        using HttpRequestMessage watch = new(HttpMethod.Get, $"/api/guild/quests/{questId:D}/events");

        watch.Headers.Add("X-Test-Stall-Guild-Response", "1");

        using HttpResponseMessage response = await client.SendAsync(watch, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);

        using StreamReader reader = new(body);

        var snapshot = await ReadFrameAsync(reader, timeout.Token);

        Assert.Equal("snapshot", snapshot.Name);

        Assert.False(string.IsNullOrWhiteSpace(snapshot.Id));

        SignetAuthority authority = factory.Services.GetRequiredService<SignetAuthority>();

        Guid firstId = Guid.NewGuid();

        Guid secondId = Guid.NewGuid();

        Guid thirdId = Guid.NewGuid();

        try
        {
            long firstSequence = await AppendTreeChangeAsync(authority, questId, firstId, timeout.Token);

            await barrier.Entered.Task.WaitAsync(timeout.Token);

            long secondSequence = await AppendTreeChangeAsync(authority, questId, secondId, timeout.Token);

            long thirdSequence = await AppendTreeChangeAsync(authority, questId, thirdId, timeout.Token);

            Assert.True(firstSequence < secondSequence && secondSequence < thirdSequence);

            if (removeEvent)
            {
                Result<long> removed = await authority.InQuestTransactionAsync<long>(questId, async (connection, transaction, _, token) =>
                {
                    await using var command = connection.CreateCommand();

                    command.Transaction = transaction;

                    command.CommandText = "DELETE FROM guild_events WHERE RootQuestId=$root AND EventSequence=$sequence";

                    command.Parameters.AddWithValue("$root", questId.ToString("D").ToUpperInvariant());

                    command.Parameters.AddWithValue("$sequence", secondSequence);

                    return await command.ExecuteNonQueryAsync(token);
                }, timeout.Token);

                Assert.True(removed.IsSuccess, removed.Error.Message);

                Assert.Equal(1, removed.Value);
            }

            barrier.Release.TrySetResult();

            var first = await ReadFrameAsync(reader, timeout.Token);

            Assert.Equal("change", first.Name);

            Assert.Equal(firstId, ChangeObjectId(first.Data));

            Assert.False(string.IsNullOrWhiteSpace(first.Id));

            if (removeEvent)
            {
                var resync = await ReadFrameAsync(reader, timeout.Token);

                Assert.Equal("resync", resync.Name);

                using JsonDocument control = JsonDocument.Parse(resync.Data!);

                Assert.Equal("Guild.CursorGone", control.RootElement.GetProperty("reasonCode").GetString());

                Assert.Null(await reader.ReadLineAsync(timeout.Token));

                using HttpResponseMessage stale = await client.GetAsync(
                    $"/api/guild/quests/{questId:D}/events?after={Uri.EscapeDataString(first.Id!)}", timeout.Token);

                Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);
            }
            else
            {
                var second = await ReadFrameAsync(reader, timeout.Token);

                var third = await ReadFrameAsync(reader, timeout.Token);

                Assert.Equal("change", second.Name);

                Assert.Equal(secondId, ChangeObjectId(second.Data));

                Assert.Equal("change", third.Name);

                Assert.Equal(thirdId, ChangeObjectId(third.Data));

                Assert.NotEqual(first.Id, second.Id);

                Assert.NotEqual(second.Id, third.Id);

                using HttpRequestMessage reconnect = new(HttpMethod.Get,
                    $"/api/guild/quests/{questId:D}/events?after={Uri.EscapeDataString(first.Id!)}");

                using HttpResponseMessage replay = await client.SendAsync(reconnect, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

                await using Stream replayBody = await replay.Content.ReadAsStreamAsync(timeout.Token);

                using StreamReader replayReader = new(replayBody);

                var replaySecond = await ReadFrameAsync(replayReader, timeout.Token);

                var replayThird = await ReadFrameAsync(replayReader, timeout.Token);

                Assert.Equal(("change", secondId), (replaySecond.Name, ChangeObjectId(replaySecond.Data)));

                Assert.Equal(("change", thirdId), (replayThird.Name, ChangeObjectId(replayThird.Data)));
            }
        }
        finally
        {
            barrier.Release.TrySetResult();
        }
    }

    private static async Task<long> AppendTreeChangeAsync(SignetAuthority authority, Guid root, Guid objectId, CancellationToken ct)
    {
        Result<long> result = await authority.InQuestTransactionAsync<long>(root, (connection, transaction, _, token) =>
            GuildPortalEvents.AppendWithinAsync(connection, transaction, root, root, GuildEventKind.QuestChanged,
                objectId, 1, null, DateTimeOffset.UtcNow, token), ct);

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static Guid ChangeObjectId(string? data)
    {
        Assert.NotNull(data);

        using JsonDocument change = JsonDocument.Parse(data);

        return change.RootElement.GetProperty("objectId").GetGuid();
    }

    private static async Task<(string? Name, string? Id, string? Data)> ReadFrameAsync(StreamReader reader, CancellationToken ct)
    {
        string? name = null;

        string? id = null;

        string? data = null;

        string? line;

        while ((line = await reader.ReadLineAsync(ct)) is { Length: > 0 })
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];

            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];

            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }

        Assert.NotNull(line);

        return (name, id, data);
    }

    private sealed class ResponseWriteBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StallTreeResponseFilter(ResponseWriteBarrier barrier) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (!context.Request.Headers.ContainsKey("X-Test-Stall-Guild-Response"))
                {
                    await continuation(context);

                    return;
                }

                Stream original = context.Response.Body;

                context.Response.Body = new StallChangeStream(original, barrier);

                try { await continuation(context); }
                finally { context.Response.Body = original; }
            });

            next(app);
        };
    }

    private sealed class StallChangeStream(Stream inner, ResponseWriteBarrier barrier) : Stream
    {
        private bool _stalled;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_stalled && Encoding.UTF8.GetString(buffer.Span).StartsWith("event: change\n", StringComparison.Ordinal))
            {
                _stalled = true;

                barrier.Entered.TrySetResult();

                await barrier.Release.Task.WaitAsync(cancellationToken);
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }
    }
}
