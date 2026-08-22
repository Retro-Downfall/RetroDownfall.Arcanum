using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Atomic, owner-only comparison-run history with bounded retention.</summary>
public sealed class ComparisonRunStore : IComparisonRunStore
{

    public const int CurrentSchemaVersion = 1;

    public const int DefaultMaxRuns = 100;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private readonly ILogger<ComparisonRunStore>? _logger;

    private readonly int _maxRuns;

    public ComparisonRunStore(
        string storePath,
        ITheForgeLocalMutationRunner mutationRunner,
        int maxRuns = DefaultMaxRuns,
        ILogger<ComparisonRunStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _mutationRunner = mutationRunner ?? throw new ArgumentNullException(nameof(mutationRunner));

        _maxRuns = Math.Max(1, maxRuns);

        _logger = logger;

    }

    public string StorePath { get; }

    internal ITheForgeLocalMutationRunner MutationRunner => _mutationRunner;

    public async Task<ComparisonStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            (ComparisonStoreDocument document, _) = await LoadVersionedAsync(cancellationToken)
                .ConfigureAwait(false);

            return document;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable comparisons file at {Path}; using empty document.", StorePath);

            return CreateEmptyDocument();

        }

    }

    public async Task SaveAsync(ComparisonStoreDocument document, CancellationToken cancellationToken = default)
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

    public async Task<ComparisonStoreDocument> UpdateAsync(
        Func<ComparisonStoreDocument, CancellationToken, Task<ComparisonStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(update);

        ComparisonStoreDocument? saved = null;

        await _mutationRunner
            .RunAsync(
                StorePath,
                async admittedCancellationToken =>
                {

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        (ComparisonStoreDocument current, TheForgeFileVersion version) =
                            await LoadVersionedAsync(admittedCancellationToken).ConfigureAwait(false);

                        ComparisonStoreDocument proposed = await update(current, admittedCancellationToken)
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

        return saved ?? throw new InvalidOperationException("The comparison history update did not complete.");

    }

    private async Task<ComparisonStoreDocument> SaveCoreAsync(
        ComparisonStoreDocument document,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<ComparisonRunRecord> runs = document.Runs
            .OrderByDescending(static r => r.StartedAt)
            .Take(_maxRuns)
            .ToArray();

        ComparisonStoreDocument capped = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAt = now,
            Runs = runs,
        };

        await TheForgeAtomicJsonFile
            .WriteAsync(
                StorePath,
                capped,
                TheForgeComparisonsJsonContext.Default.ComparisonStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        return capped;

    }

    private async Task<(ComparisonStoreDocument Document, TheForgeFileVersion Version)> LoadVersionedAsync(
        CancellationToken cancellationToken)
    {

        TheForgeVersionedJsonRead<ComparisonStoreDocument> read = await TheForgeVersionedJsonFile
            .ReadAsync(
                StorePath,
                TheForgeComparisonsJsonContext.Default.ComparisonStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.JsonError is not null)
        {

            _logger?.LogWarning(
                read.JsonError,
                "Corrupt or unreadable comparisons file at {Path}; using empty document.",
                StorePath);

        }

        return (read.Value ?? CreateEmptyDocument(), read.Version);

    }

    private static ComparisonStoreDocument CreateEmptyDocument()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new ComparisonStoreDocument(CurrentSchemaVersion, now, now, []);

    }

}
