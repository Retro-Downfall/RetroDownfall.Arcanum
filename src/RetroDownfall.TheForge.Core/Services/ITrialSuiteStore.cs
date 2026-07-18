using RetroDownfall.TheForge.Core.Models.Trials;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Load/save The Forge-local Trial suite library under <c>~/.config/arcanum/</c>.</summary>
public interface ITrialSuiteStore
{

    string StorePath { get; }

    Task<TrialSuiteStoreDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TrialSuiteStoreDocument document, CancellationToken cancellationToken = default);

}
