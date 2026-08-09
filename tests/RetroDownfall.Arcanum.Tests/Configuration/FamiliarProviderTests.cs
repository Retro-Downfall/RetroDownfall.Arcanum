using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

/// <summary>
/// A Familiar is an operator-installed vendor CLI that Arcanum calls on for inference. It is bound
/// to the operator's own subscription, so it has no endpoint and no credential of its own for
/// Arcanum to hold — the whole point of the kind is that those fields do not apply. These facts pin
/// that inversion: the fields are rejected rather than merely ignored, an empty model list is legal
/// (the vendor owns the catalogue, not <c>arcanum.json</c>), and the hide list is a display filter
/// that never becomes a policy control.
/// </summary>
public sealed class FamiliarProviderTests
{

    private readonly ConfigurationValidator _validator = new();

    [Theory]
    [InlineData(AiProviderKind.ClaudeCodeCli)]
    [InlineData(AiProviderKind.CodexCli)]
    public void Familiar_kinds_are_recognised_as_familiars(AiProviderKind kind)
    {

        Assert.True(FamiliarProviders.IsFamiliar(kind));

    }

    [Fact]
    public void OpenAiCompatible_is_not_a_familiar()
    {

        Assert.False(FamiliarProviders.IsFamiliar(AiProviderKind.OpenAICompatible));

    }

    [Theory]
    [InlineData(AiProviderKind.ClaudeCodeCli, "claude")]
    [InlineData(AiProviderKind.CodexCli, "codex")]
    public void Each_familiar_kind_names_its_default_binary(AiProviderKind kind, string expected)
    {

        Assert.Equal(expected, FamiliarProviders.DefaultCommand(kind));

    }

    /// <summary>
    /// The operator may point at a specific build; a blank override falls back to the bare name so
    /// PATH resolution still applies.
    /// </summary>
    [Theory]
    [InlineData(null, "claude")]
    [InlineData("", "claude")]
    [InlineData("   ", "claude")]
    [InlineData("/opt/homebrew/bin/claude", "/opt/homebrew/bin/claude")]
    [InlineData("  /usr/local/bin/claude  ", "/usr/local/bin/claude")]
    public void Command_override_wins_over_the_default_binary_name(string? configured, string expected)
    {

        ProviderSettings provider = new()
        {
            Name = "subscription",
            Type = AiProviderKind.ClaudeCodeCli,
            Command = configured,
        };

        Assert.Equal(expected, FamiliarProviders.ResolveCommand(provider));

    }

    [Fact]
    public void A_familiar_provider_needs_no_models_endpoint_or_credential()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsSuccess);

    }

    /// <summary>
    /// An OpenAI-compatible row still has to advertise something; only Familiars invert this.
    /// </summary>
    [Fact]
    public void An_http_provider_still_requires_models()
    {

        ArcanumSettings settings = new()
        {
            Providers = [new ProviderSettings { Name = "compat", Type = AiProviderKind.OpenAICompatible }],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static error => error.Pointer == "providers[0]");

    }

    [Fact]
    public void A_familiar_provider_rejects_an_endpoint()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Endpoint = "https://api.anthropic.com",
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[0].endpoint");

    }

    [Fact]
    public void A_familiar_provider_rejects_a_credential_environment_variable()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "Codex-subscription",
                    Type = AiProviderKind.CodexCli,
                    CredentialEnvironmentVariable = "OPENAI_API_KEY",
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[0].credentialEnvironmentVariable");

    }

    [Fact]
    public void An_http_provider_rejects_a_command_override()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["gpt-4o"],
                    Command = "claude",
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static error => error.Pointer == "providers[0].command");

    }

    [Fact]
    public void An_http_provider_rejects_a_hide_list()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Models = ["gpt-4o"],
                    HiddenModels = ["gpt-4o"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static error => error.Pointer == "providers[0].hiddenModels");

    }

    [Fact]
    public void A_blank_hide_list_entry_is_rejected()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    HiddenModels = ["claude-legacy", "   "],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[0].hiddenModels[1]");

    }

    [Fact]
    public void A_case_insensitively_duplicated_hide_list_entry_is_rejected()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    HiddenModels = ["claude-legacy", "CLAUDE-Legacy"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(
            result.Error.Details!,
            static error => error.Pointer == "providers[0].hiddenModels[1]");

    }

    [Fact]
    public void An_undefined_provider_kind_is_still_rejected()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "bogus",
                    Type = (AiProviderKind)97,
                    Models = ["m"],
                },
            ],
        };

        Result result = _validator.Validate(settings);

        Assert.True(result.IsFailure);

        Assert.Contains(result.Error.Details!, static error => error.Pointer == "providers[0].type");

    }

    /// <summary>
    /// The hide list filters what is <em>offered</em>. Resolution is a separate question, and the
    /// two must not be wired to the same enumeration — a hidden model an operator names explicitly
    /// still has to run, or a decluttering preference has quietly become a policy control.
    /// </summary>
    [Fact]
    public void Hidden_models_are_omitted_from_the_visible_enumeration()
    {

        ProviderSettings provider = new()
        {
            Name = "ClaudeCode-subscription",
            Type = AiProviderKind.ClaudeCodeCli,
            Models = ["claude-sonnet", "claude-legacy"],
            HiddenModels = ["CLAUDE-LEGACY"],
        };

        string[] visible = ProviderResolver.EnumerateVisibleModels(provider).ToArray();

        Assert.Equal(["claude-sonnet"], visible);

    }

    [Fact]
    public void Hidden_models_remain_in_the_advertised_enumeration_used_for_resolution()
    {

        ProviderSettings provider = new()
        {
            Name = "ClaudeCode-subscription",
            Type = AiProviderKind.ClaudeCodeCli,
            Models = ["claude-sonnet", "claude-legacy"],
            HiddenModels = ["claude-legacy"],
        };

        string[] advertised = ProviderResolver.EnumerateAdvertisedModels(provider).ToArray();

        Assert.Equal(["claude-sonnet", "claude-legacy"], advertised);

    }

    [Fact]
    public void A_hidden_model_still_resolves_when_named_explicitly()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "ClaudeCode-subscription",
                    Type = AiProviderKind.ClaudeCodeCli,
                    Models = ["claude-sonnet", "claude-legacy"],
                    HiddenModels = ["claude-legacy"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "claude-legacy",
            out ProviderSettings? provider,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Equal("ClaudeCode-subscription", provider!.Name);

        Assert.Equal("claude-legacy", resolvedModel);

    }

    /// <summary>
    /// The vendor ships models on its own schedule. An operator paying for a subscription expects a
    /// new release to work without re-editing <c>arcanum.json</c>, so an unmatched name is handed to
    /// the Familiar verbatim and the CLI decides whether it exists.
    /// </summary>
    [Fact]
    public void An_undeclared_model_passes_through_to_a_familiar_verbatim()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "ClaudeCode-subscription", Type = AiProviderKind.ClaudeCodeCli },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "claude-shipped-yesterday",
            out ProviderSettings? provider,
            out string resolvedModel);

        Assert.True(resolved);

        Assert.Equal("ClaudeCode-subscription", provider!.Name);

        Assert.Equal("claude-shipped-yesterday", resolvedModel);

    }

    /// <summary>
    /// Passthrough must never steal a model an HTTP provider actually declares, whichever order the
    /// rows appear in.
    /// </summary>
    [Fact]
    public void A_declared_http_model_wins_over_familiar_passthrough()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "ClaudeCode-subscription", Type = AiProviderKind.ClaudeCodeCli },
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                    Models = ["gpt-4o"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "gpt-4o",
            out ProviderSettings? provider,
            out _);

        Assert.True(resolved);

        Assert.Equal("compat", provider!.Name);

    }

    /// <summary>
    /// Two Familiars cannot both claim an unknown name, so configured order decides — the same
    /// tie-break the resolver already uses for duplicate model declarations.
    /// </summary>
    [Fact]
    public void Configured_order_breaks_a_passthrough_tie_between_familiars()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "Codex-subscription", Type = AiProviderKind.CodexCli },
                new ProviderSettings { Name = "ClaudeCode-subscription", Type = AiProviderKind.ClaudeCodeCli },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "something-new",
            out ProviderSettings? provider,
            out _);

        Assert.True(resolved);

        Assert.Equal("Codex-subscription", provider!.Name);

    }

    [Fact]
    public void Passthrough_does_not_invent_a_provider_when_none_is_a_familiar()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "compat",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                    Models = ["gpt-4o"],
                },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            "not-configured-anywhere",
            out ProviderSettings? provider,
            out _);

        Assert.False(resolved);

        Assert.Null(provider);

    }

    [Fact]
    public void Candidate_resolution_also_passes_an_undeclared_model_to_a_familiar()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "ClaudeCode-subscription", Type = AiProviderKind.ClaudeCodeCli },
            ],
        };

        IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> candidates =
            ProviderResolver.ResolveCandidates(settings, "claude-shipped-yesterday");

        (ProviderSettings provider, string model) = Assert.Single(candidates);

        Assert.Equal("ClaudeCode-subscription", provider.Name);

        Assert.Equal("claude-shipped-yesterday", model);

    }

    /// <summary>
    /// With no model named anywhere, a Familiar contributes nothing to guess from — falling back to
    /// the first provider's first advertised model must not turn into "spawn the CLI with no model".
    /// </summary>
    [Fact]
    public void A_familiar_with_no_declared_models_is_not_a_default_model_source()
    {

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "ClaudeCode-subscription", Type = AiProviderKind.ClaudeCodeCli },
            ],
        };

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            null,
            out ProviderSettings? provider,
            out _);

        Assert.False(resolved);

        Assert.Null(provider);

    }

    [Fact]
    public void Provider_diagnostics_never_echo_a_hide_list_or_command_as_a_secret()
    {

        ProviderSettings provider = new()
        {
            Name = "ClaudeCode-subscription",
            Type = AiProviderKind.ClaudeCodeCli,
            Command = "/opt/homebrew/bin/claude",
            HiddenModels = ["claude-legacy"],
        };

        string rendered = provider.ToString();

        Assert.Contains("ClaudeCodeCli", rendered, StringComparison.Ordinal);

        Assert.Contains("/opt/homebrew/bin/claude", rendered, StringComparison.Ordinal);

        Assert.Contains("claude-legacy", rendered, StringComparison.Ordinal);

    }

}
