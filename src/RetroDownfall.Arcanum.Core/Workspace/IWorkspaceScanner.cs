namespace RetroDownfall.Arcanum.Core.Workspace;

public interface IWorkspaceScanner
{

    /// <summary>

    /// Resolves the working directory, discovers solution files, and returns a short textual summary for indexing.

    /// </summary>

    Task<string> BuildProjectSummaryAsync(string? rootPath = null, CancellationToken cancellationToken = default);

}
