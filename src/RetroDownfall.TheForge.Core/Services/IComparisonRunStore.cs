using RetroDownfall.TheForge.Core.Models.Comparisons;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Load/save The Forge-local Comparison Workbench history.</summary>
public interface IComparisonRunStore
{

    string StorePath { get; }

    Task<ComparisonStoreDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ComparisonStoreDocument document, CancellationToken cancellationToken = default);

}
