using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Enables host process tools for the duration of a test via Development edition +
/// <c>ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1</c>, restoring prior env values on dispose.
/// Serializes against other env-mutating tests via <see cref="Gate"/> (async-safe).
/// </summary>
internal sealed class HostProcessToolsEscapeHatchScope : IDisposable
{

    /// <summary>Shared gate for process-wide edition / host-tool env mutations.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly string? _previousAllow;

    private readonly string? _previousEdition;

    private bool _disposed;

    public HostProcessToolsEscapeHatchScope()
    {
        Gate.Wait();

        _previousAllow = global::System.Environment.GetEnvironmentVariable(
            HostProcessToolPolicy.AllowHostProcessToolsEnvVar);

        _previousEdition = global::System.Environment.GetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar);

        global::System.Environment.SetEnvironmentVariable(
            HostProcessToolPolicy.AllowHostProcessToolsEnvVar,
            "1");

        global::System.Environment.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, "development");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            global::System.Environment.SetEnvironmentVariable(
                HostProcessToolPolicy.AllowHostProcessToolsEnvVar,
                _previousAllow);

            global::System.Environment.SetEnvironmentVariable(
                ArcanumEnvironment.EditionEnvVar,
                _previousEdition);
        }
        finally
        {
            _ = Gate.Release();
        }
    }

}
