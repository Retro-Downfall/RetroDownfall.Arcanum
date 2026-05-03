namespace RetroDownfall.Arcanum.Infrastructure.Security;

public interface IGrimoireDbPassphraseSource
{
    string Passphrase { get; }
    void SetPassphrase(string passphrase);
}
