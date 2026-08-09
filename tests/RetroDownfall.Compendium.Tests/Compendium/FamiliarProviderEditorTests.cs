using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// Compendium is where an operator configures a subscription CLI provider. The inversion that makes
/// the kind work — no endpoint, no credential, an optional model list, and a hide list that is
/// subtractive — has to survive a load/save round trip, and none of it may depend on the host being
/// up.
/// </summary>
public sealed class FamiliarProviderEditorTests
{

    [Fact]
    public void The_hide_list_round_trips_through_the_editor()
    {

        ProvidersSectionViewModel section = new(new StubDialogService());

        section.LoadFrom(
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    HiddenModels = ["claude-legacy", "claude-retired"],
                },
            ],
            defaultModel: null,
            fastModel: null);

        ProviderSettings rebuilt = Assert.Single(section.BuildProviders());

        Assert.Equal(["claude-legacy", "claude-retired"], rebuilt.HiddenModels);

    }

    [Fact]
    public void The_command_override_round_trips_and_a_blank_one_stays_absent()
    {

        ProvidersSectionViewModel section = new(new StubDialogService());

        section.LoadFrom(
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Command = "/opt/homebrew/bin/claude",
                },
                new ProviderSettings
                {
                    Name = "Codex-subscription",
                    Type = AiProviderKind.CodexCli,
                },
            ],
            defaultModel: null,
            fastModel: null);

        ProviderSettings[] rebuilt = section.BuildProviders();

        Assert.Equal("/opt/homebrew/bin/claude", rebuilt[0].Command);

        Assert.Null(rebuilt[1].Command);

    }

    /// <summary>
    /// Switching an existing OpenAI-compatible row to a Familiar kind must drop the fields that kind
    /// cannot have — otherwise the save fails validation, and the row implies Arcanum is holding a
    /// key it never reads.
    /// </summary>
    [Fact]
    public void Switching_a_row_to_a_familiar_kind_drops_the_endpoint_and_credential()
    {

        ProvidersSectionViewModel section = new(new StubDialogService());

        section.LoadFrom(
            [
                new ProviderSettings
                {
                    Name = "was-http",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                    CredentialEnvironmentVariable = "OPENAI_API_KEY",
                    Models = ["gpt-4o"],
                },
            ],
            defaultModel: null,
            fastModel: null);

        section.Providers[0].Type = AiProviderKind.ClaudeCodeCli;

        ProviderSettings rebuilt = Assert.Single(section.BuildProviders());

        Assert.Equal(string.Empty, rebuilt.Endpoint);

        Assert.Null(rebuilt.CredentialEnvironmentVariable);

        Assert.True(new ConfigurationValidator().Validate(new ArcanumSettings { Providers = [rebuilt] }).IsSuccess);

    }

    /// <summary>
    /// The reverse: an HTTP row must not carry Familiar-only fields, which the validator rejects.
    /// </summary>
    [Fact]
    public void Switching_a_row_back_to_http_drops_the_command_and_hide_list()
    {

        ProvidersSectionViewModel section = new(new StubDialogService());

        section.LoadFrom(
            [
                new ProviderSettings
                {
                    Name = "was-familiar",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Command = "/opt/homebrew/bin/claude",
                    HiddenModels = ["claude-legacy"],
                    Models = ["claude-sonnet"],
                },
            ],
            defaultModel: null,
            fastModel: null);

        section.Providers[0].Type = AiProviderKind.OpenAICompatible;

        section.Providers[0].Endpoint = "https://api.openai.com/v1";

        ProviderSettings rebuilt = Assert.Single(section.BuildProviders());

        Assert.Null(rebuilt.Command);

        Assert.Empty(rebuilt.HiddenModels);

        Assert.True(new ConfigurationValidator().Validate(new ArcanumSettings { Providers = [rebuilt] }).IsSuccess);

    }

    [Fact]
    public void The_editor_knows_which_rows_are_familiars()
    {

        ProvidersSectionViewModel section = new(new StubDialogService());

        section.LoadFrom(
            [
                new ProviderSettings { Name = "familiar", Type = AiProviderKind.CodexCli },
                new ProviderSettings
                {
                    Name = "http",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                    Models = ["gpt-4o"],
                },
            ],
            defaultModel: null,
            fastModel: null);

        Assert.True(section.Providers[0].IsFamiliar);

        Assert.False(section.Providers[0].IsHttpProvider);

        Assert.False(section.Providers[1].IsFamiliar);

    }

    /// <summary>
    /// A host that is not running is a state with an instruction, not an unresolving spinner — and
    /// the hide list must stay editable through it, because nothing about editing needs the host.
    /// </summary>
    [Fact]
    public async Task An_unavailable_host_leaves_the_hide_list_editable_and_says_what_to_do()
    {

        ProvidersSectionViewModel section = new(
            new StubDialogService(),
            new UnavailableHostProbeClient());

        section.LoadFrom(
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    HiddenModels = ["claude-legacy"],
                },
            ],
            defaultModel: null,
            fastModel: null);

        await section.Providers[0].ProbeCommand.ExecuteAsync(null);

        Assert.Contains("not running", section.Providers[0].ProbeStatus, StringComparison.OrdinalIgnoreCase);

        Assert.False(section.Providers[0].IsProbing);

        section.Providers[0].HiddenModels = "claude-legacy, claude-retired";

        Assert.Equal(
            ["claude-legacy", "claude-retired"],
            Assert.Single(section.BuildProviders()).HiddenModels);

    }

    /// <summary>Every provider kind, including the new ones, is offered in the type picker.</summary>
    [Fact]
    public void The_type_picker_offers_both_familiar_kinds()
    {

        SettingDescriptor type = Assert.Single(
            SettingDescriptors.All.Where(static descriptor => descriptor.Key == "providers.type"));

        Assert.Equal(typeof(AiProviderKind), type.EnumType);

        Assert.Contains(AiProviderKind.ClaudeCodeCli, Enum.GetValues<AiProviderKind>());

        Assert.Contains(AiProviderKind.CodexCli, Enum.GetValues<AiProviderKind>());

    }

    private sealed class UnavailableHostProbeClient : IFamiliarProbeClient
    {

        public Task<Result<FamiliarProbeResult>> ProbeAsync(
            string providerName,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<FamiliarProbeResult>.Failure(
                    new Error(
                        "Compendium.HostUnavailable",
                        "Probe unavailable — the Arcanum host is not running. Start it with `arcanum serve` and re-probe.")));

    }

    private sealed class StubDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") =>
            Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string accept = "Yes",
            string cancel = "No") => Task.FromResult(true);

    }

}
