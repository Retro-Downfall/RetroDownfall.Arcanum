using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Services.Terminal;

namespace RetroDownfall.TheForge.Ux.ViewModels.Hearth;

/// <summary>
/// The Hearth — dockable local shell command runner. Does not call Arcanum APIs.
/// Initial Git integration surface: run <c>git status</c>, <c>git diff</c>, etc. manually.
/// </summary>
public sealed partial class HearthViewModel : ViewModelBase, IDisposable
{

    public const int MaxLines = 5000;

    private readonly ITerminalCommandRunner _runner;

    private readonly object _lineGate = new();

    private readonly List<HearthLineViewModel> _pendingLines = [];

    private CancellationTokenSource? _runCts;

    private bool _disposed;

    private bool _flushScheduled;

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    [ObservableProperty]
    private string _commandText = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    public HearthViewModel(ITerminalCommandRunner runner)
    {

        _runner = runner;

        Title = "The Hearth";

        CurrentDirectory = ResolveUserHome();

        Lines.CollectionChanged += OnLinesCollectionChanged;

    }

    public ObservableCollection<HearthLineViewModel> Lines { get; } = [];

    public bool HasNoLines => Lines.Count == 0;

    public string PromptText
    {

        get
        {

            string leaf = Path.GetFileName(CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrEmpty(leaf))
            {

                leaf = CurrentDirectory;

            }

            string marker = OperatingSystem.IsWindows() ? ">" : "$";

            return $"{leaf} {marker}";

        }

    }

    public string EmptyState => "Type a command and press Enter to light The Hearth.";

    partial void OnCurrentDirectoryChanged(string value)
    {

        OnPropertyChanged(nameof(PromptText));

    }

    partial void OnIsRunningChanged(bool value)
    {

        RunCommand.NotifyCanExecuteChanged();

        StopCommand.NotifyCanExecuteChanged();

    }

    partial void OnCommandTextChanged(string value)
    {

        RunCommand.NotifyCanExecuteChanged();

    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {

        if (_disposed || IsRunning)
        {

            return;

        }

        string command = CommandText.Trim();

        if (string.IsNullOrEmpty(command))
        {

            return;

        }

        CommandText = string.Empty;

        AppendLine(new HearthLineViewModel(command, HearthLineKind.Command));

        if (TryHandleCd(command))
        {

            return;

        }

        IsRunning = true;

        CancellationTokenSource cts = new();

        _runCts = cts;

        try
        {

            Progress<TerminalOutputEvent> progress = new(OnTerminalOutput);

            TerminalCommandResult result = await _runner
                .RunAsync(command, CurrentDirectory, progress, cts.Token)
                .ConfigureAwait(true);

            FlushPendingLines();

            if (result.Cancelled)
            {

                AppendLine(new HearthLineViewModel("Command cancelled.", HearthLineKind.System));

            }
            else if (result.FailedToStart)
            {

                string message = result.ErrorMessage ?? "Failed to start command.";

                AppendLine(new HearthLineViewModel(message, HearthLineKind.StandardError));

            }
            else
            {

                AppendLine(new HearthLineViewModel(
                    $"Process exited with code {result.ExitCode ?? -1}.",
                    HearthLineKind.System));

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            FlushPendingLines();

            AppendLine(new HearthLineViewModel(ex.Message, HearthLineKind.StandardError));

        }
        catch (OperationCanceledException)
        {

            FlushPendingLines();

            AppendLine(new HearthLineViewModel("Command cancelled.", HearthLineKind.System));

        }
        finally
        {

            if (ReferenceEquals(_runCts, cts))
            {

                _runCts = null;

            }

            cts.Dispose();

            IsRunning = false;

        }

    }

    private bool CanRun() => !_disposed && !IsRunning && !string.IsNullOrWhiteSpace(CommandText);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {

        _runCts?.Cancel();

    }

    private bool CanStop() => !_disposed && IsRunning;

    [RelayCommand]
    private void Clear()
    {

        lock (_lineGate)
        {

            _pendingLines.Clear();

        }

        Lines.Clear();

        OnPropertyChanged(nameof(HasNoLines));

    }

    [RelayCommand]
    private void ResetWorkingDirectory()
    {

        CurrentDirectory = ResolveUserHome();

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        try
        {

            _runCts?.Cancel();

        }
        catch (ObjectDisposedException)
        {

        }

        CancellationTokenSource? cts = _runCts;

        _runCts = null;

        cts?.Dispose();

        Lines.CollectionChanged -= OnLinesCollectionChanged;

        RunCommand.NotifyCanExecuteChanged();

        StopCommand.NotifyCanExecuteChanged();

        GC.SuppressFinalize(this);

    }

    private void OnTerminalOutput(TerminalOutputEvent output)
    {

        HearthLineKind kind = output.Kind == TerminalOutputKind.StandardError
            ? HearthLineKind.StandardError
            : HearthLineKind.StandardOutput;

        EnqueueLine(new HearthLineViewModel(output.Text, kind));

    }

    private void EnqueueLine(HearthLineViewModel line)
    {

        lock (_lineGate)
        {

            _pendingLines.Add(line);

            if (_flushScheduled)
            {

                return;

            }

            _flushScheduled = true;

        }

        ScheduleFlush();

    }

    private void ScheduleFlush()
    {

        if (TryGetUiDispatcher(out Dispatcher? dispatcher) && dispatcher is not null)
        {

            if (dispatcher.CheckAccess())
            {

                FlushPendingLines();

                return;

            }

            dispatcher.Post(FlushPendingLines, DispatcherPriority.Background);

            return;

        }

        FlushPendingLines();

    }

    private void FlushPendingLines()
    {

        List<HearthLineViewModel> batch;

        lock (_lineGate)
        {

            _flushScheduled = false;

            if (_pendingLines.Count == 0)
            {

                return;

            }

            batch = [.. _pendingLines];

            _pendingLines.Clear();

        }

        void Apply()
        {

            foreach (HearthLineViewModel line in batch)
            {

                Lines.Add(line);

            }

            EnforceLineCap();

            OnPropertyChanged(nameof(HasNoLines));

        }

        if (TryGetUiDispatcher(out Dispatcher? dispatcher) && dispatcher is not null && !dispatcher.CheckAccess())
        {

            dispatcher.Post(Apply, DispatcherPriority.Background);

            return;

        }

        Apply();

    }

    private void AppendLine(HearthLineViewModel line)
    {

        void Apply()
        {

            Lines.Add(line);

            EnforceLineCap();

            OnPropertyChanged(nameof(HasNoLines));

        }

        if (TryGetUiDispatcher(out Dispatcher? dispatcher) && dispatcher is not null && !dispatcher.CheckAccess())
        {

            dispatcher.Post(Apply, DispatcherPriority.Background);

            return;

        }

        Apply();

    }

    private void EnforceLineCap()
    {

        while (Lines.Count > MaxLines)
        {

            Lines.RemoveAt(0);

        }

    }

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        OnPropertyChanged(nameof(HasNoLines));

    }

    private bool TryHandleCd(string command)
    {

        if (!TryParseCd(command, out string? pathArg))
        {

            return false;

        }

        string home = ResolveUserHome();

        string target;

        if (string.IsNullOrEmpty(pathArg))
        {

            target = home;

        }
        else if (pathArg == "~")
        {

            target = home;

        }
        else if (pathArg.StartsWith("~/", StringComparison.Ordinal) || pathArg.StartsWith("~\\", StringComparison.Ordinal))
        {

            target = Path.Combine(home, pathArg[2..]);

        }
        else
        {

            target = Path.IsPathRooted(pathArg)
                ? pathArg
                : Path.GetFullPath(Path.Combine(CurrentDirectory, pathArg));

        }

        try
        {

            target = Path.GetFullPath(target);

        }
        catch (Exception ex)
        {

            AppendLine(new HearthLineViewModel($"cd: {ex.Message}", HearthLineKind.StandardError));

            return true;

        }

        if (!Directory.Exists(target))
        {

            AppendLine(new HearthLineViewModel($"cd: no such directory: {pathArg}", HearthLineKind.StandardError));

            return true;

        }

        CurrentDirectory = target;

        return true;

    }

    /// <summary>
    /// Parses a built-in <c>cd</c> command. Supports no-arg, quoted paths, <c>~</c>, and simple paths.
    /// Does not expand env vars, <c>~user</c>, globs, or escaped quotes.
    /// </summary>
    internal static bool TryParseCd(string command, out string? pathArg)
    {

        pathArg = null;

        ReadOnlySpan<char> span = command.AsSpan().Trim();

        if (span.Length < 2 || !span.StartsWith("cd", StringComparison.OrdinalIgnoreCase))
        {

            return false;

        }

        if (span.Length == 2)
        {

            pathArg = null;

            return true;

        }

        if (!char.IsWhiteSpace(span[2]))
        {

            return false;

        }

        ReadOnlySpan<char> rest = span[2..].Trim();

        if (rest.IsEmpty)
        {

            pathArg = null;

            return true;

        }

        if (rest[0] is '"' or '\'')
        {

            char quote = rest[0];

            int end = rest[1..].IndexOf(quote);

            if (end < 0)
            {

                pathArg = rest[1..].ToString();

                return true;

            }

            pathArg = rest.Slice(1, end).ToString();

            return true;

        }

        pathArg = rest.ToString();

        return true;

    }

    private static string ResolveUserHome()
    {

        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrWhiteSpace(home))
        {

            return home;

        }

        return Environment.CurrentDirectory;

    }

    private static bool TryGetUiDispatcher(out Dispatcher? dispatcher)
    {

        try
        {

            if (Avalonia.Application.Current is null)
            {

                dispatcher = null;

                return false;

            }

            dispatcher = Dispatcher.UIThread;

            return true;

        }
        catch (Exception)
        {

            dispatcher = null;

            return false;

        }

    }

}
