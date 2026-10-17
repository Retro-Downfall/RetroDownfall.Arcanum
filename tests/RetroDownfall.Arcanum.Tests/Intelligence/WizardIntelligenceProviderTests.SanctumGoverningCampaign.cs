using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The tools a turn runs take their Sanctum facts from the Campaign that governs the turn. <c>run_spell_script</c> reads the resource
/// limits and the child-process boundary of a Campaign, and it must read them from the governing Campaign, not from whichever Campaign
/// (if any) its working directory happens to name.
/// </summary>
public sealed partial class WizardIntelligenceProviderTests
{
    // The turn is bound to a Campaign by its canonical context; the directory it runs in is not that Campaign's root and is registered
    // as nothing. A lookup by the working directory finds no Campaign and the script would run with the default limits.
    [SkippableFact]
    public async Task A_spell_script_of_a_turn_bound_to_a_campaign_is_limited_by_that_campaign_whatever_directory_the_turn_runs_in()
    {
        Skip.If(OperatingSystem.IsWindows(), "The script this turn runs is a POSIX shell script.");

        using HostProcessToolsEscapeHatchScope hostProcessTools = new();

        string campaignRoot = _workspace.CreateSubdir("governing-campaign");

        Campaign campaign = StrictSanctumCampaign(campaignRoot);

        await CreateSpellWithDeclaredToolsAsync("scripted", ["run_spell_script"]);

        string scripts = Directory.CreateDirectory(Path.Combine(_workspace.Root, "scripted", "scripts")).FullName;

        string script = Path.Combine(scripts, "hello.sh");

        await File.WriteAllTextAsync(script, "#!/bin/sh\necho ok\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(
            "run_spell_script",
            "governing-script",
            new Dictionary<string, object?> { ["script_name"] = "hello.sh" });

        chat.EnqueueText("done");

        LookupRecordingSanctumGuard guard = new();

        ArcanumSettings settings = DefaultSettings() with
        {
            Edition = ArcanumEdition.Development,
            Security = DefaultSettings().Security with { AllowUnsandboxedToolChildren = true },
        };

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            campaignRepository: new FakeCampaignRepository(campaign),
            sanctumGuard: guard);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            BaseRequest() with
            {
                Prompt = "run the script",
                WorkingDirectory = _workspace.Root,
                OverrideSpellName = "scripted",
                SkipSpellRouting = false,
                DisableMcpTools = true,
            },
            InvocationContexts.AttendedSession(campaign.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        string[] toolMessages =
        [
            .. chat.AllBufferedCalls
                .SelectMany(static batch => batch)
                .Where(static message => message.Role == ChatRole.Tool)
                .Select(GetMessageText),
        ];

        Assert.Contains(toolMessages, static message => message.Contains("--- exit code ---", StringComparison.Ordinal));

        Assert.Equal([campaignRoot], guard.LimitLookups);

        Assert.Equal([campaignRoot], guard.BoundaryLookups);
    }

    // An internal caller (an Apprentice) has no canonical context. The Campaign it names on the request still governs its turn, although
    // the directory it works in is outside that Campaign's root and no registered root contains it.
    [Fact]
    public async Task An_internal_turn_that_names_a_campaign_is_governed_by_it_although_its_directory_is_outside_the_root()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.CreateSubdir("named-campaign-root"));

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.Root,
            ArcanumInvocationContext.None,
            requestCampaignId: campaign.Id);

        Assert.True(probe.Succeeded);

        Assert.True(probe.Blocked);

        Assert.Equal(1, probe.ToolChecks);
    }

    [Fact]
    public async Task An_internal_turn_that_names_no_campaign_and_runs_outside_every_root_is_unrestricted()
    {
        Campaign campaign = StrictSanctumCampaign(_workspace.CreateSubdir("unnamed-campaign-root"));

        SanctumProbe probe = await ProbeSanctumAsync(
            new FakeCampaignRepository(campaign),
            _workspace.Root,
            ArcanumInvocationContext.None);

        Assert.True(probe.Succeeded);

        Assert.False(probe.Blocked);

        Assert.Equal(0, probe.ToolChecks);
    }

    /// <summary>A guard that allows everything and records the workspace each limit and boundary question was asked about.</summary>
    private sealed class LookupRecordingSanctumGuard : ISanctumGuard
    {
        public List<string?> LimitLookups { get; } = [];

        public List<string?> BoundaryLookups { get; } = [];

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default)
        {
            LimitLookups.Add(workspaceRoot);

            return Task.FromResult(new ResourceLimits());
        }

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default)
        {
            BoundaryLookups.Add(workspaceRoot);

            return Task.FromResult<SanctumChildProcessBoundary?>(null);
        }

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
