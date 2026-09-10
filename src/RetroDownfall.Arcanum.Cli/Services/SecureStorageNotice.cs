using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

public interface ISecureStorageNotice
{
    void ExplainBeforeHostBootstrap();

    void ExplainAfterSetup();

    void MarkHostBootstrapCompleted();
}

/// <summary>
/// Explains first-run operating-system credential access before the operating system presents its
/// own password dialog. The file-side check deliberately avoids opening secure storage merely to
/// decide whether warning about opening secure storage is necessary.
/// </summary>
internal sealed class SecureStorageNotice : ISecureStorageNotice
{
    private readonly IConsoleDispatcher _console;

    private readonly Func<bool> _bootstrapMarkerExists;

    private readonly Action _markBootstrapCompleted;

    private readonly string _secureStoreName;

    public SecureStorageNotice(IConsoleDispatcher console)
        : this(
            console,
            static () => File.Exists(ArcanumPaths.SecureStorageBootstrapMarkerFile),
            PublishBootstrapMarker,
            ResolveSecureStoreName())
    {
    }

    internal SecureStorageNotice(
        IConsoleDispatcher console,
        Func<bool> bootstrapMarkerExists,
        Action markBootstrapCompleted,
        string secureStoreName)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _bootstrapMarkerExists = bootstrapMarkerExists
            ?? throw new ArgumentNullException(nameof(bootstrapMarkerExists));
        _markBootstrapCompleted = markBootstrapCompleted
            ?? throw new ArgumentNullException(nameof(markBootstrapCompleted));
        _secureStoreName = string.IsNullOrWhiteSpace(secureStoreName)
            ? throw new ArgumentException("A secure-store name is required.", nameof(secureStoreName))
            : secureStoreName;
    }

    public void ExplainBeforeHostBootstrap()
    {
        if (_bootstrapMarkerExists())
        {
            return;
        }

        _console.WriteDiagnostic(
            "Secure storage: Arcanum is about to read or create its private server-authentication "
                + $"key in {_secureStoreName}. The operating system may ask for your login "
                + "password. Arcanum never receives or stores that password.");

        WriteDeferredFileKeyExplanation();
    }

    public void ExplainAfterSetup()
    {
        if (_bootstrapMarkerExists())
        {
            return;
        }

        _console.WriteDiagnostic(
            "Secure storage after setup: the first server start will read or create Arcanum's "
                + $"private server-authentication key in {_secureStoreName}. The operating system may ask "
                + "for your login password; Arcanum never receives or stores that password.");

        WriteDeferredFileKeyExplanation();
    }

    public void MarkHostBootstrapCompleted()
    {
        if (_bootstrapMarkerExists())
        {
            return;
        }

        try
        {
            _markBootstrapCompleted();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            _console.WriteDiagnostic(
                "Arcanum could not save its secure-storage startup notice state; the host is "
                    + "running, but you may see this explanation again next time.");
        }
    }

    private void WriteDeferredFileKeyExplanation()
    {
        _console.WriteDiagnostic(
            "Arcanum may also read its separate file-encryption key now if existing encrypted files "
                + "need validation. Otherwise that key is created only when you first use attachments, "
                + "uploads, or batch files, so the secure-storage request occurs in that context.");
    }

    private static string ResolveSecureStoreName()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macOS Keychain";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Windows Credential Manager";
        }

        if (OperatingSystem.IsLinux())
        {
            return "the desktop secret store";
        }

        return "the operating system's credential store";
    }

    private static void PublishBootstrapMarker()
    {
        string markerPath = ArcanumPaths.SecureStorageBootstrapMarkerFile;
        string directory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("Invalid secure-storage marker path.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        string tempPath = markerPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
            {
                stream.Write("1\n"u8);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, markerPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                return;
            }

            SecureFilePermissions.ApplyOwnerOnlyFile(markerPath);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // The marker is non-secret and the temporary file is owner-only. Cleanup is best effort.
            }
        }
    }
}
