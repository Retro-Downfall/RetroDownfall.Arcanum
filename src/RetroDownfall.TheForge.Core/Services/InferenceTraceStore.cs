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

    private readonly ILogger<InferenceTraceStore>? _logger;

    private readonly int _maxTraces;

    public InferenceTraceStore(string storePath, int maxTraces = DefaultMaxTraces, ILogger<InferenceTraceStore>? logger = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        StorePath = storePath;

        _maxTraces = Math.Max(1, maxTraces);

        _logger = logger;

    }

    public string StorePath { get; }

    public async Task<InferenceTraceStoreDocument> LoadAsync(CancellationToken cancellationToken = default)
    {

        try
        {

            InferenceTraceStoreDocument? document = await TheForgeAtomicJsonFile
                .ReadAsync(StorePath, TheForgeInferenceTracesJsonContext.Default.InferenceTraceStoreDocument, cancellationToken)
                .ConfigureAwait(false);

            if (document is null)
            {

                DateTimeOffset now = DateTimeOffset.UtcNow;

                return new InferenceTraceStoreDocument(CurrentSchemaVersion, now, now, []);

            }

            return document;

        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {

            _logger?.LogWarning(ex, "Corrupt or unreadable inference traces file at {Path}; using empty document.", StorePath);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            return new InferenceTraceStoreDocument(CurrentSchemaVersion, now, now, []);

        }

    }

    public async Task SaveAsync(InferenceTraceStoreDocument document, CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(document);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
                .WriteAsync(StorePath, capped, TheForgeInferenceTracesJsonContext.Default.InferenceTraceStoreDocument, cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _writeLock.Release();

        }

    }

}
