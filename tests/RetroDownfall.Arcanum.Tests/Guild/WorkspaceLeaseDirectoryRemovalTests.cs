using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceLeaseDirectoryRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lease-rmdir-" + Guid.NewGuid().ToString("N"));

    private readonly WorkspaceLeaseIdentityResolver _resolver = new();

    public WorkspaceLeaseDirectoryRemovalTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Owned_directory_removal_can_be_adopted_and_replacement_afterward_is_refused()
    {
        string path = Path.Combine(_root, "owned");

        Directory.CreateDirectory(path);

        var before = await _resolver.ResolveAsync(_root, [new("owned", GuildPathScopeKind.Subtree)], CancellationToken.None);

        Assert.True(before.IsSuccess);

        var prepared = WorkspaceLeaseDirectoryRemoval.Prepare(path);

        Assert.True(prepared.IsSuccess, prepared.Error.ToString());

        using var removal = prepared.Value;

        Assert.True(removal.ValidateBeforeEffect().IsSuccess);

        Directory.Delete(path);

        var completed = removal.Complete();

        Assert.True(completed.IsSuccess, completed.Error.ToString());

        using var proof = completed.Value;

        var after = await _resolver.AdoptRemovedDirectoryAsync(before.Value, proof, CancellationToken.None);

        Assert.True(after.IsSuccess, after.Error.ToString());

        Assert.True((await _resolver.ValidateAsync(after.Value, CancellationToken.None)).IsSuccess);

        Directory.CreateDirectory(path);

        Assert.True((await _resolver.ValidateAsync(after.Value, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public void Replaced_directory_is_refused_before_the_delete_effect()
    {
        string path = Path.Combine(_root, "owned");

        Directory.CreateDirectory(path);

        var prepared = WorkspaceLeaseDirectoryRemoval.Prepare(path);

        Assert.True(prepared.IsSuccess, prepared.Error.ToString());

        using var removal = prepared.Value;

        Directory.Move(path, Path.Combine(_root, "original"));

        Directory.CreateDirectory(path);

        Assert.True(removal.ValidateBeforeEffect().IsFailure);

        Assert.True(removal.Complete().IsFailure);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task Another_directory_disappearance_cannot_be_laundered_through_owned_removal()
    {
        string owned = Path.Combine(_root, "owned");

        string other = Path.Combine(_root, "other");

        Directory.CreateDirectory(owned);

        Directory.CreateDirectory(other);

        var before = await _resolver.ResolveAsync(_root, [new("owned", GuildPathScopeKind.Subtree), new("other", GuildPathScopeKind.Subtree)], CancellationToken.None);

        Assert.True(before.IsSuccess);

        var prepared = WorkspaceLeaseDirectoryRemoval.Prepare(owned);

        Assert.True(prepared.IsSuccess);

        using var removal = prepared.Value;

        Directory.Delete(owned);

        var completed = removal.Complete();

        Assert.True(completed.IsSuccess, completed.Error.ToString());

        using var proof = completed.Value;

        Directory.Delete(other);

        Assert.True((await _resolver.AdoptRemovedDirectoryAsync(before.Value, proof, CancellationToken.None)).IsFailure);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
