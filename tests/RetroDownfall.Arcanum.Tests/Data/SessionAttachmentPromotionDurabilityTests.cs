using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentPromotionDurabilityTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _dbPath = string.Empty;

    private string _root = string.Empty;

    private ArcanumDbContext _db = null!;

    private SessionAttachmentStore _store = null!;

    private readonly Guid _session = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        _root = Directory.CreateTempSubdirectory("arcanum-promotion-durability-").FullName;

        _store = new(_db, Options.Create(new ArcanumSettings()), _root, TestEncryptedBlobStore.Create());

        _db.Sessions.Add(new Session
        {
            Id = _session,
            Title = "promotion durability",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = null;

        await _db.DisposeAsync();

        File.Delete(_dbPath);

        Directory.Delete(_root, recursive: true);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Promotion_requires_a_successful_device_flush_before_binding_or_unlinking_pending_bytes(bool failFlush)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string turn = "durability-turn";

        byte[] bytes = "durable encrypted attachment"u8.ToArray();

        SessionAttachmentRecord pending = await _store.PersistNewAsync(
            null, turn, null, "durable.txt", "durable.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        string oldPath = Path.Combine(_root, pending.RelativePath);

        List<bool> flushRequests = [];

        bool reachedDatabase = false;

        SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = (path, _, _) =>
            new FlushObservingStream(path, flushRequests, failFlush);

        _store.AfterBytesCommittedBeforeDbForTesting = _ =>
        {
            reachedDatabase = true;

            if (!failFlush)
            {
                Assert.Contains(true, flushRequests);
            }

            Assert.True(File.Exists(oldPath));

            return Task.CompletedTask;
        };

        try
        {
            if (failFlush)
            {
                await Assert.ThrowsAsync<IOException>(() => _store.PromotePendingAsync(turn, _session, null));

                Assert.False(reachedDatabase);

                SessionAttachmentRecord saved = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

                Assert.Equal(SessionAttachmentState.Pending, saved.State);

                Assert.Equal(pending.RelativePath, saved.RelativePath);

                Assert.Equal(bytes, (await _store.ReadBytesAsync(saved)).ToArray());
            }
            else
            {
                await _store.PromotePendingAsync(turn, _session, null);

                Assert.True(reachedDatabase);

                Assert.False(File.Exists(oldPath));

                SessionAttachmentRecord saved = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

                Assert.Equal(SessionAttachmentState.Bound, saved.State);

                Assert.Equal(bytes, (await _store.ReadBytesAsync(saved)).ToArray());
            }
        }
        finally
        {
            SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = null;

            _store.AfterBytesCommittedBeforeDbForTesting = null;
        }
    }

    [SkippableTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Promotion_requires_both_directory_barriers_and_compensates_only_its_own_copy(int refusedBarrier, bool replaceCopy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string turn = "directory-durability-turn";

        byte[] bytes = "directory durable attachment"u8.ToArray();

        SessionAttachmentRecord pending = await _store.PersistNewAsync(
            null, turn, null, "durable.txt", "durable.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        string oldPath = Path.Combine(_root, pending.RelativePath);

        string parent = Path.Combine(_root, _session.ToString("N"));

        string destination = Path.Combine(parent, pending.Id.ToString("N"));

        byte[] foreign = "independent replacement"u8.ToArray();

        List<bool> fileFlushes = [];

        List<string> directoryFlushes = [];

        bool reachedDatabase = false;

        SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = (path, _, _) =>
            new FlushObservingStream(path, fileFlushes, false);

        _store.CopyDirectoryFlushForTesting = path =>
        {
            directoryFlushes.Add(path);

            Assert.Contains(true, fileFlushes);

            Assert.True(File.Exists(oldPath));

            if (directoryFlushes.Count == refusedBarrier)
            {
                if (replaceCopy)
                {
                    File.Move(destination, destination + ".parked-original");

                    File.WriteAllBytes(destination, foreign);
                }

                return false;
            }

            return DurableDirectoryFlush.TryFlushParentOf(path);
        };

        _store.AfterBytesCommittedBeforeDbForTesting = _ =>
        {
            reachedDatabase = true;

            if (refusedBarrier == 0)
            {
                Assert.Equal(new[] { destination, parent }, directoryFlushes);
            }

            return Task.CompletedTask;
        };

        try
        {
            if (refusedBarrier == 0)
            {
                await _store.PromotePendingAsync(turn, _session, null);

                Assert.True(reachedDatabase);

                Assert.False(File.Exists(oldPath));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => _store.PromotePendingAsync(turn, _session, null));

                Assert.False(reachedDatabase);

                SessionAttachmentRecord saved = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

                Assert.Equal(SessionAttachmentState.Pending, saved.State);

                Assert.Equal(pending.RelativePath, saved.RelativePath);

                Assert.Equal(bytes, (await _store.ReadBytesAsync(saved)).ToArray());

                if (replaceCopy)
                {
                    Assert.Equal(foreign, await File.ReadAllBytesAsync(destination));

                    File.Delete(destination);
                }
                else
                {
                    Assert.False(File.Exists(destination));
                }

                _store.CopyDirectoryFlushForTesting = null;

                _store.AfterBytesCommittedBeforeDbForTesting = null;

                await _store.PromotePendingAsync(turn, _session, null);
            }

            SessionAttachmentRecord bound = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

            Assert.Equal(SessionAttachmentState.Bound, bound.State);

            Assert.Equal(bytes, (await _store.ReadBytesAsync(bound)).ToArray());
        }
        finally
        {
            SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = null;

            _store.CopyDirectoryFlushForTesting = null;

            _store.AfterBytesCommittedBeforeDbForTesting = null;
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_promotion_never_deletes_a_destination_replaced_after_plan_registration(bool replaceCopy)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string turn = "post-plan-turn";

        byte[] bytes = "pending original"u8.ToArray();

        byte[] foreign = "foreign post-plan replacement"u8.ToArray();

        SessionAttachmentRecord pending = await _store.PersistNewAsync(
            null, turn, null, "planned.txt", "planned.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        string destination = Path.Combine(_root, _session.ToString("N"), pending.Id.ToString("N"));

        IOException expected = new("Test failed after plan registration.");

        _store.AfterBytesCommittedBeforeDbForTesting = _ =>
        {
            if (replaceCopy)
            {
                File.Move(destination, destination + ".parked-original");

                File.WriteAllBytes(destination, foreign);
            }

            throw expected;
        };

        try
        {
            IOException failure = await Assert.ThrowsAsync<IOException>(() => _store.PromotePendingAsync(turn, _session, null));

            Assert.Same(expected, failure);

            SessionAttachmentRecord saved = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

            Assert.Equal(SessionAttachmentState.Pending, saved.State);

            Assert.Equal(bytes, (await _store.ReadBytesAsync(saved)).ToArray());

            if (replaceCopy)
            {
                Assert.True(File.Exists(destination), "Compensation deleted an independently replaced destination.");

                Assert.Equal(foreign, await File.ReadAllBytesAsync(destination));
            }
            else
            {
                Assert.False(File.Exists(destination));
            }
        }
        finally
        {
            _store.AfterBytesCommittedBeforeDbForTesting = null;
        }
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fork_copy_requires_the_same_device_and_directory_barriers_before_registering_authority(int refusedBarrier)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        byte[] bytes = "source survives failed fork"u8.ToArray();

        SessionAttachmentRecord source = await _store.PersistNewAsync(
            _session, null, null, "fork.txt", "fork.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        Guid fork = Guid.NewGuid();

        _db.Sessions.Add(new Session
        {
            Id = fork,
            Title = "durable fork",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync();

        SessionAttachmentForkCopyPlan plan = new(source, Guid.NewGuid(), null);

        string parent = Path.Combine(_root, fork.ToString("N"));

        string destination = Path.Combine(parent, plan.NewAttachmentId.ToString("N"));

        List<bool> fileFlushes = [];

        List<string> directoryFlushes = [];

        SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = (path, _, _) =>
            new FlushObservingStream(path, fileFlushes, false);

        _store.CopyDirectoryFlushForTesting = path =>
        {
            directoryFlushes.Add(path);

            Assert.Contains(true, fileFlushes);

            return directoryFlushes.Count != refusedBarrier && DurableDirectoryFlush.TryFlushParentOf(path);
        };

        using SessionAttachmentForkScope scope = _store.BeginForkCopyScope(fork);

        try
        {
            if (refusedBarrier == 0)
            {
                await _store.CopyBytesForForkAsync(fork, [plan]);

                Assert.Equal(new[] { destination, parent }, directoryFlushes);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => _store.CopyBytesForForkAsync(fork, [plan]));

                Assert.False(File.Exists(destination));

                await using (var refused = await _db.Database.BeginTransactionAsync())
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => _store.InsertForkRowsInAmbientTransactionAsync(fork, [plan]));

                    await refused.RollbackAsync();
                }

                _store.CopyDirectoryFlushForTesting = null;

                await _store.CopyBytesForForkAsync(fork, [plan]);
            }

            await using (var transaction = await _db.Database.BeginTransactionAsync())
            {
                await _store.InsertForkRowsInAmbientTransactionAsync(fork, [plan]);

                await transaction.CommitAsync();
            }

            scope.Complete();

            SessionAttachmentRecord copied = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(plan.NewAttachmentId));

            Assert.Equal(bytes, (await _store.ReadBytesAsync(copied)).ToArray());

            Assert.Equal(bytes, (await _store.ReadBytesAsync(source)).ToArray());
        }
        finally
        {
            SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = null;

            _store.CopyDirectoryFlushForTesting = null;
        }
    }

    [SkippableTheory]
    [InlineData(1, "throw")]
    [InlineData(2, "throw")]
    [InlineData(1, "cancel")]
    [InlineData(2, "cancel")]
    [InlineData(1, "replace")]
    [InlineData(2, "replace")]
    public async Task Publication_barrier_interruption_preserves_pending_bytes_and_original_failure(int barrier, string interruption)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string turn = "barrier-interruption";

        byte[] bytes = "interrupted original"u8.ToArray();

        byte[] foreign = "foreign barrier replacement"u8.ToArray();

        SessionAttachmentRecord pending = await _store.PersistNewAsync(
            null, turn, null, "interrupt.txt", "interrupt.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        string destination = Path.Combine(_root, _session.ToString("N"), pending.Id.ToString("N"));

        using CancellationTokenSource cancellation = new();

        IOException expected = new("Test directory barrier failed.");

        int calls = 0;

        bool reachedDatabase = false;

        _store.CopyDirectoryFlushForTesting = path =>
        {
            if (++calls == barrier)
            {
                switch (interruption)
                {
                    case "throw":
                        throw expected;
                    case "cancel":
                        cancellation.Cancel();

                        break;
                    case "replace":
                        File.Move(destination, destination + ".parked-original");

                        File.WriteAllBytes(destination, foreign);

                        break;
                }
            }

            return DurableDirectoryFlush.TryFlushParentOf(path);
        };

        _store.AfterBytesCommittedBeforeDbForTesting = _ =>
        {
            reachedDatabase = true;

            return Task.CompletedTask;
        };

        try
        {
            if (interruption == "cancel")
            {
                OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => _store.PromotePendingAsync(turn, _session, null, cancellation.Token));

                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                IOException failure = await Assert.ThrowsAsync<IOException>(
                    () => _store.PromotePendingAsync(turn, _session, null, cancellation.Token));

                if (interruption == "throw")
                {
                    Assert.Same(expected, failure);
                }
            }

            Assert.False(reachedDatabase);

            SessionAttachmentRecord saved = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

            Assert.Equal(SessionAttachmentState.Pending, saved.State);

            Assert.Equal(bytes, (await _store.ReadBytesAsync(saved)).ToArray());

            if (interruption == "replace")
            {
                Assert.Equal(foreign, await File.ReadAllBytesAsync(destination));

                File.Delete(destination);
            }
            else
            {
                Assert.False(File.Exists(destination));
            }

            _store.CopyDirectoryFlushForTesting = null;

            _store.AfterBytesCommittedBeforeDbForTesting = null;

            await _store.PromotePendingAsync(turn, _session, null);

            SessionAttachmentRecord bound = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

            Assert.Equal(SessionAttachmentState.Bound, bound.State);

            Assert.Equal(bytes, (await _store.ReadBytesAsync(bound)).ToArray());
        }
        finally
        {
            _store.CopyDirectoryFlushForTesting = null;

            _store.AfterBytesCommittedBeforeDbForTesting = null;
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_device_flush_cleans_only_the_original_temporary_file(bool replaceTemp)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string turn = "temp-ownership";

        byte[] bytes = "pending survives temp failure"u8.ToArray();

        byte[] foreign = "foreign temp replacement"u8.ToArray();

        SessionAttachmentRecord pending = await _store.PersistNewAsync(
            null, turn, null, "temp.txt", "temp.txt", bytes, "text/plain", SessionAttachmentKind.Text);

        string? temporaryPath = null;

        IOException expected = new("Test device flush refused after temporary file replacement.");

        SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = (path, _, _) =>
        {
            temporaryPath = path;

            return new FlushObservingStream(path, [], false, () =>
            {
                if (replaceTemp)
                {
                    File.Move(path, path + ".parked-original");

                    File.WriteAllBytes(path, foreign);
                }

                throw expected;
            });
        };

        try
        {
            IOException failure = await Assert.ThrowsAsync<IOException>(() => _store.PromotePendingAsync(turn, _session, null));

            Assert.Same(expected, failure);

            Assert.NotNull(temporaryPath);

            if (replaceTemp)
            {
                Assert.True(File.Exists(temporaryPath), "Compensation deleted an independently replaced temporary path.");

                Assert.Equal(foreign, await File.ReadAllBytesAsync(temporaryPath));
            }
            else
            {
                Assert.False(File.Exists(temporaryPath));
            }

            SessionAttachmentRecord saved = Assert.IsType<SessionAttachmentRecord>(await _store.GetByIdAsync(pending.Id));

            Assert.Equal(SessionAttachmentState.Pending, saved.State);

            Assert.Equal(bytes, (await _store.ReadBytesAsync(saved)).ToArray());
        }
        finally
        {
            SecureFilePermissions.WindowsOwnerOnlyTempFileCreateForTests = null;
        }
    }

    private sealed class FlushObservingStream(string path, List<bool> requests, bool failFlush, Action? beforeDeviceFlush = null)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, 4096, FileOptions.Asynchronous)
    {
        public override void Flush(bool flushToDisk)
        {
            requests.Add(flushToDisk);

            if (flushToDisk)
            {
                beforeDeviceFlush?.Invoke();
            }

            if (flushToDisk && failFlush)
            {
                throw new IOException("Test device flush failed.");
            }

            base.Flush(flushToDisk);
        }
    }
}
