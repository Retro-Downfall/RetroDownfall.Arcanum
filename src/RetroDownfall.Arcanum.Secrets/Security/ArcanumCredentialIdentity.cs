namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// Fixed OS credential identity shared by Arcanum, The Forge, and any other local client that must
/// present the master <c>X-Arcanum-Key</c>. This is the shared "location" — not a file path.
/// </summary>
public static class ArcanumCredentialIdentity
{

    /// <summary>OS credential service / target name (Keychain service, Cred target namespace, libsecret schema).</summary>
    public const string Service = "arcanum";

    /// <summary>OS credential account / username distinguishing the master API key from future secrets.</summary>
    public const string MasterApiKeyAccount = "master-api-key";

    /// <summary>OS credential account used by the native Perplexity web-research provider.</summary>
    public const string PerplexityApiKeyAccount = "provider-perplexity-api-key";

}
