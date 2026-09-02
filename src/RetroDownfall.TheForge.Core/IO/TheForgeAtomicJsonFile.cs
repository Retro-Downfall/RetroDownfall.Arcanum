using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.TheForge.Core.IO;

namespace RetroDownfall.TheForge.Core.IO;

/// <summary>Atomic temp-file replace writer with owner-only permissions for The Forge local JSON stores.</summary>
internal static class TheForgeAtomicJsonFile
{

    public static async Task WriteAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ArgumentNullException.ThrowIfNull(value);

        ArgumentNullException.ThrowIfNull(typeInfo);

        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {

            Directory.CreateDirectory(directory);

            TheForgeOwnerOnlyPermissions.TrySetDirectory(directory);

        }

        string tempPath = path + $".{Guid.NewGuid():N}.tmp";

        try
        {

            // See TheForgeSettingsStore.SaveCoreAsync: UnixCreateMode makes owner-only the file's
            // mode at creation instead of a chmod applied after the write, closing the window where
            // create-then-chmod leaves the temp file group/other-readable for the write's duration.
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            };

            if (!OperatingSystem.IsWindows())
            {

                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            }

            await using (FileStream stream = new(tempPath, options))
            {

                await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            }

            TheForgeOwnerOnlyPermissions.TrySetFile(tempPath);

            File.Move(tempPath, path, overwrite: true);

            TheForgeOwnerOnlyPermissions.TrySetFile(path);

        }
        catch
        {

            TryDelete(tempPath);

            throw;

        }

    }

    public static async Task<T?> ReadAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        if (!File.Exists(path))
        {

            return default;

        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);

    }

    private static void TryDelete(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (IOException)
        {
        }

    }

}
