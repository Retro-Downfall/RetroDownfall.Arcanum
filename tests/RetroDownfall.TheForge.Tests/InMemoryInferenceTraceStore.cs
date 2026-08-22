using RetroDownfall.TheForge.Core.Models.Traces;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class InMemoryInferenceTraceStore : IInferenceTraceStore
{

    private InferenceTraceStoreDocument _document;

    public InMemoryInferenceTraceStore()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _document = new InferenceTraceStoreDocument(InferenceTraceStore.CurrentSchemaVersion, now, now, []);

        StorePath = "memory://traces";

    }

    public string StorePath { get; }

    public Task<InferenceTraceStoreDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_document);

    public Task SaveAsync(InferenceTraceStoreDocument document, CancellationToken cancellationToken = default)
    {

        IReadOnlyList<InferenceTraceRecord> traces = document.Traces
            .OrderByDescending(static t => t.CapturedAt)
            .Take(100)
            .ToArray();

        _document = document with { Traces = traces, UpdatedAt = DateTimeOffset.UtcNow };

        return Task.CompletedTask;

    }

    public async Task<InferenceTraceStoreDocument> UpdateAsync(
        Func<InferenceTraceStoreDocument, CancellationToken, Task<InferenceTraceStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        InferenceTraceStoreDocument document = await update(_document, cancellationToken);

        await SaveAsync(document, cancellationToken);

        return _document;

    }

}
