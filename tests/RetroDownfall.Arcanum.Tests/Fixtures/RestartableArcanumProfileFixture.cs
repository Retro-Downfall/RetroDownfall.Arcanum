using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

internal sealed class RestartableArcanumProfileFixture : IAsyncDisposable
{
    private int _initialSeedClaimed;

    private readonly object _disposalSync = new();

    private readonly Action _clearPools;

    private readonly Action _disposeGrimoire;

    private readonly Action<string>? _deleteTempHome;

    private readonly Action<string>? _report;

    private readonly InMemoryOsCredentialStore _credentialStore;

    private readonly GrimoireDbPassphraseSource _passphraseSource;

    private bool _disposed;

    internal RestartableArcanumProfileFixture(
        Action? clearPools = null,
        Action? disposeGrimoire = null,
        Action<string>? deleteTempHome = null,
        Action<string>? report = null)
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

        _passphraseSource = passphraseSource;

        PassphraseSource = _passphraseSource;

        _credentialStore = new InMemoryOsCredentialStore();

        CredentialStore = _credentialStore;

        _clearPools = clearPools ?? SqliteConnection.ClearAllPools;

        _disposeGrimoire = disposeGrimoire ?? (() => Grimoire?.Dispose());

        _deleteTempHome = deleteTempHome;

        _report = report;
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
                    _ = TestDirectoryCleanup.TryDelete(
                        TempHome,
                        nameof(RestartableArcanumProfileFixture),
                        _report,
                        _deleteTempHome);
                }
                finally
                {
                    try
                    {
                        _credentialStore.Clear();
                    }
                    finally
                    {
                        try
                        {
                            _passphraseSource.Clear();
                        }
                        finally
                        {
                            _disposed = true;
                        }
                    }
                }
            }
        }

        return ValueTask.CompletedTask;
    }
}
