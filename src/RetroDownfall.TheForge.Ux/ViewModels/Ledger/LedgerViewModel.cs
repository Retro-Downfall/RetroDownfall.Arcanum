using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Git;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

namespace RetroDownfall.TheForge.Ux.ViewModels.Ledger;

/// <summary>
/// The Ledger — desktop-local Git UI for a user-selected registered workspace / campaign path
/// (or an explicit path pasted by the operator). Does not use MCP <c>execute_command</c>, Sanctum,
/// Ward, or Hearth's shell runner. Deferred: push, pull, reset, rebase.
/// </summary>
public sealed partial class LedgerViewModel : ViewModelBase, IDisposable
{

    public const int ManyFilesCommitThreshold = 10;

    public const string NoPathSelectedMessage =
        "Select a registered workspace or paste a repository path, then refresh. The Ledger never runs git until a path is selected.";

    public const string NotAGitRepositoryMessage =
        "The selected path is not a git repository (no .git directory, or git reported it is outside a work tree).";

    public const string PreCommitDisabledExplanation =
        "No Proving Grounds suites are saved yet. Create a suite under Trial → Proving Grounds to enable the pre-commit suite button.";

    public const string PreCommitEnabledExplanation =
        "Opens Proving Grounds so you can run a saved suite before committing.";

    private static readonly HashSet<string> ForbiddenVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "push",
        "pull",
        "fetch",
        "reset",
        "rebase",
        "merge",
        "cherry-pick",
        "checkout",
        "switch",
        "remote",
    };

    private readonly IGitProcessRunner _git;

    private readonly IWorkspaceExplorerDataSource _workspaces;

    private readonly IConfirmationDialogService _confirmation;

    private readonly INavigationService _navigation;

    private readonly ITrialSuiteStore _trialSuiteStore;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IWhispersService _whispers;

    private CancellationTokenSource? _refreshCts;

    private bool _disposed;

    private bool _workspacesLoaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string _statusText = NoPathSelectedMessage;

    [ObservableProperty]
    private WorkspaceInfo? _selectedWorkspace;

    [ObservableProperty]
    private string _manualRepositoryPath = string.Empty;

    [ObservableProperty]
    private string? _activeRepositoryPath;

    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private string _commitMessage = string.Empty;

    [ObservableProperty]
    private string _diffText = string.Empty;

    [ObservableProperty]
    private GitStatusEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private bool _hasPreCommitSuites;

    [ObservableProperty]
    private string _preCommitExplanation = PreCommitDisabledExplanation;

    public LedgerViewModel(
        IGitProcessRunner git,
        IWorkspaceExplorerDataSource workspaces,
        IConfirmationDialogService confirmation,
        INavigationService navigation,
        ITrialSuiteStore trialSuiteStore,
        FoundryFloorViewModel foundryFloor,
        IWhispersService whispers)
    {

        _git = git;

        _workspaces = workspaces;

        _confirmation = confirmation;

        _navigation = navigation;

        _trialSuiteStore = trialSuiteStore;

        _foundryFloor = foundryFloor;

        _whispers = whispers;

        Title = "The Ledger";

    }

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    public ObservableCollection<GitStatusEntryViewModel> StagedEntries { get; } = [];

    public ObservableCollection<GitStatusEntryViewModel> UnstagedEntries { get; } = [];

    public bool HasRepositoryPath => !string.IsNullOrWhiteSpace(ActiveRepositoryPath);

    public bool CanOpenSelectedInEditor =>
        SelectedEntry is { } entry
        && GitArtifactPathMapper.TryMap(entry.Path, ActiveRepositoryPath, out _, out _, out _);

    public string EmptyState => HasRepositoryPath
        ? "Working tree clean."
        : NoPathSelectedMessage;

    public bool HasNoEntries => StagedEntries.Count == 0 && UnstagedEntries.Count == 0;

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_workspacesLoaded)
        {

            _workspacesLoaded = true;

            _ = LoadWorkspacesAndSuitesAsync(CancellationToken.None);

        }

    }

    partial void OnSelectedWorkspaceChanged(WorkspaceInfo? value)
    {

        if (value is null || string.IsNullOrWhiteSpace(value.Path))
        {

            return;

        }

        ManualRepositoryPath = value.Path;

        ActiveRepositoryPath = value.Path.Trim();

        OnPropertyChanged(nameof(HasRepositoryPath));

        OnPropertyChanged(nameof(EmptyState));

        _ = RefreshAsync(CancellationToken.None);

    }

    partial void OnSelectedEntryChanged(GitStatusEntryViewModel? value)
    {

        OnPropertyChanged(nameof(CanOpenSelectedInEditor));

        if (value is null || string.IsNullOrWhiteSpace(ActiveRepositoryPath))
        {

            DiffText = string.Empty;

            return;

        }

        _ = LoadDiffAsync(value, CancellationToken.None);

    }

    partial void OnActiveRepositoryPathChanged(string? value)
    {

        OnPropertyChanged(nameof(HasRepositoryPath));

        OnPropertyChanged(nameof(EmptyState));

        OnPropertyChanged(nameof(CanOpenSelectedInEditor));

        RefreshCommand.NotifyCanExecuteChanged();

        StageSelectedCommand.NotifyCanExecuteChanged();

        UnstageSelectedCommand.NotifyCanExecuteChanged();

        StageAllCommand.NotifyCanExecuteChanged();

        UnstageAllCommand.NotifyCanExecuteChanged();

        CommitCommand.NotifyCanExecuteChanged();

    }

    [RelayCommand]
    public async Task LoadWorkspacesAsync(CancellationToken cancellationToken)
    {

        await LoadWorkspacesAndSuitesAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public void UseManualPath()
    {

        string path = ManualRepositoryPath.Trim();

        if (string.IsNullOrWhiteSpace(path))
        {

            LastError = "Paste or type a repository path first.";

            StatusText = NoPathSelectedMessage;

            ActiveRepositoryPath = null;

            return;

        }

        ActiveRepositoryPath = path;

        LastError = null;

        _ = RefreshAsync(CancellationToken.None);

    }

    [RelayCommand(CanExecute = nameof(HasRepositoryPath))]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(ActiveRepositoryPath))
        {

            StatusText = NoPathSelectedMessage;

            return;

        }

        CancelRefresh();

        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        CancellationToken token = _refreshCts.Token;

        IsBusy = true;

        LastError = null;

        try
        {

            GitProcessResult branchResult = await RunGitAsync(
                    ActiveRepositoryPath,
                    ["rev-parse", "--abbrev-ref", "HEAD"],
                    token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {

                return;

            }

            if (IsNotAGitRepository(branchResult))
            {

                ApplyNotAGitRepository();

                return;

            }

            if (branchResult.FailedToStart)
            {

                LastError = branchResult.ErrorMessage ?? "Failed to start git.";

                StatusText = "git failed to start.";

                return;

            }

            BranchName = branchResult.ExitCode == 0
                ? branchResult.Stdout.Trim()
                : "(unknown)";

            GitProcessResult statusResult = await RunGitAsync(
                    ActiveRepositoryPath,
                    ["status", "--porcelain=v1"],
                    token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested)
            {

                return;

            }

            if (IsNotAGitRepository(statusResult))
            {

                ApplyNotAGitRepository();

                return;

            }

            if (statusResult.FailedToStart)
            {

                LastError = statusResult.ErrorMessage ?? "Failed to start git.";

                StatusText = "git status failed to start.";

                return;

            }

            if (statusResult.ExitCode is not 0 and not null)
            {

                LastError = FormatGitError("git status", statusResult);

                StatusText = "git status failed.";

                return;

            }

            IReadOnlyList<GitPorcelainEntry> entries = GitPorcelainParser.Parse(statusResult.Stdout);

            StagedEntries.Clear();

            UnstagedEntries.Clear();

            SelectedEntry = null;

            DiffText = string.Empty;

            foreach (GitPorcelainEntry entry in entries)
            {

                if (entry.HasStagedChange)
                {

                    StagedEntries.Add(new GitStatusEntryViewModel(entry, isStagedList: true));

                }

                if (entry.HasUnstagedChange)
                {

                    UnstagedEntries.Add(new GitStatusEntryViewModel(entry, isStagedList: false));

                }

            }

            OnPropertyChanged(nameof(HasNoEntries));

            StatusText = $"Branch {BranchName} · {StagedEntries.Count} staged · {UnstagedEntries.Count} unstaged";

            RefreshCommand.NotifyCanExecuteChanged();

            StageSelectedCommand.NotifyCanExecuteChanged();

            UnstageSelectedCommand.NotifyCanExecuteChanged();

            StageAllCommand.NotifyCanExecuteChanged();

            UnstageAllCommand.NotifyCanExecuteChanged();

            CommitCommand.NotifyCanExecuteChanged();

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            LastError = ex.Message;

            StatusText = "Refresh failed.";

            _foundryFloor.AppendLine($"The Ledger refresh error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand(CanExecute = nameof(HasRepositoryPath))]
    public async Task StageSelectedAsync(CancellationToken cancellationToken)
    {

        List<string> paths = UnstagedEntries
            .Where(static e => e.IsSelected)
            .Select(static e => e.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (paths.Count == 0)
        {

            LastError = "Select one or more unstaged files to stage.";

            return;

        }

        await MutatePathsAsync(["add", "--"], paths, "Staged", cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand(CanExecute = nameof(HasRepositoryPath))]
    public async Task UnstageSelectedAsync(CancellationToken cancellationToken)
    {

        List<string> paths = StagedEntries
            .Where(static e => e.IsSelected)
            .Select(static e => e.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (paths.Count == 0)
        {

            LastError = "Select one or more staged files to unstage.";

            return;

        }

        await MutatePathsAsync(["restore", "--staged", "--"], paths, "Unstaged", cancellationToken)
            .ConfigureAwait(true);

    }

    [RelayCommand(CanExecute = nameof(HasRepositoryPath))]
    public async Task StageAllAsync(CancellationToken cancellationToken)
    {

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Stage all changes?",
                "Stage all changes in the selected repository (git add -A)?",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        await RunMutatingAsync(["add", "-A"], "Staged all changes.", cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand(CanExecute = nameof(HasRepositoryPath))]
    public async Task UnstageAllAsync(CancellationToken cancellationToken)
    {

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Unstage all changes?",
                "Unstage all staged changes in the selected repository (git restore --staged .)?",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        await RunMutatingAsync(["restore", "--staged", "."], "Unstaged all changes.", cancellationToken)
            .ConfigureAwait(true);

    }

    [RelayCommand(CanExecute = nameof(HasRepositoryPath))]
    public async Task CommitAsync(CancellationToken cancellationToken)
    {

        string message = CommitMessage.Trim();

        bool messageInvalid = string.IsNullOrWhiteSpace(message);

        int stagedCount = StagedEntries.Count;

        bool manyFiles = stagedCount >= ManyFilesCommitThreshold;

        if (messageInvalid || manyFiles)
        {

            string reason = messageInvalid
                ? "The commit message is empty or whitespace-only."
                : $"You are about to commit {stagedCount} staged files.";

            bool confirmed = await _confirmation
                .ConfirmAsync(
                    "Confirm commit?",
                    $"{reason}\n\nCommit with the current message in the selected repository?",
                    cancellationToken,
                    confirmIsDefault: false)
                .ConfigureAwait(true);

            if (!confirmed)
            {

                return;

            }

            if (messageInvalid)
            {

                LastError = "Commit cancelled: message is still empty. Enter a commit message.";

                return;

            }

        }

        if (stagedCount == 0)
        {

            LastError = "Nothing staged to commit.";

            return;

        }

        GitProcessResult result = await RunGitAsync(
                ActiveRepositoryPath!,
                ["commit", "-m", message],
                cancellationToken)
            .ConfigureAwait(true);

        if (result.FailedToStart)
        {

            LastError = result.ErrorMessage ?? "Failed to start git commit.";

            return;

        }

        if (result.ExitCode is not 0 and not null)
        {

            LastError = FormatGitError("git commit", result);

            _whispers.Show(WhisperSeverity.Error, "Commit failed.");

            return;

        }

        CommitMessage = string.Empty;

        _whispers.Show(WhisperSeverity.Success, "Committed.");

        StatusText = "Commit succeeded.";

        await RefreshAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public void OpenSelectedInEditor()
    {

        if (SelectedEntry is null
            || !GitArtifactPathMapper.TryMap(
                SelectedEntry.Path,
                ActiveRepositoryPath,
                out DocumentKind kind,
                out string id,
                out string? workspace))
        {

            LastError = "Selected path is not a mapped Spell, Prompt, or CODEX.md.";

            return;

        }

        _navigation.OpenDocument(kind, id, workspace);

        StatusText = $"Opened {kind} in the Workbench.";

    }

    [RelayCommand(CanExecute = nameof(HasPreCommitSuites))]
    public void OpenPreCommitSuite()
    {

        _ = _navigation.OpenOrFocusProvingGrounds();

        StatusText = "Opened Proving Grounds for pre-commit suite.";

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        CancelRefresh();

        GC.SuppressFinalize(this);

    }

    private async Task LoadWorkspacesAndSuitesAsync(CancellationToken cancellationToken)
    {

        try
        {

            DataSourceResult<WorkspaceInfo[]> result = await _workspaces
                .ListWorkspacesAsync(cancellationToken)
                .ConfigureAwait(true);

            Workspaces.Clear();

            if (result.Success && result.Data is { } list)
            {

                foreach (WorkspaceInfo workspace in list)
                {

                    Workspaces.Add(workspace);

                }

            }
            else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {

                LastError = result.ErrorMessage;

            }

            TrialSuiteStoreDocument document = await _trialSuiteStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(true);

            HasPreCommitSuites = document.Suites.Count > 0;

            PreCommitExplanation = HasPreCommitSuites
                ? PreCommitEnabledExplanation
                : PreCommitDisabledExplanation;

            OpenPreCommitSuiteCommand.NotifyCanExecuteChanged();

            if (!HasRepositoryPath)
            {

                StatusText = NoPathSelectedMessage;

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"The Ledger workspace load error: {ex.Message}");

        }

    }

    private async Task LoadDiffAsync(GitStatusEntryViewModel entry, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(ActiveRepositoryPath))
        {

            return;

        }

        try
        {

            List<string> args = entry.IsStagedList
                ? ["diff", "--cached", "--", entry.Path]
                : ["diff", "--", entry.Path];

            if (entry.Entry.IsUntracked)
            {

                DiffText = "(untracked file — no diff against HEAD)";

                return;

            }

            GitProcessResult result = await RunGitAsync(ActiveRepositoryPath, args, cancellationToken)
                .ConfigureAwait(true);

            if (result.FailedToStart)
            {

                DiffText = result.ErrorMessage ?? "Failed to start git diff.";

                return;

            }

            if (result.ExitCode is not 0 and not null)
            {

                DiffText = FormatGitError("git diff", result);

                return;

            }

            DiffText = string.IsNullOrWhiteSpace(result.Stdout)
                ? "(no diff output)"
                : result.Stdout;

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            DiffText = ex.Message;

        }

    }

    private async Task MutatePathsAsync(
        IReadOnlyList<string> prefix,
        IReadOnlyList<string> paths,
        string successVerb,
        CancellationToken cancellationToken)
    {

        List<string> args = [.. prefix, .. paths];

        await RunMutatingAsync(args, $"{successVerb} {paths.Count} path(s).", cancellationToken)
            .ConfigureAwait(true);

    }

    private async Task RunMutatingAsync(
        IReadOnlyList<string> arguments,
        string successMessage,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(ActiveRepositoryPath))
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            GitProcessResult result = await RunGitAsync(ActiveRepositoryPath, arguments, cancellationToken)
                .ConfigureAwait(true);

            if (result.FailedToStart)
            {

                LastError = result.ErrorMessage ?? "Failed to start git.";

                _whispers.Show(WhisperSeverity.Error, "Git command failed to start.");

                return;

            }

            if (result.ExitCode is not 0 and not null)
            {

                LastError = FormatGitError("git", result);

                _whispers.Show(WhisperSeverity.Error, "Git command failed.");

                return;

            }

            _whispers.Show(WhisperSeverity.Success, successMessage);

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

        }
        finally
        {

            IsBusy = false;

        }

    }

    private Task<GitProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {

        EnsureAllowed(arguments);

        return _git.RunAsync(workingDirectory, arguments, cancellationToken);

    }

    private static void EnsureAllowed(IReadOnlyList<string> arguments)
    {

        if (arguments.Count == 0)
        {

            throw new InvalidOperationException("git ArgumentList must not be empty.");

        }

        string verb = arguments[0];

        if (ForbiddenVerbs.Contains(verb))
        {

            throw new InvalidOperationException(
                $"The Ledger does not support 'git {verb}' in this phase (deferred: push/pull/reset/rebase).");

        }

    }

    private static bool IsNotAGitRepository(GitProcessResult result)
    {

        if (result.FailedToStart)
        {

            return false;

        }

        string combined = $"{result.Stderr}\n{result.Stdout}";

        return combined.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("outside repository", StringComparison.OrdinalIgnoreCase);

    }

    private void ApplyNotAGitRepository()
    {

        LastError = NotAGitRepositoryMessage;

        StatusText = "Not a git repository.";

        BranchName = string.Empty;

        StagedEntries.Clear();

        UnstagedEntries.Clear();

        SelectedEntry = null;

        DiffText = string.Empty;

        OnPropertyChanged(nameof(HasNoEntries));

    }

    private static string FormatGitError(string label, GitProcessResult result)
    {

        string detail = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout.Trim()
            : result.Stderr.Trim();

        if (string.IsNullOrWhiteSpace(detail))
        {

            detail = result.ErrorMessage ?? $"exit {result.ExitCode}";

        }

        return $"{label} failed: {detail}";

    }

    private void CancelRefresh()
    {

        try
        {

            _refreshCts?.Cancel();

        }
        catch (ObjectDisposedException)
        {

            // Already disposed.

        }

        _refreshCts?.Dispose();

        _refreshCts = null;

    }

}
