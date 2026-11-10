using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Bound_memory_tool_reads_the_real_scoped_source_without_borrowing_operator_authority()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Lexicon = true;

        Guid session = Guid.NewGuid();

        await f.AddSessionAsync(session, null, binding: true);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "search_memory");

        GuildGrant grant = f.Request.Grant with
        {
            Tools = [tool],
            Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))],
            Delegation = [],
        };

        var root = await f.IssueAsync(owner, f.Request with { Grant = grant, SessionId = session });

        Assert.True(root.IsSuccess, root.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        Assert.True((await lexicon.UpsertAsync("needle", "fact", ["allowed global source"], LexiconScope.Global)).IsSuccess);

        Guid otherCampaign = Guid.NewGuid();

        await f.AddCampaignAsync(otherCampaign);

        Assert.True((await lexicon.UpsertAsync("needle", "fact", ["DENIED-CAMPAIGN-CANARY"], LexiconScope.ForCampaign(otherCampaign))).IsSuccess);

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), memorySearch: search);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool("search_memory"), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        object? returned = await context.InvokeBoundAsync(function,
            new AIFunctionArguments { ["query"] = "needle", ["scope"] = "all", ["limit"] = 1 }, default);

        GuildLocalChildToolResult result = Assert.IsType<GuildLocalChildToolResult>(returned);

        JsonElement projection = Assert.IsType<JsonElement>(result.Projection);

        string rendered = projection.GetRawText();

        Assert.Contains("allowed global source", rendered);

        Assert.DoesNotContain("DENIED-CAMPAIGN-CANARY", rendered);

        var hit = Assert.Single(projection.GetProperty("results").EnumerateArray());

        Assert.Equal("Lexicon", hit.GetProperty("scope").GetString());

        var workspace = Assert.Single(projection.GetProperty("scopes").EnumerateArray(),
            source => source.GetProperty("scope").GetString() == "Workspace");

        Assert.False(workspace.GetProperty("available").GetBoolean());
    }
    [Theory]
    [InlineData("session")]
    [InlineData("all")]
    public async Task Bound_memory_tool_honors_ArchiveSearch_off_for_transcript_grants(string requested)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.ArchiveSearch = false;

        Guid session = Guid.NewGuid();

        await f.AddSessionAsync(session, null, binding: true);

        await f.ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('11111111-1111-4111-8111-111111111111','{session.ToString("D").ToUpperInvariant()}',0,'ARCHIVE-DISABLED-CANARY','test','2026-01-01T00:00:00.0000000Z',1)");

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "search_memory");

        GuildGrant grant = f.Request.Grant with
        {
            Tools = [tool],
            Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))],
            Delegation = [],
        };

        var root = await f.IssueAsync(owner, f.Request with { Grant = grant, SessionId = session });

        Assert.True(root.IsSuccess, root.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), memorySearch: search);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool("search_memory"), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        object? returned = await context.InvokeBoundAsync(function,
            new AIFunctionArguments { ["query"] = "ARCHIVE-DISABLED-CANARY", ["scope"] = requested }, default);

        if (requested == "session")
        {
            Assert.Contains("MemorySearch.IndexUnavailable", Assert.IsType<string>(returned));
        }
        else
        {
            var result = Assert.IsType<GuildLocalChildToolResult>(returned);

            JsonElement projection = Assert.IsType<JsonElement>(result.Projection);

            Assert.DoesNotContain("ARCHIVE-DISABLED-CANARY", projection.GetProperty("results").GetRawText());

            var status = Assert.Single(projection.GetProperty("scopes").EnumerateArray(), item => item.GetProperty("scope").GetString() == "Session");

            Assert.False(status.GetProperty("available").GetBoolean());
        }
    }
}
