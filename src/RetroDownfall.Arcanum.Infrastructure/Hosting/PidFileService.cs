using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService PID file lifecycle
public sealed class PidFileService : IHostedService
{

    private readonly string? _path;

    private readonly ILogger<PidFileService> _logger;

    public PidFileService(IOptionsMonitor<ArcanumSettings> settings, ILogger<PidFileService> logger)
    {
        _path = settings.CurrentValue.Server?.PidFilePath;

        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            _logger.LogDebug("PID file is disabled.");

            return Task.CompletedTask;
        }

        string directory = Path.GetDirectoryName(_path)!;

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_path))
        {
            string existingText = File.ReadAllText(_path).Trim();

            if (int.TryParse(existingText, out int existingPid) && IsProcessRunning(existingPid))
            {
                _logger.LogError("Another Arcanum process is already running (PID {Pid}).", existingPid);

                throw new InvalidOperationException($"Another Arcanum process is already running (PID {existingPid}).");
            }

            _logger.LogWarning("Removing stale PID file at {Path}.", _path);
        }

        File.WriteAllText(_path, Environment.ProcessId.ToString());

        _logger.LogInformation("Wrote PID {Pid} to {Path}.", Environment.ProcessId, _path);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (File.Exists(_path))
            {
                string currentText = File.ReadAllText(_path).Trim();

                if (currentText != Environment.ProcessId.ToString())
                {
                    _logger.LogWarning(
                        "PID file {Path} contains a different PID ({Current}); leaving it.",
                        _path,
                        currentText);

                    return Task.CompletedTask;
                }

                File.Delete(_path);

                _logger.LogInformation("Removed PID file {Path}.", _path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove PID file {Path}.", _path);
        }

        return Task.CompletedTask;
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

}
