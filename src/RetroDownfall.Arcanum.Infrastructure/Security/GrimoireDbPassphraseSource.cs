namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class GrimoireDbPassphraseSource : IGrimoireDbPassphraseSource
{
    private string? _passphrase;

    public string Passphrase =>
        _passphrase
        ?? throw new InvalidOperationException("Grimoire database passphrase has not been initialized.");

    public void SetPassphrase(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        _passphrase = passphrase;
    }
}
