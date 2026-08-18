namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// Factory that selects the platform-native OS credential store (or reports unavailability).
/// </summary>
/// <remarks>
/// Also the single place that decides what <see cref="IsAvailable"/> means. It answers "a credential
/// backend answers this process", never the weaker "this platform is supported" — three credential
/// stores choose between failing closed and degrading to the encrypted mirror on that one property,
/// and answering the weaker question makes a save throw on precisely the headless hosts §11.2 item 4
/// promises the mirror still serves.
/// </remarks>
public sealed class OsCredentialStore : IOsCredentialStore
{

    private const int Unknown = 0;

    private const int Reachable = 1;

    private const int Unreachable = 2;

    private readonly IOsCredentialStore _inner;

    private int _observed = Unknown;

    public OsCredentialStore()
    {

        _inner = CreatePlatformStore();

    }

    /// <summary>Test seam: wrap an arbitrary store (e.g. <see cref="InMemoryOsCredentialStore"/>).</summary>
    public OsCredentialStore(IOsCredentialStore inner)
    {

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    }

    /// <summary>True when a credential backend answers this process.</summary>
    /// <remarks>
    /// The most recent unambiguous evidence wins, and an operation is stronger evidence than any
    /// probe: a call that returned a value, reported the account absent, or reported that nothing
    /// answered has settled the question outright. A failure has not. A locked keychain, an ACL
    /// denial after a resign, and a Secret Service that is not on the bus all surface as
    /// <see cref="OsCredentialStoreStatus.Failed"/>, and only the last of those may degrade a save to
    /// the mirror — so a failure clears the verdict rather than recording one, and the next question
    /// goes back to the backend's own reachability probe. Nothing is cached behind a timer, so there
    /// is no window in which a remembered answer contradicts the backend.
    /// </remarks>
    public bool IsAvailable
    {

        get
        {

            int observed = Volatile.Read(ref _observed);

            return observed == Unknown
                ? _inner.IsAvailable
                : observed == Reachable;

        }

    }

    public OsCredentialStoreResult TryGet(string service, string account) =>
        Observe(_inner.TryGet(service, account));

    public OsCredentialStoreResult Set(string service, string account, string secret) =>
        Observe(_inner.Set(service, account, secret));

    public OsCredentialStoreResult Delete(string service, string account) =>
        Observe(_inner.Delete(service, account));

    /// <summary>Records what one completed operation proved about the backend's reachability.</summary>
    private OsCredentialStoreResult Observe(OsCredentialStoreResult result)
    {

        Volatile.Write(ref _observed, Evidence(result.Status));

        return result;

    }

    private static int Evidence(OsCredentialStoreStatus status)
    {

        if (status == OsCredentialStoreStatus.Unavailable)
        {

            return Unreachable;

        }

        if (status == OsCredentialStoreStatus.Failed)
        {

            return Unknown;

        }

        return Reachable;

    }

    private static IOsCredentialStore CreatePlatformStore()
    {

        if (OperatingSystem.IsWindows())
        {

            // Credential Manager is part of the OS and has no separate service to reach; a locked or
            // policy-restricted store surfaces per call as a status the callers already handle.
            return new PlatformOsCredentialStore(
                reachable: static () => true,
                get: WindowsOsCredentialStore.TryGet,
                set: WindowsOsCredentialStore.Set,
                delete: WindowsOsCredentialStore.Delete);

        }

        if (OperatingSystem.IsMacOS())
        {

            // Security.framework ships with the OS for the same reason, and a locked keychain is
            // likewise a per-call status rather than an absent backend.
            return new PlatformOsCredentialStore(
                reachable: static () => true,
                get: MacOsCredentialStore.TryGet,
                set: MacOsCredentialStore.Set,
                delete: MacOsCredentialStore.Delete);

        }

        if (OperatingSystem.IsLinux())
        {

            if (!LinuxOsCredentialStore.ProbeAvailable())
            {

                return new UnavailableOsCredentialStore(
                    "Linux Secret Service is unavailable (install libsecret-1 and ensure a keyring daemon is running).");

            }

            // The only platform whose backend is a separate service that may simply not be there, so
            // the only one whose reachability has to be asked rather than assumed.
            return new PlatformOsCredentialStore(
                reachable: LinuxOsCredentialStore.ProbeReachable,
                get: LinuxOsCredentialStore.TryGet,
                set: LinuxOsCredentialStore.Set,
                delete: LinuxOsCredentialStore.Delete);

        }

        return new UnavailableOsCredentialStore("OS credential store is not supported on this platform.");

    }

    private sealed class PlatformOsCredentialStore(
        Func<bool> reachable,
        Func<string, string, OsCredentialStoreResult> get,
        Func<string, string, string, OsCredentialStoreResult> set,
        Func<string, string, OsCredentialStoreResult> delete) : IOsCredentialStore
    {

        public bool IsAvailable => reachable();

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
