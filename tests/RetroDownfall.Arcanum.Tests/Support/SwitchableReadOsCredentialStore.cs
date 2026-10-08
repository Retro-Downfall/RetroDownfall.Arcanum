using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The OS store an installation keeps across restarts, whose reads can be made to fail the way a
/// locked keychain's do while writes keep working.
/// </summary>
internal sealed class SwitchableReadOsCredentialStore : IOsCredentialStore
{
    private readonly InMemoryOsCredentialStore _inner = new();

    public bool FailReads { get; set; }

    public bool IsAvailable => true;

    public OsCredentialStoreResult TryGet(string service, string account) =>
        FailReads
            ? OsCredentialStoreResult.Failed("test: the keychain is locked")
            : _inner.TryGet(service, account);

    public OsCredentialStoreResult Set(string service, string account, string secret) =>
        _inner.Set(service, account, secret);

    public OsCredentialStoreResult Delete(string service, string account) =>
        _inner.Delete(service, account);
}
