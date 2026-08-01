namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class MasterApiKeyUnavailableException : InvalidOperationException
{

    public MasterApiKeyUnavailableException()
        : base(
            "The master API key store is unavailable while an existing Grimoire database is present. "
            + "Restore the matching credential and Data Protection key ring before restarting Arcanum.")
    {
    }

}
