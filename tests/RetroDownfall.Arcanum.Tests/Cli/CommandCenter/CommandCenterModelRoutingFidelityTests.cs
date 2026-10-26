using System.Reflection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Configuration;
using Terminal.Gui.App;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterModelRoutingFidelityTests
{
    [Fact]
    public void Shared_ids_offer_one_choice_and_one_active_marker_with_all_listing_providers()
    {
        ModelInfoDto[] models =
        [
            new("Shared", "Local", "OpenAICompatible", "***", 8192),
            new("SHARED", "Local", "OpenAICompatible", "***", 8192),
            new("shared", "Remote", "OpenAICompatible", "***", 8192),
        ];

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(models);

        Assert.Equal("Shared", Assert.Single(items).Model);

        string line = Assert.Single(CommandCenterModelPicker.Render(items, "shared"));

        Assert.StartsWith(CommandCenterModelPicker.ActiveMarker, line, StringComparison.Ordinal);

        Assert.Equal($"{CommandCenterModelPicker.ActiveMarker} Shared  (listed by: Local, Remote)", line);

        Assert.Same(items[0], Assert.Single(CommandCenterModelPicker.Filter(items, "remote")));
    }

    [Fact]
    public void Selecting_a_listed_model_does_not_claim_its_listing_provider_is_the_route()
    {
        ArcanumSettings settings = HiddenEarlierProviderSettings();

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(ModelInfoBuilder.BuildModelInfoList(settings));

        Assert.Single(items);

        Assert.True(ProviderResolver.TryResolveProviderForModel(settings, items[0].Model, out ProviderSettings? provider, out _));

        Assert.Equal("HiddenEarlier", provider!.Name);

        CommandCenterState state = new(new SessionLogBuffer())
        {
            ModelChoices = items,
            SelectedModelIndex = 0,
            Overlay = CommandCenterOverlayKind.ModelPicker,
        };

        using CommandCenterWindow window = new();

        using IApplication app = Application.Create().Init("ansi");

        window.ApplyAbsoluteLayout(120, 40);

        typeof(CommandCenterHost).GetMethod("ApplySelectedModel", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [state, window, app]);

        app.TimedEvents!.RunTimers();

        Assert.Equal("shared", state.Model);

        Assert.Contains("Model set to shared for this session.", state.Log.RenderPlainText(), StringComparison.Ordinal);

        Assert.DoesNotContain("VisibleLater", state.Log.RenderPlainText(), StringComparison.Ordinal);

        Assert.Contains("host", state.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Slash_confirmation_does_not_infer_a_provider_route_from_a_lossy_projection(bool hiddenOnly)
    {
        ArcanumSettings settings = HiddenEarlierProviderSettings();

        if (hiddenOnly)
        {
            settings.Providers =
            [
                new() { Name = "ActualFirst", Type = AiProviderKind.ClaudeCodeCli },
                new() { Name = "HiddenLater", Type = AiProviderKind.CodexCli, HiddenModels = ["shared"] },
            ];
        }

        ProviderInfoDto[] projected = [.. settings.Providers.Select(static p => new ProviderInfoDto(
            p.Name, p.Type.ToString(), string.Empty, string.Empty,
            [.. ProviderResolver.EnumerateVisibleModels(p)], p.ContextWindowLimit, p.HiddenModels ?? []))];

        ModelChoiceDecision decision = CommandCenterModelChoice.Decide(projected, "shared", null);

        Assert.True(decision.Accepted);

        Assert.True(ProviderResolver.TryResolveProviderForModel(settings, decision.Model, out ProviderSettings? actual, out _));

        Assert.NotEqual(actual!.Name, decision.ProviderName);

        Assert.Contains("Model set to shared for this session.", decision.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(decision.ProviderName!, decision.Message, StringComparison.Ordinal);
    }

    private static ArcanumSettings HiddenEarlierProviderSettings() => new()
    {
        Providers =
        [
            new() { Name = "HiddenEarlier", Type = AiProviderKind.ClaudeCodeCli, Models = ["shared"], HiddenModels = ["shared"] },
            new() { Name = "VisibleLater", Type = AiProviderKind.OpenAICompatible, Models = ["shared"] },
        ],
    };
}
