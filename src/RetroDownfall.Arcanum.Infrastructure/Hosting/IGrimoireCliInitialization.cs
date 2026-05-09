namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Ensures the encrypted Grimoire database is reachable from CLI processes (passphrase + migrate) at most once per process.
/// </summary>
public interface IGrimoireCliInitialization
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken);
}
