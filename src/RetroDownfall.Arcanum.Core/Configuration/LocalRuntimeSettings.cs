namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>An explicitly declared physical inference runtime shared by provider aliases.</summary>
public sealed record LocalRuntimeSettings
{
    public string Id { get; set; } = string.Empty;

    public string Adapter { get; set; } = string.Empty;

    public string? ResourceGroup { get; set; }

    public string ManagementEndpoint { get; set; } = string.Empty;

    public string? CredentialEnvironmentVariable { get; set; }
}

public static class LocalRuntimeAdapterCatalog
{
    public const string Ollama = "ollama";

    public static bool IsKnown(string? adapter) => adapter == Ollama;

    public static bool TryGetManagementUri(LocalRuntimeSettings runtime, out Uri? endpoint)
    {
        if (!Uri.TryCreate(runtime.ManagementEndpoint, UriKind.Absolute, out endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            endpoint = null;

            return false;
        }

        return true;
    }
}
