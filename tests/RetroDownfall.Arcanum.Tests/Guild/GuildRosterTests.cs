using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildRosterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "guild-roster-" + Guid.NewGuid().ToString("N"));

    public GuildRosterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Refresh_discovers_sources_and_exact_retry_returns_original_durable_acceptance()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        string source = Path.Combine(_root, "review.md");

        await File.WriteAllTextAsync(source, "---\nname: Reviewer\ndescription: Reviews a patch\n---\nUntrusted guidance");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var first = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(first.IsSuccess);

        Guid agent = Assert.Single(first.Value.Diagnostics.Items).AgentId!.Value;

        await File.WriteAllTextAsync(source, "---\nname: Reviewer\ndescription: Changed description\n---");

        var replay = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(replay.IsSuccess);

        Assert.Equal(first.Value.Receipt.Id, replay.Value.Receipt.Id);

        Assert.True(replay.Value.Receipt.Replayed);

        var pinned = await roster.ResolveAsync(caller, agent, null, 1, CancellationToken.None);

        Assert.Equal("Reviews a patch", pinned.Value.Description);

        var changedIntent = request with { AgentId = agent };

        var conflict = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(changedIntent), changedIntent, CancellationToken.None);

        Assert.Equal("Guild.IdempotencyConflict", conflict.Error.Code);
    }

    [Fact]
    public async Task Declared_card_removed_tools_are_diagnosed_on_refresh_without_rewriting_the_source()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        string source = Path.Combine(_root, "review.md");

        await File.WriteAllTextAsync(source, "---\nname: Reviewer\ndescription: Reviews a patch\ntools: [custom_external_tool]\n---");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var first = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, default);

        Assert.True(first.IsSuccess);

        Assert.True(Assert.Single(first.Value.Diagnostics.Items).Available);

        Guid agent = Assert.Single(first.Value.Diagnostics.Items).AgentId!.Value;

        const string legacy = "---\nname: Reviewer\ndescription: Reviews a patch\ntools: [READ_FILE_CHUNK, custom_external_tool]\n---\nHistorical instructions";

        await File.WriteAllTextAsync(source, legacy);

        request = new GuildRefreshAgentsRequest(Guid.NewGuid(), agent);

        var refresh = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, default);

        Assert.True(refresh.IsSuccess);

        var diagnostic = Assert.Single(refresh.Value.Diagnostics.Items);

        Assert.False(diagnostic.Available);

        Assert.Equal("Guild.LegacyToolName", diagnostic.ReasonCode);

        var tool = Assert.Single(diagnostic.ToolDiagnostics!);

        Assert.Equal("READ_FILE_CHUNK", tool.ToolName);

        Assert.Equal("read_file", tool.Replacement);

        Assert.Equal(legacy, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Invalid_refresh_keeps_pinned_revision_but_refuses_new_selection()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        string source = Path.Combine(_root, "review.md");

        await File.WriteAllTextAsync(source, "---\nname: Reviewer\ndescription: Reviews a patch\n---");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var first = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Guid agent = Assert.Single(first.Value.Diagnostics.Items).AgentId!.Value;

        await File.WriteAllTextAsync(source, "---\nname: Reviewer\nunknown: unsafe\n---");

        request = new GuildRefreshAgentsRequest(Guid.NewGuid(), agent);

        var invalid = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(invalid.IsSuccess);

        Assert.False(Assert.Single(invalid.Value.Diagnostics.Items).Available);

        var pinned = await roster.ResolveAsync(caller, agent, null, 1, CancellationToken.None);

        Assert.True(pinned.IsSuccess);

        Assert.Equal("Reviews a patch", pinned.Value.Description);

        var latest = await roster.ResolveAsync(caller, agent, null, null, CancellationToken.None);

        Assert.False(latest.IsSuccess);

        Assert.Equal("Guild.Unavailable", latest.Error.Code);
    }

    [Fact]
    public async Task Display_names_never_silently_shadow_each_other_and_selectors_are_ordinal()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        await File.WriteAllTextAsync(Path.Combine(_root, "a.md"), "---\nname: Reviewer\ndescription: First\n---");

        await File.WriteAllTextAsync(Path.Combine(_root, "b.md"), "---\nname: Reviewer\ndescription: Second\n---");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var refresh = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(refresh.IsSuccess);

        var ambiguous = await roster.ResolveSelectorAsync(caller, "Reviewer", CancellationToken.None);

        Assert.Equal("Guild.SourceConflict", ambiguous.Error.Code);

        foreach (var diagnostic in refresh.Value.Diagnostics.Items)
        {
            Assert.Contains(diagnostic.AgentId!.Value.ToString(), ambiguous.Error.Message, StringComparison.Ordinal);
        }

        var wrongCase = await roster.ResolveSelectorAsync(caller, "reviewer", CancellationToken.None);

        Assert.Equal("Guild.NotFound", wrongCase.Error.Code);
    }

    [Fact]
    public async Task Nonoperator_and_wrong_installation_contexts_are_refused_before_source_discovery()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        var peer = new GuildCallerContext("peer", "grant-rev", Guid.NewGuid(), caller.InstallationGeneration);

        var peerResult = await roster.ListAsync(peer, new(), CancellationToken.None);

        Assert.Equal("Guild.Forbidden", peerResult.Error.Code);

        var wrong = new GuildCallerContext(GuildCallerKind.Operator, caller.PrincipalId!.Value, Guid.NewGuid(), Guid.NewGuid());

        var wrongResult = await roster.ListAsync(wrong, new(), CancellationToken.None);

        Assert.Equal("Guild.AuthorityUnavailable", wrongResult.Error.Code);
    }

    [Fact]
    public async Task Duplicate_explicit_ids_disable_selection_until_operator_rebinds_after_source_removal()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        Guid alias = Guid.NewGuid();

        string firstPath = Path.Combine(_root, "a.md");

        await File.WriteAllTextAsync(firstPath, $"---\nname: First\ndescription: Original\nid: {alias:D}\n---");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var initial = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Guid agent = Assert.Single(initial.Value.Diagnostics.Items).AgentId!.Value;

        await File.WriteAllTextAsync(Path.Combine(_root, "b.md"), $"---\nname: Second\ndescription: Moved\nid: {alias:D}\n---");

        request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var duplicate = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(duplicate.IsSuccess);

        Assert.Contains(duplicate.Value.Diagnostics.Items, item => item.ReasonCode == "Guild.SourceConflict");

        Assert.Equal("Guild.Unavailable", (await roster.ResolveAsync(caller, agent, null, null, CancellationToken.None)).Error.Code);

        request = new GuildRefreshAgentsRequest(Guid.NewGuid(), agent);

        Assert.True((await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None)).IsSuccess);

        Assert.Equal("Guild.Unavailable", (await roster.ResolveAsync(caller, agent, null, null, CancellationToken.None)).Error.Code);

        var observed = await new GuildDeclaredAgentSourceReader().ReadFileAsync(_root, _root,
            GuildAgentSourceKind.DeclaredGlobal, null, "b.md", CancellationToken.None);

        var denied = await roster.RebindExplicitAsync(caller, alias, observed.Value.Items[0], 1, CancellationToken.None);

        Assert.Equal("Guild.SourceConflict", denied.Error.Code);

        File.Delete(firstPath);

        var rebound = await roster.RebindExplicitAsync(caller, alias, observed.Value.Items[0], 1, CancellationToken.None);

        Assert.True(rebound.IsSuccess);

        request = new GuildRefreshAgentsRequest(Guid.NewGuid(), agent);

        var refreshed = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(refreshed.IsSuccess);

        var selected = await roster.ResolveAsync(caller, agent, null, null, CancellationToken.None);

        Assert.Equal("Second", selected.Value.Name);

        Assert.Equal(2, selected.Value.Revision);

        Assert.Equal("First", (await roster.ResolveAsync(caller, agent, null, 1, CancellationToken.None)).Value.Name);
    }

    [Fact]
    public async Task Refresh_follows_more_than_one_page_and_replays_a_continuation_without_new_identity()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        for (int index = 0; index < 103; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"agent-{index:D3}.md"),
                $"---\nname: Agent {index}\ndescription: Example\n---");
        }

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var first = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Assert.True(first.IsSuccess);

        Assert.Equal(100, first.Value.Diagnostics.Items.Length);

        Assert.False(first.Value.Completed);

        Assert.NotNull(first.Value.Diagnostics.NextCursor);

        Guid firstAgent = first.Value.Diagnostics.Items[0].AgentId!.Value;

        Assert.Equal("Guild.Unavailable", (await roster.ResolveAsync(caller, firstAgent, null, null, CancellationToken.None)).Error.Code);

        Assert.True((await roster.ResolveAsync(caller, firstAgent, null, 1, CancellationToken.None)).IsSuccess);

        var continuation = new GuildRefreshAgentsRequest(Guid.NewGuid(), Cursor: first.Value.Diagnostics.NextCursor);

        var next = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(continuation), continuation, CancellationToken.None);

        Assert.True(next.IsSuccess);

        Assert.Equal(3, next.Value.Diagnostics.Items.Count(item => item.Available));

        string? cursor = null;

        HashSet<Guid> agents = [];

        do
        {
            var page = await roster.ListAsync(caller, new(Cursor: cursor, PageSize: 17), CancellationToken.None);

            Assert.True(page.IsSuccess);

            foreach (var item in page.Value.Items)
            {
                Assert.True(agents.Add(item.AgentId));
            }

            cursor = page.Value.NextCursor;
        } while (cursor is not null);

        Assert.Equal(103, agents.Count);

        var replay = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(continuation), continuation, CancellationToken.None);

        Assert.Equal(next.Value.Receipt.Id, replay.Value.Receipt.Id);

        Assert.True(replay.Value.Receipt.Replayed);
    }

    [Fact]
    public async Task Ambiguous_names_report_bounded_candidates_once_ambiguity_is_proven()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        string name = new('x', 1024);

        for (int index = 0; index < 100; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"agent-{index:D3}.md"),
                $"---\nname: {name}\ndescription: Example\n---");
        }

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        Assert.True((await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None)).IsSuccess);

        var result = await roster.ResolveSelectorAsync(caller, name, CancellationToken.None);

        Assert.Equal("Guild.SourceConflict", result.Error.Code);

        Assert.InRange(result.Error.Message.Length, 1, 4096);
    }

    [Fact]
    public async Task Unsafe_root_refresh_invalidates_cached_destinations_without_changing_pinned_content()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        await File.WriteAllTextAsync(Path.Combine(_root, "agent.md"), "---\nname: Agent\ndescription: Original\n---");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var initial = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Guid agent = Assert.Single(initial.Value.Diagnostics.Items).AgentId!.Value;

        string moved = _root + "-moved";

        Directory.Move(_root, moved);

        Directory.CreateSymbolicLink(_root, moved);

        try
        {
            request = new GuildRefreshAgentsRequest(Guid.NewGuid());

            var invalid = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

            Assert.False(invalid.IsSuccess);

            Assert.Equal("Guild.Unavailable", (await roster.ResolveAsync(caller, agent, null, null, CancellationToken.None)).Error.Code);

            var pinned = await roster.ResolveAsync(caller, agent, null, 1, CancellationToken.None);

            Assert.Equal("Original", pinned.Value.Description);
        }
        finally
        {
            Directory.Delete(_root);

            Directory.Move(moved, _root);
        }
    }

    [Fact]
    public async Task Superseded_scan_cursor_cannot_publish_over_a_replacement_discovery()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        for (int index = 0; index < 101; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"agent-{index:D3}.md"),
                $"---\nname: Agent {index}\ndescription: Example\n---");
        }

        var initial = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var first = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(initial), initial, CancellationToken.None);

        Assert.NotNull(first.Value.Diagnostics.NextCursor);

        var replacement = new GuildRefreshAgentsRequest(Guid.NewGuid());

        Assert.True((await roster.RefreshAsync(caller, roster.CreateRefreshMutation(replacement), replacement, CancellationToken.None)).IsSuccess);

        var continuation = new GuildRefreshAgentsRequest(Guid.NewGuid(), Cursor: first.Value.Diagnostics.NextCursor);

        var stale = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(continuation), continuation, CancellationToken.None);

        Assert.False(stale.IsSuccess);

        Assert.Equal("Guild.CursorGone", stale.Error.Code);
    }

    [Fact]
    public async Task Cross_scope_rebind_refuses_before_mutating_pinned_card_history()
    {
        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        var (roster, caller) = await CreateAsync(database);

        Guid alias = Guid.NewGuid();

        string path = Path.Combine(_root, "agent.md");

        await File.WriteAllTextAsync(path, $"---\nname: Original\ndescription: Global source\nid: {alias:D}\n---");

        var request = new GuildRefreshAgentsRequest(Guid.NewGuid());

        var first = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(request), request, CancellationToken.None);

        Guid agentId = first.Value.Diagnostics.Items[0].AgentId!.Value;

        File.Delete(path);

        GuildParsedAgent parsed = new(alias, "Replacement", "Workspace source", "1", [], ["text/plain"], ["text/plain"], [], "");

        var result = await roster.RebindExplicitAsync(caller, alias,
            new("different-scope", GuildAgentSourceKind.DeclaredWorkspace, Guid.NewGuid(), parsed, new string('A', 64), null),
            1, CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Equal("Guild.SourceConflict", result.Error.Code);

        Assert.Equal("Original", (await roster.ResolveAsync(caller, agentId, null, 1, CancellationToken.None)).Value.Name);
    }

    private async Task<(GuildRoster Roster, GuildCallerContext Caller)> CreateAsync(EvolutionScratchDatabase database)
    {
        await using (var connection = await database.OpenAsync(CancellationToken.None))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, CancellationToken.None);
        }

        var connections = new FixtureOrdinaryConnectionFactory(database.ConnectionString);

        var gate = new GrimoireConnectionAdmissionGate(TimeProvider.System);

        Guid generation = Guid.NewGuid();

        return (new GuildRoster(new GuildStore(connections, gate, TimeProvider.System),
            new GuildRosterRefreshStore(connections, gate, new GuildRosterRefreshCoordinator()), new GuildRosterContext(generation, _root),
            new GuildDeclaredAgentSourceReader(), new GuildRemoteCardReader(new SystemDnsResolver()), TimeProvider.System),
            new GuildCallerContext(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), generation));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
