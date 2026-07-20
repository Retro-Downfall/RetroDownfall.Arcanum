using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Native save/open pickers for spell and prompt JSON import-export. Returns <see langword="null"/>
/// when the operator cancels or no main window is available — callers treat cancel as a silent no-op.
/// UI-only — tests fake <see cref="IArtifactFileDialogService"/>.
/// </summary>
public sealed class AvaloniaArtifactFileDialogService : IArtifactFileDialogService
{

    private static readonly FilePickerFileType JsonFileType = new("JSON files")
    {
        Patterns = new[] { "*.json" },
    };

    private static readonly FilePickerFileType CsvFileType = new("CSV files")
    {
        Patterns = new[] { "*.csv" },
    };

    private static readonly FilePickerFileType AnyFileType = new("All files")
    {
        Patterns = new[] { "*.*" },
    };

    public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            IStorageFile? file = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save JSON",

                    SuggestedFileName = suggestedFileName,

                    DefaultExtension = "json",

                    FileTypeChoices = new[] { JsonFileType },

                    SuggestedFileType = JsonFileType,
                }).ConfigureAwait(true);

            if (file is null)
            {

                return null;

            }

            return file.TryGetLocalPath();

        });

    }

    public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            IStorageFile? file = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save CSV",

                    SuggestedFileName = suggestedFileName,

                    DefaultExtension = "csv",

                    FileTypeChoices = new[] { CsvFileType },

                    SuggestedFileType = CsvFileType,
                }).ConfigureAwait(true);

            if (file is null)
            {

                return null;

            }

            return file.TryGetLocalPath();

        });

    }

    public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<IStorageFile> files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Open JSON",

                    AllowMultiple = false,

                    FileTypeFilter = new[] { JsonFileType },
                }).ConfigureAwait(true);

            if (files.Count == 0)
            {

                return null;

            }

            return files[0].TryGetLocalPath();

        });

    }

    public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<IStorageFile> files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Upload file",

                    AllowMultiple = false,

                    FileTypeFilter = new[] { AnyFileType },
                }).ConfigureAwait(true);

            if (files.Count == 0)
            {

                return null;

            }

            return files[0].TryGetLocalPath();

        });

    }

    public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken)
    {

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {

            return Task.FromResult<string?>(null);

        }

        string extension = string.IsNullOrWhiteSpace(defaultExtension) ? "bin" : defaultExtension.TrimStart('.');

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {

            cancellationToken.ThrowIfCancellationRequested();

            IStorageFile? file = await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save file",

                    SuggestedFileName = suggestedFileName,

                    DefaultExtension = extension,

                    FileTypeChoices = new[] { AnyFileType },

                    SuggestedFileType = AnyFileType,
                }).ConfigureAwait(true);

            if (file is null)
            {

                return null;

            }

            return file.TryGetLocalPath();

        });

    }

}
