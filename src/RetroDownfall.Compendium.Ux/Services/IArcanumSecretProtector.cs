using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.Services;

public interface IArcanumSecretProtector
{

    string? Unprotect(string? stored);

    string? Protect(string? plaintext);

    ArcanumSettings DecryptProviderKeys(ArcanumSettings settings);

    ArcanumSettings EncryptProviderKeys(ArcanumSettings settings);

}
