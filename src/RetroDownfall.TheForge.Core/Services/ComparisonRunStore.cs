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

    private readonly ILogger<ComparisonRunStore>? _logger;

    private readonly int _maxRuns;

    public ComparisonRunStore(string storePath, int maxRuns = DefaultMaxRuns, ILogger<ComparisonRunStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _maxRuns = Math.Max(1, maxRuns);

        _logger = logger;

    }

    public string StorePath { get; }

    public async Task<ComparisonStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            ComparisonStoreDocument? document = await TheForgeAtomicJsonFile
                .ReadAsync(StorePath, TheForgeComparisonsJsonContext.Default.ComparisonStoreDocument, cancellationToken)
                .ConfigureAwait(false);

            if (document is null)
            {

                DateTimeOffset now = DateTimeOffset.UtcNow;

                return new ComparisonStoreDocument(CurrentSchemaVersion, now, now, []);

            }

            return document;

        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable comparisons file at {Path}; using empty document.", StorePath);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            return new ComparisonStoreDocument(CurrentSchemaVersion, now, now, []);

        }

    }

    public async Task SaveAsync(ComparisonStoreDocument document, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(document);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
                .WriteAsync(StorePath, capped, TheForgeComparisonsJsonContext.Default.ComparisonStoreDocument, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

}
