using System.Text;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

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

    [Fact]
    public async Task ResolveForReferenceAsync_uses_explicit_registered_root_for_later_resolution()
    {
        string registeredWorkspace = Path.Combine(
            Path.GetTempPath(),
            "arcanum-registered-source-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(registeredWorkspace);

        try
        {
            string path = Path.Combine(registeredWorkspace, "notes.txt");

            await File.WriteAllTextAsync(path, "registered");

            AttachmentSourceResolver resolver = new(
                new TestWorkspaceContext(_workspace),
                new TestWorkspaceRegistry(registeredWorkspace));

            AttachmentSourceClaim claim = CreateSourceClaim(path, registeredWorkspace);

            AttachmentSourceResolution referenced = await ResolveForReferenceAsync(
                resolver,
                claim,
                maxBytes: 1024,
                authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

            AttachmentSourceMetadata revalidated = await resolver.RevalidateAsync(referenced.Metadata);

            AttachmentSourceResolution refreshed = await resolver.ResolveCurrentAsync(
                referenced.Metadata,
                referenced.Metadata.LastObservedContentSha256!,
                maxBytes: 1024,
                authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

            Assert.Equal(AttachmentSourceStatus.Refreshable, referenced.Metadata.Status);

            Assert.Equal("notes.txt", referenced.Metadata.WorkspaceRelativePath);

            Assert.Equal("registered-workspace-id", referenced.Metadata.WorkspaceIdentity);

            Assert.Equal("registered", Encoding.UTF8.GetString(referenced.VerifiedBytes.Span));

            Assert.Equal(AttachmentSourceStatus.Refreshable, revalidated.Status);

            Assert.Equal(AttachmentSourceStatus.Refreshable, refreshed.Metadata.Status);

            Assert.Equal("registered", Encoding.UTF8.GetString(refreshed.VerifiedBytes.Span));
        }
        finally
        {
            Directory.Delete(registeredWorkspace, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveForReferenceAsync_reads_within_bound_and_authorizes_canonical_path()
    {
        string path = Path.Combine(_workspace, "notes.txt");

        await File.WriteAllTextAsync(path, "bounded");

        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));

        AttachmentSourceClaim claim = CreateSourceClaim(path, _workspace);

        int authorizerCalls = 0;

        string? authorizedPath = null;

        AttachmentSourceResolution result = await ResolveForReferenceAsync(
            resolver,
            claim,
            maxBytes: 7,
            authorizeCanonicalPath: (canonicalPath, _) =>
            {
                authorizerCalls++;

                authorizedPath = canonicalPath;

                return Task.FromResult(true);
            });

        Assert.Equal(1, authorizerCalls);

        Assert.Equal(Path.GetFullPath(path), authorizedPath);

        Assert.Equal(AttachmentSourceStatus.Refreshable, result.Metadata.Status);

        Assert.Equal("bounded", Encoding.UTF8.GetString(result.VerifiedBytes.Span));

        Assert.Equal("text/plain", result.DetectedMimeType);
    }

    [Fact]

    public async Task ResolveForReferenceAsync_rejects_in_workspace_symlink_retarget_before_open()

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

        resolver.BeforeSourceOpenForTesting = _ =>

        {

            File.Delete(link);

            File.CreateSymbolicLink(link, second);

            return Task.CompletedTask;

        };

        string? authorizedPath = null;

        AttachmentSourceResolution result = await ResolveForReferenceAsync(

            resolver,

            new AttachmentSourceClaim(link),

            maxBytes: 1024,

            authorizeCanonicalPath: (canonicalPath, _) =>

            {

                authorizedPath = canonicalPath;

                return Task.FromResult(true);

            });

        Assert.Equal(AttachmentSourceStatus.Unsafe, result.Metadata.Status);

        Assert.True(result.VerifiedBytes.IsEmpty);

        Assert.Null(result.DetectedMimeType);

        Assert.Null(authorizedPath);

    }

    [Fact]
    public async Task ResolveForReferenceAsync_returns_no_bytes_for_oversized_denied_or_unsafe_sources()
    {
        string path = Path.Combine(_workspace, "notes.txt");

        await File.WriteAllTextAsync(path, "12345");

        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));

        AttachmentSourceClaim claim = CreateSourceClaim(path, _workspace);

        AttachmentSourceResolution oversized = await ResolveForReferenceAsync(
            resolver,
            claim,
            maxBytes: 4,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

        AttachmentSourceResolution denied = await ResolveForReferenceAsync(
            resolver,
            claim,
            maxBytes: 1024,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(false));

        string externalPath = Path.Combine(
            Path.GetTempPath(),
            "arcanum-external-source-" + Guid.NewGuid().ToString("N") + ".txt");

        await File.WriteAllTextAsync(externalPath, "external");

        try
        {
            AttachmentSourceResolution unsafeSource = await ResolveForReferenceAsync(
                resolver,
                CreateSourceClaim(externalPath, _workspace),
                maxBytes: 1024,
                authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

            Assert.Equal(AttachmentSourceStatus.Inaccessible, oversized.Metadata.Status);

            Assert.False(oversized.Metadata.IsRefreshable);

            Assert.True(oversized.VerifiedBytes.IsEmpty);

            Assert.Null(oversized.DetectedMimeType);

            Assert.Equal(AttachmentSourceStatus.Inaccessible, denied.Metadata.Status);

            Assert.False(denied.Metadata.IsRefreshable);

            Assert.True(denied.VerifiedBytes.IsEmpty);

            Assert.Null(denied.DetectedMimeType);

            Assert.Equal(AttachmentSourceStatus.Unsafe, unsafeSource.Metadata.Status);

            Assert.False(unsafeSource.Metadata.IsRefreshable);

            Assert.True(unsafeSource.VerifiedBytes.IsEmpty);

            Assert.Null(unsafeSource.DetectedMimeType);
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    [Fact]
    public async Task ResolveForReferenceAsync_discards_bytes_when_source_grows_beyond_bound_during_read()
    {
        string path = Path.Combine(_workspace, "notes.txt");

        await File.WriteAllTextAsync(path, "1234");

        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));

        resolver.AfterFirstRefreshReadForTesting = _ => File.WriteAllTextAsync(path, "12345");

        AttachmentSourceResolution result = await ResolveForReferenceAsync(
            resolver,
            CreateSourceClaim(path, _workspace),
            maxBytes: 4,
            authorizeCanonicalPath: static (_, _) => Task.FromResult(true));

        Assert.True(new FileInfo(path).Length > 4);

        Assert.False(result.Metadata.IsRefreshable);

        Assert.True(result.VerifiedBytes.IsEmpty);

        Assert.Null(result.DetectedMimeType);
    }

    [Fact]
    public async Task RevalidateAsync_marks_grown_source_stale_without_adopting_its_unbounded_size()
    {
        string path = Path.Combine(_workspace, "notes.txt");

        byte[] original = Encoding.UTF8.GetBytes("1234");

        await File.WriteAllBytesAsync(path, original);

        AttachmentSourceResolver resolver = new(new TestWorkspaceContext(_workspace));

        AttachmentSourceResolution persisted = await resolver.ResolveForPersistenceAsync(
            new AttachmentSourceClaim(path),
            original);

        await File.WriteAllTextAsync(path, new string('x', 1024 * 1024));

        AttachmentSourceMetadata revalidated = await resolver.RevalidateAsync(persisted.Metadata);

        Assert.Equal(AttachmentSourceStatus.PriorVersion, revalidated.Status);

        Assert.Equal(original.LongLength, revalidated.LastObservedByteLength);

        Assert.Equal(persisted.Metadata.LastObservedContentSha256, revalidated.LastObservedContentSha256);
    }

    private static AttachmentSourceClaim CreateSourceClaim(
        string absolutePath,
        string workspaceRoot) =>
        new(absolutePath, workspaceRoot);

    private static async Task<AttachmentSourceResolution> ResolveForReferenceAsync(
        IAttachmentSourceResolver resolver,
        AttachmentSourceClaim claim,
        long maxBytes,
        AttachmentSourcePathAuthorizer authorizeCanonicalPath) =>
        await resolver.ResolveForReferenceAsync(
            claim,
            maxBytes,
            authorizeCanonicalPath);

    private sealed class TestWorkspaceRegistry(string workspacePath) : IWorkspaceRegistry
    {
        private readonly WorkspaceInfo _workspace = new(
            "registered-workspace-id",
            "Registered workspace",
            workspacePath,
            WorkspaceType.Custom,
            DateTimeOffset.UtcNow,
            Persisted: true);

        public Task<WorkspaceInfo[]> GetAllAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(new[] { _workspace });
        }

        public Task<WorkspaceInfo?> GetAsync(string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            WorkspaceInfo? result = string.Equals(id, _workspace.Id, StringComparison.Ordinal)
                ? _workspace
                : null;

            return Task.FromResult(result);
        }

        public Task<Result<WorkspaceInfo>> RegisterAsync(
            CreateWorkspaceRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<WorkspaceInfo>> UpdateAsync(
            string id,
            UpdateWorkspaceRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<bool>> UnregisterAsync(string id, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class TestWorkspaceContext(string? workspacePath) : IHostWorkspaceContext
    {
        public string? WorkspacePath { get; } = workspacePath;
    }
}
