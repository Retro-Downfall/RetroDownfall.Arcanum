namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class MasterApiKeyUnavailableException : InvalidOperationException
{

    public MasterApiKeyUnavailableException()
        : base(
            "The master API key store is unavailable while an existing Grimoire database is present. "
            + "Restore the matching credential and Data Protection key ring before restarting Arcanum.")
    {
    }

    /// <summary>
    /// Takes an explicit message so a refusal can name its own cause. The parameterless overload speaks
    /// only for the existing-Grimoire case, and borrowing its text for a different refusal would send
    /// the operator after the wrong artefact.
    /// </summary>
    public MasterApiKeyUnavailableException(string message)
        : base(message)
    {
    }

}
