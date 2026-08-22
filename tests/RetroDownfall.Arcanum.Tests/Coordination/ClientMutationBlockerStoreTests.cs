using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.Coordination;

[Collection("WorkspacePathPolicy")]
public sealed class ClientMutationBlockerStoreTests : IDisposable
{

    private readonly string _container;

    private readonly string _guardedRoot;

    public ClientMutationBlockerStoreTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-client-blocker-" + Guid.NewGuid().ToString("N"));

        _guardedRoot = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_guardedRoot);

    }

    public void Dispose()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Fact]
    public async Task An_unowned_or_directory_blocker_is_unsafe_not_absent()
    {

        ClientMutationBlockerStore store = new(_guardedRoot);

        ClientMutationBlockerRecord record = Record();

        using (ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock())
        {

            _ = (await store.PublishAsync(held, record)).Value;

        }

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, isDirectory) => isDirectory;

        Assert.True((await store.InspectAsync()).IsFailure);

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        File.Delete(store.BlockerPath);

        Directory.CreateDirectory(store.BlockerPath);

        Assert.True((await store.InspectAsync()).IsFailure);

    }

    [SkippableFact]
    public async Task A_hard_link_blocker_is_unsafe_and_its_target_is_unchanged()
    {

        Skip.If(
            !OperatingSystem.IsMacOS()
                && !OperatingSystem.IsLinux()
                && !OperatingSystem.IsWindows(),
            "Unsupported operating system.");

        ClientMutationBlockerStore store = new(_guardedRoot);

        using (ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock())
        {

            _ = (await store.PublishAsync(held, Record())).Value;

        }

        byte[] payload = File.ReadAllBytes(store.BlockerPath);

        File.Delete(store.BlockerPath);

        string sentinel = Path.Combine(_container, "blocker-sentinel.json");

        File.WriteAllBytes(sentinel, payload);

        Assert.True(HardLinkTestSupport.TryCreate(store.BlockerPath, sentinel));

        Assert.True((await store.InspectAsync()).IsFailure);

        Assert.Equal(payload, File.ReadAllBytes(sentinel));

    }

    [Fact]
    public async Task Publishing_under_the_exact_client_lock_is_durable_and_idempotent()
    {

        ClientMutationBlockerStore store = new(_guardedRoot);

        ClientMutationBlockerRecord record = Record();

        using ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

        ClientMutationBlockerPublication first = (await store
            .PublishAsync(held, record))
            .Value;

        ClientMutationBlockerPublication replay = (await store
            .PublishAsync(held, record))
            .Value;

        Assert.Equal(first, replay);

        held.Dispose();

        ClientMutationBlockerPublication? inspected =
            (await store.InspectAsync()).Value;

        Assert.NotNull(inspected);

        Assert.Equal(record, inspected.Record);

        Assert.True(File.Exists(store.BlockerPath));

        Assert.Equal(
            Path.Combine(
                _container,
                ".arcanum-client-mutation-arcanum.blocked.json"),
            store.BlockerPath);

    }

    [Fact]
    public async Task A_different_owner_cannot_replace_or_remove_the_durable_blocker()
    {

        ClientMutationBlockerStore store = new(_guardedRoot);

        ClientMutationBlockerRecord firstRecord = Record();

        using ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

        ClientMutationBlockerPublication first = (await store
            .PublishAsync(held, firstRecord))
            .Value;

        byte[] original = File.ReadAllBytes(store.BlockerPath);

        Assert.True((await store.PublishAsync(held, Record())).IsFailure);

        ClientMutationBlockerPublication forged = first with
        {
            Record = first.Record with { BlockerId = Guid.NewGuid() },
        };

        Assert.True((await store.RemoveAsync(held, forged)).IsFailure);

        Assert.Equal(original, File.ReadAllBytes(store.BlockerPath));

        Assert.True((await store.RemoveAsync(held, first)).IsSuccess);

        Assert.Null((await store.InspectAsync()).Value);

    }

    [Fact]
    public async Task Cancellation_after_the_temp_is_flushed_but_before_publication_leaves_no_evidence()
    {

        using CancellationTokenSource cancelled = new();

        ClientMutationBlockerStore store = new(
            _guardedRoot,
            new ClientMutationBlockerStoreOptions
            {
                BeforePublishForTests = cancelled.Cancel,
            });

        using ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.PublishAsync(held, Record(), cancelled.Token));

        Assert.Null((await store.InspectAsync()).Value);

        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(_container),
            static path => Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));

    }

    [Fact]
    public async Task A_failure_after_the_atomic_move_retains_one_complete_crash_blocker()
    {

        ClientMutationBlockerStore store = new(
            _guardedRoot,
            new ClientMutationBlockerStoreOptions
            {
                AfterPublishMoveBeforeVerifyForTests = static () =>
                    throw new IOException("synthetic crash window"),
            });

        ClientMutationBlockerRecord record = Record();

        using ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

        Assert.True((await store.PublishAsync(held, record)).IsFailure);

        ClientMutationBlockerPublication? retained =
            (await store.InspectAsync()).Value;

        Assert.NotNull(retained);

        Assert.Equal(record, retained.Record);

        Assert.DoesNotContain(
            Directory.GetFileSystemEntries(_container),
            static path => Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));

    }

    [Fact]
    public async Task A_preexisting_symlink_blocker_is_never_followed_or_overwritten()
    {

        ClientMutationBlockerStore store = new(_guardedRoot);

        using (ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock())
        {

            _ = (await store.PublishAsync(held, Record())).Value;

        }

        byte[] validPayload = File.ReadAllBytes(store.BlockerPath);

        File.Delete(store.BlockerPath);

        string sentinel = Path.Combine(_container, "symlink-sentinel.json");

        File.WriteAllBytes(sentinel, validPayload);

        File.CreateSymbolicLink(store.BlockerPath, sentinel);

        using ArcanumClientMutationLock reacquired = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

        Assert.True((await store.PublishAsync(reacquired, Record())).IsFailure);

        Assert.Equal(validPayload, File.ReadAllBytes(sentinel));

    }

    [Fact]
    public async Task A_valid_blocker_appearing_after_initial_inspection_is_not_replaced()
    {

        ClientMutationBlockerRecord competing = Record();

        byte[] competingBytes = JsonSerializer.SerializeToUtf8Bytes(
            competing,
            ClientMutationBlockerJsonContext.Default.ClientMutationBlockerRecord);

        ClientMutationBlockerStore? store = null;

        store = new ClientMutationBlockerStore(
            _guardedRoot,
            new ClientMutationBlockerStoreOptions
            {
                BeforeAtomicPublishForTests = () =>
                {

                    File.WriteAllBytes(store!.BlockerPath, competingBytes);

                    Assert.True(SecureFilePermissions.TryApplyOwnerOnlyFileStrict(
                        store.BlockerPath,
                        logFailure: false));

                },
            });

        using ArcanumClientMutationLock held = ArcanumClientMutationLock
            .AcquireDetailed(_guardedRoot)
            .BorrowAcquiredLock();

        Assert.True((await store.PublishAsync(held, Record())).IsFailure);

        Assert.Equal(competingBytes, File.ReadAllBytes(store.BlockerPath));

        Assert.Equal(competing, (await store.InspectAsync()).Value!.Record);

    }

    private static ClientMutationBlockerRecord Record() =>
        new(
            ClientMutationBlockerStore.CurrentVersion,
            Guid.NewGuid(),
            ClientMutationBlockerKind.InstallationReset,
            InstallationResetScope.All,
            "accepted-plan",
            OperationId: null);

}
