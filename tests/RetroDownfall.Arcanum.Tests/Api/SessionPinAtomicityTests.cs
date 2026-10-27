using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SessionPinAtomicityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task Concurrent_admitted_scopes_cannot_both_claim_the_last_pin_slot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        PinObserver observer = new() { Race = true };
        await using ArcanumWebApplicationFactory factory = Factory(observer);
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, Guid second) = await SeedAsync(factory, pinned: 9);
        Task<HttpResponseMessage> a = client.PostAsync($"/api/sessions/{session:D}/entries/{first:D}/pin", null);
        Task<HttpResponseMessage> b = client.PostAsync($"/api/sessions/{session:D}/entries/{second:D}/pin", null);
        HttpResponseMessage[] responses = await Task.WhenAll(a, b).WaitAsync(Deadline);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(response => response.StatusCode).OrderBy(status => status));
        Assert.Equal(10, await CountPinnedAsync(factory, session));
        HttpResponseMessage refusal = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        ApiResponse<bool>? envelope = JsonSerializer.Deserialize(await refusal.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseBoolean);
        Assert.Equal(ErrorCodes.Session.TooManyPinned, envelope?.Error?.Code);
        Assert.Equal(2, observer.SetCalls);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Entry_deleted_at_the_mutation_boundary_returns_404_for_pin_and_unpin(bool pin)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        PinObserver observer = new() { DeleteBeforeSet = true };
        await using ArcanumWebApplicationFactory factory = Factory(observer);
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, _) = await SeedAsync(factory, pinned: 0);
        string path = $"/api/sessions/{session:D}/entries/{first:D}/pin";
        HttpResponseMessage response = pin ? await client.PostAsync(path, null) : await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        ApiResponse<bool>? envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseBoolean);
        Assert.Equal(ErrorCodes.Session.EntryNotFound, envelope?.Error?.Code);
        Assert.True(observer.Deleted);
    }

    [SkippableFact]
    public async Task Idempotent_pin_at_cap_and_repeated_unpin_preserve_the_limit_and_session_boundary()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        await using ArcanumWebApplicationFactory factory = Factory(new PinObserver());
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, Guid second) = await SeedAsync(factory, pinned: 9);
        string path = $"/api/sessions/{session:D}/entries/{first:D}/pin";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(path, null)).StatusCode);
        Assert.Equal(10, await CountPinnedAsync(factory, session));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/sessions/{session:D}/entries/{second:D}/pin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/sessions/{Guid.NewGuid():D}/entries/{first:D}/pin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(9, await CountPinnedAsync(factory, session));
    }

    [SkippableFact]
    public async Task A_competing_SQLite_writer_bypassing_the_local_session_lock_cannot_overbook_the_last_slot()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        PinObserver observer = new();
        await using ArcanumWebApplicationFactory factory = Factory(observer);
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, Guid second) = await SeedAsync(factory, pinned: 9);
        observer.BeforeMutation = async token =>
        {
            using IServiceScope scope = factory.Services.CreateScope();
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
            await db.Database.OpenConnectionAsync(token);
            await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "UPDATE \"Entries\" SET \"IsPinned\" = 1 WHERE \"Id\" = $id AND \"SessionId\" = $session;";
            System.Data.Common.DbParameter entryParameter = command.CreateParameter();
            entryParameter.ParameterName = "$id";
            entryParameter.Value = second.ToString("D").ToUpperInvariant();
            command.Parameters.Add(entryParameter);
            System.Data.Common.DbParameter sessionParameter = command.CreateParameter();
            sessionParameter.ParameterName = "$session";
            sessionParameter.Value = session.ToString("D").ToUpperInvariant();
            command.Parameters.Add(sessionParameter);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        };
        HttpResponseMessage response = await client.PostAsync($"/api/sessions/{session:D}/entries/{first:D}/pin", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(10, await CountPinnedAsync(factory, session));
    }

    [SkippableFact]
    public async Task Cancellation_before_atomic_repository_admission_leaves_the_pin_unchanged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        await using ArcanumWebApplicationFactory factory = Factory(new PinObserver());
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, _) = await SeedAsync(factory, pinned: 0);
        using IServiceScope scope = factory.Services.CreateScope();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IGrimoireRepository>()
            .SetEntryPinnedWithinLimitAsync(session, first, true, 10, cancellation.Token));
        Assert.Equal(0, await CountPinnedAsync(factory, session));
    }

    [SkippableFact]
    public async Task Cancellation_after_the_SQL_update_rolls_back_before_the_request_scope_finishes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        PinObserver observer = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        await using ArcanumWebApplicationFactory factory = Factory(observer);
        factory.AdditionalDbContextInterceptors = [new PausePinInterceptor(entered, release)];
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, _) = await SeedAsync(factory, pinned: 0);
        using (IServiceScope setup = factory.Services.CreateScope())
        {
            ArcanumDbContext db = setup.ServiceProvider.GetRequiredService<ArcanumDbContext>();
            await db.Database.OpenConnectionAsync();
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                CREATE TRIGGER test_pin_pause AFTER UPDATE OF "IsPinned" ON "Entries"
                BEGIN SELECT arcanum_test_pause_pin(); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        using CancellationTokenSource cancellation = new();
        Task<HttpResponseMessage> send = Task.Run(() => client.PostAsync(
            $"/api/sessions/{session:D}/entries/{first:D}/pin", null, cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await cancellation.CancelAsync();
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await observer.MutationSettled.Task.WaitAsync(Deadline);
        Assert.Equal(0, await CountPinnedAsync(factory, session));
    }

    [SkippableFact]
    public async Task A_statement_that_already_rolled_back_keeps_its_original_SQLite_failure_diagnosable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        await using ArcanumWebApplicationFactory factory = Factory(new PinObserver());
        using HttpClient client = factory.CreateAuthenticatedClient();
        (Guid session, Guid first, _) = await SeedAsync(factory, pinned: 0);
        using IServiceScope scope = factory.Services.CreateScope();
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
        await db.Database.OpenConnectionAsync();
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            CREATE TRIGGER test_pin_abort BEFORE UPDATE OF "IsPinned" ON "Entries"
            BEGIN SELECT RAISE(ROLLBACK, 'fixed-private-pin-failure-canary'); END;
            """;
        await command.ExecuteNonQueryAsync();
        Exception? failure = await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<IGrimoireRepository>()
            .SetEntryPinnedWithinLimitAsync(session, first, true, 10));
        SqliteException original = Assert.IsType<SqliteException>(failure);
        Assert.Contains("fixed-private-pin-failure-canary", original.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountPinnedAsync(factory, session));
    }

    private sealed class PausePinInterceptor(TaskCompletionSource entered, ManualResetEventSlim release) : DbConnectionInterceptor
    {
        private void Register(DbConnection connection) => ((SqliteConnection)connection).CreateFunction("arcanum_test_pause_pin", () =>
        {
            entered.TrySetResult();
            if (!release.Wait(Deadline)) throw new TimeoutException("The controlled pin update was not released.");
            return 1;
        });
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Register(connection);
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Register(connection);
            return Task.CompletedTask;
        }
    }

    internal static ArcanumWebApplicationFactory Factory(PinObserver observer) => new()
    {
        SettingsOverride = settings => settings with { Features = settings.Features with { MemoryManagement = true } },
        ServiceOverrides = services =>
        {
            ServiceDescriptor registration = services.Last(descriptor => descriptor.ServiceType == typeof(IGrimoireRepository));
            Func<IServiceProvider, object> compose = registration.ImplementationFactory ?? throw new InvalidOperationException("Expected the explicit admitted repository factory.");
            services.Replace(ServiceDescriptor.Scoped<IGrimoireRepository>(provider => new ObservedRepository((IGrimoireRepository)compose(provider), observer)));
        },
    };

    private static async Task<(Guid Session, Guid First, Guid Second)> SeedAsync(ArcanumWebApplicationFactory factory, int pinned)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
        Guid sessionId = Guid.NewGuid();
        Session session = new() { Id = sessionId, Title = "pin admission", Status = "active", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Sessions.Add(session);
        Entry[] entries = Enumerable.Range(0, pinned + 2).Select(index => new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = "entry " + index,
            ModelUsed = "test-model",
            CreatedAt = DateTimeOffset.UtcNow,
            Sequence = index + 1,
            IsPinned = index < pinned,
        }).ToArray();
        db.Entries.AddRange(entries);
        await db.SaveChangesAsync();
        return (sessionId, entries[^2].Id, entries[^1].Id);
    }

    private static async Task<int> CountPinnedAsync(ArcanumWebApplicationFactory factory, Guid session)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();
        return await db.Entries.CountAsync(entry => entry.SessionId == session && entry.IsPinned);
    }

    internal sealed class PinObserver
    {
        public TaskCompletionSource MutationSettled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Race { get; init; }
        public bool DeleteBeforeSet { get; init; }
        public Func<CancellationToken, Task>? BeforeMutation { get; set; }
        public Func<Guid, CancellationToken, Task>? AfterSessionRead { get; set; }
        private int _beforeMutation;

        public bool Deleted => Volatile.Read(ref _deleted) != 0;

        public int SetCalls => Volatile.Read(ref _sets);

        private int _counts;

        private int _sets;

        private int _deleted;
        private readonly TaskCompletionSource _counted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _setting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CountedAsync(CancellationToken token)
        {
            if (!Race) return;
            if (Interlocked.Increment(ref _counts) == 2) _counted.TrySetResult();
            await _counted.Task.WaitAsync(token);
        }

        public async Task BeforeSetAsync(IGrimoireRepository inner, Guid session, Guid entry, CancellationToken token)
        {
            await DeleteIfRequestedAsync(inner, session, entry, token);
            if (BeforeMutation is { } before && Interlocked.Exchange(ref _beforeMutation, 1) == 0)
            {
                await before(token);
            }

            if (!Race) return;
            if (Interlocked.Increment(ref _sets) == 2) _setting.TrySetResult();
            await _setting.Task.WaitAsync(token);
        }

        public async Task DeleteIfRequestedAsync(IGrimoireRepository inner, Guid session, Guid entry, CancellationToken token)
        {
            if (DeleteBeforeSet && Interlocked.Exchange(ref _deleted, 1) == 0)
            {
                Assert.True(await inner.DeleteEntryAsync(session, entry, token));
            }
        }
    }

    private sealed class ObservedRepository(IGrimoireRepository inner, PinObserver observer) : IGrimoireRepository
    {
        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
        Guid? sessionId,
        string prompt,
        string model,
        CancellationToken cancellationToken = default)
        => inner.BeginAssistantReplyAsync(sessionId, prompt, model, cancellationToken);

        public Task FinalizeAssistantEntryAsync(
        Guid assistantEntryId,
        string fullContent,
        CancellationToken cancellationToken = default)
        => inner.FinalizeAssistantEntryAsync(assistantEntryId, fullContent, cancellationToken);

        public Task DiscardAssistantEntryAsync(
        Guid assistantEntryId,
        CancellationToken cancellationToken = default)
        => inner.DiscardAssistantEntryAsync(assistantEntryId, cancellationToken);

        public Task AppendToolInteractionAsync(
        Guid sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken = default)
        => inner.AppendToolInteractionAsync(sessionId, toolName, arguments, result, modelUsed, cancellationToken);

        public Task SaveCompletedExchangeAsync(
        string userPrompt,
        string assistantText,
        string modelUsed,
        CancellationToken cancellationToken = default)
        => inner.SaveCompletedExchangeAsync(userPrompt, assistantText, modelUsed, cancellationToken);

        public async Task<Session?> GetSessionAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            Session? session = await inner.GetSessionAsync(id, cancellationToken);
            if (observer.AfterSessionRead is { } after) await after(id, cancellationToken);
            return session;
        }

        public Task<Session?> GetSessionHeaderAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => inner.GetSessionHeaderAsync(id, cancellationToken);

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => inner.GetSessionEntriesAsync(sessionId, cancellationToken);

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
        Guid sessionId,
        int takeLast,
        CancellationToken cancellationToken = default)
        => inner.GetRecentSessionEntriesAsync(sessionId, takeLast, cancellationToken);

        public async Task<GrimoireEntryDto?> GetEntryByIdAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default)
        {
            GrimoireEntryDto? entry = await inner.GetEntryByIdAsync(sessionId, entryId, cancellationToken);
            await observer.DeleteIfRequestedAsync(inner, sessionId, entryId, cancellationToken);
            return entry;
        }

        public Task<bool> DeleteEntryAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default)
        => inner.DeleteEntryAsync(sessionId, entryId, cancellationToken);

        public async Task<bool> SetEntryPinnedAsync(
        Guid sessionId,
        Guid entryId,
        bool pinned,
        CancellationToken cancellationToken = default)
        {
            await observer.BeforeSetAsync(inner, sessionId, entryId, cancellationToken);
            return await inner.SetEntryPinnedAsync(sessionId, entryId, pinned, cancellationToken);
        }

        public async Task<Result<bool>> SetEntryPinnedWithinLimitAsync(
            Guid sessionId,
            Guid entryId,
            bool pinned,
            int maxPinnedEntries,
            CancellationToken cancellationToken = default)
        {
            await observer.BeforeSetAsync(inner, sessionId, entryId, cancellationToken);
            try
            {
                return await inner.SetEntryPinnedWithinLimitAsync(sessionId, entryId, pinned, maxPinnedEntries, cancellationToken);
            }
            finally
            {
                observer.MutationSettled.TrySetResult();
            }
        }

        public Task<Result<SessionCompactionModelSnapshot?>> ReadSessionCompactionModelAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.ReadSessionCompactionModelAsync(sessionId, cancellationToken);

        public async Task<int> GetPinnedEntryCountAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        {
            int count = await inner.GetPinnedEntryCountAsync(sessionId, cancellationToken);
            await observer.CountedAsync(cancellationToken);
            return count;
        }

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
        int threshold,
        DateTime idleCutoff,
        CancellationToken cancellationToken = default)
        => inner.GetSessionsNeedingSummarizationAsync(threshold, idleCutoff, cancellationToken);

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(
        Guid sessionId,
        DateTime watermark,
        int batchSize,
        CancellationToken cancellationToken = default)
        => inner.GetUnsummarizedEntriesAsync(sessionId, watermark, batchSize, cancellationToken);

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => inner.SessionExistsAsync(sessionId, cancellationToken);

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default)
        => inner.IncrementSessionTokensAsync(sessionId, totalTokens, cancellationToken);

        public Task IncrementSessionTokensAndCostAsync(
        Guid sessionId,
        long totalTokens,
        decimal costUsd,
        CancellationToken cancellationToken = default)
        => inner.IncrementSessionTokensAndCostAsync(sessionId, totalTokens, costUsd, cancellationToken);

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default)
        => inner.GetTodaySpendAsync(cancellationToken);

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => inner.AdvanceCampaignLogWatermarkAsync(sessionId, cancellationToken);

        public Task UpdateSessionCampaignRollupAsync(
        Guid sessionId,
        string summary,
        DateTime lastSummarizedMessageAt,
        CancellationToken cancellationToken = default)
        => inner.UpdateSessionCampaignRollupAsync(sessionId, summary, lastSummarizedMessageAt, cancellationToken);

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default)
        => inner.ReadLoreAsync(key, cancellationToken);

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default)
        => inner.ScribeLoreAsync(key, value, cancellationToken);

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
        => inner.DeleteLoreAsync(key, cancellationToken);

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
        => inner.ListLoreAsync(limit, offset, cancellationToken);

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default)
        => inner.GetLoreAsync(key, cancellationToken);

        public Task<Result<string>> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        => inner.SearchArchivesAsync(query, maxResults, cancellationToken);

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default)
        => inner.RecordWorkspaceContextAsync(context, cancellationToken);

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default)
        => inner.GetLatestWorkspaceContextAsync(workspacePath, cancellationToken);
    }
}
