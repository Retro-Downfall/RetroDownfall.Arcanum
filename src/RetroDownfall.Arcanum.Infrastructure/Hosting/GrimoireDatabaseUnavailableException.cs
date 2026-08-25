namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// The Grimoire cannot be opened, stated in terms the operator can act on.
/// </summary>
/// <remarks>
/// Public because the CLI has to render its message. Every one of these carries recovery steps that
/// were previously written only to the rolling log — which has no console sink unless enterprise
/// telemetry is on — so the operator saw "An unexpected CLI error occurred" and nothing else on the
/// one class of failure where they most need to be told what to do.
/// </remarks>
public sealed class GrimoireDatabaseUnavailableException : InvalidOperationException
{

    public GrimoireDatabaseUnavailableException(string message)
        : base(message)
    {

    }

    /// <summary>
    /// Wraps the failure that made the Grimoire unopenable, so the cause survives the normalization.
    /// </summary>
    public GrimoireDatabaseUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {

    }

}
