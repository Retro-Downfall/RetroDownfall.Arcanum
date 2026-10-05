using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ApprenticeEndpointTests
{
    private readonly ArcanumWebApplicationFactory _factory;

    public ApprenticeEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task PostApprentices_WithValidBody_ReturnsCreatedApprentice()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateApprenticeRequest request = new(
            Name: "Integration Apprentice",
            Goal: "Verify apprentice endpoints",
            WorkspacePath: _factory.TempHome);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateApprenticeRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/apprentices",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal("Integration Apprentice", body.Data.Name);

        Assert.Equal("Verify apprentice endpoints", body.Data.Goal);

        HttpResponseMessage getResponse = await client.GetAsync($"/api/apprentices/{body.Data.Id:D}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/apprentices/{body.Data.Id:D}");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [SkippableFact]
    public async Task PostApprentices_EmptyName_ReturnsBadRequest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateApprenticeRequest request = new(
            Name: "   ",
            Goal: "goal",
            WorkspacePath: _factory.TempHome);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateApprenticeRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/apprentices",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Apprentice.InvalidName", body.Error?.Code);
    }

    [SkippableFact]
    public async Task GetApprentice_MissingId_ReturnsNotFound()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/apprentices/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Apprentice.NotFound", body.Error?.Code);
    }

    [SkippableFact]
    public async Task PostApprenticeStart_MissingId_ReturnsNotFound()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/apprentices/{Guid.NewGuid():D}/start",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task PostApprenticeReweave_EmptyPlan_ReturnsBadRequest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateApprenticeRequest create = new(
            Name: "Reweave target",
            Goal: "Plan validation",
            WorkspacePath: _factory.TempHome);

        string createPayload = JsonSerializer.Serialize(create, ArcanumJsonContext.Default.CreateApprenticeRequest);

        HttpResponseMessage createResponse = await client.PostAsync(
            "/api/apprentices",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));

        ApiResponse<ApprenticeDetailDto>? created = JsonSerializer.Deserialize(
            await createResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(created?.Data);

        ReweaveApprenticeRequest reweave = new([]);

        string reweavePayload = JsonSerializer.Serialize(reweave, ArcanumJsonContext.Default.ReweaveApprenticeRequest);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/apprentices/{created.Data.Id:D}/reweave",
            new StringContent(reweavePayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await client.DeleteAsync($"/api/apprentices/{created.Data.Id:D}");
    }

    /// <summary>
    /// A page that is one timestamp wider than the tie-group bound is the server's problem to report, not
    /// something to clip: the route answers a logged 500 <c>Hub.Unhandled</c> and never echoes the
    /// repository's message.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/apprentices</c> has no catch of its own, so the repository's
    /// <see cref="InvalidOperationException"/> reaches <c>ArcanumExceptionHandler</c>. The repository throw
    /// and the handler arm are each pinned on their own; this is the one place the route's status is.
    /// <c>docs/Arcanum.API.md</c> and DESIGN section 19.6 state it as contract. The group is seeded through
    /// EF (the write lane) in a factory of its own so no other test in the shared host ever lists it.
    /// </remarks>
    [SkippableFact]
    public async Task GetApprentices_a_tie_group_past_the_widening_bound_is_a_logged_500_Hub_Unhandled()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingLoggerProvider recording = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<ILoggerFactory>();

                services.AddSingleton<ILoggerFactory>(new LoggerFactory([recording]));
            },
        };

        DateTimeOffset tied = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            for (int index = 0; index < ApprenticeRepository.MaxTieGroupWidening + 1; index++)
            {
                db.Apprentices.Add(
                    new Apprentice
                    {
                        Id = Guid.NewGuid(),
                        Name = "tied-" + index,
                        Goal = "Share one timestamp",
                        Status = ApprenticeStatus.Idle.ToString(),
                        WorkspacePath = factory.TempHome,
                        CreatedAt = tied,
                        UpdatedAt = tied,
                    });
            }

            _ = await db.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/apprentices?limit=1");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<string>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Hub.Unhandled, body.Error?.Code);

        Assert.DoesNotContain("share the timestamp", json, StringComparison.Ordinal);

        Assert.Contains(
            recording.Entries,
            static entry => entry.Level == LogLevel.Error
                && entry.Exception is InvalidOperationException failure
                && failure.Message.Contains("share the timestamp", StringComparison.Ordinal)
                && entry.Message.Contains("Unhandled exception on GET /api/apprentices", StringComparison.Ordinal));
    }

    /// <summary>
    /// A client that stops reading is told, in its own stream, that events were dropped for it.
    /// </summary>
    /// <remarks>
    /// The route puts a buffer between the hub subscription and the response. That buffer used to drop
    /// its oldest frames without saying so, and the hub's own channel, which is where overflow is
    /// reported, never filled because the pump drained it as fast as it could. The events are large so
    /// the transport stops accepting frames after a few of them and the buffer is what overflows.
    /// </remarks>
    [SkippableFact]
    public async Task Chronicle_slow_reader_receives_eventsDropped_marker()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        Guid apprenticeId = await CreateApprenticeAsync(client);

        ChronicleHub hub = _factory.Services.GetRequiredService<ChronicleHub>();

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/apprentices/{apprenticeId:D}/chronicle",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using Stream stream = await response.Content.ReadAsStreamAsync();

        for (int attempt = 0; attempt < 200 && hub.TrackedApprenticeCount == 0; attempt++)
        {
            await Task.Delay(25);
        }

        Assert.True(hub.TrackedApprenticeCount > 0, "The chronicle route never subscribed to the hub.");

        string bulk = new('x', 4096);

        // More than the route's buffer holds (the EventBus channel capacity, 256 by default) and fewer than
        // the hub's own channel does, so any marker has to come from the route's buffer.
        for (int index = 0; index < 600; index++)
        {
            hub.Publish(
                apprenticeId,
                new ApprenticeEvent
                {
                    Type = ApprenticeEventType.StepCompleted,
                    ApprenticeId = apprenticeId,
                    Timestamp = DateTimeOffset.UtcNow,
                    StepIndex = index,
                    Result = bulk,
                });
        }

        await Task.Delay(500);

        bool markerSeen = false;

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));

        using StreamReader reader = new(stream, Encoding.UTF8);

        try
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Contains("\"type\":\"eventsDropped\"", StringComparison.Ordinal))
                {
                    markerSeen = true;

                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(markerSeen, "The slow reader was never told that events were dropped for it.");
    }

    private async Task<Guid> CreateApprenticeAsync(HttpClient client)
    {
        CreateApprenticeRequest request = new(
            Name: "Chronicle slow reader",
            Goal: "Observe the stream",
            WorkspacePath: _factory.TempHome);

        HttpResponseMessage response = await client.PostAsync(
            "/api/apprentices",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateApprenticeRequest),
                Encoding.UTF8,
                "application/json"));

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body?.Data);

        return body.Data.Id;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<RecordedLog> _entries = new();

        public IReadOnlyCollection<RecordedLog> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<RecordedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new RecordedLog(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record RecordedLog(LogLevel Level, string Message, Exception? Exception);
}
