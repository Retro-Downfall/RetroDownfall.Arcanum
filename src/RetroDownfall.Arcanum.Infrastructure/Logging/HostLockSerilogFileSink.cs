using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using Serilog;

using Serilog.Core;

using Serilog.Events;

using Serilog.Formatting.Compact;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

/// <summary>
/// A rolling-file sink that stays inert until lock-first startup has converged restore topology.
/// </summary>
/// <remarks>
/// Registration, host build, and pre-lock diagnostics must not materialize the guarded root. The
/// database hosted service activates this sink with its exact live maintenance lock only after
/// physical restore recovery has finished. Deactivation is terminal so shutdown diagnostics can
/// never reopen a file after that lock has been detached and released.
/// </remarks>
internal sealed class HostLockSerilogFileSink : ILogEventSink, IDisposable
{

    /// <summary>
    /// Default per-file cap, matching Serilog's own default value but paired with
    /// <c>rollOnFileSizeLimit: true</c> below - without an explicit limit, Serilog's default is the
    /// same 1 GiB ceiling but with rolling off, so an event past it is silently dropped rather than
    /// written to a new file (W8-6). Naming this rather than relying on the library default keeps the
    /// behavior stable if that default ever changes upstream; it is a constructor parameter, not a
    /// hard-coded value, so a test can exercise the roll-instead-of-drop behavior without writing a
    /// gigabyte of log data first.
    /// </summary>
    internal const long DefaultFileSizeLimitBytes = 1L * 1024L * 1024L * 1024L;

    private readonly string _guardedRoot;

    private readonly string _logDirectory;

    private readonly string _logFilePath;

    private readonly int _retainedFileCountLimit;

    private readonly long _fileSizeLimitBytes;

    private readonly bool _enabled;

    private readonly object _gate = new();

    private Logger? _inner;

    private SinkState _state;

    internal HostLockSerilogFileSink(
        string guardedRoot,
        string logDirectory,
        int retainedFileCountLimit,
        bool enabled,
        long fileSizeLimitBytes = DefaultFileSizeLimitBytes)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        _guardedRoot = Path.GetFullPath(guardedRoot);

        _logDirectory = Path.GetFullPath(logDirectory);

        _logFilePath = Path.Combine(_logDirectory, "arcanum-api-.json");

        _retainedFileCountLimit = retainedFileCountLimit;

        _fileSizeLimitBytes = fileSizeLimitBytes;

        _enabled = enabled;

    }

    internal static HostLockSerilogFileSink Create(IServiceProvider serviceProvider)
    {

        int retained = ArcanumSettingClamps.RetainedLogFileCount(
            ArcanumRuntimeDefaults.RetainedLogFileCount);

        return new HostLockSerilogFileSink(
            ArcanumPaths.GrimoireDirectory,
            LoggingBootstrapper.ResolveLogDirectory(),
            retained,
            enabled: !LoggingBootstrapper.IsTesting(serviceProvider));

    }

    internal void Activate(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(
                _guardedRoot,
                Path.GetFullPath(guardedDirectory),
                comparison))
        {

            throw new InvalidOperationException(
                "The deferred log sink belongs to a different guarded installation root.");

        }

        lock (_gate)
        {

            if (_state is SinkState.Disabled)
            {

                throw new InvalidOperationException(
                    "The host-bound rolling log sink cannot be reactivated after lock release.");

            }

            if (_state is SinkState.Active)
            {

                return;

            }

            try
            {

                if (_enabled)
                {

                    SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(_logDirectory);

                    _inner = new LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .WriteTo.File(
                            new CompactJsonFormatter(),
                            _logFilePath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: _retainedFileCountLimit,
                            fileSizeLimitBytes: _fileSizeLimitBytes,
                            rollOnFileSizeLimit: true,
                            hooks: new SecureSerilogFileHooks())
                        .CreateLogger();

                }

                _state = SinkState.Active;

            }
            catch
            {

                _inner?.Dispose();

                _inner = null;

                _state = SinkState.Disabled;

                throw;

            }

        }

    }

    internal void Deactivate()
    {

        Logger? inner;

        lock (_gate)
        {

            if (_state is SinkState.Disabled)
            {

                return;

            }

            _state = SinkState.Disabled;

            inner = _inner;

            _inner = null;

        }

        inner?.Dispose();

    }

    public void Emit(LogEvent logEvent)
    {

        ArgumentNullException.ThrowIfNull(logEvent);

        lock (_gate)
        {

            if (_state is SinkState.Active)
            {

                _inner?.Write(logEvent);

            }

        }

    }

    public void Dispose() => Deactivate();

    private enum SinkState : byte
    {

        Inert,

        Active,

        Disabled,

    }

}
