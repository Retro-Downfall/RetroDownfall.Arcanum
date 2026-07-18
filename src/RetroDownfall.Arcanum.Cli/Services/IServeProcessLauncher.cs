namespace RetroDownfall.Arcanum.Cli.Services;

internal sealed record ServeProcessStartOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Env,
    string WorkingDirectory);

internal sealed record StartedProcess(int ProcessId);

/// <summary>
/// Seam for spawning <c>arcanum serve</c> without shelling out. Tests inject a fake;
/// production uses <see cref="ServeProcessLauncher"/>.
/// </summary>
internal interface IServeProcessLauncher
{

    Task<StartedProcess> StartServeAsync(ServeProcessStartOptions options, CancellationToken cancellationToken);

}
