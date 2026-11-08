using RetroDownfall.Arcanum.Infrastructure.Guild;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed partial class SpellRepositoryTests
{
    [SkippableTheory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("clone")]
    [InlineData("import")]
    [InlineData("create-version")]
    [InlineData("update-version")]
    [InlineData("activate-version")]
    public async Task Workspace_fence_admits_complete_spell_operation_without_claiming_sibling_spells(string operation)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        SpellRepository repository = CreateRepository();

        Assert.True((await repository.CreateAsync(_workspaceRoot,
            new CreateSpellRequest("existing", "old", [], null, null, null, null, [], [], Body: "old body"), CancellationToken.None)).IsSuccess);

        Assert.True((await repository.CreateVersionAsync("existing", _workspaceRoot,
            new CreateSpellVersionRequest("1", "version body"), CancellationToken.None)).IsSuccess);

        string neighbour = Path.Combine(_workspaceRoot, "spells", "neighbour", "SPELL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(neighbour)!);

        await File.WriteAllTextAsync(neighbour, "neighbour bytes");

        SpellPlanFence fence = new(_workspaceRoot);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        Result result = operation switch
        {
            "create" => await repository.CreateAsync(_workspaceRoot,
                new CreateSpellRequest("created", "new", [], null, null, null, null, [], [], Body: "new body"), CancellationToken.None),
            "update" => await repository.UpdateAsync("existing", _workspaceRoot,
                new UpdateSpellRequest("changed", null, null, null, null, null, null, null), CancellationToken.None),
            "delete" => await repository.DeleteAsync("existing", _workspaceRoot, CancellationToken.None),
            "clone" => await repository.CloneAsync("existing", _workspaceRoot, new CloneSpellRequest("cloned"), CancellationToken.None),
            "import" => await repository.ImportAsync(new SpellImportRequest(ImportPayloadNamed("imported"), _workspaceRoot, null), CancellationToken.None),
            "create-version" => await repository.CreateVersionAsync("existing", _workspaceRoot, new CreateSpellVersionRequest("2", "new version"), CancellationToken.None),
            "update-version" => await repository.UpdateVersionAsync("existing", "1", _workspaceRoot, new UpdateSpellVersionRequest("changed version"), CancellationToken.None),
            _ => await repository.ActivateVersionAsync("existing", "1", _workspaceRoot, CancellationToken.None),
        };

        Assert.True(result.IsSuccess, result.Error.ToString());

        Assert.NotEmpty(fence.Plans);

        Assert.All(fence.Plans.SelectMany(scopes => scopes), scope =>
        {
            Assert.NotEqual("spells", scope.RelativePath);

            Assert.False(scope.RelativePath.StartsWith("spells/neighbour", StringComparison.Ordinal));
        });

        Assert.Equal("neighbour bytes", await File.ReadAllTextAsync(neighbour));

        Assert.Empty(fence.RecoveryReasons);

        Assert.Empty(Directory.EnumerateDirectories(_workspaceRoot, ".staging-*", SearchOption.AllDirectories));
    }

    [SkippableFact]
    public async Task Native_spell_rollback_preserves_an_external_edit_and_retains_recovery()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        string spellDirectory = await WriteExportableSpellAsync("external-rollback");

        string path = Path.Combine(spellDirectory, "SPELL.md");

        Directory.CreateDirectory(Path.Combine(spellDirectory, "SPELL.json"));

        SpellRepository repository = CreateRepository(logger: new ExternalEditLogger(path));

        SpellPlanFence fence = new(_workspaceRoot);

        using IDisposable registration = WorkspaceMutationFence.Install(fence);

        Result result = await repository.UpdateAsync("external-rollback", _workspaceRoot,
            new UpdateSpellRequest("changed", null, null, null, null, null, null, null, Version: "3"), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("external edit", await File.ReadAllTextAsync(path));

        Assert.Contains("rollback_incomplete", fence.RecoveryReasons);
    }

    private sealed class ExternalEditLogger(string path) : ILogger<SpellRepository>
    {
        private bool _changed;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error && !_changed)
            {
                _changed = true;

                File.WriteAllText(path, "external edit");
            }
        }
    }

    private sealed class SpellPlanFence(string root) : IWorkspaceMutationFence
    {
        internal List<ImmutableArray<GuildPathScope>> Plans { get; } = [];

        internal List<string> RecoveryReasons { get; } = [];

        public ValueTask<Result<IWorkspaceMutationLease>> AdmitAsync(string workspaceRoot, ImmutableArray<GuildPathScope> scopes, CancellationToken cancellationToken)
        {
            if (workspaceRoot == root)
            {
                Plans.Add(scopes);
            }

            return ValueTask.FromResult<Result<IWorkspaceMutationLease>>(new SpellPlanLease(RecoveryReasons));
        }
    }

    private sealed class SpellPlanLease(List<string>? recoveryReasons = null) : IWorkspaceMutationLease, IDisposable
    {
        public ValueTask<Result> ValidateDirectoryCreationAsync(WorkspaceLeaseDirectoryCreation preparation, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AdoptCreatedDirectoryAsync(WorkspaceLeaseCreatedDirectory proof, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ValidateDirectoryRemovalAsync(WorkspaceLeaseDirectoryRemoval preparation, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> AdoptRemovedDirectoryAsync(WorkspaceLeaseRemovedDirectory proof, bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ValidateAsync(bool rollback, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> RetainRecoveryAsync(string reasonCode, CancellationToken cancellationToken)
        {
            recoveryReasons?.Add(reasonCode);

            return ValueTask.FromResult(Result.Success());
        }

        public IDisposable Enter() => this;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
