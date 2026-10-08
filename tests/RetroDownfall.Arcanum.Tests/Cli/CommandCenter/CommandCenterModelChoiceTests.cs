using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// <c>/model &lt;name&gt;</c> used to take any name, so a typo such as <c>bogus-model</c> became the
/// session model and the next turn failed with "The requested model is not configured". The rule
/// refuses only a name the host could not route either, and it keeps both documented exceptions: a
/// hidden model is not blocked, and a Familiar is asked for a model no row lists.
/// </summary>
public sealed class CommandCenterModelChoiceTests
{
    private static readonly ProviderInfoDto[] Ollama =
    [
        Provider("Ollama", AiProviderKind.OpenAICompatible, ["ornith1.5:35b", "gemma4:e4b"]),
    ];

    [Fact]
    internal void A_listed_model_takes_the_provider_casing_and_names_the_provider()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [Provider("compat", AiProviderKind.OpenAICompatible, ["gpt-4o"])],
            "GPT-4O",
            currentModel: null);

        Assert.Equal(ModelChoiceRoute.Listed, decision.Route);

        Assert.True(decision.Accepted);

        Assert.Equal("gpt-4o", decision.Model);

        Assert.Equal("compat", decision.ProviderName);

        Assert.Equal("Model set to gpt-4o (compat) for this session.", decision.Message);
    }

    /// <summary>The drop-down confirms a selection in the same words <c>/model</c> does.</summary>
    [Fact]
    internal void The_confirmation_names_the_model_and_its_provider()
    {
        Assert.Equal(
            "Model set to gemma4:e4b (Ollama) for this session.",
            CommandCenterModelChoice.Confirmation("gemma4:e4b", "Ollama"));
    }

    /// <summary>
    /// A hide list declutters listings; it is not a policy control, so a hidden model named
    /// explicitly is still accepted, and the confirmation says why it was not in the list.
    /// </summary>
    [Fact]
    internal void A_hidden_model_is_accepted()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [Provider("claude-sub", AiProviderKind.ClaudeCodeCli, ["sonnet"], hidden: ["opus"])],
            "opus",
            currentModel: "sonnet");

        Assert.Equal(ModelChoiceRoute.Hidden, decision.Route);

        Assert.True(decision.Accepted);

        Assert.Equal("opus", decision.Model);

        Assert.Equal("claude-sub", decision.ProviderName);

        Assert.Equal(
            "Model set to opus (claude-sub, hidden from model lists) for this session.",
            decision.Message);
    }

    /// <summary>
    /// A Familiar's catalogue belongs to the vendor, so a model no row lists is handed to a Familiar
    /// exactly as typed — and to the same one the host would pick.
    /// </summary>
    [Fact]
    internal void An_unlisted_name_goes_to_a_familiar_exactly_as_typed()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [
                Provider("compat", AiProviderKind.OpenAICompatible, ["gpt-4o"]),
                Provider("claude-sub", AiProviderKind.ClaudeCodeCli, ["sonnet"]),
            ],
            "Claude-Next",
            currentModel: "gpt-4o");

        Assert.Equal(ModelChoiceRoute.FamiliarPassThrough, decision.Route);

        Assert.True(decision.Accepted);

        Assert.Equal("Claude-Next", decision.Model);

        Assert.Equal("claude-sub", decision.ProviderName);

        Assert.Equal(
            "Model set to Claude-Next for this session. No provider lists it, so claude-sub (ClaudeCodeCli) will be asked for it.",
            decision.Message);
    }

    [Fact]
    internal void Pass_through_goes_to_the_first_familiar_in_configured_order()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [
                Provider("compat", AiProviderKind.OpenAICompatible, ["gpt-4o"]),
                Provider("codex", AiProviderKind.CodexCli, []),
                Provider("claude-sub", AiProviderKind.ClaudeCodeCli, ["sonnet"]),
            ],
            "brand-new-model",
            currentModel: null);

        Assert.Equal(ModelChoiceRoute.FamiliarPassThrough, decision.Route);

        Assert.Equal("codex", decision.ProviderName);

        Assert.Contains("codex (CodexCli)", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    internal void An_unknown_name_is_refused_with_the_available_models_in_configured_order()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [
                .. Ollama,
                Provider("compat", AiProviderKind.OpenAICompatible, ["gpt-4o"]),
            ],
            "bogus-model",
            currentModel: "ornith1.5:35b");

        Assert.Equal(ModelChoiceRoute.Unknown, decision.Route);

        Assert.False(decision.Accepted);

        Assert.Null(decision.ProviderName);

        Assert.Equal(
            "Unknown model `bogus-model`; still using ornith1.5:35b. Available: ornith1.5:35b (Ollama), gemma4:e4b (Ollama), gpt-4o (compat).",
            decision.Message);
    }

    [Fact]
    internal void An_unknown_name_with_no_session_model_says_the_default_is_still_in_use()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(Ollama, "bogus-model", currentModel: null);

        Assert.StartsWith(
            "Unknown model `bogus-model`; still using the default model.",
            decision.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    internal void An_unknown_name_when_no_provider_lists_a_model_says_so()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [Provider("compat", AiProviderKind.OpenAICompatible, [])],
            "bogus-model",
            currentModel: null);

        Assert.Equal(
            "Unknown model `bogus-model`; still using the default model. No provider lists any model.",
            decision.Message);
    }

    [Theory]
    [InlineData("  gemma4:e4b  ", ModelChoiceRoute.Listed, "gemma4:e4b")]
    [InlineData("\tbogus-model ", ModelChoiceRoute.Unknown, "bogus-model")]
    internal void Surrounding_whitespace_is_trimmed(string requested, ModelChoiceRoute route, string model)
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(Ollama, requested, currentModel: null);

        Assert.Equal(route, decision.Route);

        Assert.Equal(model, decision.Model);

        Assert.Contains(model, decision.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(requested, decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    internal void A_familiar_is_asked_for_the_trimmed_name()
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            [Provider("claude-sub", AiProviderKind.ClaudeCodeCli, [])],
            "  claude-next ",
            currentModel: null);

        Assert.Equal("claude-next", decision.Model);
    }

    /// <summary>The host being unreachable refuses the name and says what is still in use.</summary>
    [Fact]
    internal void An_unreachable_provider_list_refuses_the_name_and_keeps_the_current_model()
    {
        Assert.Equal(
            "Could not check `gemma4:e4b` against the configured providers: Arcanum is not running. Still using ornith1.5:35b.",
            CommandCenterModelChoice.ProvidersUnavailable("gemma4:e4b", "Arcanum is not running.", "ornith1.5:35b"));

        Assert.Equal(
            "Could not check `gemma4:e4b` against the configured providers: down. Still using the default model.",
            CommandCenterModelChoice.ProvidersUnavailable(" gemma4:e4b ", "down", currentModel: null));
    }

    public static TheoryData<string> ParityNames =>
    [
        "gpt-4o",
        "GPT-4O",
        "sonnet",
        "opus",
        "brand-new-model",
        "bogus",
    ];

    /// <summary>
    /// The decision is a CLI-side copy of <see cref="ProviderResolver.TryResolveProviderForModel"/>,
    /// fed from <c>GET /api/providers</c>. Running the same names through both keeps the copy from
    /// drifting silently: whatever the host would route, the Command Center accepts, and the other
    /// way round.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParityNames))]
    internal void The_decision_agrees_with_the_host_resolver(string name)
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://127.0.0.1:11434/v1",
                    Models = ["gpt-4o"],
                },
                new ProviderSettings
                {
                    Name = "claude-sub",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Models = ["sonnet", "opus"],
                    HiddenModels = ["opus"],
                },
                new ProviderSettings
                {
                    Name = "codex",
                    Type = AiProviderKind.CodexCli,
                },
            ],
        };

        AssertParity(settings, name);
    }

    [Fact]
    internal void With_no_familiar_both_the_decision_and_the_host_refuse_an_unknown_name()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://127.0.0.1:11434/v1",
                    Models = ["gpt-4o"],
                },
            ],
        };

        ModelChoiceDecision decision = AssertParity(settings, "bogus");

        Assert.False(decision.Accepted);
    }

    private static ModelChoiceDecision AssertParity(ArcanumSettings settings, string name)
    {
        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(
            ProjectAsTheProvidersEndpointDoes(settings),
            name,
            currentModel: null);

        bool hostRoutes = ProviderResolver.TryResolveProviderForModel(
            settings,
            name,
            out ProviderSettings? provider,
            out string resolvedModel);

        Assert.Equal(hostRoutes, decision.Accepted);

        if (decision.Route is ModelChoiceRoute.Listed or ModelChoiceRoute.FamiliarPassThrough)
        {
            Assert.Equal(provider!.Name, decision.ProviderName);

            Assert.Equal(resolvedModel, decision.Model);
        }

        return decision;
    }

    /// <summary>The projection <c>GET /api/providers</c> makes in ConfigurationEndpoints.</summary>
    private static ProviderInfoDto[] ProjectAsTheProvidersEndpointDoes(ArcanumSettings settings) =>
        [
            .. (settings.Providers ?? []).Select(static provider => new ProviderInfoDto(
                provider.Name,
                provider.Type.ToString(),
                string.Empty,
                string.Empty,
                [.. ProviderResolver.EnumerateVisibleModels(provider)],
                provider.ContextWindowLimit,
                [.. provider.HiddenModels ?? []])),
        ];

    private static ProviderInfoDto Provider(
        string name,
        AiProviderKind type,
        string[] models,
        string[]? hidden = null) =>
        new(name, type.ToString(), string.Empty, string.Empty, models, 8_192, hidden ?? []);
}
