namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// Factory that selects the platform-native OS credential store (or reports unavailability).
/// </summary>
public sealed class OsCredentialStore : IOsCredentialStore
{

    private readonly IOsCredentialStore _inner;

    public OsCredentialStore()
    {

        _inner = CreatePlatformStore();

    }

    /// <summary>Test seam: wrap an arbitrary store (e.g. <see cref="InMemoryOsCredentialStore"/>).</summary>
    public OsCredentialStore(IOsCredentialStore inner)
    {

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    }

    public bool IsAvailable => _inner.IsAvailable;

    public OsCredentialStoreResult TryGet(string service, string account) => _inner.TryGet(service, account);

    public OsCredentialStoreResult Set(string service, string account, string secret) =>
        _inner.Set(service, account, secret);

    public OsCredentialStoreResult Delete(string service, string account) => _inner.Delete(service, account);

    private static IOsCredentialStore CreatePlatformStore()
    {

        if (OperatingSystem.IsWindows())
        {

            return new PlatformOsCredentialStore(
                available: true,
                get: WindowsOsCredentialStore.TryGet,
                set: WindowsOsCredentialStore.Set,
                delete: WindowsOsCredentialStore.Delete);

        }

        if (OperatingSystem.IsMacOS())
        {

            return new PlatformOsCredentialStore(
                available: true,
                get: MacOsCredentialStore.TryGet,
                set: MacOsCredentialStore.Set,
                delete: MacOsCredentialStore.Delete);

        }

        if (OperatingSystem.IsLinux())
        {

            bool available = LinuxOsCredentialStore.ProbeAvailable();

            if (!available)
            {

                return new UnavailableOsCredentialStore(
                    "Linux Secret Service is unavailable (install libsecret-1 and ensure a keyring daemon is running).");

            }

            return new PlatformOsCredentialStore(
                available: true,
                get: LinuxOsCredentialStore.TryGet,
                set: LinuxOsCredentialStore.Set,
                delete: LinuxOsCredentialStore.Delete);

        }

        return new UnavailableOsCredentialStore("OS credential store is not supported on this platform.");

    }

    private sealed class PlatformOsCredentialStore(
        bool available,
        Func<string, string, OsCredentialStoreResult> get,
        Func<string, string, string, OsCredentialStoreResult> set,
        Func<string, string, OsCredentialStoreResult> delete) : IOsCredentialStore
    {

        public bool IsAvailable => available;

        public OsCredentialStoreResult TryGet(string service, string account)
        {

            ArgumentException.ThrowIfNullOrWhiteSpace(service);

            ArgumentException.ThrowIfNullOrWhiteSpace(account);

            return get(service, account);

        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {

            ArgumentException.ThrowIfNullOrWhiteSpace(service);

            ArgumentException.ThrowIfNullOrWhiteSpace(account);

            ArgumentNullException.ThrowIfNull(secret);

            return set(service, account, secret);

        }

        public OsCredentialStoreResult Delete(string service, string account)
        {

            ArgumentException.ThrowIfNullOrWhiteSpace(service);

            ArgumentException.ThrowIfNullOrWhiteSpace(account);

            return delete(service, account);

        }

    }

    private sealed class UnavailableOsCredentialStore(string message) : IOsCredentialStore
    {

        public bool IsAvailable => false;

        public OsCredentialStoreResult TryGet(string service, string account) =>
            OsCredentialStoreResult.Unavailable(message);

        public OsCredentialStoreResult Set(string service, string account, string secret) =>
            OsCredentialStoreResult.Unavailable(message);

        public OsCredentialStoreResult Delete(string service, string account) =>
            OsCredentialStoreResult.Unavailable(message);

    }

}
