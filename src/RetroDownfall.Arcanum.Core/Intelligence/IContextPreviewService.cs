using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public interface IContextPreviewService
{

    /// <summary>
    /// Builds one context preview under the caller's explicit authority classification.
    /// </summary>
    /// <remarks>
    /// Inspection is an operator surface: it may load Covenant context so the preview tells the truth
    /// about what a real turn would send. It never stages a mutation, because it has no durable
    /// assistant entry to publish one against (§10.12).
    /// </remarks>
    Task<Result<ContextPreviewResult>> PreviewContextAsync(

        ContextPreviewRequest request,

        ArcanumInvocationContext invocationContext,

        CancellationToken cancellationToken);

}
