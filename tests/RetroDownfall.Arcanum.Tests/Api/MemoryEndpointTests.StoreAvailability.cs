using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class MemoryEndpointTests
{
    [SkippableTheory]
    [InlineData(MemorySearchScope.Saga, "Guild.AuthorityUnavailable")]
    [InlineData(MemorySearchScope.Lexicon, "MemorySearch.IndexUnavailable")]
    [InlineData(MemorySearchScope.Attachments, "MemorySearch.IndexUnavailable")]
    [InlineData(MemorySearchScope.Workspace, "Guild.AuthorityUnavailable")]
    public async Task Explicit_unavailable_model_scope_returns_a_typed_failure(MemorySearchScope requested, string code)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { Saga = false, Lexicon = false, Attachments = false },
            },
        };

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        Result<MemorySearchResponse> result = await search.SearchForInvocationAsync(new("needle", requested), 4096);

        Assert.True(result.IsFailure);

        Assert.Equal(code, result.Error.Code);
    }

    [SkippableFact]
    public async Task Search_all_keeps_Saga_readable_when_native_search_projection_is_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        await SeedSagaMemoryAsync(factory, "independent-search", "independentneedle Saga fact", null);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = "DELETE FROM memory_search_fts_config WHERE k='secure-delete'";

            await command.ExecuteNonQueryAsync();
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/memory/search",
            new MemorySearchRequest("independentneedle"), ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal("Saga", Assert.Single(data.GetProperty("results").EnumerateArray()).GetProperty("scope").GetString());

        JsonElement session = data.GetProperty("scopes").EnumerateArray().Single(row => row.GetProperty("scope").GetString() == "Session");

        Assert.False(session.GetProperty("available").GetBoolean());

        Assert.Equal("MemorySearch.IndexUnavailable", session.GetProperty("errorCode").GetString());
    }
    [SkippableFact]
    public async Task Model_search_uses_the_callers_allocation_and_continuation_never_skips_excerpted_hits()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid session = await SeedContinuationEntriesAsync(factory, 3, oldMatchOnly: false);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        await using (SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand())
        {
            command.CommandText = "UPDATE Entries SET Content=$content WHERE SessionId=$session";

            command.Parameters.AddWithValue("$content", "relay " + new string('x', 80_000));

            command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

            await command.ExecuteNonQueryAsync();
        }

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        string? continuation = null;

        HashSet<string> seen = [];

        for (int page = 0; page < 10; page++)
        {
            Result<MemorySearchResponse> result = await search.SearchForInvocationAsync(
                new("relay", MemorySearchScope.Session, session, Limit: 100, Continuation: continuation), 4096);

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(result.Value,
                ArcanumJsonContext.Default.MemorySearchResponse).Length, 1, 4096);

            foreach (MemorySearchResultDto hit in result.Value.Results)
            {
                Assert.True(seen.Add(hit.SourceId), "Continuation repeated a disclosed hit.");

                Assert.Contains("TRUNCATED excerpt", hit.Content);
            }

            if (!result.Value.HasMore) break;

            continuation = Assert.IsType<string>(result.Value.Continuation);
        }

        Assert.Equal(3, seen.Count);
    }
    [SkippableFact]
    public async Task Search_all_keeps_Lexicon_readable_when_Saga_table_is_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            Assert.True((await lexicon.UpsertAsync("independentneedle", "fact", ["retained Lexicon"], LexiconScope.Global)).IsSuccess);

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = "DROP TABLE saga_memories";

            await command.ExecuteNonQueryAsync();
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/memory/search",
            new MemorySearchRequest("independentneedle"), ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement data = document.RootElement.GetProperty("data");

        Assert.Equal("Lexicon", Assert.Single(data.GetProperty("results").EnumerateArray()).GetProperty("scope").GetString());

        JsonElement saga = Assert.Single(data.GetProperty("scopes").EnumerateArray(), row => row.GetProperty("scope").GetString() == "Saga");

        Assert.False(saga.GetProperty("available").GetBoolean());

        Assert.Equal("MemorySearch.IndexUnavailable", saga.GetProperty("errorCode").GetString());
    }
    [SkippableFact]
    public async Task Model_Lexicon_search_retains_a_match_beyond_the_bounded_excerpt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with { Features = settings.Features with { Lexicon = true } },
        };

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

        var added = await lexicon.UpsertAsync("Retained source", "fact", [new string('x', 5000) + " suffixneedle"], LexiconScope.Global);

        Assert.True(added.IsSuccess);

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        var page = await search.SearchForInvocationAsync(new("suffixneedle", MemorySearchScope.Lexicon, Limit: 1), 4096);

        Assert.True(page.IsSuccess, page.Error.Message);

        MemorySearchResultDto hit = Assert.Single(page.Value.Results);

        Assert.Equal(added.Value.Id.ToString("D"), hit.SourceId);

        Assert.Contains("TRUNCATED excerpt", hit.Content);
    }
    [SkippableTheory]
    [InlineData(MemorySearchScope.Session)]
    [InlineData(MemorySearchScope.All)]
    public async Task ArchiveSearch_off_blocks_model_transcripts_and_preserves_operator_inspection(MemorySearchScope requested)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with { Features = settings.Features with { ArchiveSearch = false } },
        };

        Guid session = await SeedContinuationEntriesAsync(factory, 2, oldMatchOnly: false);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        var model = await search.SearchForInvocationAsync(new("relay", requested, session), 8192);

        if (requested == MemorySearchScope.Session)
        {
            Assert.True(model.IsFailure);

            Assert.Equal("MemorySearch.IndexUnavailable", model.Error.Code);
        }
        else
        {
            Assert.True(model.IsSuccess, model.Error.Message);

            Assert.DoesNotContain(model.Value.Results, hit => hit.Scope == MemorySearchScope.Session);

            Assert.False(Assert.Single(model.Value.Scopes!, status => status.Scope == MemorySearchScope.Session).Available);
        }

        var inspection = await search.SearchAsync(new("relay", MemorySearchScope.Session, session), null);

        Assert.True(inspection.IsSuccess, inspection.Error.Message);

        Assert.Equal(2, inspection.Value.Value.Results.Length);
    }
    [SkippableTheory]
    [InlineData(MemorySearchScope.Saga)]
    [InlineData(MemorySearchScope.Lexicon)]
    public async Task Explicit_missing_store_HTTP_search_returns_a_typed_unavailable_failure(MemorySearchScope requested)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = requested == MemorySearchScope.Saga ? "DROP TABLE saga_memories" : "DROP TABLE lexicon_entries";

            await command.ExecuteNonQueryAsync();
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/memory/search",
            new MemorySearchRequest("needle", requested), ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("MemorySearch.IndexUnavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        using HttpResponseMessage all = await client.PostAsJsonAsync("/api/memory/search",
            new MemorySearchRequest("needle"), ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, all.StatusCode);

        using JsonDocument allDocument = JsonDocument.Parse(await all.Content.ReadAsStringAsync());

        JsonElement status = Assert.Single(allDocument.RootElement.GetProperty("data").GetProperty("scopes").EnumerateArray(),
            item => item.GetProperty("scope").GetString() == requested.ToString());

        Assert.False(status.GetProperty("available").GetBoolean());
    }
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_search_reports_missing_later_stores_when_an_early_scope_saturates(bool model)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        var configure = factory.SettingsOverride!;

        factory.SettingsOverride = settings =>
        {
            var configured = configure(settings);

            return configured with { Features = configured.Features with { ArchiveSearch = true, Lexicon = true } };
        };

        Guid session = await SeedContinuationEntriesAsync(factory, 3, oldMatchOnly: false);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        await using (SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand())
        {
            command.CommandText = "DROP TABLE saga_memories; DROP TABLE lexicon_entries";

            await command.ExecuteNonQueryAsync();
        }

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        MemorySearchRequest request = new("relay", MemorySearchScope.All, session, Limit: 1);

        Result<MemorySearchResponse> result;

        if (model) result = await search.SearchForInvocationAsync(request, 8192);
        else
        {
            var inspection = await search.SearchAsync(request, null);

            Assert.True(inspection.IsSuccess, inspection.Error.Message);

            result = inspection.Value.Value;
        }

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Single(result.Value.Results);

        Assert.True(result.Value.HasMore);

        foreach (MemorySearchScope store in new[] { MemorySearchScope.Saga, MemorySearchScope.Lexicon })
        {
            MemorySearchScopeStatusDto status = Assert.Single(result.Value.Scopes!, status => status.Scope == store);

            Assert.False(status.Available);

            Assert.False(status.HasMore);

            Assert.Equal("MemorySearch.IndexUnavailable", status.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task All_model_search_reports_disabled_later_stores_when_an_early_scope_saturates()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ArchiveSearch = true, Saga = false, Lexicon = false, Attachments = false },
            },
        };

        Guid session = await SeedContinuationEntriesAsync(factory, 3, oldMatchOnly: false);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        var page = await search.SearchForInvocationAsync(new("relay", MemorySearchScope.All, session, Limit: 1), 8192);

        Assert.True(page.IsSuccess, page.Error.Message);

        Assert.Single(page.Value.Results);

        Assert.True(page.Value.HasMore);

        foreach (MemorySearchScope store in new[] { MemorySearchScope.Attachments, MemorySearchScope.Workspace, MemorySearchScope.Saga, MemorySearchScope.Lexicon })
        {
            MemorySearchScopeStatusDto status = Assert.Single(page.Value.Scopes!, status => status.Scope == store);

            Assert.False(status.Available);

            Assert.False(status.HasMore);

            Assert.NotNull(status.ErrorCode);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_search_preserves_unavailable_earlier_scope_status_on_a_later_store_continuation(bool disabledModelGate)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        if (disabledModelGate)
        {
            var configure = factory.SettingsOverride!;

            factory.SettingsOverride = settings =>
            {
                var configured = configure(settings);

                return configured with { Features = configured.Features with { ArchiveSearch = false, Attachments = false } };
            };
        }

        await SeedSagaMemoryAsync(factory, "status-first", "statusneedle first", null);

        await SeedSagaMemoryAsync(factory, "status-second", "statusneedle second", null);

        await SeedSagaMemoryAsync(factory, "status-third", "statusneedle third", null);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        if (!disabledModelGate)
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            await db.Database.OpenConnectionAsync();

            await using SqliteCommand command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();

            command.CommandText = "DELETE FROM memory_search_fts_config WHERE k='secure-delete'";

            await command.ExecuteNonQueryAsync();
        }

        IMemorySearchService search = scope.ServiceProvider.GetRequiredService<IMemorySearchService>();

        string? continuation = null;

        HashSet<string> seen = [];

        for (int index = 0; index < 2; index++)
        {
            MemorySearchRequest request = new("statusneedle", Limit: 1, Continuation: continuation);

            Result<MemorySearchResponse> result;

            if (disabledModelGate) result = await search.SearchForInvocationAsync(request, 8192);
            else
            {
                var inspection = await search.SearchAsync(request, null);

                Assert.True(inspection.IsSuccess, inspection.Error.Message);

                result = inspection.Value.Value;
            }

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.True(seen.Add(Assert.Single(result.Value.Results).SourceId));

            MemorySearchScopeStatusDto status = Assert.Single(result.Value.Scopes!, status => status.Scope == MemorySearchScope.Session);

            Assert.False(status.Available);

            Assert.False(status.HasMore);

            Assert.Equal("MemorySearch.IndexUnavailable", status.ErrorCode);

            continuation = Assert.IsType<string>(result.Value.Continuation);
        }
    }

}
