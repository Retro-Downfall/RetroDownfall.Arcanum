using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class InMemoryComparisonRunStore : IComparisonRunStore
{

    private ComparisonStoreDocument _document;

    public InMemoryComparisonRunStore()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _document = new ComparisonStoreDocument(ComparisonRunStore.CurrentSchemaVersion, now, now, []);

        StorePath = "memory://comparisons";

    }

    public string StorePath { get; }

    public Task<ComparisonStoreDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_document);

    public Task SaveAsync(ComparisonStoreDocument document, CancellationToken cancellationToken = default)
    {

        IReadOnlyList<ComparisonRunRecord> runs = document.Runs
            .OrderByDescending(static r => r.StartedAt)
            .Take(100)
            .ToArray();

        _document = document with { Runs = runs, UpdatedAt = DateTimeOffset.UtcNow };

        return Task.CompletedTask;

    }

    public async Task<ComparisonStoreDocument> UpdateAsync(
        Func<ComparisonStoreDocument, CancellationToken, Task<ComparisonStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        ComparisonStoreDocument document = await update(_document, cancellationToken);

        await SaveAsync(document, cancellationToken);

        return _document;

    }

}
