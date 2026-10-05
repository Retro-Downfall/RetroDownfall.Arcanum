using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The owner-only posture of one inventoried path. <see cref="IsOwnerOnly"/> is <see langword="true"/>
/// for a path that does not exist: a secret that was never written is not a permission fault, and
/// reporting it as one would bury the real finding in noise on a fresh installation.
/// </summary>
public readonly record struct SensitivePathPosture(
    string Path,
    bool IsDirectory,
    bool Exists,
    bool IsOwnerOnly);

/// <summary>
/// Applies owner-only permissions on sensitive Arcanum paths at creation time.
/// </summary>
public static partial class SecureFilePermissions
{
    private static readonly UnixFileMode OwnerOnlyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly AsyncLocal<Func<string, bool, bool?>?> StrictOwnerOnlyVerificationOverride = new();

    /// <summary>
    /// Test seam that replaces the strict owner-only verification for the current async flow only.
    /// Every secret-bearing save consults it, so a process-global override would fail unrelated saves
    /// in tests running in parallel.
    /// </summary>
    internal static Func<string, bool, bool?>? StrictOwnerOnlyVerificationForTests
    {
        get => StrictOwnerOnlyVerificationOverride.Value;

        set => StrictOwnerOnlyVerificationOverride.Value = value;
    }

    internal static Action<string, bool, bool, bool, bool>?
        WindowsOwnerOnlyDirectoryCreateForTests
    { get; set; }

    private static readonly AsyncLocal<Action<string>?> AfterOwnerOnlyTempFileCreatedOverride = new();

    /// <summary>
    /// Test seam observing a temp file between its create and the post-hoc permission repair, for the
    /// current async flow only.
    /// </summary>
    internal static Action<string>? AfterOwnerOnlyTempFileCreatedForTests
    {
        get => AfterOwnerOnlyTempFileCreatedOverride.Value;

        set => AfterOwnerOnlyTempFileCreatedOverride.Value = value;
    }

    /// <summary>
    /// Creates <paramref name="directoryPath"/> when missing and restricts it to the current user.
    /// </summary>
    public static void EnsureOwnerOnlyDirectoryExists(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);

        ApplyOwnerOnlyDirectory(directoryPath);
    }

    public static void ApplyOwnerOnlyFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            TryApplyUnixFileMode(path, OwnerOnlyFileMode);
        }

        if (OperatingSystem.IsWindows())
        {
            TryApplyWindowsOwnerOnlyFileAcl(path);
        }
    }

    public static void ApplyOwnerOnlyDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            TryApplyUnixFileMode(path, OwnerOnlyDirectoryMode);
        }

        if (OperatingSystem.IsWindows())
        {
            TryApplyWindowsOwnerOnlyDirectoryAcl(path);
        }
    }

    internal static void CreateOwnerOnlyDirectoryAtPath(
        string path)
    {
        const bool protectFromInheritance = true;
        const bool grantFullControl = true;
        const bool inheritToContainers = true;
        const bool inheritToObjects = true;

        if (WindowsOwnerOnlyDirectoryCreateForTests is { } testCreate)
        {
            testCreate(
                path,
                protectFromInheritance,
                grantFullControl,
                inheritToContainers,
                inheritToObjects);

            return;
        }

        if (OperatingSystem.IsWindows())
        {
            CreateWindowsOwnerOnlyDirectoryAtPath(path);

            return;
        }

        Directory.CreateDirectory(
            path,
            OwnerOnlyDirectoryMode);
    }

    /// <summary>
    /// Creates every missing directory on <paramref name="directoryPath"/>, from the topmost missing
    /// ancestor down to the leaf, each owner-only at the moment it is created, and leaves every directory
    /// that already exists exactly as it is.
    /// </summary>
    /// <remarks>
    /// For a writer that owns only what it creates. <c>Directory.CreateDirectory</c> followed by a chmod
    /// leaves every created parent at the umask default and the leaf briefly open, and chmods a
    /// directory another process created in between; creating each component with its final posture
    /// has neither gap. A component that appears concurrently is left alone, because creating an existing
    /// directory changes nothing on either platform.
    /// </remarks>
    internal static void CreateMissingOwnerOnlyDirectories(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        Stack<string> missing = new();

        for (string? cursor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
            !string.IsNullOrEmpty(cursor) && !Directory.Exists(cursor);
            cursor = Path.GetDirectoryName(cursor))
        {
            missing.Push(cursor);
        }

        while (missing.TryPop(out string? next))
        {
            if (OperatingSystem.IsWindows())
            {
                CreateWindowsOwnerOnlyDirectoryAtPath(next);
            }
            else
            {
                _ = Directory.CreateDirectory(next, OwnerOnlyDirectoryMode);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsOwnerOnlyDirectoryAtPath(
        string path)
    {
        SecurityIdentifier? currentUser =
            WindowsIdentity.GetCurrent().User;

        if (currentUser is null)
        {
            throw new UnauthorizedAccessException(
                "The current Windows user has no security identifier.");
        }

        DirectorySecurity security = new();

        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.AddAccessRule(
            new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit
                | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

        new DirectoryInfo(path).Create(security);
    }

    /// <summary>
    /// Creates <paramref name="directoryPath"/> when missing and requires a verified owner-only
    /// posture. Secret-bearing directories use this rather than the warn-only
    /// <see cref="EnsureOwnerOnlyDirectoryExists"/>: nothing secret is written into a directory whose
    /// posture could not be established.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The posture could not be established.</exception>
    internal static void RequireOwnerOnlyDirectory(string directoryPath)
    {
        if (!TryEnsureOwnerOnlyDirectoryExistsStrict(directoryPath))
        {
            throw new UnauthorizedAccessException(
                $"The directory '{Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath))}' could not be "
                + "restricted to the current user, so no secret was written into it. Check its ownership "
                + "and permissions.");
        }
    }

    /// <summary>
    /// Requires a verified owner-only posture on an existing secret-bearing file.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The posture could not be established.</exception>
    internal static void RequireOwnerOnlyFile(string path)
    {
        if (!TryApplyOwnerOnlyFileStrict(path))
        {
            throw new UnauthorizedAccessException(
                $"'{Path.GetFileName(path)}' could not be restricted to the current user. Check its "
                + "ownership and permissions before relying on it.");
        }
    }

    internal static bool TryEnsureOwnerOnlyDirectoryExistsStrict(
        string directoryPath,
        bool logFailure = true)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    TryApplyWindowsOwnerOnlyDirectoryAcl(directoryPath, logFailure);
                }
                else
                {
                    File.SetUnixFileMode(directoryPath, OwnerOnlyDirectoryMode);
                }
            }
            catch (Exception ex)
            {
                if (logFailure)
                {
                    Serilog.Log.Warning(
                        ex,
                        "Failed to apply owner-only permissions to {Path}; verifying its existing posture.",
                        directoryPath);
                }
            }

            return VerifyOwnerOnly(directoryPath, isDirectory: true);
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                Serilog.Log.Warning(
                    ex,
                    "Failed to strictly apply owner-only permissions to {Path}.",
                    directoryPath);
            }

            return false;
        }
    }

    internal static bool TryApplyOwnerOnlyFileStrict(
        string path,
        bool logFailure = true)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    TryApplyWindowsOwnerOnlyFileAcl(path, logFailure);
                }
                else
                {
                    File.SetUnixFileMode(path, OwnerOnlyFileMode);
                }
            }
            catch (Exception ex)
            {
                if (logFailure)
                {
                    Serilog.Log.Warning(
                        ex,
                        "Failed to apply owner-only permissions to {Path}; verifying its existing posture.",
                        path);
                }
            }

            return VerifyOwnerOnly(path, isDirectory: false);
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                Serilog.Log.Warning(
                    ex,
                    "Failed to strictly apply owner-only permissions to {Path}.",
                    path);
            }

            return false;
        }
    }

    private static bool VerifyOwnerOnly(string path, bool isDirectory)
    {
        bool? testResult = StrictOwnerOnlyVerificationForTests?.Invoke(path, isDirectory);

        if (testResult.HasValue)
        {
            return testResult.Value;
        }

        if (!OperatingSystem.IsWindows())
        {
            return FileHandleIdentityInterop.TryGetUnixOwnerUserId(
                    path,
                    out uint ownerUserId)
                && UnixOwnerOnlyPostureMatches(
                    File.GetUnixFileMode(path),
                    ownerUserId,
                    GetEffectiveUserId(),
                    isDirectory);
        }

        return VerifyWindowsOwnerOnly(path, isDirectory);
    }

    internal static bool HasOwnerOnlyPosture(string path, bool isDirectory)
    {
        try
        {
            return VerifyOwnerOnly(path, isDirectory);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static bool HasOwnerControlledFileHandlePosture(
        SafeFileHandle handle,
        string path,
        FileHandleIdentity openedIdentity)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return FileHandleIdentityInterop.TryGetUnixHandleAccessMetadata(
                        handle,
                        out UnixFileMode mode,
                        out uint ownerUserId)
                    && UnixOwnerControlledReadableFilePostureMatches(
                        mode,
                        ownerUserId,
                        GetEffectiveUserId());
            }

            return VerifyWindowsOwnerOnly(path, isDirectory: false)
                && FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
                    path,
                    out FileHandleMetadata current)
                && FileHandleIdentity.IdentitiesMatch(
                    openedIdentity,
                    current.Identity);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or System.Security.SecurityException
                or IdentityNotMappedException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool UnixOwnerOnlyPostureMatches(
        UnixFileMode mode,
        uint ownerUserId,
        uint effectiveUserId,
        bool isDirectory)
    {
        UnixFileMode expected = isDirectory
            ? OwnerOnlyDirectoryMode
            : OwnerOnlyFileMode;

        return mode == expected
            && ownerUserId == effectiveUserId;
    }

    internal static bool UnixOwnerControlledReadableFilePostureMatches(
        UnixFileMode mode,
        uint ownerUserId,
        uint effectiveUserId)
    {
        const UnixFileMode allowed =
            UnixFileMode.UserRead | UnixFileMode.UserWrite;

        return ownerUserId == effectiveUserId
            && (mode & UnixFileMode.UserRead) != 0
            && (mode & ~allowed) == 0;
    }

    [UnsupportedOSPlatform("windows")]
    private static uint GetEffectiveUserId() => GetEffectiveUserIdNative();

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [UnsupportedOSPlatform("windows")]
    private static partial uint GetEffectiveUserIdNative();

    [SupportedOSPlatform("windows")]
    private static bool VerifyWindowsOwnerOnly(string path, bool isDirectory)
    {
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;

        if (currentUser is null)
        {
            return false;
        }

        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();

        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(currentUser))
        {
            return false;
        }

        bool currentUserAllowed = false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: false,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType is not AccessControlType.Allow)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid
                || !sid.Equals(currentUser))
            {
                return false;
            }

            currentUserAllowed = true;
        }

        return currentUserAllowed;
    }

    /// <summary>
    /// Creates a new empty file at <paramref name="tempPath"/> and applies owner-only Unix permissions
    /// before any bytes are written, so the temp file is never world/group-readable during the write
    /// window. On Windows the file is created with <see cref="FileShare.None"/>; ACL hardening is applied
    /// by <see cref="ApplyOwnerOnlyFile"/> after the final move.
    /// </summary>
    public static FileStream CreateOwnerOnlyTempFile(string tempPath)
    {
        FileStream stream = new(tempPath, OwnerOnlyCreateOptions(FileMode.Create, FileAccess.Write, FileShare.None));

        AfterOwnerOnlyTempFileCreatedForTests?.Invoke(tempPath);

        // Belt and braces: the create mode already made a new file owner-only (a umask can only remove
        // bits), but FileMode.Create also reuses an existing file whose mode it does not touch.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, OwnerOnlyFileMode);
        }

        return stream;
    }

    /// <summary>
    /// Appends UTF-8 text to <paramref name="path"/>, creating it owner-only on Unix from the open
    /// itself rather than with the umask's permissions followed by a repair. Callers still apply the
    /// owner-only posture afterwards for a file that predates them.
    /// </summary>
    internal static async Task AppendOwnerOnlyTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);

        FileStream stream = new(path, OwnerOnlyCreateOptions(FileMode.Append, FileAccess.Write, FileShare.Read));

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asynchronous file options whose create mode is owner-only on Unix, so a new file never exists
    /// with group or other permissions. Windows has no create mode; its callers apply the owner-only
    /// ACL after the open.
    /// </summary>
    private static FileStreamOptions OwnerOnlyCreateOptions(FileMode mode, FileAccess access, FileShare share)
    {
        FileStreamOptions options = new()
        {
            Mode = mode,
            Access = access,
            Share = share,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFileMode;
        }

        return options;
    }

    /// <summary>
    /// Warns (does not fail startup) when sensitive paths are readable by group or other principals.
    /// </summary>
    public static void RunStartupPermissionSelfCheck(ILogger logger) =>
        RunStartupPermissionSelfCheck(logger, DefaultSecretFilePaths());

    /// <summary>
    /// Warns (does not fail startup) when sensitive paths are readable by group or other principals.
    /// The <paramref name="secretFilePaths"/> override is intended for tests that want to verify the
    /// self-check covers the secret store files without touching the real <c>%APPDATA%/arcanum/</c> paths.
    /// </summary>
    internal static void RunStartupPermissionSelfCheck(ILogger logger, IReadOnlyList<string> secretFilePaths)
    {
        string grimoireDir = ArcanumPaths.GrimoireDirectory;

        string logDirectory = ArcanumPaths.LogDirectory;

        CheckPath(logger, grimoireDir, isDirectory: true);

        foreach (string sensitiveFile in DefaultSensitiveFilePaths())
        {
            CheckPath(logger, sensitiveFile, isDirectory: false);
        }

        foreach (string secretFile in secretFilePaths)
        {
            CheckPath(logger, secretFile, isDirectory: false);
        }

        CheckPath(logger, logDirectory, isDirectory: true);

        if (Directory.Exists(logDirectory))
        {
            try
            {
                foreach (string logFile in Directory.EnumerateFiles(logDirectory))
                {
                    CheckPath(logger, logFile, isDirectory: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not enumerate log files under {LogDirectory} for permission self-check.", logDirectory);
            }
        }
    }

    /// <summary>
    /// Every sensitive and secret file Arcanum keeps owner-only, optionally widened to
    /// the per-provider credential mirrors for <paramref name="providerNames"/>. Those mirrors are
    /// installation-specific, so the caller supplies the configured provider names rather than this
    /// class guessing them.
    /// </summary>
    public static IReadOnlyList<string> EnumerateOwnerOnlyPaths(
        IReadOnlyList<string>? providerNames = null)
    {
        List<string> paths = [.. DefaultSensitiveFilePaths(), .. DefaultSecretFilePaths()];

        foreach (string providerName in providerNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                continue;
            }

            string mirror = ArcanumPaths.InferenceProviderApiKeyStoreFile(providerName);

            if (!paths.Contains(mirror, StringComparer.Ordinal))
            {
                paths.Add(mirror);
            }
        }

        return paths;
    }

    /// <summary>
    /// Reports the owner-only posture of the inventory without changing anything — not the modes, and
    /// not the existence of any path. This is the read-only detector that
    /// <c>arcanum doctor --repair permissions.apply_owner_only</c> is planned from; it deliberately
    /// never creates a directory, because a diagnostic that materializes the thing it is inspecting
    /// cannot tell an operator whether it was there before.
    /// </summary>
    public static IReadOnlyList<SensitivePathPosture> InspectOwnerOnlyPosture(
        IReadOnlyList<string>? providerNames = null)
    {
        List<SensitivePathPosture> posture = [];

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string path in EnumerateOwnerOnlyPaths(providerNames))
        {
            if (seen.Add(path))
            {
                posture.Add(InspectPath(path, isDirectory: false));
            }
        }

        // On a default layout the secret store lives inside the Grimoire directory, so the same path
        // can arrive twice; report it once or the operator sees a doubled finding.
        foreach (string directory in new[]
        {
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.SecretStoreDirectory,
            ArcanumPaths.LogDirectory,
            DataProtectionKeyPaths.Directory,
        })
        {
            if (seen.Add(directory))
            {
                posture.Add(InspectPath(directory, isDirectory: true));
            }
        }

        return posture;
    }

    private static SensitivePathPosture InspectPath(string path, bool isDirectory)
    {
        bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);

        if (!exists)
        {
            return new SensitivePathPosture(path, isDirectory, Exists: false, IsOwnerOnly: true);
        }

        try
        {
            return new SensitivePathPosture(
                path,
                isDirectory,
                Exists: true,
                VerifyOwnerOnly(path, isDirectory));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // An unreadable ACL is itself a posture problem, but not one this class can describe
            // safely; report it as not-owner-only so the detector degrades rather than throwing.
            return new SensitivePathPosture(path, isDirectory, Exists: true, IsOwnerOnly: false);
        }
    }

    private static IReadOnlyList<string> DefaultSensitiveFilePaths() =>
        [
            ArcanumPaths.ConfigurationFile,
            ArcanumPaths.GrimoireDatabaseFile,
            Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt"),
            Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-context.json"),
            ArcanumPaths.ConfigurationPresetStateFile,
            ArcanumPaths.ConfigurationPresetRollbackFile,
            ArcanumPaths.ConfigurationPresetJournalFile,
        ];

    private static IReadOnlyList<string> DefaultSecretFilePaths() =>
        [
            ArcanumPaths.ApiKeyStoreFile,
            ArcanumPaths.GrimoireKeyStoreFile,
            ArcanumPaths.FileEncryptionKeyStoreFile,
            ArcanumPaths.PerplexityApiKeyStoreFile,
        ];

    private static void CheckPath(ILogger logger, string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (isDirectory)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
        }
        else if (!File.Exists(path))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            CheckUnixPermissions(logger, path, isDirectory);

            return;
        }

        CheckWindowsPermissions(logger, path, isDirectory);
    }

    [UnsupportedOSPlatform("windows")]
    private static void CheckUnixPermissions(ILogger logger, string path, bool isDirectory)
    {
        try
        {
            UnixFileMode mode = isDirectory
                ? File.GetUnixFileMode(path)
                : File.GetUnixFileMode(path);

            const UnixFileMode groupOrOtherReadWriteExecute =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

            if ((mode & groupOrOtherReadWriteExecute) != 0)
            {
                logger.LogWarning(
                    "Permission self-check: {Path} is group/other accessible (mode {Mode}). Restrict to owner-only (600 for files, 700 for directories).",
                    path,
                    mode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Permission self-check could not read Unix mode for {Path}.", path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CheckWindowsPermissions(ILogger logger, string path, bool isDirectory)
    {
        try
        {
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();

            IdentityReference? currentUser = WindowsIdentity.GetCurrent().User;

            if (currentUser is null)
            {
                return;
            }

            AuthorizationRuleCollection rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>())
            {
                if ((rule.FileSystemRights & (FileSystemRights.Read | FileSystemRights.ReadData | FileSystemRights.ReadExtendedAttributes)) == 0)
                {
                    continue;
                }

                if (rule.IdentityReference.Equals(currentUser))
                {
                    continue;
                }

                if (rule.IdentityReference is SecurityIdentifier sid
                    && (sid.IsWellKnown(WellKnownSidType.WorldSid)
                        || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
                        || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)))
                {
                    logger.LogWarning(
                        "Permission self-check: {Path} grants read access to {Principal}. Restrict to the current user only.",
                        path,
                        rule.IdentityReference.Value);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Permission self-check could not read ACL for {Path}.", path);
        }
    }

    [UnsupportedOSPlatform("windows")]
    internal static void TryApplyUnixFileMode(string path, UnixFileMode mode)
    {
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to apply owner-only permissions to {Path}.", path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryApplyWindowsOwnerOnlyFileAcl(
        string path,
        bool logFailure = true)
    {
        try
        {
            FileInfo fileInfo = new(path);

            FileSecurity security = fileInfo.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            IdentityReference? currentUser = WindowsIdentity.GetCurrent().User;

            if (currentUser is null)
            {
                return;
            }

            security.SetOwner(currentUser);

            security.ResetAccessRule(
                new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.Modify | FileSystemRights.Read | FileSystemRights.Write,
                    AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                Serilog.Log.Warning(ex, "Failed to apply owner-only permissions to {Path}.", path);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryApplyWindowsOwnerOnlyDirectoryAcl(
        string path,
        bool logFailure = true)
    {
        try
        {
            DirectoryInfo directoryInfo = new(path);

            DirectorySecurity security = directoryInfo.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            IdentityReference? currentUser = WindowsIdentity.GetCurrent().User;

            if (currentUser is null)
            {
                return;
            }

            security.SetOwner(currentUser);

            security.ResetAccessRule(
                new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

            directoryInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                Serilog.Log.Warning(ex, "Failed to apply owner-only permissions to {Path}.", path);
            }
        }
    }
}
