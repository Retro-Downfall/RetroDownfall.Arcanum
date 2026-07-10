namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// Cross-platform OS credential store (macOS Keychain, Windows Credential Manager, Linux Secret Service).
/// </summary>
public interface IOsCredentialStore
{

    /// <summary>True when this process can talk to a usable OS credential backend.</summary>
    bool IsAvailable { get; }

    OsCredentialStoreResult TryGet(string service, string account);

    OsCredentialStoreResult Set(string service, string account, string secret);

    OsCredentialStoreResult Delete(string service, string account);

}
