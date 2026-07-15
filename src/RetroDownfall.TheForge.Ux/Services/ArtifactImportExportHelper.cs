using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Shared open/save JSON helpers for spell, prompt, and campaign import/export. Cancelled file
/// dialogs return <c>null</c> / no-op; callers own Whispers (short) and Floor (detailed) messaging.
/// </summary>
public static class ArtifactImportExportHelper
{

    public static Task<string?> PickSavePathOrNullAsync(
        IArtifactFileDialogService fileDialog,
        string suggestedFileName,
        CancellationToken cancellationToken) =>
        fileDialog.PickSaveJsonPathAsync(suggestedFileName, cancellationToken);

    public static Task<string?> PickOpenPathOrNullAsync(
        IArtifactFileDialogService fileDialog,
        CancellationToken cancellationToken) =>
        fileDialog.PickOpenJsonPathAsync(cancellationToken);

    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        string json = JsonSerializer.Serialize(value, typeInfo);

        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(true);

    }

    public static async Task<(T? Value, string? Error)> ReadJsonAsync<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        try
        {

            string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);

            T? value = JsonSerializer.Deserialize(json, typeInfo);

            if (value is null)
            {

                return (default, "The JSON file could not be deserialized.");

            }

            return (value, null);

        }
        catch (Exception ex)
        {

            return (default, ex.Message);

        }

    }

}
