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

/// <summary>
/// A metadata-only credential lookup that never requests or returns secret bytes.
/// </summary>
/// <remarks>
/// <see cref="OsCredentialStoreStatus.Ok"/> means the item exists,
/// <see cref="OsCredentialStoreStatus.NotFound"/> proves it absent, and either failure status is
/// indeterminate. Callers must fail closed without reading secret data when the answer is
/// indeterminate; they must never turn it into absence or a reason to prompt.
/// </remarks>
public interface IOsCredentialPresenceProbe
{
    OsCredentialStoreStatus ProbePresence(string service, string account);
}
