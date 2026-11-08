using System.Security.AccessControl;
using System.Security.Principal;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceLeaseDirectoryCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lease-mkdir-" + Guid.NewGuid().ToString("N"));

    private readonly WorkspaceLeaseIdentityResolver _resolver = new();

    public WorkspaceLeaseDirectoryCreationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Exclusive_creation_can_be_adopted_one_missing_parent_at_a_time()
    {
        var before = await Resolve("new/deep/file");

        using var preparation = Prepare("new");

        Assert.True((await _resolver.ValidateDirectoryCreationAsync(before, preparation, CancellationToken.None)).IsSuccess);

        var created = preparation.CreateExclusive();

        Assert.True(created.IsSuccess, created.Error.ToString());

        using var proof = created.Value;

        Assert.True((await _resolver.ValidateAsync(before, CancellationToken.None)).IsFailure);

        var adopted = await _resolver.AdoptCreatedDirectoryAsync(before, proof, CancellationToken.None);

        Assert.True(adopted.IsSuccess, adopted.Error.ToString());

        Assert.True((await _resolver.ValidateAsync(adopted.Value, CancellationToken.None)).IsSuccess);

        Assert.True(before.ConflictsWith(await Resolve("new/other")));

        using var nested = Prepare("new/deep");

        var nestedCreated = nested.CreateExclusive();

        Assert.True(nestedCreated.IsSuccess, nestedCreated.Error.ToString());

        using var nestedProof = nestedCreated.Value;

        var final = await _resolver.AdoptCreatedDirectoryAsync(adopted.Value, nestedProof, CancellationToken.None);

        Assert.True(final.IsSuccess, final.Error.ToString());

        Assert.True((await _resolver.ValidateAsync(final.Value, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public void Existing_or_concurrently_created_directory_never_gets_a_creation_proof()
    {
        Directory.CreateDirectory(Path.Combine(_root, "existing"));

        Assert.True(WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(_root, "existing")).IsFailure);

        using var preparation = Prepare("new");

        Directory.CreateDirectory(Path.Combine(_root, "new"));

        Assert.True(preparation.CreateExclusive().IsFailure);
    }

    [Fact]
    public async Task Replaced_created_directory_or_unrelated_new_parent_is_not_adopted()
    {
        var before = await _resolver.ResolveAsync(_root, [new("a/file", GuildPathScopeKind.File), new("b/file", GuildPathScopeKind.File)], CancellationToken.None);

        Assert.True(before.IsSuccess);

        using var preparation = Prepare("a");

        var created = preparation.CreateExclusive();

        Assert.True(created.IsSuccess, created.Error.ToString());

        using var proof = created.Value;

        Directory.CreateDirectory(Path.Combine(_root, "b"));

        Assert.True((await _resolver.AdoptCreatedDirectoryAsync(before.Value, proof, CancellationToken.None)).IsFailure);

        Directory.Delete(Path.Combine(_root, "b"));

        Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "old-a"));

        Directory.CreateDirectory(Path.Combine(_root, "a"));

        Assert.True((await _resolver.AdoptCreatedDirectoryAsync(before.Value, proof, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public void Parent_replacement_between_preparation_and_effect_refuses_before_creating()
    {
        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        using var preparation = Prepare("parent/new");

        Directory.Move(Path.Combine(_root, "parent"), Path.Combine(_root, "old-parent"));

        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        Assert.True(preparation.CreateExclusive().IsFailure);

        Assert.False(Directory.Exists(Path.Combine(_root, "parent/new")));

        Assert.False(Directory.Exists(Path.Combine(_root, "old-parent/new")));
    }

    [Fact]
    public async Task Proof_outside_the_reserved_subtree_and_disposed_proof_are_refused()
    {
        var before = await Resolve("allowed/file");

        using var preparation = Prepare("other");

        var created = preparation.CreateExclusive();

        Assert.True(created.IsSuccess);

        using var proof = created.Value;

        Assert.True((await _resolver.AdoptCreatedDirectoryAsync(before, proof, CancellationToken.None)).IsFailure);

        proof.Dispose();

        Assert.True((await _resolver.AdoptCreatedDirectoryAsync(before, proof, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Retained_staging_identity_can_move_to_one_reserved_absent_destination()
    {
        string stage = Path.Combine(_root, "stage");

        Directory.CreateDirectory(stage);

        var before = await _resolver.ResolveAsync(_root, [new("stage", GuildPathScopeKind.Subtree), new("target/file", GuildPathScopeKind.File)], CancellationToken.None);

        Assert.True(before.IsSuccess);

        Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(stage, out var handle, out _));

        using (handle)
        {
            var preparation = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(_root, "target"), handle);

            Assert.True(preparation.IsSuccess, preparation.Error.ToString());

            using var pending = preparation.Value;

            Directory.Move(stage, Path.Combine(_root, "target"));

            var created = pending.Complete(handle);

            Assert.True(created.IsSuccess, created.Error.ToString());

            using var proof = created.Value;

            handle.Dispose();

            var adopted = await _resolver.AdoptCreatedDirectoryAsync(before.Value, proof, CancellationToken.None);

            Assert.True(adopted.IsSuccess, adopted.Error.ToString());

            Assert.True((await _resolver.ValidateAsync(adopted.Value, CancellationToken.None)).IsSuccess);
        }
    }

    [Fact]
    public void Private_directory_is_created_owner_only()
    {
        using var preparation = Prepare("private");

        var created = preparation.CreateExclusive(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(created.IsSuccess, created.Error.ToString());

        using var proof = created.Value;

        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();

            DirectorySecurity security = new DirectoryInfo(Path.Combine(_root, "private")).GetAccessControl();

            Assert.True(security.AreAccessRulesProtected);

            Assert.Equal(current.User!.Value, security.GetOwner(typeof(SecurityIdentifier))!.Value);

            FileSystemAccessRule[] rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();

            Assert.NotEmpty(rules);

            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.Equal(current.User.Value, rule.IdentityReference.Value);

                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);

                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            }
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(_root, "private")));
        }
    }

    [Fact]
    public async Task An_externally_relocated_source_cannot_be_laundered_as_the_declared_source()
    {
        string stage = Path.Combine(_root, "stage");

        string outside = Path.Combine(_root, "outside");

        Directory.CreateDirectory(stage);

        var before = await _resolver.ResolveAsync(_root, [new("stage", GuildPathScopeKind.Subtree), new("target", GuildPathScopeKind.Subtree)], CancellationToken.None);

        Assert.True(before.IsSuccess);

        Directory.Move(stage, outside);

        Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(outside, out var handle, out _));

        using (handle)
        {
            var prepared = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(_root, "target"), handle);

            Assert.True(prepared.IsSuccess);

            using var preparation = prepared.Value;

            Directory.Move(outside, Path.Combine(_root, "target"));

            var completed = preparation.Complete(handle);

            Assert.True(completed.IsSuccess, completed.Error.ToString());

            using var proof = completed.Value;

            Assert.True((await _resolver.AdoptCreatedDirectoryAsync(before.Value, proof, CancellationToken.None)).IsFailure);
        }
    }

    private WorkspaceLeaseDirectoryCreation Prepare(string relative)
    {
        var result = WorkspaceLeaseDirectoryCreation.Prepare(Path.Combine(_root, relative));

        Assert.True(result.IsSuccess, result.Error.ToString());

        return result.Value;
    }

    private async Task<WorkspaceLeaseIdentitySet> Resolve(string relative)
    {
        var result = await _resolver.ResolveAsync(_root, [new(relative, GuildPathScopeKind.File)], CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.ToString());

        return result.Value;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
