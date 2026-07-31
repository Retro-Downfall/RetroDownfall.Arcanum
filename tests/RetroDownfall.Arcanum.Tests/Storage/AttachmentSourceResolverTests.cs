using System.Text;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class AttachmentSourceResolverTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "arcanum-source-" + Guid.NewGuid().ToString("N"));

    public AttachmentSourceResolverTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveForPersistenceAsync_accepts_verified_workspace_file()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        byte[] bytes = Encoding.UTF8.GetBytes("verified");
        await File.WriteAllBytesAsync(path, bytes);
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));

        AttachmentSourceResolution result = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path), bytes);

        Assert.Equal(AttachmentSourceKind.WorkspaceFile, result.Metadata.Kind);
        Assert.Equal(AttachmentSourceStatus.Refreshable, result.Metadata.Status);
        Assert.Equal("notes.txt", result.Metadata.WorkspaceRelativePath);
        Assert.True(result.Metadata.IsRefreshable);
        Assert.NotNull(result.Metadata.LastObservedFileIdentity);
    }

    [Fact]
    public async Task ResolveForPersistenceAsync_marks_prior_snapshot_without_claiming_refreshable()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "current");
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));

        AttachmentSourceResolution result = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path), Encoding.UTF8.GetBytes("older"));

        Assert.Equal(AttachmentSourceKind.WorkspaceFile, result.Metadata.Kind);
        Assert.Equal(AttachmentSourceStatus.PriorVersion, result.Metadata.Status);
        Assert.False(result.Metadata.IsRefreshable);
    }

    [Fact]
    public async Task ResolveForPersistenceAsync_rejects_external_and_missing_sources()
    {
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        string external = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(external, "outside");
        try
        {
            AttachmentSourceResolution outside = await resolver.ResolveForPersistenceAsync(
                new AttachmentSourceClaim(external), Encoding.UTF8.GetBytes("outside"));
            AttachmentSourceResolution missing = await resolver.ResolveForPersistenceAsync(
                new AttachmentSourceClaim(Path.Combine(_workspace, "missing.txt")), ReadOnlyMemory<byte>.Empty);

            Assert.Equal(AttachmentSourceKind.SnapshotOnly, outside.Metadata.Kind);
            Assert.Equal(AttachmentSourceStatus.Unsafe, outside.Metadata.Status);
            Assert.Equal(AttachmentSourceStatus.Missing, missing.Metadata.Status);
        }
        finally
        {
            File.Delete(external);
        }
    }

    [Fact]
    public async Task RevalidateAsync_fails_closed_when_workspace_identity_changes()
    {
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        AttachmentSourceMetadata source = new(
            AttachmentSourceKind.WorkspaceFile,
            "different-workspace",
            "notes.txt",
            null,
            null,
            null,
            null,
            null,
            AttachmentSourceStatus.Refreshable,
            null);

        AttachmentSourceMetadata result = await resolver.RevalidateAsync(source);

        Assert.Equal(AttachmentSourceStatus.WorkspaceChanged, result.Status);
        Assert.False(result.IsRefreshable);
    }

    [Fact]
    public async Task ResolveCurrentAsync_reads_changed_bytes_from_verified_handle()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "current");
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        AttachmentSourceResolution persisted = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path), Encoding.UTF8.GetBytes("older"));

        AttachmentSourceResolution refreshed = await resolver.ResolveCurrentAsync(
            persisted.Metadata,
            expectedSnapshotSha256: Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("older"))),
            maxBytes: 1024,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

        Assert.Equal(AttachmentSourceStatus.PriorVersion, refreshed.Metadata.Status);
        Assert.Equal("current", Encoding.UTF8.GetString(refreshed.VerifiedBytes.Span));
        Assert.Equal("text/plain", refreshed.DetectedMimeType);
    }

    [Fact]
    public async Task ResolveCurrentAsync_fails_closed_when_file_changes_during_read()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "before");
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        AttachmentSourceResolution persisted = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path), Encoding.UTF8.GetBytes("before"));
        resolver.AfterFirstRefreshReadForTesting = _ => File.WriteAllTextAsync(path, "after-change");

        AttachmentSourceResolution refreshed = await resolver.ResolveCurrentAsync(
            persisted.Metadata,
            persisted.Metadata.LastObservedContentSha256!,
            maxBytes: 1024,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

        Assert.Equal(AttachmentSourceStatus.Unsafe, refreshed.Metadata.Status);
        Assert.True(refreshed.VerifiedBytes.IsEmpty);
        Assert.Contains("changed", refreshed.Metadata.DiagnosticReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveCurrentAsync_enforces_bound_before_read_and_sanctum_authorizer()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "too long");
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        AttachmentSourceResolution persisted = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path), Encoding.UTF8.GetBytes("too long"));
        int authorizerCalls = 0;

        AttachmentSourceResolution denied = await resolver.ResolveCurrentAsync(
            persisted.Metadata,
            persisted.Metadata.LastObservedContentSha256!,
            maxBytes: 4,
            authorizeCanonicalPath: (_, _) =>
            {
                authorizerCalls++;
                return Task.FromResult(false);
            });

        Assert.Equal(AttachmentSourceStatus.Inaccessible, denied.Metadata.Status);
        Assert.True(denied.VerifiedBytes.IsEmpty);
        Assert.Equal(1, authorizerCalls);
    }

    [Fact]
    public async Task ResolveCurrentAsync_fails_closed_for_missing_or_oversized_source()
    {
        string path = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "12345");
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        AttachmentSourceResolution persisted = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path), Encoding.UTF8.GetBytes("12345"));

        AttachmentSourceResolution oversized = await resolver.ResolveCurrentAsync(
            persisted.Metadata,
            persisted.Metadata.LastObservedContentSha256!,
            maxBytes: 4,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));
        File.Delete(path);
        AttachmentSourceResolution missing = await resolver.ResolveCurrentAsync(
            persisted.Metadata,
            persisted.Metadata.LastObservedContentSha256!,
            maxBytes: 1024,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

        Assert.Equal(AttachmentSourceStatus.Inaccessible, oversized.Metadata.Status);
        Assert.Contains("size limit", oversized.Metadata.DiagnosticReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AttachmentSourceStatus.Missing, missing.Metadata.Status);
        Assert.True(missing.VerifiedBytes.IsEmpty);
    }

    [Fact]
    public async Task ResolveCurrentAsync_detects_symlink_target_swap_after_open()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string first = Path.Combine(_workspace, "first.txt");
        string second = Path.Combine(_workspace, "second.txt");
        string link = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        File.CreateSymbolicLink(link, first);
        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));
        AttachmentSourceResolution persisted = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(link), Encoding.UTF8.GetBytes("first"));
        resolver.AfterFirstRefreshReadForTesting = _ =>
        {
            File.Delete(link);
            File.CreateSymbolicLink(link, second);
            return Task.CompletedTask;
        };

        AttachmentSourceResolution refreshed = await resolver.ResolveCurrentAsync(
            persisted.Metadata,
            persisted.Metadata.LastObservedContentSha256!,
            maxBytes: 1024,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

        Assert.Equal(AttachmentSourceStatus.Unsafe, refreshed.Metadata.Status);
        Assert.True(refreshed.VerifiedBytes.IsEmpty);
    }

    private sealed class TestWorkspaceContext(string? workspacePath) : IHostWorkspaceContext
    {
        public string? WorkspacePath { get; } = workspacePath;
    }
}
