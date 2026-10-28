using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Tests.Operations;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LongRunningOperationPaginationEndpointTests(ArcanumWebApplicationFactory factory)
{
    [SkippableTheory]
    [InlineData(0, 100, 0, false)]
    [InlineData(100, 100, 100, false)]
    [InlineData(150, 100, 100, true)]
    [InlineData(520, 500, 500, true)]
    public async Task List_returns_a_bounded_page_with_explicit_continuation(int total, int limit, int expectedCount, bool hasMore)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        string kind = await SeedAsync(total);

        string limitQuery = limit == 100 ? string.Empty : $"&limit={limit}";

        using HttpResponseMessage response = await client.GetAsync($"/api/operations?kind={kind}{limitQuery}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement page = document.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Object, page.ValueKind);

        Assert.Equal(expectedCount, page.GetProperty("items").GetArrayLength());

        Assert.Equal(hasMore, page.GetProperty("hasMore").GetBoolean());

        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextBeforeUpdatedAt").ValueKind);

        if (hasMore)
        {
            int next = page.GetProperty("nextOffset").GetInt32();

            Assert.Equal(expectedCount, next);

            using HttpResponseMessage second = await client.GetAsync($"/api/operations?kind={kind}&limit={limit}&offset={next}");

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            using JsonDocument secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

            JsonElement last = secondDocument.RootElement.GetProperty("data");

            Assert.False(last.GetProperty("hasMore").GetBoolean());

            Assert.Equal(total - expectedCount, last.GetProperty("items").GetArrayLength());

            Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);

            Guid[] ids = page.GetProperty("items").EnumerateArray()
                .Concat(last.GetProperty("items").EnumerateArray())
                .Select(static row => row.GetProperty("id").GetGuid())
                .ToArray();

            Assert.Equal(total, ids.Distinct().Count());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
        }
    }

    [SkippableTheory]
    [InlineData("limit=0")]
    [InlineData("limit=-1")]
    [InlineData("limit=501")]
    [InlineData("limit=2147483647")]
    [InlineData("offset=-1")]
    [InlineData("offset=2147483647")]
    [InlineData("limit=100&offset=2147483597")]
    public async Task Invalid_page_input_returns_a_typed_400_instead_of_clamping_or_throwing(string query)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/operations?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(document.RootElement.GetProperty("isSuccess").GetBoolean());

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Invalid_state_keeps_its_existing_priority_over_page_validation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/operations?state=unknown&limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(ErrorCodes.Operation.InvalidState, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [SkippableTheory]
    [InlineData("limit=2147483648")]
    [InlineData("offset=2147483648")]
    public async Task A_numeric_binding_overflow_is_refused_before_the_endpoint(string query)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/operations?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task The_state_filter_applies_to_both_visible_rows_and_the_lookahead()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = factory.CreateAuthenticatedClient();

        string kind = await SeedAsync(150, alternateState: true);

        using HttpResponseMessage response = await client.GetAsync($"/api/operations?kind={kind}&state=Pending&limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement page = document.RootElement.GetProperty("data");

        Assert.Equal(JsonValueKind.Object, page.ValueKind);

        Assert.Equal(75, page.GetProperty("items").GetArrayLength());

        Assert.False(page.GetProperty("hasMore").GetBoolean());

        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [SkippableTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(100, true)]
    public async Task Integer_frontier_returns_only_usable_continuations(int remainingWindow, bool hasMore)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeLongRunningOperationStore store = new(TimeProvider.System);

        LongRunningOperation row = store.Seed("frontier-fixture", LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        int offset = int.MaxValue - 100 - remainingWindow;

        store.ListOverride = query =>
        {
            Assert.Equal(offset, query.Offset);

            Assert.Equal(100, query.Limit);

            Assert.True(query.IncludeLookahead);

            return Enumerable.Repeat(row, hasMore ? 101 : 100).ToArray();
        };

        // The real endpoint sees a bounded store response at the integer frontier; this is not a two-billion-row SQL fixture.
        await using ArcanumWebApplicationFactory isolatedFactory = new();

        await using WebApplicationFactory<Program> scoped = isolatedFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILongRunningOperationStore>();

                services.AddSingleton<ILongRunningOperationStore>(store);
            }));

        using HttpClient client = scoped.CreateClient();

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

        using HttpResponseMessage response = await client.GetAsync($"/api/operations?kind=frontier-fixture&limit=100&offset={offset}");

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(1, store.ListCallCount);

        if (hasMore && remainingWindow == 0)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            Assert.Equal(ErrorCodes.Validation.InvalidQuery, document.RootElement.GetProperty("error").GetProperty("code").GetString());

            Assert.False(document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind != JsonValueKind.Null);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            JsonElement page = document.RootElement.GetProperty("data");

            Assert.Equal(100, page.GetProperty("items").GetArrayLength());

            Assert.Equal(hasMore, page.GetProperty("hasMore").GetBoolean());

            if (hasMore) Assert.Equal(offset + 100, page.GetProperty("nextOffset").GetInt32());
            else Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
        }
    }

    private async Task<string> SeedAsync(int count, bool alternateState = false)
    {
        string kind = "page-fixture-" + Guid.NewGuid().ToString("N");

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        await db.Database.OpenConnectionAsync();

        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM rows WHERE n < @count)
            INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary)
            SELECT @prefix || printf('%08x', n), @kind,
                   CASE WHEN @alternate = 1 AND n % 2 = 0 THEN @failed ELSE @pending END,
                   @policy, @created, 'Safe pagination fixture.'
            FROM rows
            WHERE n <= @count
            """;

        void Add(string name, object value)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value;

            command.Parameters.Add(parameter);
        }

        Add("@prefix", Guid.NewGuid().ToString("N")[..24]);

        Add("@kind", kind);

        Add("@count", count);

        Add("@alternate", alternateState ? 1 : 0);

        Add("@failed", (int)LongRunningOperationState.Failed);

        Add("@pending", (int)LongRunningOperationState.Pending);

        Add("@policy", (int)LongRunningOperationRecoveryPolicy.RestartIdempotently);

        Add("@created", UtcInstantText.Format(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal(count, await command.ExecuteNonQueryAsync());

        return kind;
    }
}
