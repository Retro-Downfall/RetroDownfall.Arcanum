using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public interface IContextPreviewService
{

    Task<Result<ContextPreviewResult>> PreviewContextAsync(

        ContextPreviewRequest request,

        CancellationToken cancellationToken = default);

}
