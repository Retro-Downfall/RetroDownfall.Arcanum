using RetroDownfall.TheForge.Core.Models.Traces;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Load/save The Forge-local inference trace history.</summary>
public interface IInferenceTraceStore
{

    string StorePath { get; }

    Task<InferenceTraceStoreDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(InferenceTraceStoreDocument document, CancellationToken cancellationToken = default);

    Task<InferenceTraceStoreDocument> UpdateAsync(
        Func<InferenceTraceStoreDocument, CancellationToken, Task<InferenceTraceStoreDocument>> update,
        CancellationToken cancellationToken = default);

}
