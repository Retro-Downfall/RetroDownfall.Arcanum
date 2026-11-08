using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Guild;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceLeaseIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lease-identity-" + Guid.NewGuid().ToString("N"));

    private readonly WorkspaceLeaseIdentityResolver _resolver = new();

    public WorkspaceLeaseIdentityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task File_slots_exclude_siblings_but_subtrees_cover_descendants_and_nested_roots()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));

        var first = await Resolve("a/b");

        var sibling = await Resolve("a/b2");

        var tree = await Resolve("a", GuildPathScopeKind.Subtree);

        var nested = await _resolver.ResolveAsync(Path.Combine(_root, "a"), [new("b", GuildPathScopeKind.File)], CancellationToken.None);

        Assert.False(first.ConflictsWith(sibling));

        Assert.True(tree.ConflictsWith(first));

        Assert.True(tree.Covers(first));

        Assert.False(first.Covers(tree));

        Assert.True(nested.IsSuccess);

        Assert.True(first.ConflictsWith(nested.Value));
    }

    [Fact]
    public async Task Missing_shared_ancestor_is_reserved_as_subtree()
    {
        var first = await Resolve("missing/a");

        var second = await Resolve("missing/b");

        Assert.True(first.ConflictsWith(second));

        Assert.Contains(first.Scopes, scope => scope.Kind == GuildPathScopeKind.Subtree);
    }

    [Fact]
    public async Task Parent_and_root_replacement_invalidate_evidence()
    {
        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        var first = await Resolve("parent/a");

        Directory.Move(Path.Combine(_root, "parent"), Path.Combine(_root, "old-parent"));

        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        Assert.True((await _resolver.ValidateAsync(first, CancellationToken.None)).IsFailure);

        var second = await Resolve("new-file");

        Directory.Move(_root, _root + "-old");

        Directory.CreateDirectory(_root);

        try
        {
            Assert.True((await _resolver.ValidateAsync(second, CancellationToken.None)).IsFailure);
        }
        finally
        {
            Directory.Delete(_root + "-old", recursive: true);
        }
    }

    [Fact]
    public async Task Atomic_file_replacement_keeps_namespace_reservation()
    {
        File.WriteAllText(Path.Combine(_root, "file"), "old");

        var first = await Resolve("file");

        File.WriteAllText(Path.Combine(_root, "replacement"), "new");

        File.Move(Path.Combine(_root, "replacement"), Path.Combine(_root, "file"), overwrite: true);

        var second = await Resolve("file");

        Assert.True(first.ConflictsWith(second));

        Assert.True((await _resolver.ValidateAsync(first, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Missing_leaf_may_be_created_but_a_reparse_alias_is_refused()
    {
        var first = await Resolve("file");

        File.WriteAllText(Path.Combine(_root, "file"), "new");

        Assert.True((await _resolver.ValidateAsync(first, CancellationToken.None)).IsSuccess);

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), _root);

            var alias = await _resolver.ResolveAsync(_root, [new("alias/file", GuildPathScopeKind.File)], CancellationToken.None);

            Assert.True(alias.IsFailure);

            Assert.Equal(ErrorCodes.Guild.WorkspaceIdentityUnavailable, alias.Error.Code);
        }
    }

    [Fact]
    public async Task Missing_names_follow_actual_directory_case_policy()
    {
        File.WriteAllText(Path.Combine(_root, "CaseProbe"), "probe");

        bool insensitive = File.Exists(Path.Combine(_root, "caseprobe"));

        var first = await Resolve("FutureName");

        var second = await Resolve("futurename");

        Assert.Equal(insensitive, first.ConflictsWith(second));
    }

    [Fact]
    public async Task Scope_order_does_not_change_digest_and_traversal_is_refused()
    {
        var first = await _resolver.ResolveAsync(_root, [new("a", GuildPathScopeKind.File), new("b", GuildPathScopeKind.File)], CancellationToken.None);

        var second = await _resolver.ResolveAsync(_root, [new("b", GuildPathScopeKind.File), new("a", GuildPathScopeKind.File)], CancellationToken.None);

        Assert.True(first.IsSuccess);

        Assert.True(second.IsSuccess);

        Assert.Equal(first.Value.Digest, second.Value.Digest);

        Assert.True((await _resolver.ResolveAsync(_root, [new("../escape", GuildPathScopeKind.File)], CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Durable_evidence_round_trips_and_malformed_ancestry_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        var original = await Resolve("parent/leaf");

        var restored = WorkspaceLeaseIdentitySet.FromJson(original.ToJson());

        Assert.True(restored.IsSuccess);

        Assert.Equal(original.Digest, restored.Value.Digest);

        Assert.True(restored.Value.Covers(original));

        Assert.True((await _resolver.ValidateAsync(restored.Value, CancellationToken.None)).IsSuccess);

        Assert.True(WorkspaceLeaseIdentitySet.FromJson("{}").IsFailure);

        string malformed = original.ToJson().Replace("\"Ancestors\":[{", "\"Ancestors\":[null,{");

        Assert.True(WorkspaceLeaseIdentitySet.FromJson(malformed).IsFailure);
    }

    [Fact]
    public async Task A_created_missing_parent_requires_fresh_identity_under_the_same_gate()
    {
        var original = await Resolve("new/leaf");

        Directory.CreateDirectory(Path.Combine(_root, "new"));

        Assert.True((await _resolver.ValidateAsync(original, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task File_scope_never_covers_a_subtree_at_the_same_missing_slot()
    {
        var file = await Resolve("missing");

        var subtree = await Resolve("missing", GuildPathScopeKind.Subtree);

        Assert.True(file.ConflictsWith(subtree));

        Assert.False(file.Covers(subtree));

        Assert.True(subtree.Covers(file));
    }

    [Fact]
    public async Task A_directory_moved_outside_the_root_during_capture_is_refused()
    {
        string parent = Path.Combine(_root, "parent");

        string outside = _root + "-outside";

        Directory.CreateDirectory(parent);

        Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(_root, out var rootHandle, out _));

        using (rootHandle)
        {
            Assert.True(FileHandleIdentityInterop.TryGetHandleKernelPath(rootHandle, out string? canonicalRoot));

            var previous = FileHandleIdentityInterop.TryGetHandleKernelPathForTests;

            int calls = 0;

            FileHandleIdentityInterop.TryGetHandleKernelPathForTests = _ =>
            {
                if (++calls == 1)
                {
                    return canonicalRoot;
                }

                Directory.Move(parent, outside);

                return outside;
            };

            try
            {
                var result = await _resolver.ResolveAsync(_root, [new("parent/leaf", GuildPathScopeKind.File)], CancellationToken.None);

                Assert.True(result.IsFailure);
            }
            finally
            {
                FileHandleIdentityInterop.TryGetHandleKernelPathForTests = previous;

                if (Directory.Exists(outside))
                {
                    Directory.Delete(outside, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task Durable_evidence_rejects_traversing_or_inconsistent_relative_paths()
    {
        var original = await Resolve("leaf");

        string json = original.ToJson();

        Assert.True(WorkspaceLeaseIdentitySet.FromJson(json.Replace("\"RelativePath\":\"leaf\"", "\"RelativePath\":\"../leaf\"")).IsFailure);

        Assert.True(WorkspaceLeaseIdentitySet.FromJson(json.Replace("\"RelativePath\":\"leaf\"", "\"RelativePath\":\"other\"")).IsFailure);
    }

    [Fact]
    public async Task Replacing_a_parent_cannot_escape_an_existing_file_reservation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        var before = await Resolve("parent/file");

        Directory.Move(Path.Combine(_root, "parent"), Path.Combine(_root, "old-parent"));

        Directory.CreateDirectory(Path.Combine(_root, "parent"));

        var after = await Resolve("parent/file");

        Assert.True(before.ConflictsWith(after));

        Assert.False(before.Covers(after));
    }

    [Fact]
    public async Task Changed_directory_case_policy_conflicts_without_granting_coverage()
    {
        var before = await Resolve("file");

        var scope = before.Scopes[0];

        var after = before with
        {
            Scopes = [scope with { Ancestors = scope.Ancestors.SetItem(scope.Ancestors.Length - 1, scope.Ancestors[^1] with { CaseSensitive = !scope.Ancestors[^1].CaseSensitive }) }],
        };

        Assert.True(before.ConflictsWith(after));

        Assert.False(before.Covers(after));
    }

    [Fact]
    public async Task Whole_workspace_subtree_covers_a_nested_registration()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));

        var outer = await Resolve(".", GuildPathScopeKind.Subtree);

        var nested = await _resolver.ResolveAsync(Path.Combine(_root, "nested"), [new("file", GuildPathScopeKind.File)], CancellationToken.None);

        Assert.True(nested.IsSuccess);

        Assert.True(outer.ConflictsWith(nested.Value));

        Assert.True(outer.Covers(nested.Value));
    }

    [SkippableFact]
    public async Task A_kernel_leaf_alias_beyond_case_is_refused()
    {
        string file = Path.Combine(_root, "leaf");

        File.WriteAllText(file, "bytes");

        Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(_root, out var root, out _));

        using (root)
        {
            Assert.True(FileHandleIdentityInterop.TryGetDirectoryCaseSensitivity(root, out bool sensitive));

            Skip.If(sensitive, "This regression exercises the case-insensitive canonical-name branch.");

            Assert.True(FileHandleIdentityInterop.TryGetHandleKernelPath(root, out string? canonicalRoot));

            Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(file, out var target));

            var previous = FileHandleIdentityInterop.TryGetHandleKernelPathForTests;

            string? KernelPath(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
            {
                if (FileHandleIdentityInterop.TryGetHandleIdentity(handle, out var identity) && identity == target)
                {
                    // NTFS can report a long name when opened through an 8.3 spelling.
                    return Path.Combine(canonicalRoot!, "different-canonical-name");
                }

                FileHandleIdentityInterop.TryGetHandleKernelPathForTests = previous;

                try
                {
                    return FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? actual) ? actual : null;
                }
                finally
                {
                    FileHandleIdentityInterop.TryGetHandleKernelPathForTests = KernelPath;
                }
            }

            FileHandleIdentityInterop.TryGetHandleKernelPathForTests = KernelPath;

            try
            {
                var result = await _resolver.ResolveAsync(_root, [new("leaf", GuildPathScopeKind.File)], CancellationToken.None);

                Assert.True(result.IsFailure);
            }
            finally
            {
                FileHandleIdentityInterop.TryGetHandleKernelPathForTests = previous;
            }
        }
    }

    [Fact]
    public async Task File_identity_keeps_conflict_after_rename_without_covering_a_new_namespace_slot()
    {
        File.WriteAllText(Path.Combine(_root, "before"), "bytes");

        var original = await Resolve("before");

        File.Move(Path.Combine(_root, "before"), Path.Combine(_root, "after"));

        var renamed = await Resolve("after");

        Assert.True(original.ConflictsWith(renamed));

        Assert.False(original.Covers(renamed));
    }

    private async Task<WorkspaceLeaseIdentitySet> Resolve(string path, GuildPathScopeKind kind = GuildPathScopeKind.File)
    {
        Result<WorkspaceLeaseIdentitySet> result = await _resolver.ResolveAsync(_root, [new(path, kind)], CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.ToString());

        return result.Value;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
