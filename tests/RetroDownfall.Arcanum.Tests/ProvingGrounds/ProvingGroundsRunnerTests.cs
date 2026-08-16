using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.ProvingGrounds;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class ProvingGroundsRunnerTests
{

    [Fact]
    public async Task RunAsync_NullTrial_FailsValidation()
    {
        ProvingGroundsRunner runner = CreateRunner(new FakeIntelligenceProvider());

        Result<TrialResult> result = await runner.RunAsync(null!, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("ProvingGrounds.InvalidTrial", result.Error.Code);
    }

    [Fact]
    public async Task RunAsync_ApprenticeGoal_BuildsPlanPrompt()
    {
        FakeIntelligenceProvider intelligence = new() { NextText = "plan output" };

        ProvingGroundsRunner runner = CreateRunner(intelligence);

        Trial trial = new(
            TargetKind: TrialTargetKind.ApprenticeGoal,
            Target: "Organize {{topic}}",
            Inquisitors: [new RegexInquisitor("plan output", ShouldMatch: true)],
            Variables: new Dictionary<string, string> { ["topic"] = "codex" },
            Name: "Trial");

        Result<TrialResult> result = await runner.RunAsync(trial, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value!.Passed);

        Assert.Equal("plan output", result.Value.Output);

        Assert.Contains("Organize codex", intelligence.LastPrompt!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_InferenceFailure_Propagates()
    {
        FakeIntelligenceProvider intelligence = new()
        {
            NextFailure = new Error("Hub.Error", "boom"),
        };

        ProvingGroundsRunner runner = CreateRunner(intelligence);

        Trial trial = new(
            TargetKind: TrialTargetKind.ApprenticeGoal,
            Target: "goal",
            Inquisitors: [new RegexInquisitor("x")]);

        Result<TrialResult> result = await runner.RunAsync(trial, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("ProvingGrounds.InferenceFailed", result.Error.Code);
    }

    [Fact]
    public async Task RunAsync_WorkspaceOutsideAllowlist_Fails()
    {
        FakeIntelligenceProvider intelligence = new();

        ProvingGroundsRunner runner = CreateRunner(
            intelligence,
            allowedWorkspaceRoots: [Path.GetTempPath()]);

        Trial trial = new(
            TargetKind: TrialTargetKind.ApprenticeGoal,
            Target: "goal",
            Inquisitors: [new RegexInquisitor("x")],
            Workspace: "/tmp/outside-" + Guid.NewGuid().ToString("N"));

        Result<TrialResult> result = await runner.RunAsync(trial, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("ProvingGrounds.WorkspaceNotAllowed", result.Error.Code);
    }

    private static ProvingGroundsRunner CreateRunner(
        IArcanumIntelligenceProvider intelligence,
        string[]? allowedWorkspaceRoots = null)
    {
        ArcanumSettings settings = new()
        {
            Security = new SecuritySettings
            {
                SpellWorkspaceRoots =
                    allowedWorkspaceRoots ?? [Path.GetTempPath(), System.Environment.CurrentDirectory],
            },
        };

        SpellWorkspaceResolver workspaceResolver = new(
            new FakeHostWorkspaceContext(null),
            Microsoft.Extensions.Options.Options.Create(settings));

        return new ProvingGroundsRunner(
            new FakeSpellRepository(),
            new FakePromptRepository(),
            intelligence,
            new ProvingGroundsArbiter(intelligence),
            new PromptRenderer(new FakeTokenCounter(), new TestOptionsMonitor<ArcanumSettings>(settings)),
            workspaceResolver,
            new TestOptionsSnapshot<ArcanumSettings>(settings));
    }

    private sealed class FakeHostWorkspaceContext : IHostWorkspaceContext
    {

        public FakeHostWorkspaceContext(string? path)
        {

            WorkspacePath = path;

        }

        public string? WorkspacePath { get; }

    }

    private sealed class FakeIntelligenceProvider : IArcanumIntelligenceProvider
    {

        public string NextText { get; init; } = "YES";

        public Error? NextFailure { get; init; }

        public string? LastPrompt { get; private set; }

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            LastPrompt = request.Prompt;

            if (NextFailure is Error failure)
            {
                return Task.FromResult(Result<PromptTurnResult>.Failure(failure));
            }

            return Task.FromResult(
                Result<PromptTurnResult>.Success(new PromptTurnResult(NextText, null)));
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            yield break;
        }

    }

    private sealed class FakeSpellRepository : ISpellRepository
    {

        public Task<SpellDetail?> GetAsync(string name, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult<SpellDetail?>(null);

        public Task<SpellSummary[]> ListAsync(string? workingDirectory, CancellationToken ct) =>
            Task.FromResult(Array.Empty<SpellSummary>());

        public Task<Result> CreateAsync(string? workingDirectory, CreateSpellRequest request, CancellationToken ct) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(string name, string? workingDirectory, UpdateSpellRequest request, CancellationToken ct) =>
            Task.FromResult(Result.Success());

        public Task<Result> DeleteAsync(string name, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult(Result.Success());

        public Task<SpellSummary[]> SearchAsync(SpellSearchQuery query, CancellationToken ct) =>
            Task.FromResult(Array.Empty<SpellSummary>());

        public Task<SpellValidationResultDto> ValidateAsync(string name, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult(new SpellValidationResultDto(true, [], []));

        public Task<SpellExportDto?> ExportAsync(string name, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult<SpellExportDto?>(null);

        public Task<Result<SpellSummary>> ImportAsync(SpellImportRequest request, CancellationToken ct) =>
            Task.FromResult(Result<SpellSummary>.Failure(new Error("Spell.NotFound", "missing")));

        public Task<Result<SpellSummary>> CloneAsync(string name, string? workingDirectory, CloneSpellRequest request, CancellationToken ct) =>
            Task.FromResult(Result<SpellSummary>.Failure(new Error("Spell.NotFound", "missing")));

        public Task<Result<SpellVersionDto>> CreateVersionAsync(string name, string? workingDirectory, CreateSpellVersionRequest request, CancellationToken ct) =>
            Task.FromResult(Result<SpellVersionDto>.Failure(new Error("Spell.NotFound", "missing")));

        public Task<Result<SpellVersionDto>> UpdateVersionAsync(string name, string version, string? workingDirectory, UpdateSpellVersionRequest request, CancellationToken ct) =>
            Task.FromResult(Result<SpellVersionDto>.Failure(new Error("Spell.NotFound", "missing")));

        public Task<Result<SpellVersionDto>> ActivateVersionAsync(string name, string version, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult(Result<SpellVersionDto>.Failure(new Error("Spell.NotFound", "missing")));

        public Task<Result<SpellVersionDetailDto>> GetVersionDetailAsync(string name, string version, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult(Result<SpellVersionDetailDto>.Failure(new Error("Spell.NotFound", "missing")));

    }

    private sealed class FakePromptRepository : IPromptRepository
    {

        public Task<Prompt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Prompt?>(null);

        public Task<Prompt?> GetByNameAndVersionAsync(
            string name,
            string version,
            Guid? campaignId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Prompt?>(null);

        public Task<IReadOnlyList<Prompt>> ListVersionsAsync(string name, Guid? campaignId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Prompt>>([]);

        public Task<ListPageResult<Prompt>> ListAsync(
            Guid? campaignId,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Prompt>([], false));

        public Task<Prompt> AddAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(prompt);

        public Task<Prompt> UpdateAsync(Prompt prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(prompt);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

    }

    private sealed class FakeTokenCounter : IManaMeter
    {

        public int CountTokens(string text) => text.Length;

    }

}
