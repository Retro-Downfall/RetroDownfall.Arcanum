using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>In-process store for unit tests and environments without an OS keychain.</summary>
public sealed class InMemoryOsCredentialStore : IOsCredentialStore
{

    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool IsAvailable => true;

    public OsCredentialStoreResult TryGet(string service, string account)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        return _secrets.TryGetValue(Key(service, account), out string? value)
            ? OsCredentialStoreResult.Ok(value)
            : OsCredentialStoreResult.NotFound();

    }

    public OsCredentialStoreResult Set(string service, string account, string secret)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        ArgumentNullException.ThrowIfNull(secret);

        _secrets[Key(service, account)] = secret;

        return OsCredentialStoreResult.Ok(secret);

    }

    public OsCredentialStoreResult Delete(string service, string account)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        _ = _secrets.TryRemove(Key(service, account), out _);

        return OsCredentialStoreResult.Ok(string.Empty);

    }

    private static string Key(string service, string account) => service + "\0" + account;

}
