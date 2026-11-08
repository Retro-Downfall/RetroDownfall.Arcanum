using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Overlaps_conflict_before_admission_but_siblings_and_terminal_replay_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var first = await f.AcquireAsync("a.txt");

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);

        Assert.True((await f.AcquireAsync("b.txt")).IsSuccess);

        Guid key = Guid.NewGuid();

        var released = await f.Broker.ReleaseAsync(f.Policy.Caller, new(key, new string('B', 64)), first.Value.Id,
            new(key, first.Value.Revision, first.Value.Epoch), default);

        Assert.True(released.IsSuccess, released.Error.Message);

        var replay = await f.Broker.AcquireAsync(f.Policy.Caller, f.LastFirstMutation!, f.LastFirstRequest!, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(GuildWorkspaceLeaseState.Released, replay.Value.State);
    }

    [Fact]
    public async Task Expiry_does_not_steal_admitted_writer_and_copied_permit_cannot_release_it()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var acquired = await f.AcquireAsync("a.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("a.txt"), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        GuildMutationPermit original = admitted.Value;

        GuildMutationPermit forged = new(original.LeaseId, original.Epoch, original.Authority, original.EffectPlanDigest);

        Assert.True((await f.Broker.CompleteMutationAsync(forged, default)).IsFailure);

        f.Time.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(ErrorCodes.Guild.LeaseLapsed, (await f.Broker.ValidateCommitAsync(original, default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);

        Assert.True((await f.Broker.CompleteMutationAsync(original, default)).IsSuccess);

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);
    }

    [Fact]
    public async Task Complete_scope_set_is_atomic_and_same_owner_cannot_commit_twice()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.AcquireAsync("b.txt")).IsSuccess);

        var denied = await f.AcquireAsync("a.txt", "b.txt");

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, denied.Error.Code);

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);

        var permit = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("a.txt"), default);

        Assert.True(permit.IsSuccess, permit.Error.Message);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict,
            (await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("a.txt"), default)).Error.Code);

        Assert.True((await f.Broker.CompleteMutationAsync(permit.Value, default)).IsSuccess);
    }

    [Fact]
    public async Task Copied_caller_cannot_adopt_operator_lease()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var acquired = await f.AcquireAsync("a.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        GuildCallerContext real = f.Policy.Caller;

        GuildCallerContext forged = new(real.Kind, real.PrincipalId!.Value, real.InvocationId, real.InstallationGeneration);

        Guid key = Guid.NewGuid();

        var result = await f.Broker.ReleaseAsync(forged, new(key, new string('C', 64)), acquired.Value.Id,
            new(key, acquired.Value.Revision, acquired.Value.Epoch), default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, (await f.AcquireAsync("a.txt")).Error.Code);
    }

    [Fact]
    public async Task Lease_acquisition_cannot_reserve_outside_the_physical_authorized_scope()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Policy.AllowedScopes = [new("allowed", GuildPathScopeKind.Subtree)];

        Result<WorkspaceLeaseDto> denied = await f.AcquireAsync("outside.txt");

        Assert.True(denied.IsFailure);

        Assert.True((await f.AcquireAsync("allowed/inside.txt")).IsSuccess);
    }

    [Theory]
    [InlineData("", ".")]
    [InlineData(".", "")]
    public async Task Empty_and_dot_root_scope_are_equivalent(string grantRoot, string requestedRoot)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Policy.AllowedScopes = [new(grantRoot, GuildPathScopeKind.Subtree)];

        Guid key = Guid.NewGuid();

        Result<WorkspaceLeaseDto> acquired = await f.Broker.AcquireAsync(f.Policy.Caller, new(key, new string('A', 64)),
            new(key, f.Policy.WorkspaceId, [new(requestedRoot, GuildPathScopeKind.Subtree)]), default);

        Assert.True(acquired.IsSuccess, acquired.Error.Message);
    }

    [Fact]
    public async Task Missing_parent_outside_granted_subtree_cannot_be_reserved_or_mutated()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Policy.AllowedScopes = [new("allowed/deep", GuildPathScopeKind.Subtree)];

        Assert.True((await f.AcquireAsync("allowed/deep/file.txt")).IsFailure);

        Assert.True((await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("allowed/deep/file.txt"), default)).IsFailure);
    }

    [Fact]
    public async Task Narrowed_scope_refuses_mutation_and_commit_even_when_lease_remains_held()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.AcquireAsync("outside.txt")).IsSuccess);

        Result<GuildMutationPermit> admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("outside.txt"), default);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        f.Policy.AllowedScopes = [new("allowed", GuildPathScopeKind.Subtree)];

        Assert.True((await f.Broker.ValidateCommitAsync(admitted.Value, default)).IsFailure);

        Assert.True((await f.Broker.CompleteMutationAsync(admitted.Value, default)).IsSuccess);

        Assert.True((await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("outside.txt"), default)).IsFailure);
    }

    [Fact]
    public async Task Narrowed_scope_refuses_renewal_but_exact_owner_can_release()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<WorkspaceLeaseDto> acquired = await f.AcquireAsync("outside.txt");

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        f.Policy.AllowedScopes = [new("allowed", GuildPathScopeKind.Subtree)];

        Guid renewKey = Guid.NewGuid();

        Assert.True((await f.Broker.RenewAsync(f.Policy.Caller, new(renewKey, new string('A', 64)), acquired.Value.Id,
            new(renewKey, acquired.Value.Revision, acquired.Value.Epoch), default)).IsFailure);

        f.Policy.AllowedScopes = [new(".", GuildPathScopeKind.Subtree)];

        Guid retryKey = Guid.NewGuid();

        Assert.True((await f.Broker.RenewAsync(f.Policy.Caller, new(retryKey, new string('C', 64)), acquired.Value.Id,
            new(retryKey, acquired.Value.Revision, acquired.Value.Epoch), default)).IsFailure);

        Guid releaseKey = Guid.NewGuid();

        Assert.True((await f.Broker.ReleaseAsync(f.Policy.Caller, new(releaseKey, new string('B', 64)), acquired.Value.Id,
            new(releaseKey, acquired.Value.Revision, acquired.Value.Epoch), default)).IsSuccess);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly EvolutionScratchDatabase _database;

        private readonly string _directory;

        internal string Root => _directory;

        internal ManualWallClockTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        internal GuildWorkspaceLeaseHostLock Host { get; }

        internal string HostLockPath => Path.Combine(_directory + "-control", "broker.lock");

        internal Policy Policy { get; }

        internal GuildWorkspaceLeaseBroker Broker { get; }

        internal GuildMutationIdentity? LastFirstMutation { get; private set; }

        internal AcquireWorkspaceLeaseRequest? LastFirstRequest { get; private set; }

        private Fixture(EvolutionScratchDatabase database, string directory)
        {
            _database = database;

            _directory = directory;

            Policy = new(directory);

            var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(HostLockPath);

            Assert.True(acquired.IsSuccess, acquired.Error.Message);

            Host = acquired.Value;

            Broker = new(new(new FixtureOrdinaryConnectionFactory(database.ConnectionString)),
                new WorkspaceLeaseIdentityResolver(), Policy, Time, Host);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            EvolutionScratchDatabase database = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await database.OpenAsync(default);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

            string directory = Path.Combine(Path.GetTempPath(), "guild-lease-broker-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);

            return new(database, directory);
        }

        internal Task<Result<WorkspaceLeaseDto>> AcquireAsync(params string[] paths)
        {
            Guid key = Guid.NewGuid();

            AcquireWorkspaceLeaseRequest request = new(key, Policy.WorkspaceId,
                paths.Select(path => new GuildPathScope(path, GuildPathScopeKind.File)).ToImmutableArray());

            GuildMutationIdentity mutation = new(key, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("|", paths)))));

            LastFirstMutation ??= mutation;

            LastFirstRequest ??= request;

            return Broker.AcquireAsync(Policy.Caller, mutation, request, default);
        }

        internal GuildMutationPlan Plan(string path) => new(Policy.WorkspaceId, [new(path, GuildPathScopeKind.File)], new string('A', 64));

        public async ValueTask DisposeAsync()
        {
            await Broker.DisposeAsync();

            Host.Dispose();

            Directory.Delete(_directory, recursive: true);

            Directory.Delete(_directory + "-control", recursive: true);

            _database.Dispose();
        }
    }

    private sealed class Policy : IGuildWorkspaceLeaseHostPolicy
    {
        internal Guid WorkspaceId { get; } = Guid.NewGuid();

        internal GuildCallerContext Caller { get; } = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        internal GuildVerifiedAuthority Authority { get; }

        internal ImmutableArray<GuildPathScope> AllowedScopes { get; set; } = [new(".", GuildPathScopeKind.Subtree)];

        private readonly string _root;

        internal Policy(string root)
        {
            _root = root;

            Authority = new(Caller, GuildAuthorityAction.WorkspaceMutate, WorkspaceId, 1);
        }

        private GuildWorkspaceLeaseBinding Binding => new(new(GuildWorkspaceLeaseOwnerKind.Operator, Caller.PrincipalId!.Value,
            null, null, null, null, WorkspaceId, Caller.InstallationGeneration), _root, Authority, AllowedScopes);

        public Task<Result<T>> AuthorizeAsync<T>(GuildCallerContext caller, Guid workspaceId, GuildAuthorityAction action,
            Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct) =>
            ReferenceEquals(caller, Caller) && workspaceId == WorkspaceId
                ? effect(Binding, ct) : Task.FromResult(Result<T>.Failure(new(ErrorCodes.Guild.Forbidden, "Unknown caller.")));

        public Task<Result<T>> RevalidateAsync<T>(GuildVerifiedAuthority authority, Guid workspaceId,
            Func<GuildWorkspaceLeaseBinding, CancellationToken, Task<Result<T>>> effect, CancellationToken ct) =>
            ReferenceEquals(authority, Authority) && workspaceId == WorkspaceId
                ? effect(Binding, ct) : Task.FromResult(Result<T>.Failure(new(ErrorCodes.Guild.Forbidden, "Unknown authority.")));
    }
}
