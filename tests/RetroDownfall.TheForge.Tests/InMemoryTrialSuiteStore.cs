using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class InMemoryTrialSuiteStore : ITrialSuiteStore
{

    private TrialSuiteStoreDocument _document;

    public InMemoryTrialSuiteStore()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _document = new TrialSuiteStoreDocument(TrialSuiteStore.CurrentSchemaVersion, now, now, []);

    }

    public string StorePath => "memory://the-forge-trial-suites.json";

    public int SaveCount { get; private set; }

    public Task<TrialSuiteStoreDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_document);

    public Task SaveAsync(TrialSuiteStoreDocument document, CancellationToken cancellationToken = default)
    {

        SaveCount++;

        List<TrialSuiteRecord> suites = [];

        foreach (TrialSuiteRecord suite in document.Suites)
        {

            suites.Add(suite with
            {
                Runs = suite.Runs.OrderByDescending(static r => r.StartedAt).Take(100).ToArray(),
            });

        }

        _document = document with { Suites = suites, UpdatedAt = DateTimeOffset.UtcNow };

        return Task.CompletedTask;

    }

}
