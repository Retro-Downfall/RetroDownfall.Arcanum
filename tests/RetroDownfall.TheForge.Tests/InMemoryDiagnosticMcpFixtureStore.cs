using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class InMemoryDiagnosticMcpFixtureStore : IDiagnosticMcpFixtureStore
{

    private DiagnosticMcpFixtureStoreDocument _document;

    public InMemoryDiagnosticMcpFixtureStore()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _document = new DiagnosticMcpFixtureStoreDocument(DiagnosticMcpFixtureStore.CurrentSchemaVersion, now, now, []);

    }

    public string StorePath => "memory://the-forge-diagnostic-mcp-fixtures.json";

    public int SaveCount { get; private set; }

    public Task<DiagnosticMcpFixtureStoreDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_document);

    public Task SaveAsync(DiagnosticMcpFixtureStoreDocument document, CancellationToken cancellationToken = default)
    {

        SaveCount++;

        IReadOnlyList<DiagnosticMcpFixtureRecord> fixtures = document.Fixtures
            .GroupBy(static f => f.Name, StringComparer.Ordinal)
            .Select(static g => g.OrderByDescending(static f => f.UpdatedAt).First())
            .OrderByDescending(static f => f.UpdatedAt)
            .Take(DiagnosticMcpFixtureStore.DefaultMaxFixtures)
            .ToArray();

        _document = document with { Fixtures = fixtures, UpdatedAt = DateTimeOffset.UtcNow };

        return Task.CompletedTask;

    }

    public async Task<DiagnosticMcpFixtureStoreDocument> UpdateAsync(
        Func<DiagnosticMcpFixtureStoreDocument, CancellationToken, Task<DiagnosticMcpFixtureStoreDocument>> update,
        CancellationToken cancellationToken = default)
    {

        DiagnosticMcpFixtureStoreDocument document = await update(_document, cancellationToken);

        await SaveAsync(document, cancellationToken);

        return _document;

    }

}
