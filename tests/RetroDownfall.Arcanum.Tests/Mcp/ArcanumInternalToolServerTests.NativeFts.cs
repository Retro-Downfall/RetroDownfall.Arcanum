using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class ArcanumInternalToolServerTests
{
    [SkippableTheory]
    [InlineData(25)]
    [InlineData(100)]
    public async Task Native_parser_nesting_limit_is_an_actionable_MCP_tool_error(int depth)
    {
        using GrimoireFixture grimoire = new();

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = grimoire.CreateContext(grimoire.CopyDatabase());

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()), covenantKernel: null, availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(db), FixtureLabeledArtifactGuard.For(db), memoryDelegation: null);

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with { EnableArchiveSearch = true },
            grimoireRepository: repository);

        string expression = new string('(', depth) + "schema" + new string(')', depth);

        McpToolsCallResultWire result = await session.CallToolAsync("search_archives",
            JsonSerializer.SerializeToElement(new SearchArchivesParams(expression), McpJsonSerializerContext.Default.SearchArchivesParams));

        Assert.Equal(depth == 100, result.IsError);

        string text = Assert.Single(result.Content).Text!;

        if (depth == 100)
        {
            Assert.Contains("FTS5", text, StringComparison.Ordinal);

            Assert.Contains("nesting", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(expression, text, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("schema OR migration", true, true)]
    [InlineData("\"schema migration\"", true, false)]
    [InlineData("NEAR(schema migration, 0)", true, false)]
    [InlineData("\"C++\"", false, true)]
    public async Task Native_archive_grammar_reaches_the_real_MCP_surface(string query, bool firstHit, bool secondHit)
    {
        using GrimoireFixture grimoire = new();

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = grimoire.CreateContext(grimoire.CopyDatabase());

        Session first = new() { Title = "one" };

        Session second = new() { Title = "two" };

        db.Sessions.AddRange(first, second);

        db.Entries.AddRange(
            new Entry { SessionId = first.Id, Role = MessageRole.User, Content = "schema migration firstmarker", Sequence = 1 },
            new Entry { SessionId = second.Id, Role = MessageRole.User, Content = "migration C++ secondmarker", Sequence = 1 });

        await db.SaveChangesAsync();

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()), covenantKernel: null, availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(db), FixtureLabeledArtifactGuard.For(db), memoryDelegation: null);

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with { EnableArchiveSearch = true },
            grimoireRepository: repository);

        McpToolsCallResultWire result = await session.CallToolAsync("search_archives",
            JsonSerializer.SerializeToElement(new SearchArchivesParams(query), McpJsonSerializerContext.Default.SearchArchivesParams));

        Assert.False(result.IsError);

        string text = Assert.Single(result.Content).Text!;

        Assert.Equal(firstHit, text.Contains("firstmarker", StringComparison.Ordinal));

        Assert.Equal(secondHit, text.Contains("secondmarker", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("schema OR")]
    [InlineData("\"private-query-sentinel")]
    [InlineData("schema OR migration OR ancient OR azure OR beacon")]
    public async Task Native_archive_expression_errors_are_MCP_tool_errors(string query)
    {
        using GrimoireFixture grimoire = new();

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumDbContext db = grimoire.CreateContext(grimoire.CopyDatabase());

        ArcanumSettings options = new();

        GrimoireRepository repository = new(db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(options), covenantKernel: null, availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(db), FixtureLabeledArtifactGuard.For(db), memoryDelegation: null);

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with
            {
                EnableLexiconSystem = false,
                EnableArchiveSearch = true,
                ArchiveSearchMaxQueryLength = 32,
            },
            grimoireRepository: repository);

        McpToolsCallResultWire result = await session.CallToolAsync("search_archives",
            JsonSerializer.SerializeToElement(new SearchArchivesParams(query), McpJsonSerializerContext.Default.SearchArchivesParams));

        Assert.True(result.IsError);

        string error = Assert.Single(result.Content).Text!;

        Assert.Contains("FTS5", error, StringComparison.Ordinal);

        Assert.DoesNotContain("private-query-sentinel", error, StringComparison.Ordinal);
    }
}
