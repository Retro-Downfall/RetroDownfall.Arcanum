using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Counts, per account, every secret read, every write and every metadata-only presence probe a host
/// makes into one in-memory store, and can fail one account on demand.
/// </summary>
/// <remarks>
/// <para>A secret read and a presence probe are counted apart, because whether a surface asked for the
/// key's bytes or only whether the item exists is exactly what an erasure status must keep apart. While
/// an account is failed, every member asked about it answers that status and the inner store is left
/// alone, so a refusal under failure can never have written anything.</para>
///
/// <para>Wrap one instance around the one <see cref="InMemoryOsCredentialStore"/> a test hands every host
/// it starts, so the key and the counts both survive a restart.</para>
/// </remarks>
internal sealed class SecretAccessRecordingCredentialStore(InMemoryOsCredentialStore inner)
    : IOsCredentialStore, IOsCredentialPresenceProbe
{
    private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int> _writes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, int> _probes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, OsCredentialStoreStatus> _failures = new(StringComparer.Ordinal);

    public bool IsAvailable => inner.IsAvailable;

    /// <summary>How many times the account's secret was read.</summary>
    internal int TryGetCount(string account) => _reads.GetValueOrDefault(account);

    /// <summary>How many times the account was written, by a host or by the test.</summary>
    internal int SetCount(string account) => _writes.GetValueOrDefault(account);

    /// <summary>How many times the account was asked only whether it exists.</summary>
    internal int ProbeCount(string account) => _probes.GetValueOrDefault(account);

    /// <summary>Makes every member answer <paramref name="status"/> for the account until cleared.</summary>
    internal void FailAccount(string account, OsCredentialStoreStatus status) => _failures[account] = status;

    internal void ClearFailure(string account) => _ = _failures.TryRemove(account, out _);

    public OsCredentialStoreResult TryGet(string service, string account)
    {
        Count(_reads, account);

        return Failure(account) ?? inner.TryGet(service, account);
    }

    public OsCredentialStoreResult Set(string service, string account, string secret)
    {
        Count(_writes, account);

        return Failure(account) ?? inner.Set(service, account, secret);
    }

    public OsCredentialStoreResult Delete(string service, string account) =>
        Failure(account) ?? inner.Delete(service, account);

    public OsCredentialStoreStatus ProbePresence(string service, string account)
    {
        Count(_probes, account);

        return _failures.TryGetValue(account, out OsCredentialStoreStatus status)
            ? status
            : inner.ProbePresence(service, account);
    }

    private static void Count(ConcurrentDictionary<string, int> counts, string account) =>
        _ = counts.AddOrUpdate(account, 1, static (_, count) => count + 1);

    private OsCredentialStoreResult? Failure(string account) =>
        _failures.TryGetValue(account, out OsCredentialStoreStatus status)
            ? new OsCredentialStoreResult(status, null, "test credential store failure")
            : null;
}

/// <summary>Puts a <see cref="SecretAccessRecordingCredentialStore"/> in front of a host.</summary>
internal static class SecretAccessRecordingCredentialStoreExtensions
{
    /// <summary>
    /// Keeps every service override the factory already carries, then replaces the credential store
    /// with <paramref name="credentials"/>. Call it before the first client or service access.
    /// </summary>
    internal static ArcanumWebApplicationFactory WithRecordingCredentials(
        this ArcanumWebApplicationFactory factory,
        SecretAccessRecordingCredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(factory);

        ArgumentNullException.ThrowIfNull(credentials);

        Action<IServiceCollection>? existing = factory.ServiceOverrides;

        factory.ServiceOverrides = services =>
        {
            existing?.Invoke(services);

            services.RemoveAll<IOsCredentialStore>();

            services.AddSingleton<IOsCredentialStore>(credentials);
        };

        return factory;
    }
}
