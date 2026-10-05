using System.Diagnostics;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Direct <see cref="ProcessStartInfo"/> spawn — no shell, no reflection, no P/Invoke.
/// The child is deliberately not detached: it shares the launching command's session and process
/// group, so a terminal Ctrl+C or hangup reaches it only while that command is still the terminal's
/// foreground job. Once the launcher has exited, as <c>run</c> and <c>ask</c> do, the host keeps
/// running until <c>arcanum serve quit</c> (DESIGN 4.4.1, host lifetime).
/// </summary>
internal sealed class ServeProcessLauncher : IServeProcessLauncher
{
    public Task<StartedProcess> StartServeAsync(ServeProcessStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = options.WorkingDirectory,
        };

        foreach (string argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in options.Env)
        {
            startInfo.Environment[key] = value;
        }

        // Only the id leaves this method, so the handle is released here. Disposing a Process never
        // stops the child it started: the host keeps running exactly as before.
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{options.ExecutablePath}'.");

        return Task.FromResult(new StartedProcess(process.Id));
    }
}
