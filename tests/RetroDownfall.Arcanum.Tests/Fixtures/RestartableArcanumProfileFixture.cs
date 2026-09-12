using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal sealed class RestartableArcanumProfileFixture : IAsyncDisposable
{

    private int _initialSeedClaimed;

    private readonly object _disposalSync = new();

    private readonly Action _clearPools;

    private readonly Action _disposeGrimoire;

    private bool _disposed;

    internal RestartableArcanumProfileFixture(
        Action? clearPools = null,
        Action? disposeGrimoire = null)
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

        _clearPools = clearPools ?? SqliteConnection.ClearAllPools;

        _disposeGrimoire = disposeGrimoire ?? (() => Grimoire?.Dispose());

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

        lock (_disposalSync)
        {

            if (_disposed)
            {

                return ValueTask.CompletedTask;

            }

            try
            {

                try
                {

                    _clearPools();

                }
                finally
                {

                    _disposeGrimoire();

                }

            }
            finally
            {

                try
                {

                    try
                    {

                        if (Directory.Exists(TempHome))
                        {

                            Directory.Delete(TempHome, recursive: true);

                        }

                    }
                    catch
                    {

                    }

                }
                finally
                {

                    _disposed = true;

                }

            }

        }

        return ValueTask.CompletedTask;

    }

}
