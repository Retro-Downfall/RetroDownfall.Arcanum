using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task External_file_rename_does_not_move_an_explicit_lease_authority_to_a_new_slot()
    {
        await using Fixture f = await Fixture.CreateAsync();

        File.WriteAllText(Path.Combine(f.Root, "a.txt"), "owned");

        Assert.True((await f.AcquireAsync("a.txt")).IsSuccess);

        File.Move(Path.Combine(f.Root, "a.txt"), Path.Combine(f.Root, "b.txt"));

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("b.txt"), default);

        if (admitted.IsSuccess)
        {
            _ = await f.Broker.CompleteMutationAsync(admitted.Value, default);
        }

        Assert.True(admitted.IsFailure);
    }

    [Fact]
    public async Task Owned_creation_adoption_after_expiry_records_physical_fact_without_reopening_forward_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var admitted = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("new/file"), default);

        Assert.True(admitted.IsSuccess, admitted.Error.ToString());

        var prepared = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(f.Root, "new"));

        Assert.True(prepared.IsSuccess, prepared.Error.ToString());

        using var preparation = prepared.Value;

        Assert.True((await f.Broker.ValidateDirectoryCreationAsync(admitted.Value, preparation, false, default)).IsSuccess);

        var created = preparation.CreateExclusive();

        Assert.True(created.IsSuccess, created.Error.ToString());

        using var proof = created.Value;

        f.Time.Advance(TimeSpan.FromMinutes(16));

        var adopted = await f.Broker.AdoptCreatedDirectoryAsync(admitted.Value, proof, false, default);

        var forward = await f.Broker.ValidateCommitAsync(admitted.Value, default);

        var rollback = await f.Broker.ValidateRollbackAsync(admitted.Value, default);

        _ = await f.Broker.CompleteMutationAsync(admitted.Value, default);

        Assert.True(adopted.IsSuccess, adopted.Error.ToString());

        Assert.True(forward.IsFailure);

        Assert.True(rollback.IsSuccess, rollback.Error.ToString());
    }

    [Fact]
    public async Task A_moved_directorys_original_durable_reservation_does_not_grant_its_new_slot()
    {
        await using Fixture f = await Fixture.CreateAsync();

        string stage = Path.Combine(f.Root, "stage");

        Directory.CreateDirectory(stage);

        Guid key = Guid.NewGuid();

        var lease = await f.Broker.AcquireAsync(f.Policy.Caller, new(key, new string('C', 64)),
            new(key, f.Policy.WorkspaceId, [new("stage", GuildPathScopeKind.Subtree)]), default);

        Assert.True(lease.IsSuccess, lease.Error.ToString());

        var permit = await f.Broker.AdmitMutationAsync(f.Policy.Authority,
            new(f.Policy.WorkspaceId, [new("stage", GuildPathScopeKind.Subtree), new("target", GuildPathScopeKind.Subtree)], new string('D', 64)), default);

        Assert.True(permit.IsSuccess, permit.Error.ToString());

        Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(stage, out var handle, out _));

        using (handle)
        {
            var prepared = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(f.Root, "target"), handle);

            Assert.True(prepared.IsSuccess, prepared.Error.ToString());

            using var preparation = prepared.Value;

            Assert.True((await f.Broker.ValidateDirectoryCreationAsync(permit.Value, preparation, false, default)).IsSuccess);

            Directory.Move(stage, Path.Combine(f.Root, "target"));

            var completed = preparation.Complete(handle);

            Assert.True(completed.IsSuccess, completed.Error.ToString());

            using var proof = completed.Value;

            Assert.True((await f.Broker.AdoptCreatedDirectoryAsync(permit.Value, proof, false, default)).IsSuccess);
        }

        Assert.True((await f.Broker.CompleteMutationAsync(permit.Value, default)).IsSuccess);

        var outside = await f.Broker.AdmitMutationAsync(f.Policy.Authority, f.Plan("target/new-file"), default);

        if (outside.IsSuccess)
        {
            _ = await f.Broker.CompleteMutationAsync(outside.Value, default);
        }

        Assert.True(outside.IsFailure);
    }
}
