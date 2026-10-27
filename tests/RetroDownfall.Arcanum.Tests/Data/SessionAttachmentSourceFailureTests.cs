using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentSourceFailureTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const string PrivateCanary = "PRIVATE-REGISTRY-PATH-AND-CREDENTIAL";
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly FaultingRegistry _registry = new();
    private readonly TestCapturingLogger<SessionAttachmentStore> _logger = new();
    private string _dbPath = string.Empty;

    private string _root = string.Empty;

    private string _sourcePath = string.Empty;
    private ArcanumDbContext _db = null!;
    private SessionAttachmentStore _store = null!;

    private AttachmentSourceResolver _resolver = null!;
    private SessionAttachmentRecord _saved = null!;

    public async Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();
        _db = fixture.CreateContext(_dbPath);
        _root = Path.Combine(Path.GetTempPath(), "arcanum-source-failure-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        _sourcePath = Path.Combine(workspace, "source.txt");
        byte[] bytes = "retained encrypted snapshot"u8.ToArray();
        await File.WriteAllBytesAsync(_sourcePath, bytes);
        _db.Sessions.Add(new Session { Id = _sessionId, Title = "source recovery", Status = "active", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        _resolver = new AttachmentSourceResolver(new WorkspaceContext(workspace), _registry);

        _store = new(_db, Options.Create(new ArcanumSettings()), Path.Combine(_root, "attachments"),
            TestEncryptedBlobStore.Create(),
            _logger, _resolver);
        _saved = await _store.PersistNewFromSourceAsync(_sessionId, null, null, "source.txt", "source.txt", bytes,
            "text/plain", SessionAttachmentKind.Text, new AttachmentSourceClaim(_sourcePath));
        _registry.Reads = 0;
    }

    [SkippableTheory]
    [InlineData(false, "Unexpected")]
    [InlineData(true, "Unexpected")]
    [InlineData(false, "IO")]
    [InlineData(true, "IO")]
    [InlineData(false, "Access")]
    [InlineData(true, "Access")]
    [InlineData(false, "Storage")]
    [InlineData(true, "Storage")]
    public async Task Unexpected_registry_failure_preserves_source_and_snapshot_and_logs_safe_cause(bool startup, string kind)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        _registry.Failure = kind switch
        {
            "IO" => new IOException(PrivateCanary),
            "Access" => new UnauthorizedAccessException(PrivateCanary),
            "Storage" => new SqliteException(PrivateCanary, 5),
            _ => new InvalidOperationException(PrivateCanary),
        };
        await RevalidateAsync(startup);
        Assert.Equal(_saved.Source, (await _store.GetByIdAsync(_saved.Id))!.Source);
        Assert.Equal("retained encrypted snapshot"u8.ToArray(), (await _store.ReadBytesAsync(_saved)).ToArray());
        TestLogEntry warning = Assert.Single(_logger.Entries, x => x.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        Assert.Contains(_saved.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains("retained", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Retry", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateCanary, warning.Message, StringComparison.Ordinal);
        Assert.Equal(kind, Assert.Single(warning.Properties!, x => x.Key == "FailureKind").Value);

        Assert.Equal(kind == "Storage" ? (object)5 : null, Assert.Single(warning.Properties!, x => x.Key == "SqliteCode").Value);

        Assert.Equal(kind == "Storage" ? (object)5 : null, Assert.Single(warning.Properties!, x => x.Key == "SqliteExtendedCode").Value);
        Assert.DoesNotContain(warning.Properties!, x => x.Value is Exception || x.Value?.ToString()?.Contains(PrivateCanary, StringComparison.Ordinal) == true);
        _registry.Failure = null;
        await File.WriteAllTextAsync(_sourcePath, "changed after recovery");
        await RevalidateAsync(startup);
        AttachmentSourceMetadata current = (await _store.GetByIdAsync(_saved.Id))!.Source!;
        Assert.Equal(AttachmentSourceStatus.PriorVersion, current.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("changed after recovery"u8)), current.LastObservedContentSha256);
        Assert.Single(_logger.Entries, x => x.Level == LogLevel.Warning);
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancellation_is_propagated_without_reclassifying_metadata(bool startup, bool cancelCaller)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        using CancellationTokenSource cancellation = new();
        OperationCanceledException expected = new(PrivateCanary, cancellation.Token);
        _registry.Failure = expected;
        _registry.BeforeFailure = () => { if (cancelCaller) cancellation.Cancel(); };
        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RevalidateAsync(startup, cancellation.Token));
        Assert.Same(expected, actual);
        Assert.Equal(_saved.Source, (await _store.GetByIdAsync(_saved.Id))!.Source);
        Assert.DoesNotContain(_logger.Entries, x => x.Level >= LogLevel.Warning);
        Assert.Equal("retained encrypted snapshot"u8.ToArray(), (await _store.ReadBytesAsync(_saved)).ToArray());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proven_missing_source_still_updates_status_without_destroying_snapshot(bool startup)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        File.Delete(_sourcePath);
        await RevalidateAsync(startup);
        Assert.Equal(AttachmentSourceStatus.Missing, (await _store.GetByIdAsync(_saved.Id))!.Source!.Status);
        Assert.Equal("retained encrypted snapshot"u8.ToArray(), (await _store.ReadBytesAsync(_saved)).ToArray());
        Assert.DoesNotContain(_logger.Entries, x => x.Level >= LogLevel.Warning);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_wins_over_a_simultaneous_registry_fault(bool startup)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        using CancellationTokenSource cancellation = new();
        _registry.Failure = new InvalidOperationException(PrivateCanary);
        _registry.BeforeFailure = cancellation.Cancel;
        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RevalidateAsync(startup, cancellation.Token));
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(_saved.Source, (await _store.GetByIdAsync(_saved.Id))!.Source);
        Assert.DoesNotContain(_logger.Entries, x => x.Level >= LogLevel.Warning);
    }

    [SkippableFact]
    public async Task Bound_revalidation_memoizes_an_unavailable_source_without_rewriting_any_version()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionAttachmentRecord other = await AddSecondSnapshotAsync();
        _registry.Reads = 0;
        _registry.Failure = new InvalidOperationException(PrivateCanary);
        IReadOnlyList<SessionAttachmentRecord> rows = await _store.RevalidateBoundSourcesAsync(_sessionId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(_saved.Source, row.Source));
        Assert.Equal(1, _registry.Reads);
        Assert.Single(_logger.Entries, x => x.Level == LogLevel.Warning);
        Assert.Equal(other.Source, (await _store.GetByIdAsync(other.Id))!.Source);
        Assert.Equal(_saved.Source, (await _store.GetByIdAsync(_saved.Id))!.Source);
    }

    [SkippableFact]
    public async Task Startup_continues_to_a_proven_observation_after_an_unavailable_row()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
        SessionAttachmentRecord other = await AddSecondSnapshotAsync();
        File.Delete(_sourcePath);
        _registry.Failure = new InvalidOperationException(PrivateCanary);
        _registry.FailOnce = true;
        await _store.ReconcileAsync(TimeSpan.FromDays(365));
        SessionAttachmentRecord[] rows = [(await _store.GetByIdAsync(_saved.Id))!, (await _store.GetByIdAsync(other.Id))!];
        Assert.Single(rows, x => x.Source!.Status == AttachmentSourceStatus.Refreshable);
        Assert.Single(rows, x => x.Source!.Status == AttachmentSourceStatus.Missing);
        Assert.Single(_logger.Entries, x => x.Level == LogLevel.Warning);
        foreach (SessionAttachmentRecord row in rows)
        {
            Assert.Equal("retained encrypted snapshot"u8.ToArray(), (await _store.ReadBytesAsync(row)).ToArray());
        }
    }

    [SkippableFact]
    public async Task Last_known_refreshable_observation_does_not_authorize_a_later_missing_source_read()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _registry.Failure = new InvalidOperationException(PrivateCanary);

        SessionAttachmentRecord prior = Assert.Single(await _store.RevalidateBoundSourcesAsync(_sessionId));

        Assert.True(prior.Source!.IsRefreshable);

        _registry.Failure = null;

        File.Delete(_sourcePath);

        int authorizations = 0;

        AttachmentSourceResolution result = await _resolver.ResolveCurrentAsync(prior.Source, prior.ContentSha256, 1024,
            (_, _) => { authorizations++; return Task.FromResult(true); });

        Assert.Equal(AttachmentSourceStatus.Missing, result.Metadata.Status);

        Assert.True(result.VerifiedBytes.IsEmpty);

        Assert.Equal(0, authorizations);

        Assert.Equal(_saved.Source, (await _store.GetByIdAsync(_saved.Id))!.Source);
    }

    private async Task<SessionAttachmentRecord> AddSecondSnapshotAsync() =>
        await _store.PersistNewFromSourceAsync(_sessionId, null, null, "other.txt", "other.txt", "retained encrypted snapshot"u8.ToArray(),
            "text/plain", SessionAttachmentKind.Text, new AttachmentSourceClaim(_sourcePath));

    private async Task RevalidateAsync(bool startup, CancellationToken cancellationToken = default)
    {
        if (startup) await _store.ReconcileAsync(TimeSpan.FromDays(365), cancellationToken);
        else
        {
            SessionAttachmentRecord row = Assert.Single(await _store.RevalidateBoundSourcesAsync(_sessionId, cancellationToken));
            Assert.Equal(_saved.Id, row.Id);
        }
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        File.Delete(_dbPath);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class WorkspaceContext(string path) : IHostWorkspaceContext
    {
        public string? WorkspacePath => path;
    }

    private sealed class FaultingRegistry : IWorkspaceRegistry
    {
        public Exception? Failure { get; set; }
        public Action? BeforeFailure { get; set; }
        public int Reads { get; set; }

        public bool FailOnce { get; set; }
        public Task<WorkspaceInfo?> GetAsync(string id, CancellationToken ct)
        {
            Reads++;
            if (Failure is not null)
            {
                BeforeFailure?.Invoke();
                Exception failure = Failure;

                if (FailOnce) Failure = null;

                throw failure;
            }
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkspaceInfo?>(null);
        }

        public Task<WorkspaceInfo[]> GetAllAsync(CancellationToken ct) => Task.FromResult<WorkspaceInfo[]>([]);
        public Task<Result<WorkspaceInfo>> RegisterAsync(CreateWorkspaceRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<WorkspaceInfo>> UpdateAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<bool>> UnregisterAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
}
