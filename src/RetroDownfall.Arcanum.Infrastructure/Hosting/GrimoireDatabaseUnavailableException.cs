namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class GrimoireDatabaseUnavailableException : InvalidOperationException
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
