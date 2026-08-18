using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// A save/open picker that always answers with one path, so an export test can point a view model at
/// a location the filesystem will reject and assert how the failure is reported.
/// </summary>
internal sealed class FixedPathArtifactFileDialogService(string path) : IArtifactFileDialogService
{

    public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(path);

    public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(path);

    public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(path);

    public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(path);

    public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(path);

    /// <summary>A path inside a directory that does not exist — every write to it fails.</summary>
    public static string UnwritablePath() =>
        Path.Combine(Path.GetTempPath(), $"forge-missing-{Guid.NewGuid():N}", "export.json");

}
