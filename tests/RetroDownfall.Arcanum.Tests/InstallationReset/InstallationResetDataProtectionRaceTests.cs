using System.Security.Cryptography;

using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection;

using Microsoft.AspNetCore.DataProtection.KeyManagement;

using Microsoft.AspNetCore.DataProtection.Repositories;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("ProcessEnvironment")]
public sealed class InstallationResetDataProtectionRaceTests
{
    [Fact]
    public async Task Key_ring_refresh_finishes_on_the_protecting_caller_before_offline_cleanup()
    {
        using ArcanumTestHomeScope home = new("arcanum-reset-key-ring-race");

        ServiceCollection services = new();

        services.AddDataProtection().SetApplicationName("Arcanum.ResetRace").PersistKeysToOwnerOnlyKeyRing();

        using QueuedReadGate gate = new();

        services.PostConfigure<KeyManagementOptions>(options =>
            options.XmlRepository = new GatedRepository(options.XmlRepository
                ?? throw new InvalidOperationException("The real key repository was not configured."), gate));

        await using ServiceProvider provider = services.BuildServiceProvider();

        IDataProtector protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("reset-race");

        _ = protector.Protect("warm the real persisted key ring");

        IKeyManager manager = provider.GetRequiredService<IKeyManager>();

        // Public key management invalidates this provider's cached ring. Refresh must finish on
        // the caller: reset's caller-drain boundary cannot account for detached framework tasks.
        manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        gate.Arm();

        int callerThreadId = 0;

        Task<string> protecting = Task.Factory.StartNew(() =>
        {
            Volatile.Write(ref callerThreadId, System.Environment.CurrentManagedThreadId);

            return protector.Protect("refresh before the caller completes");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            int repositoryThreadId = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(Volatile.Read(ref callerThreadId), repositoryThreadId);
        }
        finally
        {
            gate.Release();

            // Join the complete public Protect call, including key generation and recursive reads,
            // before cleanup. Even a failed assertion must release and join the dedicated caller.
            _ = await protecting.WaitAsync(TimeSpan.FromSeconds(15));

            await gate.Completed.WaitAsync(TimeSpan.FromSeconds(15));
        }

        string root = ArcanumPaths.GrimoireDirectory;

        InstallationResetAcceptedBinding binding = new("binding", [root], [], [], [], ["data"]);

        InstallationResetPlan plan = new("plan", InstallationResetScope.Global, Workspace: null,
            DateTimeOffset.UtcNow, DataInventoryAvailable: true, CredentialInventoryAvailable: true,
            Targets: [], Credentials: [], PreservedBackups: [], Exclusions: [], Blockers: [],
            Rows: 0, Files: 0, EstimatedBytes: 0, binding);

        Result<InstallationResetOfflineCleanupResult> cleaned = await new InstallationResetOfflineCleanup()
            .ExecuteAsync(plan, CancellationToken.None);

        Assert.True(cleaned.IsSuccess, cleaned.IsFailure ? cleaned.Error.Message : string.Empty);

        Assert.True(cleaned.Value.Verification.Succeeded);

        Assert.False(Directory.Exists(root), "No key-ring work may survive the drained caller and recreate the reset root.");
    }

    [Fact]
    public async Task Owner_only_key_ring_preserves_creation_rotation_old_ciphertexts_and_revocation()
    {
        using ArcanumTestHomeScope home = new("arcanum-reset-key-ring-lifecycle");

        ServiceCollection services = new();

        services.AddDataProtection().SetApplicationName("Arcanum.ResetLifecycle").PersistKeysToOwnerOnlyKeyRing();

        await using ServiceProvider initial = services.BuildServiceProvider();

        IDataProtector firstProtector = initial.GetRequiredService<IDataProtectionProvider>().CreateProtector("lifecycle");

        string firstCiphertext = firstProtector.Protect("before rotation");

        IKeyManager manager = initial.GetRequiredService<IKeyManager>();

        IKey firstKey = Assert.Single(manager.GetAllKeys());

        Assert.True(SecureFilePermissions.HasOwnerOnlyPosture(DataProtectionKeyPaths.Directory, isDirectory: true));

        IKey replacement = manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        Assert.NotEqual(firstKey.KeyId, replacement.KeyId);

        Assert.Equal(2, manager.GetAllKeys().Count);

        // Fresh providers assert the persisted lifecycle rather than relying on a stale cached ring
        // to observe a rotation or revocation immediately.
        await using ServiceProvider rotated = services.BuildServiceProvider();

        IDataProtector rotatedProtector = rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector("lifecycle");

        string rotatedCiphertext = rotatedProtector.Protect("after rotation");

        Assert.Equal("before rotation", rotatedProtector.Unprotect(firstCiphertext));

        Assert.Equal("after rotation", rotatedProtector.Unprotect(rotatedCiphertext));

        manager.RevokeKey(firstKey.KeyId, "regression test revocation");

        await using ServiceProvider revoked = services.BuildServiceProvider();

        IDataProtector revokedProtector = revoked.GetRequiredService<IDataProtectionProvider>().CreateProtector("lifecycle");

        Assert.Throws<CryptographicException>(() => revokedProtector.Unprotect(firstCiphertext));

        Assert.Equal("after rotation", revokedProtector.Unprotect(rotatedCiphertext));

        Assert.True(SecureFilePermissions.HasOwnerOnlyPosture(DataProtectionKeyPaths.Directory, isDirectory: true));
    }

    private sealed class GatedRepository(IXmlRepository inner, QueuedReadGate gate) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            bool held = gate.TryEnter();

            try
            {
                return inner.GetAllElements();
            }
            finally
            {
                if (held)
                {
                    gate.Complete();
                }
            }
        }

        public void StoreElement(XElement element, string friendlyName) => inner.StoreElement(element, friendlyName);
    }

    private sealed class QueuedReadGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();

        private readonly TaskCompletionSource<int> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _armed;

        internal Task<int> Entered => _entered.Task;

        internal Task Completed => _completed.Task;

        internal void Arm() => Volatile.Write(ref _armed, 1);

        internal bool TryEnter()
        {
            if (Interlocked.Exchange(ref _armed, 0) != 1)
            {
                return false;
            }

            _entered.TrySetResult(System.Environment.CurrentManagedThreadId);

            if (!_release.Wait(TimeSpan.FromSeconds(30)))
            {
                _completed.TrySetResult();

                throw new TimeoutException("The queued key-ring read was not released.");
            }

            return true;
        }

        internal void Complete() => _completed.TrySetResult();

        internal void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();

            _release.Dispose();
        }
    }
}
