namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed class GrimoireDatabaseUnavailableException : InvalidOperationException
{

    public GrimoireDatabaseUnavailableException(string message)
        : base(message)
    {

    }

}
