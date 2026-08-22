using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.TheForge.Core.Services;
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

    /// <summary>
    /// Serializes and writes <paramref name="value"/>, returning <see langword="null"/> on success or
    /// the failure message otherwise — the same contract as <see cref="ReadJsonAsync{T}"/>, so a
    /// caller cannot accidentally leave an export write unguarded. Cancellation is not a failure and
    /// still propagates, because callers report a cancelled export differently from a failed one.
    /// </summary>
    public static async Task<string?> WriteJsonAsync<T>(
        ITheForgeLocalMutationRunner mutationRunner,
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        try
        {

            await mutationRunner
                .RunAsync(
                    path,
                    admittedCancellationToken => WriteJsonAlreadyAdmittedAsync(
                        path,
                        value,
                        typeInfo,
                        admittedCancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);

            return null;

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            return ex.Message;

        }

    }

    internal static Task WriteJsonAlreadyAdmittedAsync<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        string json = JsonSerializer.Serialize(value, typeInfo);

        return File.WriteAllTextAsync(path, json, cancellationToken);

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
