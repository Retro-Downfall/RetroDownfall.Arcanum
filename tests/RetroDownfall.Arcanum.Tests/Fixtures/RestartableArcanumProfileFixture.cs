using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal sealed class RestartableArcanumProfileFixture : IAsyncDisposable
{

    private int _initialSeedClaimed;

    private int _disposed;

    internal RestartableArcanumProfileFixture()
    {

        TempHome = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"api-host-{Guid.NewGuid():N}");

        Directory.CreateDirectory(TempHome);

        if (GrimoireFixture.SqlCipherAvailable)
        {

            Grimoire = new GrimoireFixture();

        }

        GrimoireDbPassphraseSource passphraseSource = new();

        if (Grimoire is not null)
        {

            passphraseSource.SetPassphrase(Grimoire.Passphrase);

        }

        PassphraseSource = passphraseSource;

        CredentialStore = new InMemoryOsCredentialStore();

    }

    internal string TempHome { get; }

    internal GrimoireFixture? Grimoire { get; }

    internal IOsCredentialStore CredentialStore { get; }

    internal IGrimoireDbPassphraseSource PassphraseSource { get; }

    internal bool ClaimInitialSeed() =>
        Interlocked.Exchange(ref _initialSeedClaimed, 1) == 0;

    internal ArcanumWebApplicationFactory CreateFactory() => new(this);

    public ValueTask DisposeAsync()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return ValueTask.CompletedTask;

        }

        try
        {

            SqliteConnection.ClearAllPools();

            Grimoire?.Dispose();

            if (Directory.Exists(TempHome))
            {

                Directory.Delete(TempHome, recursive: true);

            }

        }
        catch
        {

        }

        return ValueTask.CompletedTask;

    }

}
