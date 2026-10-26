using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class MemoryEndpointTests
{
    [SkippableFact]
    public async Task Search_continuation_reaches_an_old_short_literal_after_an_empty_work_limited_page()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid sessionId = await SeedContinuationEntriesAsync(factory, 513, oldMatchOnly: true);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using JsonDocument first = await ReadContinuationPageAsync(client, sessionId, "z", 20);

        JsonElement firstPage = first.RootElement.GetProperty("data");

        Assert.Empty(firstPage.GetProperty("results").EnumerateArray());

        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());

        string continuation = Assert.IsType<string>(firstPage.GetProperty("continuation").GetString());

        Assert.NotEmpty(continuation);

        using JsonDocument second = await ReadContinuationPageAsync(client, sessionId, "z", 20, continuation);

        JsonElement secondPage = second.RootElement.GetProperty("data");

        JsonElement hit = Assert.Single(secondPage.GetProperty("results").EnumerateArray());

        Assert.Equal("z: oldest retained entry", hit.GetProperty("content").GetString());

        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
    }

    [SkippableFact]
    public async Task Search_continuation_preserves_the_trimmed_matching_lookahead()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid sessionId = await SeedContinuationEntriesAsync(factory, 2, oldMatchOnly: false);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using JsonDocument first = await ReadContinuationPageAsync(client, sessionId, "relay", 1);

        JsonElement firstPage = first.RootElement.GetProperty("data");

        Assert.Equal("relay 2", Assert.Single(firstPage.GetProperty("results").EnumerateArray())
            .GetProperty("content").GetString());

        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());

        string continuation = Assert.IsType<string>(firstPage.GetProperty("continuation").GetString());

        Assert.NotEmpty(continuation);

        using JsonDocument second = await ReadContinuationPageAsync(client, sessionId, "relay", 1, continuation);

        JsonElement secondPage = second.RootElement.GetProperty("data");

        Assert.Equal("relay 1", Assert.Single(secondPage.GetProperty("results").EnumerateArray())
            .GetProperty("content").GetString());

        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("not-a-memory-search-cursor")]
    public async Task Search_continuation_refuses_a_malformed_explicit_token(string continuation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using StringContent request = CreateContinuationRequest(null, "relay", 1, continuation);

        using HttpResponseMessage response = await client.PostAsync("/api/memory/search", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Search_continuation_pages_the_real_Saga_and_Lexicon_tail_without_repeating_or_skipping_hits()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateSagaEnabledFactory();

        await SeedSagaMemoryAsync(factory, "continuation-first", "relaytail oldest Saga", null);

        await SeedSagaMemoryAsync(factory, "continuation-second", "relaytail newest Saga", null);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var lexicon = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Lexicon.ILexiconService>();

            foreach (string name in new[] { "Zulu relaytail", "Alpha relaytail" })
            {
                var added = await lexicon.UpsertAsync(name, "test", ["relaytail retained fact"],
                    RetroDownfall.Arcanum.Core.Lexicon.LexiconScope.Global);

                Assert.True(added.IsSuccess);
            }
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        string? token = null;

        List<string> ids = [];

        List<string> scopes = [];

        for (int page = 0; page < 8; page++)
        {
            var request = new RetroDownfall.Arcanum.Core.Memory.MemorySearchRequest("relaytail", Limit: page == 0 ? 1 : 2, Continuation: token);

            using HttpResponseMessage response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
                "/api/memory/search", request, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            JsonElement data = document.RootElement.GetProperty("data");

            foreach (JsonElement hit in data.GetProperty("results").EnumerateArray())
            {
                string id = hit.GetProperty("sourceId").GetString()!;

                Assert.DoesNotContain(id, ids);

                ids.Add(id);

                scopes.Add(hit.GetProperty("scope").GetString()!);
            }

            if (!data.GetProperty("hasMore").GetBoolean()) break;

            token = data.GetProperty("continuation").GetString();

            Assert.False(string.IsNullOrEmpty(token));
        }

        Assert.Equal(4, ids.Count);

        Assert.Equal(new[] { "Saga", "Saga", "Lexicon", "Lexicon" }, scopes);
    }

    [SkippableFact]
    public async Task Search_refuses_an_unhealthy_derived_index_instead_of_reporting_a_complete_negative()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid sessionId = await SeedContinuationEntriesAsync(factory, 2, oldMatchOnly: false);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            _ = await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database,
                "INSERT INTO memory_search_fts(memory_search_fts,rank) VALUES('secure-delete',0)");
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent request = CreateContinuationRequest(sessionId, "not-present", 20, null);

        using HttpResponseMessage response = await client.PostAsync("/api/memory/search", request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("MemorySearch.IndexUnavailable", body, StringComparison.Ordinal);

        Assert.DoesNotContain("relay 1", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Lexicon_continuation_carries_no_canonical_private_name_and_refuses_a_deleted_boundary()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var lexicon = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Lexicon.ILexiconService>();

            foreach (string name in new[] { "SensitiveAlpha", "SensitiveZulu" })
                Assert.True((await lexicon.UpsertAsync(name, "test", ["privatetailneedle"], RetroDownfall.Arcanum.Core.Lexicon.LexiconScope.Global)).IsSuccess);
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        var request = new RetroDownfall.Arcanum.Core.Memory.MemorySearchRequest("privatetailneedle",
            RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Lexicon, Limit: 1);

        using HttpResponseMessage first = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        string token = document.RootElement.GetProperty("data").GetProperty("continuation").GetString()!;

        string encoded = token[4..].Replace('-', '+').Replace('_', '/');

        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));

        Assert.DoesNotContain("SensitiveAlpha", token, StringComparison.Ordinal);

        Assert.DoesNotContain("SensitiveAlpha", decoded, StringComparison.Ordinal);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var lexicon = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Lexicon.ILexiconService>();

            var deleted = await lexicon.DeleteByNameAsync("SensitiveAlpha", RetroDownfall.Arcanum.Core.Lexicon.LexiconScope.Global);

            Assert.True(deleted.IsSuccess && deleted.Value);
        }

        using HttpResponseMessage resumed = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request with { Continuation = token }, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, resumed.StatusCode);

        string failure = await resumed.Content.ReadAsStringAsync();

        Assert.Contains("restart", failure, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("SensitiveAlpha", failure, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Lexicon_continuation_refuses_missing_wrong_scope_or_no_longer_matching_boundary(int change)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var lexicon = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Lexicon.ILexiconService>();

            foreach (string name in new[] { "BoundaryAlpha", "BoundaryZulu" })
                Assert.True((await lexicon.UpsertAsync(name, "test", ["boundaryneedle"], RetroDownfall.Arcanum.Core.Lexicon.LexiconScope.Global)).IsSuccess);
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        var request = new RetroDownfall.Arcanum.Core.Memory.MemorySearchRequest("boundaryneedle",
            RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Lexicon, Limit: 1);

        using HttpResponseMessage first = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        string token = document.RootElement.GetProperty("data").GetProperty("continuation").GetString()!;

        if (change == 1)
        {
            string encoded = token[4..].Replace('-', '+').Replace('_', '/');

            byte[] bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));

            var cursor = JsonSerializer.Deserialize(bytes, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchCursor)!;

            cursor = cursor with { Position = cursor.Position with { Tier = Guid.NewGuid().ToString("D").ToUpperInvariant() } };

            token = "ms1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor,
                RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchCursor)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        else
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            if (change == 0)
            {
                var lexicon = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Lexicon.ILexiconService>();

                var deleted = await lexicon.DeleteByNameAsync("BoundaryAlpha", RetroDownfall.Arcanum.Core.Lexicon.LexiconScope.Global);

                Assert.True(deleted.IsSuccess && deleted.Value);
            }
            else
            {
                var curation = scope.ServiceProvider.GetRequiredService<RetroDownfall.Arcanum.Core.Lexicon.ILexiconCurationService>();

                var shown = await curation.ShowExactAsync(new(RetroDownfall.Arcanum.Core.Lexicon.LexiconScopeKind.Global, null), "BoundaryAlpha", null);

                Assert.True(shown.IsSuccess);

                var corrected = await curation.CorrectAsync(shown.Value.Value.Target, new("test", ["a different retained fact"]), null);

                Assert.True(corrected.IsSuccess);
            }
        }

        using HttpResponseMessage resumed = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request with { Continuation = token }, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, resumed.StatusCode);

        string failure = await resumed.Content.ReadAsStringAsync();

        Assert.Contains("restart", failure, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("BoundaryAlpha", failure, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Search_continuation_keeps_limit_adjustable_and_ignores_an_irrelevant_workspace_selector()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid sessionId = await SeedContinuationEntriesAsync(factory, 2, oldMatchOnly: false);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using JsonDocument first = await ReadContinuationPageAsync(client, sessionId, "relay", 1);

        string token = first.RootElement.GetProperty("data").GetProperty("continuation").GetString()!;

        var request = new RetroDownfall.Arcanum.Core.Memory.MemorySearchRequest("relay",
            RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Session, sessionId, "irrelevant-missing-workspace", 2, token);

        using HttpResponseMessage response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument resumed = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("relay 1", Assert.Single(resumed.RootElement.GetProperty("data").GetProperty("results").EnumerateArray()).GetProperty("content").GetString());
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Search_continuation_refuses_changed_query_scope_or_existing_session_selector(int changed)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        Guid sessionId = await SeedContinuationEntriesAsync(factory, 2, oldMatchOnly: false);

        Guid otherSession = await SeedContinuationEntriesAsync(factory, 1, oldMatchOnly: false);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using JsonDocument first = await ReadContinuationPageAsync(client, sessionId, "relay", 1);

        string token = first.RootElement.GetProperty("data").GetProperty("continuation").GetString()!;

        var request = new RetroDownfall.Arcanum.Core.Memory.MemorySearchRequest(changed == 0 ? "different" : "relay",
            changed == 1 ? RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Attachments : RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Session,
            changed == 2 ? otherSession : sessionId, Limit: 2, Continuation: token);

        using HttpResponseMessage response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lexicon_continuation_resolves_current_name_and_refuses_an_actually_moved_scope(bool moved)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            _ = await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database,
                """
                INSERT INTO lexicon_entries(Id,Name,NameNormalized,Type,FactsJson,FactsText,UpdatedAt)
                VALUES('11111111111141118111111111111111','Alpha Cursor','ALPHA CURSOR','test','["movingneedle"]','movingneedle','2026-01-01T00:00:00.0000000Z'),
                ('22222222222242228222222222222222','Zulu Cursor','ZULU CURSOR','test','["movingneedle"]','movingneedle','2026-01-01T00:00:00.0000000Z');
                """);
        }

        using HttpClient client = factory.CreateAuthenticatedClient();

        var request = new RetroDownfall.Arcanum.Core.Memory.MemorySearchRequest("movingneedle",
            RetroDownfall.Arcanum.Core.Memory.MemorySearchScope.Lexicon, Limit: 1);

        using HttpResponseMessage first = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await first.Content.ReadAsStringAsync());

        string token = document.RootElement.GetProperty("data").GetProperty("continuation").GetString()!;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            _ = await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, moved
                ? "UPDATE lexicon_entries SET ScopeCampaignId='33333333-3333-4333-8333-333333333333' WHERE Name='Alpha Cursor'"
                : "UPDATE lexicon_entries SET Name='Middle Cursor',NameNormalized='MIDDLE CURSOR' WHERE Name='Alpha Cursor'");
        }

        using HttpResponseMessage resumed = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client,
            "/api/memory/search", request with { Continuation = token }, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.Equal(moved ? HttpStatusCode.BadRequest : HttpStatusCode.OK, resumed.StatusCode);

        if (!moved)
        {
            using JsonDocument page = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());

            Assert.Equal("22222222222242228222222222222222", Assert.Single(page.RootElement.GetProperty("data").GetProperty("results").EnumerateArray())
                .GetProperty("sourceId").GetString()!.Replace("-", "", StringComparison.Ordinal));
        }
    }

    private static async Task<Guid> SeedContinuationEntriesAsync(
        ArcanumWebApplicationFactory factory,
        int count,
        bool oldMatchOnly)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        DateTimeOffset instant = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

        Guid sessionId = Guid.NewGuid();

        db.Sessions.Add(new Session
        {
            Id = sessionId,
            Status = "active",
            CreatedAt = instant,
            UpdatedAt = instant,
        });

        for (int sequence = 1; sequence <= count; sequence++)
        {
            db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = oldMatchOnly
                    ? sequence == 1 ? "z: oldest retained entry" : "ordinary retained entry"
                    : $"relay {sequence}",
                ModelUsed = "test-model",
                CreatedAt = instant,
                Sequence = sequence,
            });
        }

        await db.SaveChangesAsync();

        return sessionId;
    }

    private static async Task<JsonDocument> ReadContinuationPageAsync(
        HttpClient client,
        Guid sessionId,
        string query,
        int limit,
        string? continuation = null)
    {
        using StringContent request = CreateContinuationRequest(sessionId, query, limit, continuation);

        using HttpResponseMessage response = await client.PostAsync("/api/memory/search", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static StringContent CreateContinuationRequest(
        Guid? sessionId,
        string query,
        int limit,
        string? continuation)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();

            writer.WriteString("query", query);

            writer.WriteString("scope", "session");

            writer.WriteNumber("limit", limit);

            if (sessionId is { } selected)
            {
                writer.WriteString("sessionId", selected);
            }

            if (continuation is not null)
            {
                writer.WriteString("continuation", continuation);
            }

            writer.WriteEndObject();
        }

        return new StringContent(Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8, "application/json");
    }
}
