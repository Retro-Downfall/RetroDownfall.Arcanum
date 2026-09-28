using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Lexicon;
using SQLitePCL;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LexiconInspectionQueryBudgetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("list", 48, 48)]
    [InlineData("filtered-list", 8, 8)]
    [InlineData("search", 3, 2)]
    [InlineData("sources", 0, 48)]
    [InlineData("explain", 0, 1)]
    public async Task Inspection_uses_constant_statements_and_projects_only_its_required_rows(
        string operation, int projectedRows, int returnedRows)
    {
        using GrimoireFixture grimoire = new();

        await using CorrectionFixture owner = new(grimoire, annals: true);

        for (int index = 0; index < 48; index++)
        {
            var seeded = await owner.Concrete.UpsertAsync($"Entity {index:D3}", "general",
                [index >= 40 ? "Éclair needle" : "unrelated"], LexiconScope.Global);

            Assert.True(seeded.IsSuccess, seeded.Error.Message);
        }

        HashSet<string> materialized = [];

        owner.Connection.CreateFunction<string, string, string>("observe_lexicon_row", (stamp, id) =>
        {
            materialized.Add(id);

            return stamp;
        });

        // TEMP shadows only this fixture connection. Observe a projected column that is never
        // used for selection, ordering, joins, or verification of another entity.
        await owner.ExecuteAsync("""
            CREATE TEMP VIEW lexicon_entries AS
            SELECT Id, Name, NameNormalized, Type, FactsJson, observe_lexicon_row(FactsText, Id) AS FactsText,
                   UpdatedAt, ScopeCampaignId,
                   RetiredAtUtc, PinnedAtUtc, CurationGeneration
            FROM main.lexicon_entries;
            """);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services => services.AddSingleton<ILexiconCurationService>(owner.Service),
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        List<string> statements = [];

        raw.sqlite3_trace(owner.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        using HttpResponseMessage response = operation == "search"
            ? await client.PostAsJsonAsync("/api/memory/search",
                new MemorySearchRequest("éCLAIR", MemorySearchScope.Lexicon, Limit: 2),
                ArcanumJsonContext.Default.MemorySearchRequest)
            : await client.GetAsync(operation switch
            {
                "list" => "/api/memory/lexicon",
                "filtered-list" => "/api/memory/lexicon?q=%C3%A9CLAIR",
                _ => "/api/memory/" + operation,
            });

        string body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);

        using JsonDocument json = JsonDocument.Parse(body);

        JsonElement data = json.RootElement.GetProperty("data");

        int actual = operation switch
        {
            "list" or "filtered-list" => data.GetProperty("entries").GetArrayLength(),
            "search" => data.GetProperty("results").GetArrayLength(),
            "sources" => data.GetProperty("sources").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Lexicon").GetProperty("count").GetInt32(),
            _ => data.GetProperty("sources").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Lexicon").GetProperty("eligible").GetBoolean() ? 1 : 0,
        };

        Assert.Equal(returnedRows, actual);

        output.WriteLine($"{operation}: SQL statements={statements.Count}; projected canonical identities={materialized.Count}; expected={projectedRows}.");

        Assert.True(statements.Count <= 8 && materialized.Count == projectedRows,
            $"{operation}: SQL statements={statements.Count} (maximum 8); materialized canonical identities={materialized.Count} (expected {projectedRows}).");
    }
}
