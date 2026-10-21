using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class MemoryRecallToolTests
{
    [Fact]
    public async Task Recall_returns_bounded_typed_results_and_continuation_belongs_to_one_turn()
    {
        var reader = new Reader();

        var tool = Tool(reader);

        var first = await tool.InvokeAsync(Arguments(limit: 1));

        var page = Page(first);

        Assert.Equal("query first", Assert.Single(page.Items).Content);

        Assert.NotNull(page.NextCursor);

        var continued = Page(await tool.InvokeAsync(Arguments(limit: 1, cursor: page.NextCursor)));

        Assert.Equal("query second", Assert.Single(continued.Items).Content);

        Assert.Null(continued.NextCursor);

        var otherTurn = await Tool(reader).InvokeAsync(Arguments(limit: 1, cursor: page.NextCursor));

        Assert.Contains("unavailable", otherTurn!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("query second", otherTurn.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unattended")]
    [InlineData("background")]
    [InlineData("no-context")]
    [InlineData("no-tools")]
    [InlineData("invocation-no-tools")]
    [InlineData("unbound")]
    [InlineData("forwarded")]
    public async Task Direct_invocation_cannot_bypass_advertisement_gates(string mode)
    {
        var reader = new Reader();

        var invocation = mode == "background" ? ArcanumInvocationContext.None
            : mode == "no-context" ? InvocationContexts.NoContextSession()
            : mode is "invocation-no-tools" or "unbound"
                ? ArcanumInvocationContext.Create(ArcanumExecutionSurface.StatelessOperatorTurn,
                    mode == "unbound" ? null : CanonicalCampaignContext.GlobalOnly, InvocationAttendance.Attended,
                    CovenantContextPolicy.Default, mode == "invocation-no-tools" ? ToolPolicy.NoTools : ToolPolicy.AllTools, null).Value
                : InvocationContexts.AttendedGlobalOnlySession();

        var request = new PingRequest("parent", UnattendedMode: mode == "unattended", DisableAllTools: mode == "no-tools", ForwardClientTools: mode == "forwarded");

        var tool = new ArcanumMemoryRecallTool(reader, request, invocation, null, mode != "disabled", true, 4096);

        var result = await tool.InvokeAsync(Arguments());

        Assert.Contains("unavailable", result!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Isolated_child_cannot_invoke_a_parent_tool_it_retained()
    {
        var reader = new Reader();

        var tool = Tool(reader);

        using var child = SubagentExecutionAmbient.EnterChild(new DelegatedManaTracker(100, null));

        var result = await tool.InvokeAsync(Arguments());

        Assert.Contains("unavailable", result!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Disabling_recall_during_a_read_refuses_the_result()
    {
        bool enabled = true;

        var reader = new Reader { AfterRead = () => enabled = false };

        var tool = new ArcanumMemoryRecallTool(reader, new PingRequest("parent"), InvocationContexts.AttendedGlobalOnlySession(),
            null, true, true, 4096, enabledNow: () => enabled);

        var result = await tool.InvokeAsync(Arguments());

        Assert.Contains("unavailable", result!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("query first", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaignId", "stolen-campaign")]
    [InlineData("sessionId", "stolen-session")]
    [InlineData("scope", "All; DROP TABLE")]
    [InlineData("cursor", "forged-cursor")]
    public async Task Invalid_or_model_supplied_authority_is_refused_before_read(string key, string value)
    {
        var reader = new Reader();

        var args = Arguments();

        args[key] = value;

        var result = await Tool(reader).InvokeAsync(args);

        Assert.DoesNotContain("query first", result!.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain(value, result.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Covenant_recall_uses_only_linked_eligible_versions_and_retains_generation_sensitivity()
    {
        var invocation = InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId);

        var global = CovenantTask6Fixture.GlobalConfirmed("query.preference", CovenantTask6Fixture.G1, CovenantTask6Fixture.G2, 1, 1);

        var campaign = CovenantTask6Fixture.CampaignConfirmed("query.preference", CovenantTask6Fixture.G3, CovenantTask6Fixture.G4,
            2, 4, CovenantTask6Fixture.CampaignId);

        var plan = new CovenantLinker().Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, global, campaign)).Value;

        await using var turn = new CovenantTurnScope(CovenantTurnContext.ForPlan(plan,
            new CovenantTurnLease(new LeaseRegistration()), null, Guid.NewGuid()), Guid.NewGuid(), null, false);

        var authority = new Authority(invocation.ReadAuthorityEpoch!);

        var tool = new ArcanumMemoryRecallTool(new Reader(), new PingRequest("parent"), invocation, turn, true, true, 4096, authority);

        var args = Arguments();

        args["scope"] = "Covenant";

        var result = await tool.InvokeAsync(args);

        var page = Page(result);

        Assert.Equal(CovenantTask6Fixture.G4, Assert.Single(page.Items).VersionId);

        Assert.DoesNotContain(CovenantTask6Fixture.G2.ToString("D"), result!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(ContentSensitivity.CovenantDerived, turn.DerivedSensitivity!.Level);

        Assert.Contains(CovenantTask6Fixture.DatasetGeneration, turn.DerivedSensitivity.Provenance.ExactGenerationIds);

        authority.Current = authority.Current! with { AuthorityEpoch = authority.Current.AuthorityEpoch + 1 };

        var refused = await tool.InvokeAsync(args);

        Assert.Contains("unavailable", refused!.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("query.preference", refused.ToString(), StringComparison.Ordinal);
    }

    private sealed class Authority(CovenantReadAuthorityEpoch epoch) : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; set; } = new(epoch.RuntimeAuthorityGeneration,
            epoch.InstallationIdentity, epoch.AuthorityEpoch, 1, 1, CovenantHostToolsState.Clean, null);
    }

    private sealed class LeaseRegistration : ICovenantLeaseRegistration
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1, CovenantLeaseKind.Turn,
            CovenantLeaseCoverage.Scoped, CovenantOperationScope.Global, CovenantTask6Fixture.DatasetGeneration,
            1, 11, 0, 1, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }

    private static ArcanumMemoryRecallTool Tool(Reader reader) => new(reader, new PingRequest("parent"),
        InvocationContexts.AttendedGlobalOnlySession(), null, true, true, 4096);

    private static AIFunctionArguments Arguments(int limit = 10, string? cursor = null) => new()
    {
        ["query"] = "query",
        ["scope"] = "Lexicon",
        ["limit"] = limit,
        ["cursor"] = cursor,
    };

    private static MemoryRecallPage Page(object? value)
    {
        Assert.NotNull(value);

        return JsonSerializer.Deserialize(value.ToString()!, ArcanumJsonContext.Default.MemoryRecallPage)!;
    }

    private sealed class Reader : IMemoryRecallCandidateReader
    {
        internal int Reads { get; private set; }

        internal Action? AfterRead { get; init; }

        public Task<Result<MemoryRecallCandidates>> ReadAsync(MemoryRecallRequest request,
            CanonicalCampaignContext canonical, MemoryScope memoryScope, CancellationToken cancellationToken)
        {
            Reads++;

            AfterRead?.Invoke();

            return Task.FromResult(Result<MemoryRecallCandidates>.Success(new([
                new(MemoryRecallScope.Lexicon, "one", "query first"),
                new(MemoryRecallScope.Lexicon, "two", "query second"),
            ])));
        }
    }
}
