using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Workspace_search_reads_current_granted_file_without_transcript_authority_or_foreign_candidates(bool rewrite, bool registrationChange, bool attributeChange)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed"));

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", "read.txt"), "needle allowed physical source");

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "private.txt"), "needle PRIVATE-WORKSPACE-CANARY");

        await SeedWorkspaceSearchChunkAsync(f, "allowed-chunk", "allowed/read.txt", "needle allowed physical source", "2026-01-01T00:00:00.0000000Z");

        await SeedWorkspaceSearchChunkAsync(f, "denied-chunk", "private.txt", "needle PRIVATE-WORKSPACE-CANARY", "2026-01-02T00:00:00.0000000Z");

        GuildCallerContext owner = await f.OperatorAsync();

        GuildGrant grant = f.Request.Grant with { Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.Read)], Memory = [] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, run.Execution!));

        var result = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.Workspace, root.Value.Quest.SessionId, f.WorkspaceRoot, 1), 8192);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal("needle allowed physical source", Assert.Single(result.Value.Results).Content);

        Assert.True(Assert.Single(result.Value.Scopes!).Available);

        Guid foreign = Guid.NewGuid();

        await f.AddCampaignAsync(foreign);

        var otherWorkspace = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.Workspace,
            root.Value.Quest.SessionId, foreign.ToString("D"), 1), 8192);

        Assert.True(otherWorkspace.IsFailure);

        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));

        using IDisposable checkpoint = GrimoireScopedConsumerTestSeam.Override(
            "GuildWorkspaceMemorySearchReader.Disclosure:" + run.Execution!.SignetId.ToString("D"),
            async (_, token) => { ready.TrySetResult(); await release.Task.WaitAsync(token); });

        var pending = search.SearchForInvocationAsync(new(registrationChange ? "no matching source" : "needle", MemorySearchScope.Workspace,
            root.Value.Quest.SessionId, f.WorkspaceRoot, 1), 8192, watchdog.Token);

        try
        {
            await ready.Task.WaitAsync(watchdog.Token);

            if (rewrite)
            {
                await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", "read.txt"), "changed physical content", watchdog.Token);
            }
            else if (registrationChange)
            {
                await f.ExecuteAsync("UPDATE campaign_path_identities SET Revision=Revision+1");
            }
            else if (attributeChange)
            {
                string path = Path.Combine(f.WorkspaceRoot, "allowed", "read.txt");

                if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                else File.Move(path, path + ".removed");
            }
            else
            {
                Guid key = Guid.NewGuid();

                var current = await f.Store.GetAsync(owner, root.Value.Quest.Id, watchdog.Token);

                Assert.True(current.IsSuccess, current.Error.Message);

                var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
                    new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), watchdog.Token);

                Assert.True(revoked.IsSuccess, revoked.Error.Message);
            }

            release.TrySetResult();

            var denied = await pending.WaitAsync(watchdog.Token);

            Assert.True(denied.IsFailure);

            if (!rewrite && !registrationChange && !attributeChange) Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.Guild.AuthorityRevoked, denied.Error.Code);
        }
        finally
        {
            release.TrySetResult();

            await pending;
        }
    }

    [Fact]
    public async Task Workspace_search_preserves_stored_time_spellings_and_pages_duplicate_instants_once()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed"));

        string[] names = ["first", "second", "third"];

        for (int index = 0; index < names.Length; index++)
        {
            string relative = "allowed/" + names[index] + ".txt";

            string content = "needle " + names[index];

            await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, relative), content);

            await SeedWorkspaceSearchChunkAsync(f, names[index], relative, content,
                index == 0 ? "2026-01-03T00:00:00Z" : "2026-01-03T00:00:00.0000000Z");
        }

        GuildCallerContext owner = await f.OperatorAsync();

        GuildGrant grant = f.Request.Grant with { Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.Read)], Memory = [] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, run.Execution!));

        MemorySearchRequest request = new("needle", MemorySearchScope.Workspace, root.Value.Quest.SessionId, f.WorkspaceRoot, 1);

        foreach (string name in names)
        {
            var page = await search.SearchForInvocationAsync(request, 8192);

            Assert.True(page.IsSuccess, page.Error.Message);

            Assert.Equal("needle " + name, Assert.Single(page.Value.Results).Content);

            request = request with { Continuation = page.Value.Continuation };
        }
    }

    [Fact]
    public async Task Workspace_search_skips_stale_and_ungranted_files_and_continues_without_losing_the_next_hit()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed"));

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed-other"));

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", "first.txt"), "needle first");

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", "second.txt"), "needle second");

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", "stale.txt"), "changed content");

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed", ".hidden"));

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", ".hidden", "secret.txt"), "needle hidden");

        await SeedWorkspaceSearchChunkAsync(f, "hidden", "allowed/.hidden/secret.txt", "needle hidden", "2026-01-08T00:00:00.0000000Z");

        string secret = Path.Combine(f.WorkspaceRoot, "allowed-other", "secret.txt");

        await File.WriteAllTextAsync(secret, "needle forbidden");

        Assert.True(HardLinkTestSupport.TryCreate(Path.Combine(f.WorkspaceRoot, "allowed", "hard.txt"), secret));

        await SeedWorkspaceSearchChunkAsync(f, "hard", "allowed/hard.txt", "needle forbidden", "2026-01-07T00:00:00.0000000Z");

        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(f.WorkspaceRoot, "allowed", "link.txt"), secret);

            await SeedWorkspaceSearchChunkAsync(f, "link", "allowed/link.txt", "needle forbidden", "2026-01-06T00:00:00.0000000Z");
        }

        await SeedWorkspaceSearchChunkAsync(f, "stale", "allowed/stale.txt", "needle stale", "2026-01-04T00:00:00.0000000Z");

        await SeedWorkspaceSearchChunkAsync(f, "foreign", "allowed-other/secret.txt", "needle forbidden", "2026-01-05T00:00:00.0000000Z");

        await SeedWorkspaceSearchChunkAsync(f, "first", "allowed/first.txt", "needle first", "2026-01-03T00:00:00.0000000Z");

        await SeedWorkspaceSearchChunkAsync(f, "second", "allowed/second.txt", "needle second", "2026-01-02T00:00:00.0000000Z");

        GuildCallerContext owner = await f.OperatorAsync();

        GuildGrant grant = f.Request.Grant with { Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.Read)], Memory = [] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, run.Execution!));

        MemorySearchRequest request = new("needle", MemorySearchScope.Workspace, root.Value.Quest.SessionId, f.WorkspaceRoot, 1);

        var first = await search.SearchForInvocationAsync(request, 8192);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal("needle first", Assert.Single(first.Value.Results).Content);

        Assert.NotNull(first.Value.Continuation);

        var second = await search.SearchForInvocationAsync(request with { Continuation = first.Value.Continuation }, 8192);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.Equal("needle second", Assert.Single(second.Value.Results).Content);

        Assert.DoesNotContain(second.Value.Results, item => item.Content.Contains("forbidden", StringComparison.Ordinal));

        var changedQuery = await search.SearchForInvocationAsync(request with { Query = "other", Continuation = first.Value.Continuation }, 8192);

        Assert.True(changedQuery.IsFailure);
    }

    [Fact]
    public async Task Bound_memory_tool_uses_its_host_owned_workspace_without_transcript_grant()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        Guid workspace = await f.AddWorkspaceAsync();

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, "allowed"));

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "allowed", "read.txt"), "needle bound workspace");

        await SeedWorkspaceSearchChunkAsync(f, "bound", "allowed/read.txt", "needle bound workspace", "2026-01-01T00:00:00.0000000Z");

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "search_memory");

        GuildGrant grant = f.Request.Grant with { Tools = [tool], Workspaces = [new(workspace, "allowed", GuildWorkspaceAccess.Read)], Memory = [] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        ConclaveArchmage coordinator = new(null!, f.Settings, f.Store, new ChildQueue(), memorySearch: search);

        using GuildLocalToolRegistry registry = new(execution, input.Value, coordinator);

        var context = f.Store.CreateToolContext(execution, registry, _ => true);

        var function = await context.AdvertiseAsync(new InertChildTool("search_memory"), tool, default);

        Assert.NotNull(function);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        object? returned = await context.InvokeBoundAsync(function,
            new Microsoft.Extensions.AI.AIFunctionArguments { ["query"] = "needle", ["scope"] = "workspace", ["limit"] = 1 }, default);

        GuildLocalChildToolResult result = Assert.IsType<GuildLocalChildToolResult>(returned);

        System.Text.Json.JsonElement projection = Assert.IsType<System.Text.Json.JsonElement>(result.Projection);

        var hit = Assert.Single(projection.GetProperty("results").EnumerateArray());

        Assert.Equal("needle bound workspace", hit.GetProperty("content").GetString());
    }

    [Fact]
    public async Task All_scope_preserves_granted_lexicon_when_bound_workspace_is_not_granted()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.Lexicon = true;

        Guid workspace = await f.AddWorkspaceAsync();

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, "private.txt"), "needle private workspace");

        await SeedWorkspaceSearchChunkAsync(f, "private", "private.txt", "needle private workspace", "2026-01-01T00:00:00.0000000Z");

        GuildCallerContext owner = await f.OperatorAsync();

        GuildGrant grant = f.Request.Grant with { Workspaces = [], Memory = [new(GuildMemoryStore.Lexicon, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global))] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        Assert.True((await lexicon.UpsertAsync("needle", "fact", ["allowed lexicon"], RetroDownfall.Arcanum.Core.Lexicon.LexiconScope.Global)).IsSuccess);

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, run.Execution!));

        var result = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.All, root.Value.Quest.SessionId, f.WorkspaceRoot, 10), 8192);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(result.Value.Results, hit => hit.Scope == MemorySearchScope.Lexicon && hit.Content.Contains("allowed lexicon", StringComparison.Ordinal));

        Assert.DoesNotContain(result.Value.Results, hit => hit.Scope == MemorySearchScope.Workspace);

        Assert.False(Assert.Single(result.Value.Scopes!, item => item.Scope == MemorySearchScope.Workspace).Available);
    }

    [Fact]
    public async Task Workspace_search_streams_long_utf16_prefix_and_bounds_unicode_excerpt()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        Guid workspace = await f.AddWorkspaceAsync();

        string actualDirectory = OperatingSystem.IsWindows() ? "ärea" : "Ärea";

        Directory.CreateDirectory(Path.Combine(f.WorkspaceRoot, actualDirectory));

        string prefix = string.Concat(Enumerable.Repeat("😀", 20_000));

        string chunk = "needle " + string.Concat(Enumerable.Repeat("é😀", 700));

        await File.WriteAllTextAsync(Path.Combine(f.WorkspaceRoot, actualDirectory, "unicode.txt"), prefix + chunk);

        await SeedWorkspaceSearchChunkAsync(f, "unicode", actualDirectory + "/unicode.txt", chunk, "2026-01-01T00:00:00.0000000Z", prefix.Length);

        GuildCallerContext owner = await f.OperatorAsync();

        GuildGrant grant = f.Request.Grant with { Workspaces = [new(workspace, "Ärea", GuildWorkspaceAccess.Read)], Memory = [] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        await using var connection = await f.OpenAsync();

        await using (var predicate = connection.CreateCommand())
        {
            predicate.CommandText = "SELECT " + GuildWorkspaceMemorySearchReader.ScopePrefixPredicate(true, 0)
                + " FROM (SELECT $path AS RelativePath)";

            predicate.Parameters.AddWithValue("@scope0", "Ärea/");

            predicate.Parameters.AddWithValue("$path", "ärea/file.txt");

            Assert.Equal(1L, await predicate.ExecuteScalarAsync());

            predicate.Parameters["$path"].Value = "ärea-sibling/file.txt";

            Assert.Equal(0L, await predicate.ExecuteScalarAsync());

            predicate.CommandText = "SELECT substr('ärea/file.txt',1,length(@scope0))=@scope0 COLLATE NOCASE";

            Assert.Equal(0L, await predicate.ExecuteScalarAsync());
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, run.Execution!));

        var result = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.Workspace, root.Value.Quest.SessionId, f.WorkspaceRoot, 1), 8192);

        Assert.True(result.IsSuccess, result.Error.Message);

        string excerpt = Assert.Single(result.Value.Results).Content;

        Assert.StartsWith("needle é😀", excerpt);

        Assert.EndsWith(" [TRUNCATED excerpt; inspect the source for full content]", excerpt);

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(excerpt), 1, 1024);

        Assert.DoesNotContain("�", excerpt);

        string oversizedPath = actualDirectory + "/" + new string('x', checked((int)NativeMemorySearchReader.ExaminedByteLimit));

        await SeedWorkspaceSearchChunkAsync(f, "oversized", oversizedPath, "needle", "2027-01-01T00:00:00.0000000Z");

        var oversized = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.Workspace, root.Value.Quest.SessionId, f.WorkspaceRoot, 1), 8192);

        Assert.True(oversized.IsFailure);

        Assert.Equal(RetroDownfall.Arcanum.Core.Primitives.ErrorCodes.MemorySearch.IndexUnavailable, oversized.Error.Code);

        await f.ExecuteAsync("DELETE FROM workspace_file_chunks WHERE ChunkId='oversized'");

        var recovered = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.Workspace, root.Value.Quest.SessionId, f.WorkspaceRoot, 1), 8192);

        Assert.True(recovered.IsSuccess, recovered.Error.Message);

        Assert.Single(recovered.Value.Results);
    }

    [Fact]
    public async Task All_scope_marks_ungranted_workspace_unavailable_even_when_prior_source_fills_page()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        f.Settings.CurrentValue.Features.ArchiveSearch = true;

        Guid workspace = await f.AddWorkspaceAsync();

        Guid session = Guid.NewGuid();

        await f.AddSessionAsync(session, workspace, binding: true);

        await f.ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('11111111-1111-4111-8111-111111111111','{session.ToString("D").ToUpperInvariant()}',0,'needle permitted transcript','test','2026-01-01T00:00:00.0000000Z',1)");

        await f.ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('22222222-2222-4222-8222-222222222222','{session.ToString("D").ToUpperInvariant()}',0,'needle another transcript','test','2026-01-02T00:00:00.0000000Z',2)");

        GuildCallerContext owner = await f.OperatorAsync();

        GuildGrant grant = f.Request.Grant with { Workspaces = [], Memory = [new(GuildMemoryStore.Transcript, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Session, session))] };

        var root = await f.IssueAsync(owner, f.Request with { WorkspaceId = workspace, SessionId = session, Grant = grant });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildCallerContext run = await RunCallerAsync(f, owner, root.Value);

        await using var connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        LexiconService lexicon = new(db, NullLogger<LexiconService>.Instance, f.Settings, MemoryErasureTestKeys.Isolated());

        SagaMemoryStore saga = new(db, f.Settings, MemoryErasureTestKeys.Isolated());

        MemorySearchService search = new(db, f.Connections, new MemoryScopeResolver(db, f.Settings), lexicon, lexicon, saga, f.Settings, f.Policy, f.Opener);

        using IDisposable invocation = GuildMemoryInvocationAmbient.Begin(new(f.Authority, run.Execution!));

        var result = await search.SearchForInvocationAsync(new("needle", MemorySearchScope.All, session, f.WorkspaceRoot, 1), 8192);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(MemorySearchScope.Session, Assert.Single(result.Value.Results).Scope);

        Assert.True(result.Value.HasMore);

        Assert.False(Assert.Single(result.Value.Scopes!, item => item.Scope == MemorySearchScope.Workspace).Available);
    }

    private static async Task SeedWorkspaceSearchChunkAsync(Fixture f, string id, string relativePath, string content, string indexedAt, int offset = 0)
    {
        await using var connection = await f.OpenAsync();

        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO workspace_file_chunks(ChunkId,WorkspacePath,RelativePath,ChunkIndex,Content,CharOffset,CharLength,FileLastWriteTime,IndexedAt,FileLength)
            VALUES($id,$root,$path,0,$content,$offset,$length,$indexed,$indexed,$bytes)
            """;

        command.Parameters.AddWithValue("$id", id);

        command.Parameters.AddWithValue("$root", f.WorkspaceRoot);

        command.Parameters.AddWithValue("$path", relativePath);

        command.Parameters.AddWithValue("$content", content);

        command.Parameters.AddWithValue("$offset", offset);

        command.Parameters.AddWithValue("$length", content.Length);

        command.Parameters.AddWithValue("$indexed", indexedAt);

        command.Parameters.AddWithValue("$bytes", System.Text.Encoding.UTF8.GetByteCount(content));

        await command.ExecuteNonQueryAsync();
    }
}
