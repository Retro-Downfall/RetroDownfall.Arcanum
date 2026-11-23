using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

/// <summary>Only copies of admitted regular files reach the guest. It never mounts an operator directory.</summary>
internal sealed class WorkspaceCheckPortableInputs : IDisposable
{
    private readonly FileHandleIdentity _identity;

    private readonly FileStream _ownership;

    private const string OwnerFile = "stage.lock";

    private bool _retain;

    internal string Root { get; }

    internal string Source => Path.Combine(Root, "source");

    internal string Packages => Path.Combine(Root, "packages");

    internal static string StagingDirectory => Path.Combine(Path.GetTempPath(), "arcanum-portable-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ArcanumPaths.GrimoireDirectory))));

    private WorkspaceCheckPortableInputs(string root, FileHandleIdentity identity)
    {
        Root = root;

        _identity = identity;

        _ownership = new(Path.Combine(root, OwnerFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    internal static async Task<WorkspaceCheckPortableInputs> CreateAsync(string workspace, FileHandleIdentity identity,
        string feed, FileHandleIdentity feedIdentity, long sourceByteLimit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string parent = StagingDirectory;

        if (WorkspaceRootPath.IsWithinOrEqual(parent, workspace) || WorkspaceRootPath.IsWithinOrEqual(parent, feed))
            throw new IOException("The portable check requires staging outside its input roots.");

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(parent);

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(parent, FileSystemObjectKind.Directory, out var stagingParent))
            throw new IOException("The portable check staging parent is unavailable.");

        SecureFilePermissions.RequireOwnerOnlyDirectory(parent);

        string path = Path.Combine(parent, Guid.NewGuid().ToString("N"));

        SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(path);

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(path, FileSystemObjectKind.Directory, out var created))
            throw new IOException("The portable check could not identify its staging directory.");

        WorkspaceCheckPortableInputs inputs = new(path, created.Metadata.Identity);

        try
        {
            long remaining = await CopyRootAsync(workspace, identity, stagingParent.Metadata.Identity, inputs.Source, packages: false, sourceByteLimit, ct).ConfigureAwait(false);

            await CopyRootAsync(feed, feedIdentity, stagingParent.Metadata.Identity, inputs.Packages, packages: true, remaining, ct).ConfigureAwait(false);

            return inputs;
        }
        catch
        {
            inputs.Dispose();

            throw;
        }
    }

    private static async Task<long> CopyRootAsync(string path, FileHandleIdentity expected, FileHandleIdentity stagingParent, string destination,
        bool packages, long byteLimit, CancellationToken ct)
    {
        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(path, requestEnumeration: true, out var root, out var metadata))
        {
            root.Dispose();

            throw new IOException("The portable check input directory is unavailable.");
        }

        using (root)
        {
            if (expected != metadata.Identity || expected == stagingParent
                || !FileHandleIdentityInterop.TryGetHandleKernelPath(root, out string? kernelRoot))
                throw new IOException("A selected input directory changed before staging.");

            long remaining = byteLimit;

            byte[] buffer = new byte[64 * 1024];

            await CopyDirectoryAsync(root, kernelRoot, destination).ConfigureAwait(false);

            return remaining;

            async Task CopyDirectoryAsync(SafeFileHandle directory, string input, string output)
            {
                SecureFilePermissions.CreateOwnerOnlyDirectoryAtPath(output);

                if (OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                string? after = null;

                bool more;

                do
                {
                    ct.ThrowIfCancellationRequested();

                    if (!SecureDirectoryNameEnumerator.TryEnumeratePage(directory, after, 100, packages ? ".nupkg" : "",
                            ct, out var names, out more))
                        throw new IOException("The portable check could not enumerate an input directory.");

                    foreach (string name in names)
                    {
                        after = name;

                        ct.ThrowIfCancellationRequested();

                        if (!packages && (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                            || name.Equals(".arcanum", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("bin", StringComparison.OrdinalIgnoreCase))) continue;

                        string target = Path.Combine(output, name);

                        string original = Path.Combine(input, name);

                        if (!packages && FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(directory, name, false,
                                true, out var child, out var childMetadata))
                        {
                            using (child)
                            {
                                if (childMetadata.Identity.VolumeId != metadata.Identity.VolumeId || childMetadata.Identity == stagingParent)
                                    throw new IOException("The portable check cannot stage another volume or its own staging directory.");

                                EnsurePosition(child, original);

                                await CopyDirectoryAsync(child, original, target).ConfigureAwait(false);

                                EnsurePosition(child, original);
                            }

                            continue;
                        }

                        var status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(directory, name, out var handle);

                        using (handle)
                        {
                            if (status != SecureFileOpenStatus.Success || handle is null
                                || !SecureFileReader.TryValidateRegularFileHandle(handle, null, out var file)
                                || file.Identity.VolumeId != metadata.Identity.VolumeId)
                                throw new IOException("The portable check requires regular, unaliased inputs.");

                            using FileStream source = new(handle, FileAccess.Read);

                            EnsurePosition(handle, original);

                            long bytes = source.Length;

                            if (bytes > remaining) throw new IOException("Workspace inputs exceed the Sanctum staging allowance.");

                            using FileStream copy = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                            if (OperatingSystem.IsMacOS())
                            {
                                const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

                                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

                                if (!packages && (File.GetUnixFileMode(handle) & execute) != 0) mode |= execute;

                                File.SetUnixFileMode(copy.SafeFileHandle, mode);
                            }

                            while (copy.Position < bytes)
                            {
                                int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(bytes - copy.Position, buffer.Length)), ct)
                                    .ConfigureAwait(false);

                                if (count == 0) throw new IOException("A portable check input changed while staging.");

                                await copy.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                            }

                            if (source.Length != bytes) throw new IOException("A portable check input changed while staging.");

                            EnsurePosition(handle, original);

                            if (!SecureFileReader.TryValidateRegularFileHandle(handle, file.Identity, out _))
                                throw new IOException("A portable check input changed identity while staging.");

                            remaining -= bytes;
                        }
                    }
                }
                while (more);
            }
        }
    }

    private static void EnsurePosition(SafeFileHandle handle, string expected)
    {
        if (!FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out var path) || path != expected)
            throw new IOException("A portable check input moved outside its selected path.");
    }

    internal void RetainForRecovery() => _retain = true;

    internal static async Task RecoverAsync(WorkspaceCheckAppleContainerRunner runner, CancellationToken ct)
    {
        string parent = StagingDirectory;

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(parent, FileSystemObjectKind.Directory, out _)) return;

        SecureFilePermissions.RequireOwnerOnlyDirectory(parent);

        foreach (string root in Directory.EnumerateDirectories(parent))
        {
            ct.ThrowIfCancellationRequested();

            if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _)
                || !IdentityOwnedFileSystemCleanup.TryCapturePath(root, FileSystemObjectKind.Directory, out var owned)
                || !IdentityOwnedFileSystemCleanup.TryCapturePath(Path.Combine(root, OwnerFile), FileSystemObjectKind.RegularFile, out _))
                continue;

            try
            {
                using FileStream lease = new(Path.Combine(root, OwnerFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                if (File.Exists(Path.Combine(root, WorkspaceCheckAppleContainerRunner.RecoveryMarkerName)) && !await runner.RecoverAsync(root).ConfigureAwait(false))
                    continue;

                if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(root, out var metadata) && metadata == owned.Metadata)
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Another host can own this invocation, or the retained directory may have already been removed.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_retain && !File.Exists(Path.Combine(Root, WorkspaceCheckAppleContainerRunner.RecoveryMarkerName))
                && FileHandleIdentityInterop.TryGetPathMetadataNoFollow(Root, out var entry)
                && entry.Kind == FileSystemObjectKind.Directory && entry.Identity == _identity)
                Directory.Delete(Root, recursive: true);
        }
        finally
        {
            _ownership.Dispose();
        }
    }
}
