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

    private readonly ILogger<DiagnosticMcpFixtureStore>? _logger;

    private readonly int _maxFixtures;

    public DiagnosticMcpFixtureStore(string storePath, int maxFixtures = DefaultMaxFixtures, ILogger<DiagnosticMcpFixtureStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _maxFixtures = Math.Max(1, maxFixtures);

        _logger = logger;

    }

    public string StorePath { get; }

    public async Task<DiagnosticMcpFixtureStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            DiagnosticMcpFixtureStoreDocument? document = await TheForgeAtomicJsonFile
                .ReadAsync(StorePath, TheForgeDiagnosticMcpFixturesJsonContext.Default.DiagnosticMcpFixtureStoreDocument, cancellationToken)
                .ConfigureAwait(false);

            if (document is null)
            {

                DateTimeOffset now = DateTimeOffset.UtcNow;

                return new DiagnosticMcpFixtureStoreDocument(CurrentSchemaVersion, now, now, []);

            }

            return document;

        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable diagnostic MCP fixtures file at {Path}; using empty document.", StorePath);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            return new DiagnosticMcpFixtureStoreDocument(CurrentSchemaVersion, now, now, []);

        }

    }

    public async Task SaveAsync(DiagnosticMcpFixtureStoreDocument document, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(document);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
                .WriteAsync(StorePath, capped, TheForgeDiagnosticMcpFixturesJsonContext.Default.DiagnosticMcpFixtureStoreDocument, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

}
