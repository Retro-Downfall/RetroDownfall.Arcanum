using System.Net;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal readonly record struct ProviderResourceKey(string Scheme, string Host, int Port)
{
    internal static ProviderResourceKey? FromProvider(ProviderSettings provider, ArcanumSettings? settings = null)
    {
        string? binding = provider.LocalRuntimeId;

        if (string.IsNullOrEmpty(binding))
        {
            return null;
        }

        if (provider.Type != AiProviderKind.OpenAICompatible || binding != binding.Trim() || settings is null)
        {
            throw new InvalidOperationException("The local runtime binding is invalid.");
        }

        LocalRuntimeSettings? resolved = null;

        foreach (LocalRuntimeSettings? runtime in settings.LocalRuntimes ?? [])
        {
            if (runtime is null || !string.Equals(runtime.Id, binding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (resolved is not null)
            {
                throw new InvalidOperationException("The local runtime binding is ambiguous.");
            }

            resolved = runtime;
        }

        if (resolved is null || !LocalRuntimeAdapterCatalog.IsKnown(resolved.Adapter)
            || !LocalRuntimeAdapterCatalog.TryGetManagementUri(resolved, out Uri? managementUri))
        {
            throw new InvalidOperationException("The local runtime binding is unavailable or invalid.");
        }

        if (resolved.ResourceGroup is { } group)
        {
            if (string.IsNullOrWhiteSpace(group) || group != group.Trim())
            {
                throw new InvalidOperationException("The local runtime resource group is invalid.");
            }

            return new("runtime-group", group.ToUpperInvariant(), 0);
        }

        return FromEndpoint(managementUri!.AbsoluteUri)
            ?? throw new InvalidOperationException("The local runtime endpoint is invalid.");
    }

    internal static ProviderResourceKey? FromEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string host = uri.IdnHost.Trim('[', ']');

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address)))
        {
            host = "localhost";
        }
        else
        {
            host = host.ToLowerInvariant();
        }

        return new(uri.Scheme.ToLowerInvariant(), host, uri.Port);
    }
}
