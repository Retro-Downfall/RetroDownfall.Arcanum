using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Reflection;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]

public sealed class SecureFilePermissionsTests : IAsyncLifetime
{

    private TempWorkspace _temp = null!;

    private string? _originalDotnetEnvironment;

    private string? _originalAspNetCoreEnvironment;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _temp = new TempWorkspace();

        await _temp.InitializeAsync();

        _originalDotnetEnvironment =
            global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        _originalAspNetCoreEnvironment =
            global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        _originalTestHome =
            global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

        global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", _temp.Root);

    }

    public async Task DisposeAsync()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        SecureFilePermissions.WindowsOwnerOnlyDirectoryCreateForTests = null;

        global::System.Environment.SetEnvironmentVariable(
            "DOTNET_ENVIRONMENT",
            _originalDotnetEnvironment);

        global::System.Environment.SetEnvironmentVariable(
            "ASPNETCORE_ENVIRONMENT",
            _originalAspNetCoreEnvironment);

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

        await _temp.DisposeAsync();

    }

    [Theory]
    [InlineData(true, 448, 42u, 42u, true)]
    [InlineData(false, 384, 42u, 42u, true)]
    [InlineData(true, 448, 42u, 43u, false)]
    [InlineData(true, 493, 42u, 42u, false)]
    public void Unix_strict_posture_requires_exact_mode_and_effective_user_ownership(
        bool isDirectory,
        int mode,
        uint ownerUserId,
        uint effectiveUserId,
        bool expected)
    {

        bool actual = SecureFilePermissions.UnixOwnerOnlyPostureMatches(
            (UnixFileMode)mode,
            ownerUserId,
            effectiveUserId,
            isDirectory);

        Assert.Equal(expected, actual);

    }

    [SkippableFact]
    public void ApplyOwnerOnlyToSensitivePaths_restricts_configuration_preset_sidecars()
    {

        Skip.If(OperatingSystem.IsWindows(), "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string[] paths =
        [
            ArcanumPaths.ConfigurationPresetStateFile,
            ArcanumPaths.ConfigurationPresetRollbackFile,
            ArcanumPaths.ConfigurationPresetJournalFile,
        ];

        foreach (string path in paths)
        {

            File.WriteAllText(path, "{}");

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead);

        }

        SecureFilePermissions.ApplyOwnerOnlyToSensitivePaths();

        foreach (string path in paths)
        {

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));

        }

    }

    [SkippableFact]
    public void RunStartupPermissionSelfCheck_warns_for_configuration_preset_sidecars()
    {

        Skip.If(OperatingSystem.IsWindows(), "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string[] paths =
        [
            ArcanumPaths.ConfigurationPresetStateFile,
            ArcanumPaths.ConfigurationPresetRollbackFile,
            ArcanumPaths.ConfigurationPresetJournalFile,
        ];

        foreach (string path in paths)
        {

            File.WriteAllText(path, "{}");

            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead);

        }

        CapturingLogger logger = new();

        SecureFilePermissions.RunStartupPermissionSelfCheck(logger, []);

        foreach (string path in paths)
        {

            Assert.Contains(
                logger.Warnings,
                warning => warning.Message.Contains(path, StringComparison.Ordinal));

        }

    }

    [Fact]
    public void EnsureOwnerOnlyDirectoryExists_creates_restricted_directory()
    {

        string path = Path.Combine(_temp.Root, "secure-dir");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        Assert.True(Directory.Exists(path));

        if (!OperatingSystem.IsWindows())
        {

            UnixFileMode mode = File.GetUnixFileMode(path);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);

        }

    }

    [Fact]
    public void CreateOwnerOnlyDirectoryAtPath_uses_atomic_protected_acl_construction()
    {
        PropertyInfo? seamProperty = typeof(SecureFilePermissions)
            .GetProperty(
                "WindowsOwnerOnlyDirectoryCreateForTests",
                BindingFlags.Static
                | BindingFlags.NonPublic);

        Assert.NotNull(seamProperty);

        string path = Path.Combine(
            _temp.Root,
            "atomic-windows-directory");

        bool invoked = false;

        Action<string, bool, bool, bool, bool>
            seam = (
                actualPath,
                protectFromInheritance,
                grantFullControl,
                inheritToContainers,
                inheritToObjects) =>
            {
                invoked = true;

                Assert.Equal(path, actualPath);
                Assert.True(protectFromInheritance);
                Assert.True(grantFullControl);
                Assert.True(inheritToContainers);
                Assert.True(inheritToObjects);
            };

        seamProperty.SetValue(null, seam);

        try
        {
            SecureFilePermissions
                .CreateOwnerOnlyDirectoryAtPath(path);
        }
        finally
        {
            seamProperty.SetValue(null, null);
        }

        Assert.True(invoked);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void ApplyOwnerOnlyFile_restricts_new_file()
    {

        string path = Path.Combine(_temp.Root, "secret.txt");

        File.WriteAllText(path, "secret");

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        if (!OperatingSystem.IsWindows())
        {

            UnixFileMode mode = File.GetUnixFileMode(path);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        }

    }

    [Fact]
    public void Strict_owner_only_file_application_rejects_missing_file()
    {

        string path = Path.Combine(_temp.Root, "missing-secret.txt");

        Assert.False(
            SecureFilePermissions.TryApplyOwnerOnlyFileStrict(path));

    }

    [Fact]
    public void RunStartupPermissionSelfCheck_does_not_throw_for_missing_paths()
    {

        SecureFilePermissions.RunStartupPermissionSelfCheck(NullLogger.Instance);

    }

    [Fact]
    public void Default_secret_file_paths_include_file_encryption_key_store()
    {

        MethodInfo? method = typeof(SecureFilePermissions).GetMethod(
            "DefaultSecretFilePaths",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        IReadOnlyList<string> paths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            method.Invoke(null, null));

        Assert.Contains(ArcanumPaths.FileEncryptionKeyStoreFile, paths);

    }

    [SkippableFact]
    public void RunStartupPermissionSelfCheck_warns_for_world_readable_file()
    {

        Skip.If(OperatingSystem.IsWindows(), "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (OperatingSystem.IsWindows())
        {

            return;

        }

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(ArcanumPaths.SecretStoreDirectory);

        string path = ArcanumPaths.ApiKeyStoreFile;

        File.WriteAllText(path, "data");

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead
            | UnixFileMode.OtherRead);

        CapturingLogger logger = new();

        SecureFilePermissions.RunStartupPermissionSelfCheck(logger);

        Assert.Contains(
            logger.Warnings,
            warning => warning.Message.Contains(path, StringComparison.Ordinal));

    }

    [Fact]
    public void EnsureOwnerOnlyDirectoryExists_is_idempotent()
    {

        string path = Path.Combine(_temp.Root, "existing-dir");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        Assert.True(Directory.Exists(path));

    }

    [SkippableFact]
    public void RunStartupPermissionSelfCheck_warns_for_world_readable_secret_files()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        string secFile = Path.Combine(dir, "security.dat");

        string keyFile = Path.Combine(dir, "grimoire-key.dat");

        File.WriteAllText(secFile, "x");

        File.WriteAllText(keyFile, "y");

        File.SetUnixFileMode(secFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        CapturingLogger logger = new();

        try
        {

            SecureFilePermissions.RunStartupPermissionSelfCheck(logger, [secFile, keyFile]);

            Assert.Contains(logger.Warnings, w => w.Message.Contains(secFile, StringComparison.Ordinal));

            Assert.Contains(logger.Warnings, w => w.Message.Contains(keyFile, StringComparison.Ordinal));

        }
        finally
        {

            Directory.Delete(dir, recursive: true);

        }

    }

    [SkippableFact]
    public void TryApplyUnixFileMode_logs_warning_when_chmod_fails()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        try
        {

            string bogusPath = Path.Combine(_temp.Root, "does-not-exist-" + Guid.NewGuid().ToString("N"));

            SecureFilePermissions.TryApplyUnixFileMode(bogusPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Warning
                && e.MessageTemplate.Text.Contains("Failed to apply owner-only permissions", StringComparison.OrdinalIgnoreCase));

        }
        finally
        {

            Serilog.Log.Logger = previous;

        }

    }

    [SkippableFact]
    public void CreateOwnerOnlyTempFile_creates_file_with_owner_only_mode()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Owner-only Unix mode bits are what this asserts against.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string tempPath = Path.Combine(_temp.Root, "secret.tmp." + Guid.NewGuid().ToString("N"));

        using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
        {

            stream.Write(new byte[] { 1, 2, 3 });

            stream.Flush();

        }

        UnixFileMode mode = File.GetUnixFileMode(tempPath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        File.Delete(tempPath);

    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {

        private readonly List<(LogLevel Level, string Message)> _warnings = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Warnings
        {

            get
            {

                lock (_warnings)
                {

                    return _warnings.ToList();

                }

            }

        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            if (logLevel == LogLevel.Warning)
            {

                lock (_warnings)
                {

                    _warnings.Add((logLevel, formatter(state, exception)));

                }

            }

        }

        private sealed class NoopDisposable : IDisposable
        {

            public static readonly NoopDisposable Instance = new();

            public void Dispose()
            {
            }

        }

    }

    private sealed class CapturingSink : ILogEventSink
    {

        private readonly List<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events
        {

            get
            {

                lock (_events)
                {

                    return _events.ToList();

                }

            }

        }

        public void Emit(LogEvent logEvent)
        {

            lock (_events)
            {

                _events.Add(logEvent);

            }

        }

    }

}
