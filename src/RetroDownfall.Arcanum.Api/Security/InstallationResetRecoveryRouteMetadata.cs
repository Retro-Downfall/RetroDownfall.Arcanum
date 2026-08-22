namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Marks one exact authenticated route as callable while the host exists only to replay an admitted
/// installation factory reset. The method is retained in the marker so a future route refactor cannot
/// accidentally widen recovery admission by reusing a name on a different verb.
/// </summary>
internal sealed class InstallationResetRecoveryApiRouteMetadata(string method)
{

    internal static InstallationResetRecoveryApiRouteMetadata GetHealth { get; } =
        new("GET");

    internal static InstallationResetRecoveryApiRouteMetadata QuitServer { get; } =
        new("POST");

    internal static InstallationResetRecoveryApiRouteMetadata FactoryReset { get; } =
        new("POST");

    internal string Method { get; } = method;

}

/// <summary>
/// Marks an anonymously authenticated peer callback that must stay indistinguishably unavailable
/// while this process is a factory-reset recovery host.
/// </summary>
internal sealed class InstallationResetRecoveryHiddenRouteMetadata
{

    internal static InstallationResetRecoveryHiddenRouteMetadata Instance { get; } = new();

    private InstallationResetRecoveryHiddenRouteMetadata()
    {
    }

}

/// <summary>
/// Marks an API-adjacent route that may be anonymous in a normal loopback host but must return a
/// typed conflict without running while the process is a factory-reset recovery host.
/// </summary>
internal sealed class InstallationResetRecoveryBlockedRouteMetadata
{

    internal static InstallationResetRecoveryBlockedRouteMetadata Instance { get; } = new();

    private InstallationResetRecoveryBlockedRouteMetadata()
    {
    }

}
