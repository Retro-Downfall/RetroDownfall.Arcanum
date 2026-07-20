using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Git;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.Ledger;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class LedgerViewModelTests
{

    [Fact]
    public async Task Refresh_PopulatesStagedAndUnstaged_FromPorcelain()
    {

        FakeGitProcessRunner git = new();

        git.When(
            args => args is ["rev-parse", "--abbrev-ref", "HEAD"],
            GitProcessResult.Completed(0, "main\n", string.Empty, ["rev-parse", "--abbrev-ref", "HEAD"]));

        git.When(
            args => args is ["status", "--porcelain=v1"],
            GitProcessResult.Completed(
                0,
                "M  staged.txt\n M unstaged.txt\n?? new.txt\n",
                string.Empty,
                ["status", "--porcelain=v1"]));

        LedgerViewModel viewModel = NewViewModel(git);

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        Assert.Equal("main", viewModel.BranchName);

        Assert.Single(viewModel.StagedEntries);

        Assert.Equal(2, viewModel.UnstagedEntries.Count);

        Assert.Contains(viewModel.StagedEntries, e => e.Path == "staged.txt");

        Assert.Contains(viewModel.UnstagedEntries, e => e.Path == "unstaged.txt");

        Assert.Contains(viewModel.UnstagedEntries, e => e.Path == "new.txt");

        Assert.DoesNotContain(git.Invocations, inv => inv.Arguments.Any(IsForbiddenVerb));

        viewModel.Dispose();

    }

    [Fact]
    public async Task StageSelected_CallsAddWithArgumentList()
    {

        FakeGitProcessRunner git = new();

        ConfigureCleanRefresh(git, porcelain: " M a.txt\n M b.txt\n");

        LedgerViewModel viewModel = NewViewModel(git);

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        viewModel.UnstagedEntries.First(e => e.Path == "a.txt").IsSelected = true;

        git.When(
            args => args.Count >= 3 && args[0] == "add",
            GitProcessResult.Completed(0, string.Empty, string.Empty, ["add", "--", "a.txt"]));

        ConfigureCleanRefresh(git, porcelain: "M  a.txt\n M b.txt\n");

        await viewModel.StageSelectedCommand.ExecuteAsync(null);

        Assert.Contains(
            git.Invocations,
            inv => inv.Arguments.SequenceEqual(new[] { "add", "--", "a.txt" }));

        Assert.DoesNotContain(git.Invocations, inv => inv.Arguments.Any(IsForbiddenVerb));

        viewModel.Dispose();

    }

    [Fact]
    public async Task UnstageSelected_CallsRestoreStaged()
    {

        FakeGitProcessRunner git = new();

        ConfigureCleanRefresh(git, porcelain: "M  a.txt\n");

        LedgerViewModel viewModel = NewViewModel(git);

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        viewModel.StagedEntries.First().IsSelected = true;

        git.When(
            args => args.Count >= 3 && args[0] == "restore",
            GitProcessResult.Completed(0, string.Empty, string.Empty, ["restore", "--staged", "--", "a.txt"]));

        ConfigureCleanRefresh(git, porcelain: " M a.txt\n");

        await viewModel.UnstageSelectedCommand.ExecuteAsync(null);

        Assert.Contains(
            git.Invocations,
            inv => inv.Arguments.SequenceEqual(new[] { "restore", "--staged", "--", "a.txt" }));

        viewModel.Dispose();

    }

    [Fact]
    public async Task StageAll_Confirms_ThenAddDashA()
    {

        FakeGitProcessRunner git = new();

        ConfigureCleanRefresh(git, porcelain: " M a.txt\n");

        ControllableConfirmation confirmation = new(accept: true);

        LedgerViewModel viewModel = NewViewModel(git, confirmation: confirmation);

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        git.When(
            args => args is ["add", "-A"],
            GitProcessResult.Completed(0, string.Empty, string.Empty, ["add", "-A"]));

        ConfigureCleanRefresh(git, porcelain: "M  a.txt\n");

        await viewModel.StageAllCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.CallCount);

        Assert.Contains(git.Invocations, inv => inv.Arguments.SequenceEqual(new[] { "add", "-A" }));

        viewModel.Dispose();

    }

    [Fact]
    public async Task StageAll_Cancel_IsNoOp()
    {

        FakeGitProcessRunner git = new();

        ConfigureCleanRefresh(git, porcelain: " M a.txt\n");

        ControllableConfirmation confirmation = new(accept: false);

        LedgerViewModel viewModel = NewViewModel(git, confirmation: confirmation);

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        int before = git.Invocations.Count;

        await viewModel.StageAllCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.CallCount);

        Assert.Equal(before, git.Invocations.Count);

        Assert.DoesNotContain(git.Invocations, inv => inv.Arguments.SequenceEqual(new[] { "add", "-A" }));

        viewModel.Dispose();

    }

    [Fact]
    public async Task Commit_EmptyMessage_Confirms_AndDoesNotInvokeCommit()
    {

        FakeGitProcessRunner git = new();

        ConfigureCleanRefresh(git, porcelain: "M  a.txt\n");

        ControllableConfirmation confirmation = new(accept: true);

        LedgerViewModel viewModel = NewViewModel(git, confirmation: confirmation);

        viewModel.CommitMessage = "   ";

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.CallCount);

        Assert.DoesNotContain(git.Invocations, inv => inv.Arguments.Count > 0 && inv.Arguments[0] == "commit");

        viewModel.Dispose();

    }

    [Fact]
    public async Task Commit_ManyFiles_ConfirmsThenCommits()
    {

        FakeGitProcessRunner git = new();

        string porcelain = string.Join(
            '\n',
            Enumerable.Range(0, LedgerViewModel.ManyFilesCommitThreshold)
                .Select(i => $"M  file{i}.txt")) + "\n";

        ConfigureCleanRefresh(git, porcelain);

        ControllableConfirmation confirmation = new(accept: true);

        LedgerViewModel viewModel = NewViewModel(git, confirmation: confirmation);

        viewModel.CommitMessage = "batch";

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        Assert.True(viewModel.StagedEntries.Count >= LedgerViewModel.ManyFilesCommitThreshold);

        git.When(
            args => args is ["commit", "-m", "batch"],
            GitProcessResult.Completed(0, string.Empty, string.Empty, ["commit", "-m", "batch"]));

        ConfigureCleanRefresh(git, porcelain: string.Empty);

        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.CallCount);

        Assert.Contains(
            git.Invocations,
            inv => inv.Arguments.SequenceEqual(new[] { "commit", "-m", "batch" }));

        viewModel.Dispose();

    }

    [Fact]
    public async Task Refresh_NotAGitRepository_SurfacesClearError()
    {

        FakeGitProcessRunner git = new();

        git.When(
            _ => true,
            GitProcessResult.Completed(
                128,
                string.Empty,
                "fatal: not a git repository (or any of the parent directories): .git\n",
                ["rev-parse", "--abbrev-ref", "HEAD"]));

        LedgerViewModel viewModel = NewViewModel(git);

        viewModel.ManualRepositoryPath = "/not-git";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        Assert.Equal(LedgerViewModel.NotAGitRepositoryMessage, viewModel.LastError);

        viewModel.Dispose();

    }

    [Fact]
    public void Dispose_IsIdempotent()
    {

        LedgerViewModel viewModel = NewViewModel(new FakeGitProcessRunner());

        viewModel.Dispose();

        viewModel.Dispose();

    }

    [Fact]
    public async Task NeverInvokesPushOrPull()
    {

        FakeGitProcessRunner git = new();

        ConfigureCleanRefresh(git, porcelain: " M a.txt\n");

        ControllableConfirmation confirmation = new(accept: true);

        LedgerViewModel viewModel = NewViewModel(git, confirmation: confirmation);

        viewModel.CommitMessage = "msg";

        viewModel.ManualRepositoryPath = "/repo";

        viewModel.UseManualPath();

        await WaitForIdleAsync(viewModel);

        viewModel.UnstagedEntries.First().IsSelected = true;

        git.When(args => args[0] == "add", GitProcessResult.Completed(0, "", "", ["add"]));

        ConfigureCleanRefresh(git, porcelain: "M  a.txt\n");

        await viewModel.StageSelectedCommand.ExecuteAsync(null);

        await viewModel.StageAllCommand.ExecuteAsync(null);

        git.When(args => args[0] == "restore", GitProcessResult.Completed(0, "", "", ["restore"]));

        ConfigureCleanRefresh(git, porcelain: string.Empty);

        await viewModel.UnstageAllCommand.ExecuteAsync(null);

        Assert.DoesNotContain(git.Invocations, inv => inv.Arguments.Any(IsForbiddenVerb));

        viewModel.Dispose();

    }

    [Fact]
    public async Task PreCommitSuite_DisabledWithoutSuites()
    {

        LedgerViewModel viewModel = NewViewModel(new FakeGitProcessRunner(), suites: []);

        viewModel.IsVisible = true;

        await WaitForIdleAsync(viewModel);

        Assert.False(viewModel.HasPreCommitSuites);

        Assert.False(viewModel.OpenPreCommitSuiteCommand.CanExecute(null));

        Assert.Equal(LedgerViewModel.PreCommitDisabledExplanation, viewModel.PreCommitExplanation);

        viewModel.Dispose();

    }

    [Fact]
    public async Task PreCommitSuite_EnabledWhenSuitesExist()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        TrialSuiteRecord suite = new(Guid.NewGuid(), "Suite", null, now, now, [], []);

        LedgerViewModel viewModel = NewViewModel(new FakeGitProcessRunner(), suites: [suite]);

        viewModel.IsVisible = true;

        await WaitForIdleAsync(viewModel);

        Assert.True(viewModel.HasPreCommitSuites);

        Assert.True(viewModel.OpenPreCommitSuiteCommand.CanExecute(null));

        viewModel.Dispose();

    }

    private static void ConfigureCleanRefresh(FakeGitProcessRunner git, string porcelain)
    {

        git.When(
            args => args is ["rev-parse", "--abbrev-ref", "HEAD"],
            GitProcessResult.Completed(0, "main\n", string.Empty, ["rev-parse", "--abbrev-ref", "HEAD"]));

        git.When(
            args => args is ["status", "--porcelain=v1"],
            GitProcessResult.Completed(0, porcelain, string.Empty, ["status", "--porcelain=v1"]));

    }

    private static async Task WaitForIdleAsync(LedgerViewModel viewModel)
    {

        for (int i = 0; i < 50 && viewModel.IsBusy; i++)
        {

            await Task.Delay(20);

        }

        // Allow fire-and-forget refresh/load to settle.
        await Task.Delay(50);

    }

    private static bool IsForbiddenVerb(string arg) =>
        arg is "push" or "pull" or "fetch" or "reset" or "rebase" or "merge";

    private static LedgerViewModel NewViewModel(
        FakeGitProcessRunner git,
        ControllableConfirmation? confirmation = null,
        IReadOnlyList<TrialSuiteRecord>? suites = null,
        NavigationService? navigation = null)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        InMemoryTrialSuiteStore store = new();

        if (suites is { Count: > 0 })
        {

            store.SaveAsync(new TrialSuiteStoreDocument(1, now, now, suites.ToArray())).GetAwaiter().GetResult();

        }

        return new LedgerViewModel(
            git,
            new FakeWorkspaceListDataSource(),
            confirmation ?? new ControllableConfirmation(accept: false),
            navigation ?? new NavigationService(),
            store,
            new FoundryFloorViewModel(new NullLogService()),
            new FakeWhispersService());

    }

    private sealed class FakeGitProcessRunner : IGitProcessRunner
    {

        private readonly List<(Func<IReadOnlyList<string>, bool> Match, GitProcessResult Result)> _handlers = [];

        public List<(string WorkingDirectory, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public void When(Func<IReadOnlyList<string>, bool> match, GitProcessResult result) =>
            _handlers.Insert(0, (match, result));

        public Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {

            Invocations.Add((workingDirectory, arguments.ToArray()));

            foreach ((Func<IReadOnlyList<string>, bool> match, GitProcessResult result) in _handlers)
            {

                if (match(arguments))
                {

                    return Task.FromResult(result with { Arguments = arguments.ToArray() });

                }

            }

            return Task.FromResult(
                GitProcessResult.Completed(0, string.Empty, string.Empty, arguments.ToArray()));

        }

    }

    private sealed class ControllableConfirmation(bool accept) : IConfirmationDialogService
    {

        public int CallCount { get; private set; }

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken,
            bool confirmIsDefault = true)
        {

            CallCount++;

            return Task.FromResult(accept);

        }

    }

    private sealed class FakeWorkspaceListDataSource : IWorkspaceExplorerDataSource
    {

        public Task<DataSourceResult<WorkspaceInfo[]>> ListWorkspacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(
                new DataSourceResult<WorkspaceInfo[]>(
                    [
                        new WorkspaceInfo("ws1", "Demo", "/repo", WorkspaceType.Custom, DateTimeOffset.UtcNow),
                    ],
                    true,
                    null,
                    null));

        public Task<DataSourceResult<FileListResult>> ListFilesAsync(
            string workspaceId,
            string? relativePath,
            bool? recursive,
            string? searchPattern,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileListResult>(null, true, null, null));

        public Task<DataSourceResult<FileEntry>> GetFileInfoAsync(
            string workspaceId,
            string? relativePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileEntry>(null, true, null, null));

        public Task<DataSourceResult<FileReadResult>> GetFileContentsAsync(
            string workspaceId,
            string relativePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileReadResult>(null, true, null, null));

        public Task<DataSourceResult<bool>> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

        public Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(
            string workspaceId,
            WorkspaceSemanticSearchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceSearchResult[]>([], true, null, null));

        public Task<DataSourceResult<FileWriteResult>> WriteFileContentsAsync(
            string workspaceId,
            string relativePath,
            string content,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileWriteResult>(null, true, null, null));

        public Task<DataSourceResult<TextBlockReplaceResult>> ReplaceTextBlockAsync(
            string workspaceId,
            string relativePath,
            string oldString,
            string newString,
            int? expectedReplacements,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<TextBlockReplaceResult>(null, true, null, null));

        public Task<DataSourceResult<FileDeleteResult>> DeleteFileAsync(
            string workspaceId,
            string relativePath,
            bool? recursive,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileDeleteResult>(null, true, null, null));

        public Task<DataSourceResult<DirectoryCreateResult>> CreateDirectoryAsync(
            string workspaceId,
            string relativePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<DirectoryCreateResult>(null, true, null, null));

    }

}
