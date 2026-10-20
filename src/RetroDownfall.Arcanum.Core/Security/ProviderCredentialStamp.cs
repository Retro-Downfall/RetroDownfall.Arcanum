using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Security;

public sealed record ProviderCredentialStamp(Guid ResolverBootId, Guid CredentialIdentity, long Generation);

/// <summary>Ephemeral secret-bearing resolution. Never serialize or log this value.</summary>
public sealed class ResolvedProviderCredential(string? secret, ProviderCredentialStamp? stamp)
{
    [JsonIgnore]
    public string? Secret { get; } = secret;

    public ProviderCredentialStamp? Stamp { get; } = stamp;

    public override string ToString() => "ResolvedProviderCredential { Secret = [REDACTED] }";
}
