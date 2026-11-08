using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_list_cursor_refuses_changed_query_filters(bool changeQuest)
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);

        Assert.True((await f.AcquireAsync("b.txt")).IsSuccess);

        var first = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null, new(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Single(first.Value.Items);

        Assert.NotNull(first.Value.NextCursor);

        var continuation = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId,
            changeQuest ? Guid.NewGuid() : null, changeQuest ? null : GuildWorkspaceLeaseState.Held,
            new(first.Value.NextCursor, 1), default);

        Assert.True(continuation.IsFailure);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, continuation.Error.Code);
    }

    [Fact]
    public async Task Lease_list_cursor_accepts_changed_page_size_with_unchanged_owner_and_filters()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var acquiredFirst = await f.AcquireAsync("a.txt");

        var acquiredSecond = await f.AcquireAsync("b.txt");

        Assert.True(acquiredFirst.IsSuccess, acquiredFirst.Error.Message);

        Assert.True(acquiredSecond.IsSuccess, acquiredSecond.Error.Message);

        var first = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null,
            GuildWorkspaceLeaseState.Held, new(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Single(first.Value.Items);

        Assert.NotNull(first.Value.NextCursor);

        var continuation = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null,
            GuildWorkspaceLeaseState.Held, new(first.Value.NextCursor, 100), default);

        Assert.True(continuation.IsSuccess, continuation.Error.Message);

        WorkspaceLeaseDto remaining = Assert.Single(continuation.Value.Items);

        Assert.NotEqual(first.Value.Items[0].Id, remaining.Id);

        Assert.Contains(remaining.Id, new[] { acquiredFirst.Value.Id, acquiredSecond.Value.Id });

        Assert.Null(continuation.Value.NextCursor);
    }

    [Fact]
    public async Task Lease_list_cursor_refuses_changed_authenticated_owner_in_the_same_workspace()
    {
        await using Fixture f = await Fixture.CreateAsync();

        using EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

        await using (SqliteConnection connection = await database.OpenAsync(default))
        {
            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);
        }

        CursorScopePolicy policy = new(f.Policy);

        await using GuildWorkspaceLeaseBroker broker = new(new(new FixtureOrdinaryConnectionFactory(database.ConnectionString)),
            new WorkspaceLeaseIdentityResolver(), policy, f.Time, f.Host);

        foreach (string path in new[] { "a.txt", "b.txt" })
        {
            Guid key = Guid.NewGuid();

            var acquired = await broker.AcquireAsync(f.Policy.Caller, new(key, new string('A', 64)),
                new(key, f.Policy.WorkspaceId, [new(path, GuildPathScopeKind.File)]), default);

            Assert.True(acquired.IsSuccess, acquired.Error.Message);
        }

        var first = await broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null, new(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Single(first.Value.Items);

        Assert.NotNull(first.Value.NextCursor);

        policy.PrincipalId = Guid.NewGuid();

        var continuation = await broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null,
            new(first.Value.NextCursor, 1), default);

        Assert.True(continuation.IsFailure);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, continuation.Error.Code);
    }

    [Fact]
    public async Task Lease_list_refuses_malformed_cursor_for_an_authenticated_caller()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var result = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null,
            new("malformed-cursor", 1), default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.CursorInvalid, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lease_list_authenticates_caller_before_inspecting_cursor(bool malformedCursor)
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);

        Assert.True((await f.AcquireAsync("b.txt")).IsSuccess);

        var first = await f.Broker.ListAsync(f.Policy.Caller, f.Policy.WorkspaceId, null, null,
            new(PageSize: 1), default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.NotNull(first.Value.NextCursor);

        GuildCallerContext real = f.Policy.Caller;

        GuildCallerContext forged = new(real.Kind, real.PrincipalId!.Value, real.InvocationId, real.InstallationGeneration);

        var result = await f.Broker.ListAsync(forged, f.Policy.WorkspaceId, null, null,
            new(malformedCursor ? "malformed-cursor" : first.Value.NextCursor, 1), default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal("Unknown caller.", result.Error.Message);
    }

    private sealed class CursorScopePolicy(Policy inner) : IGuildWorkspaceLeaseHostPolicy
    {
        internal Guid? PrincipalId { get; set; }

        private GuildWorkspaceLeaseBinding Bind(GuildWorkspaceLeaseBinding binding) => PrincipalId is { } principal
            ? binding with { Owner = binding.Owner with { PrincipalId = principal } } : binding;

        public Task<Result<T>> AuthorizeAsync<T>(GuildCallerContext caller, Guid workspaceId, GuildAuthorityAction action,
            Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct) =>
            inner.AuthorizeAsync(caller, workspaceId, action, (binding, token) => effect(Bind(binding), token), ct);

        public Task<Result<T>> RevalidateAsync<T>(GuildVerifiedAuthority authority, Guid workspaceId,
            Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct) =>
            inner.RevalidateAsync(authority, workspaceId, (binding, token) => effect(Bind(binding), token), ct);
    }
}
