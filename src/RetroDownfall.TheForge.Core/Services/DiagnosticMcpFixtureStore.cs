using Microsoft.Extensions.Logging;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Atomic, owner-only Diagnostic MCP Invocation fixture store with bounded retention.</summary>
public sealed class DiagnosticMcpFixtureStore : IDiagnosticMcpFixtureStore
{

    public const int CurrentSchemaVersion = 1;

    public const int DefaultMaxFixtures = 100;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private readonly ILogger<DiagnosticMcpFixtureStore>? _logger;

    private readonly int _maxFixtures;

    public DiagnosticMcpFixtureStore(
        string storePath,
        ITheForgeLocalMutationRunner mutationRunner,
        int maxFixtures = DefaultMaxFixtures,
        ILogger<DiagnosticMcpFixtureStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _mutationRunner = mutationRunner ?? throw new ArgumentNullException(nameof(mutationRunner));

        _maxFixtures = Math.Max(1, maxFixtures);

        _logger = logger;

    }

    public string StorePath { get; }

    internal ITheForgeLocalMutationRunner MutationRunner => _mutationRunner;

    public async Task<DiagnosticMcpFixtureStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            (DiagnosticMcpFixtureStoreDocument document, _) = await LoadVersionedAsync(cancellationToken)
                .ConfigureAwait(false);

            return document;

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable diagnostic MCP fixtures file at {Path}; using empty document.", StorePath);

            return CreateEmptyDocument();

        }

    }

    public async Task SaveAsync(DiagnosticMcpFixtureStoreDocument document, CancellationToken cancellationToken = default)
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

    public async Task<DiagnosticMcpFixtureStoreDocument> UpdateAsync(
        Func<DiagnosticMcpFixtureStoreDocument, CancellationToken, Task<DiagnosticMcpFixtureStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(update);

        DiagnosticMcpFixtureStoreDocument? saved = null;

        await _mutationRunner
            .RunAsync(
                StorePath,
                async admittedCancellationToken =>
                {

                    await _writeLock.WaitAsync(admittedCancellationToken).ConfigureAwait(false);

                    try
                    {

                        (DiagnosticMcpFixtureStoreDocument current, TheForgeFileVersion version) =
                            await LoadVersionedAsync(admittedCancellationToken).ConfigureAwait(false);

                        DiagnosticMcpFixtureStoreDocument proposed = await update(current, admittedCancellationToken)
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

        return saved ?? throw new InvalidOperationException("The diagnostic fixture update did not complete.");

    }

    private async Task<DiagnosticMcpFixtureStoreDocument> SaveCoreAsync(
        DiagnosticMcpFixtureStoreDocument document,
        CancellationToken cancellationToken)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // User-managed-by-name: dedupe by Name (keep newest), then cap by recency.
        IReadOnlyList<DiagnosticMcpFixtureRecord> fixtures = document.Fixtures
            .GroupBy(static f => f.Name, StringComparer.Ordinal)
            .Select(static g => g.OrderByDescending(static f => f.UpdatedAt).First())
            .OrderByDescending(static f => f.UpdatedAt)
            .Take(_maxFixtures)
            .ToArray();

        DiagnosticMcpFixtureStoreDocument capped = document with
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAt = now,
            Fixtures = fixtures,
        };

        await TheForgeAtomicJsonFile
            .WriteAsync(
                StorePath,
                capped,
                TheForgeDiagnosticMcpFixturesJsonContext.Default.DiagnosticMcpFixtureStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        return capped;

    }

    private async Task<(DiagnosticMcpFixtureStoreDocument Document, TheForgeFileVersion Version)> LoadVersionedAsync(
        CancellationToken cancellationToken)
    {

        TheForgeVersionedJsonRead<DiagnosticMcpFixtureStoreDocument> read = await TheForgeVersionedJsonFile
            .ReadAsync(
                StorePath,
                TheForgeDiagnosticMcpFixturesJsonContext.Default.DiagnosticMcpFixtureStoreDocument,
                cancellationToken)
            .ConfigureAwait(false);

        if (read.JsonError is not null)
        {

            _logger?.LogWarning(
                read.JsonError,
                "Corrupt or unreadable diagnostic MCP fixtures file at {Path}; using empty document.",
                StorePath);

        }

        return (read.Value ?? CreateEmptyDocument(), read.Version);

    }

    private static DiagnosticMcpFixtureStoreDocument CreateEmptyDocument()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return new DiagnosticMcpFixtureStoreDocument(CurrentSchemaVersion, now, now, []);

    }

}
