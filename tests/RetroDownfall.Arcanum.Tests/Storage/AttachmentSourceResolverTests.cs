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

    private sealed class TestWorkspaceContext(string? workspacePath) : IHostWorkspaceContext
    {
        public string? WorkspacePath { get; } = workspacePath;
    }
}
