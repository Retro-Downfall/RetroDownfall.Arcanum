using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>
/// Atomic, owner-only persistence for Trial suites. Caps run history per suite on save.
/// </summary>
public sealed class TrialSuiteStore : ITrialSuiteStore
{

    public const int CurrentSchemaVersion = 1;

    public const int DefaultMaxRunsPerSuite = 100;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private readonly ILogger<TrialSuiteStore>? _logger;

    private readonly int _maxRunsPerSuite;

    public TrialSuiteStore(
        string storePath,
        ITheForgeLocalMutationRunner mutationRunner,
        int maxRunsPerSuite = DefaultMaxRunsPerSuite,
        ILogger<TrialSuiteStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _mutationRunner = mutationRunner ?? throw new ArgumentNullException(nameof(mutationRunner));

        _maxRunsPerSuite = Math.Max(1, maxRunsPerSuite);

        _logger = logger;

    }

    public string StorePath { get; }

    internal ITheForgeLocalMutationRunner MutationRunner => _mutationRunner;

    public async Task<TrialSuiteStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            (TrialSuiteStoreDocument document, _) = await LoadVersionedAsync(cancellationToken)
                .ConfigureAwait(false);

            return document;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable trial suites file at {Path}; using empty document.", StorePath);

            return CreateEmptyDocument();

        }

    }

    public async Task SaveAsync(TrialSuiteStoreDocument document, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(document);

        TheForgeFileVersion expected = await TheForgeVersionedJsonFile
            .CaptureVersionAsync(StorePath, cancellationToken)
            .ConfigureAwait(false);

        await _mutationRunner
            .RunAsync(
                StorePath,
                async admittedCancellationToken =>
                {

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        await TheForgeVersionedJsonFile
                            .EnsureUnchangedAsync(StorePath, expected, admittedCancellationToken)
                            .ConfigureAwait(false);

                        await SaveCoreAsync(document, admittedCancellationToken).ConfigureAwait(false);

                    }
                    finally
                    {

                        _writeLock.Release();

                    }

                },
                cancellationToken)
            .ConfigureAwait(false);

    }

    public async Task<TrialSuiteStoreDocument> UpdateAsync(
        Func<TrialSuiteStoreDocument, CancellationToken, Task<TrialSuiteStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(update);

        TrialSuiteStoreDocument? saved = null;

        await _mutationRunner
            .RunAsync(
                StorePath,
                async admittedCancellationToken =>
                {

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        (TrialSuiteStoreDocument current, TheForgeFileVersion version) =
                            await LoadVersionedAsync(admittedCancellationToken).ConfigureAwait(false);

                        TrialSuiteStoreDocument proposed = await update(current, admittedCancellationToken)
                            .ConfigureAwait(false);

                        ArgumentNullException.ThrowIfNull(proposed);

                        await TheForgeVersionedJsonFile
                            .EnsureUnchangedAsync(StorePath, version, admittedCancellationToken)
                            .ConfigureAwait(false);

                        saved = await SaveCoreAsync(proposed, admittedCancellationToken).ConfigureAwait(false);

                    }
                    finally
                    {

                        _writeLock.Release();

                    }

                },
                cancellationToken)
            .ConfigureAwait(false);

        return saved ?? throw new InvalidOperationException("The trial suite update did not complete.");

    }

    public async Task<TrialSuiteStoreDocument> UpdatePreparedAsync<TPreparation>(
        Func<TrialSuiteStoreDocument, CancellationToken, Task<TPreparation>> prepare,
        Func<TrialSuiteStoreDocument, TPreparation, TrialSuiteStoreDocument> commit,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(prepare);

        ArgumentNullException.ThrowIfNull(commit);

        TrialSuiteStoreDocument? saved = null;

        await _mutationRunner
            .RunAsync(
                StorePath,
                async admittedCancellationToken =>
                {

                    TrialSuiteStoreDocument initial;

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        (initial, _) = await LoadVersionedAsync(admittedCancellationToken).ConfigureAwait(false);

                    }
                    finally
                    {

                        _writeLock.Release();

                    }

                    TPreparation preparation = await prepare(initial, admittedCancellationToken)
                        .ConfigureAwait(false);

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        (TrialSuiteStoreDocument current, TheForgeFileVersion version) =
                            await LoadVersionedAsync(admittedCancellationToken).ConfigureAwait(false);

                        TrialSuiteStoreDocument proposed = commit(current, preparation);

                        ArgumentNullException.ThrowIfNull(proposed);

                        await TheForgeVersionedJsonFile
                            .EnsureUnchangedAsync(StorePath, version, admittedCancellationToken)
                            .ConfigureAwait(false);

                        saved = await SaveCoreAsync(proposed, admittedCancellationToken).ConfigureAwait(false);

                    }
                    finally
                    {

                        _writeLock.Release();

                    }

                },
                cancellationToken)
            .ConfigureAwait(false);

        return saved ?? throw new InvalidOperationException("The prepared trial suite update did not complete.");

    }

    private async Task<TrialSuiteStoreDocument> SaveCoreAsync(
        TrialSuiteStoreDocument document,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<TrialSuiteRecord> suites = [];

        foreach (TrialSuiteRecord suite in document.Suites)
        {

            IReadOnlyList<TrialSuiteRunRecord> runs = suite.Runs
                .OrderByDescending(static r => r.StartedAt)
                .Take(_maxRunsPerSuite)
                .ToArray();

            suites.Add(suite with { Runs = runs, UpdatedAt = suite.UpdatedAt });

        }

        TrialSuiteStoreDocument capped = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAt = now,
            Suites = suites,
        };

        await TheForgeAtomicJsonFile
            .WriteAsync(
                StorePath,
                capped,
                TheForgeTrialSuitesJsonContext.Default.TrialSuiteStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        return capped;

    }

    private async Task<(TrialSuiteStoreDocument Document, TheForgeFileVersion Version)> LoadVersionedAsync(
        CancellationToken cancellationToken)
    {

        TheForgeVersionedJsonRead<TrialSuiteStoreDocument> read = await TheForgeVersionedJsonFile
            .ReadAsync(
                StorePath,
                TheForgeTrialSuitesJsonContext.Default.TrialSuiteStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.JsonError is not null)
        {

            _logger?.LogWarning(
                read.JsonError,
                "Corrupt or unreadable trial suites file at {Path}; using empty document.",
                StorePath);

        }

        return (read.Value ?? CreateEmptyDocument(), read.Version);

    }

    private static TrialSuiteStoreDocument CreateEmptyDocument()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new TrialSuiteStoreDocument(CurrentSchemaVersion, now, now, []);

    }

}
