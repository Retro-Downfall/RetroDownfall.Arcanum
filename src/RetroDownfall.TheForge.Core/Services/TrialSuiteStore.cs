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

    private readonly ILogger<TrialSuiteStore>? _logger;

    private readonly int _maxRunsPerSuite;

    public TrialSuiteStore(string storePath, int maxRunsPerSuite = DefaultMaxRunsPerSuite, ILogger<TrialSuiteStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _maxRunsPerSuite = Math.Max(1, maxRunsPerSuite);

        _logger = logger;

    }

    public string StorePath { get; }

    public async Task<TrialSuiteStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            TrialSuiteStoreDocument? document = await TheForgeAtomicJsonFile
                .ReadAsync(StorePath, TheForgeTrialSuitesJsonContext.Default.TrialSuiteStoreDocument, cancellationToken)
                .ConfigureAwait(false);

            if (document is null)
            {

                DateTimeOffset now = DateTimeOffset.UtcNow;

                return new TrialSuiteStoreDocument(CurrentSchemaVersion, now, now, []);

            }

            return document;

        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable trial suites file at {Path}; using empty document.", StorePath);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            return new TrialSuiteStoreDocument(CurrentSchemaVersion, now, now, []);

        }

    }

    public async Task SaveAsync(TrialSuiteStoreDocument document, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(document);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
                .WriteAsync(StorePath, capped, TheForgeTrialSuitesJsonContext.Default.TrialSuiteStoreDocument, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

}
