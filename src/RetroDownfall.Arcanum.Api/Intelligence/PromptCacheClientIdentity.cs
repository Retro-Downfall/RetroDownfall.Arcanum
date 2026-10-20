using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Facts frozen with the actual SDK client. This identity grants no read or egress authority.</summary>
public sealed class PromptCacheClientIdentity
{
    private readonly byte[] _provider;

    internal PromptCacheClientIdentity(
        IChatClient client,
        ProviderSettings provider,
        string resolvedModel,
        PromptCacheConfigurationStamp configurationStamp,
        ProviderCredentialStamp? credentialStamp,
        PromptCacheDispatchPolicy? dispatchPolicy = null)
    {
        DispatchPolicy = dispatchPolicy;

        Client = client;

        _provider = JsonSerializer.SerializeToUtf8Bytes(provider, ConfigurationJsonContext.Default.ProviderSettings);

        ResolvedModel = resolvedModel;

        ConfigurationStamp = configurationStamp;

        CredentialStamp = credentialStamp;
    }

    internal PromptCacheDispatchPolicy? DispatchPolicy { get; }

    public IChatClient Client { get; }

    public ProviderSettings Provider => JsonSerializer.Deserialize(_provider, ConfigurationJsonContext.Default.ProviderSettings)!;

    public string ResolvedModel { get; }

    public PromptCacheConfigurationStamp ConfigurationStamp { get; }

    public ProviderCredentialStamp? CredentialStamp { get; }

    internal static ProviderSettings Freeze(ProviderSettings provider) => JsonSerializer.Deserialize(
        JsonSerializer.SerializeToUtf8Bytes(provider, ConfigurationJsonContext.Default.ProviderSettings),
        ConfigurationJsonContext.Default.ProviderSettings)!;
}
