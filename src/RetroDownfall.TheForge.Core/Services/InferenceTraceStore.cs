using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models.Traces;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Atomic, owner-only inference-trace history with bounded retention.</summary>
public sealed class InferenceTraceStore : IInferenceTraceStore
{

    public const int CurrentSchemaVersion = 1;

    public const int DefaultMaxTraces = 100;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private readonly ILogger<InferenceTraceStore>? _logger;

    private readonly int _maxTraces;

    public InferenceTraceStore(
        string storePath,
        ITheForgeLocalMutationRunner mutationRunner,
        int maxTraces = DefaultMaxTraces,
        ILogger<InferenceTraceStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _mutationRunner = mutationRunner ?? throw new ArgumentNullException(nameof(mutationRunner));

        _maxTraces = Math.Max(1, maxTraces);

        _logger = logger;

    }

    public string StorePath { get; }

    internal ITheForgeLocalMutationRunner MutationRunner => _mutationRunner;

    public async Task<InferenceTraceStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            (InferenceTraceStoreDocument document, _) = await LoadVersionedAsync(cancellationToken)
                .ConfigureAwait(false);

            return document;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable inference traces file at {Path}; using empty document.", StorePath);

            return CreateEmptyDocument();

        }

    }

    public async Task SaveAsync(InferenceTraceStoreDocument document, CancellationToken cancellationToken = default)
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

    public async Task<InferenceTraceStoreDocument> UpdateAsync(
        Func<InferenceTraceStoreDocument, CancellationToken, Task<InferenceTraceStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(update);

        InferenceTraceStoreDocument? saved = null;

        await _mutationRunner
            .RunAsync(
                StorePath,
                async admittedCancellationToken =>
                {

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        (InferenceTraceStoreDocument current, TheForgeFileVersion version) =
                            await LoadVersionedAsync(admittedCancellationToken).ConfigureAwait(false);

                        InferenceTraceStoreDocument proposed = await update(current, admittedCancellationToken)
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

        return saved ?? throw new InvalidOperationException("The inference trace update did not complete.");

    }

    private async Task<InferenceTraceStoreDocument> SaveCoreAsync(
        InferenceTraceStoreDocument document,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        IReadOnlyList<InferenceTraceRecord> traces = document.Traces
            .OrderByDescending(static t => t.CapturedAt)
            .Take(_maxTraces)
            .ToArray();

        InferenceTraceStoreDocument capped = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAt = now,
            Traces = traces,
        };

        await TheForgeAtomicJsonFile
            .WriteAsync(
                StorePath,
                capped,
                TheForgeInferenceTracesJsonContext.Default.InferenceTraceStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        return capped;

    }

    private async Task<(InferenceTraceStoreDocument Document, TheForgeFileVersion Version)> LoadVersionedAsync(
        CancellationToken cancellationToken)
    {

        TheForgeVersionedJsonRead<InferenceTraceStoreDocument> read = await TheForgeVersionedJsonFile
            .ReadAsync(
                StorePath,
                TheForgeInferenceTracesJsonContext.Default.InferenceTraceStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.JsonError is not null)
        {

            _logger?.LogWarning(
                read.JsonError,
                "Corrupt or unreadable inference traces file at {Path}; using empty document.",
                StorePath);

        }

        return (read.Value ?? CreateEmptyDocument(), read.Version);

    }

    private static InferenceTraceStoreDocument CreateEmptyDocument()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new InferenceTraceStoreDocument(CurrentSchemaVersion, now, now, []);

    }

}
