using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.TheForge.Core.IO;

internal readonly record struct TheForgeFileVersion(
    bool Exists,
    string? ContentSha256);

internal sealed record TheForgeVersionedJsonRead<T>(
    T? Value,
    TheForgeFileVersion Version,
    JsonException? JsonError);

/// <summary>
/// Reads a JSON document and its content version from the same byte snapshot. Store writers use the
/// version as an optimistic precondition after mutation admission, so a reset or an external writer
/// cannot be silently replaced by work prepared against an older installation generation.
/// </summary>
internal static class TheForgeVersionedJsonFile
{

    public static async Task<TheForgeVersionedJsonRead<T>> ReadAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        byte[]? bytes = await ReadBytesOrNullAsync(path, cancellationToken).ConfigureAwait(false);

        if (bytes is null)
        {

            return new TheForgeVersionedJsonRead<T>(
                default,
                new TheForgeFileVersion(false, null),
                null);

        }

        TheForgeFileVersion version = new(
            true,
            Convert.ToHexString(SHA256.HashData(bytes)));

        try
        {

            T? value = JsonSerializer.Deserialize(bytes, typeInfo);

            return new TheForgeVersionedJsonRead<T>(value, version, null);

        }
        catch (JsonException ex)
        {

            return new TheForgeVersionedJsonRead<T>(default, version, ex);

        }

    }

    public static async Task<TheForgeFileVersion> CaptureVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {

        byte[]? bytes = await ReadBytesOrNullAsync(path, cancellationToken).ConfigureAwait(false);

        return bytes is null
            ? new TheForgeFileVersion(false, null)
            : new TheForgeFileVersion(true, Convert.ToHexString(SHA256.HashData(bytes)));

    }

    public static async Task EnsureUnchangedAsync(
        string path,
        TheForgeFileVersion expected,
        CancellationToken cancellationToken)
    {

        TheForgeFileVersion current = await CaptureVersionAsync(path, cancellationToken).ConfigureAwait(false);

        if (current != expected)
        {

            throw new TheForgeStoreChangedException(path);

        }

    }

    private static async Task<byte[]?> ReadBytesOrNullAsync(
        string path,
        CancellationToken cancellationToken)
    {

        try
        {

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        }
        catch (FileNotFoundException)
        {

            return null;

        }
        catch (DirectoryNotFoundException)
        {

            return null;

        }

    }

}

/// <summary>A local store changed after the caller's mutation began and must be reloaded.</summary>
public sealed class TheForgeStoreChangedException(string path) : InvalidOperationException(
    $"Data.Conflict: The local store changed before the mutation could be committed: {path}")
{
}
