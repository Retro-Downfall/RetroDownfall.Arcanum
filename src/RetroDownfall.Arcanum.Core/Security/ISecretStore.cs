namespace RetroDownfall.Arcanum.Core.Security;

public interface ISecretStore
{

    Task<string?> GetApiKeyAsync();

    Task SaveApiKeyAsync(string apiKey);

}
