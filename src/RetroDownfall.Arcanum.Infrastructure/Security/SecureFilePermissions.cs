using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Applies owner-only permissions on sensitive Arcanum paths at creation time.
/// </summary>
public static class SecureFilePermissions
{

    private static readonly UnixFileMode OwnerOnlyFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static Func<string, bool, bool?>? StrictOwnerOnlyVerificationForTests { get; set; }

    internal static Action<string, bool, bool, bool, bool>?
        WindowsOwnerOnlyDirectoryCreateForTests
    { get; set; }

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

    internal static bool TryEnsureOwnerOnlyDirectoryExistsStrict(string directoryPath)
    {

        try
        {
            Directory.CreateDirectory(directoryPath);

            if (OperatingSystem.IsWindows())
            {
                TryApplyWindowsOwnerOnlyDirectoryAcl(directoryPath);
            }
            else
            {
                File.SetUnixFileMode(directoryPath, OwnerOnlyDirectoryMode);
            }

            return VerifyOwnerOnly(directoryPath, isDirectory: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(
                ex,
                "Failed to strictly apply owner-only permissions to {Path}.",
                directoryPath);

            return false;
        }

    }

    internal static bool TryApplyOwnerOnlyFileStrict(string path)
    {

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                TryApplyWindowsOwnerOnlyFileAcl(path);
            }
            else
            {
                File.SetUnixFileMode(path, OwnerOnlyFileMode);
            }

            return VerifyOwnerOnly(path, isDirectory: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(
                ex,
                "Failed to strictly apply owner-only permissions to {Path}.",
                path);

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
            UnixFileMode expected = isDirectory
                ? OwnerOnlyDirectoryMode
                : OwnerOnlyFileMode;

            return File.GetUnixFileMode(path) == expected;
        }

        return VerifyWindowsOwnerOnly(path, isDirectory);

    }

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

        FileStream stream = new(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(tempPath, OwnerOnlyFileMode);

        }

        return stream;

    }

    /// <summary>
    /// Applies owner-only permissions to all sensitive Arcanum paths.
    /// </summary>
    public static void ApplyOwnerOnlyToSensitivePaths()
    {

        EnsureOwnerOnlyDirectoryExists(ArcanumPaths.GrimoireDirectory);

        string configFile = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        if (File.Exists(configFile))
        {

            ApplyOwnerOnlyFile(configFile);

        }

        string databaseFile = ArcanumPaths.GrimoireDatabaseFile;

        if (File.Exists(databaseFile))
        {

            ApplyOwnerOnlyFile(databaseFile);

        }

        string sessionFile = Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt");

        if (File.Exists(sessionFile))
        {

            ApplyOwnerOnlyFile(sessionFile);

        }

        string securityFile = ArcanumPaths.ApiKeyStoreFile;

        if (File.Exists(securityFile))
        {

            ApplyOwnerOnlyFile(securityFile);

        }

        string grimoireKeyFile = ArcanumPaths.GrimoireKeyStoreFile;

        if (File.Exists(grimoireKeyFile))
        {

            ApplyOwnerOnlyFile(grimoireKeyFile);

        }

        string logDirectory = ArcanumPaths.LogDirectory;

        if (Directory.Exists(logDirectory))
        {

            EnsureOwnerOnlyDirectoryExists(logDirectory);

            try
            {

                foreach (string logFile in Directory.EnumerateFiles(logDirectory))
                {

                    ApplyOwnerOnlyFile(logFile);

                }

            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {

                // Best effort — individual files may be locked by the running host.

            }

        }

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

        string configFile = Path.Combine(grimoireDir, "arcanum.json");

        string databaseFile = ArcanumPaths.GrimoireDatabaseFile;

        string sessionFile = Path.Combine(grimoireDir, "cli-session.txt");

        string logDirectory = ArcanumPaths.LogDirectory;

        CheckPath(logger, grimoireDir, isDirectory: true);

        CheckPath(logger, configFile, isDirectory: false);

        CheckPath(logger, databaseFile, isDirectory: false);

        CheckPath(logger, sessionFile, isDirectory: false);

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

    private static IReadOnlyList<string> DefaultSecretFilePaths() =>
        [ArcanumPaths.ApiKeyStoreFile, ArcanumPaths.GrimoireKeyStoreFile];

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
                    "Permission self-check: {Path} is group/other accessible (mode {Mode:o}). Restrict to owner-only (600 for files, 700 for directories).",
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
    private static void TryApplyWindowsOwnerOnlyFileAcl(string path)
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

            Serilog.Log.Warning(ex, "Failed to apply owner-only permissions to {Path}.", path);

        }

    }

    [SupportedOSPlatform("windows")]
    private static void TryApplyWindowsOwnerOnlyDirectoryAcl(string path)
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

            Serilog.Log.Warning(ex, "Failed to apply owner-only permissions to {Path}.", path);

        }

    }

}
