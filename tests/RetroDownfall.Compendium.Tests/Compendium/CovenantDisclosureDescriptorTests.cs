using System.Collections.Immutable;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views;
using RetroDownfall.Compendium.Ux.Views.Controls;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// Issue #89 — the operator reads what enabling The Covenant costs before the toggle can be flipped.
/// </summary>
/// <remarks>
/// Order is the assertion, not the presence of text. A disclosure rendered underneath the switch is
/// read after the decision, which makes it a receipt rather than a warning. The toggle is therefore
/// constructed last, and the test walks the logical tree in order rather than asking whether both
/// controls exist somewhere.
///
/// <para>The copy is compared against the shared Core constant rather than a string literal here. A
/// Compendium-owned paraphrase would let the desktop app and <c>arcanum memory covenant doctor</c>
/// describe the same irreversible external disclosure differently.</para>
/// </remarks>
[Collection("AvaloniaBinding")]
public sealed class CovenantDisclosureDescriptorTests
{

    [Fact]
    public void Covenant_descriptor_warns_that_eligible_context_is_sent_on_every_provider_attempt()
    {

        SettingDescriptor descriptor = CovenantDescriptor();

        Assert.Equal(SettingKind.Bool, descriptor.Kind);

        Assert.Equal(ConfigSection.Features, descriptor.Section);

        // The approved copy enumerates the attempt kinds rather than saying "every provider attempt"
        // as one phrase, because "every" without the list reads as "every turn" and understates it:
        // one turn can reach several providers. Assert the claim, not one phrasing of it.
        Assert.Contains("on every primary, fallback, retry, compression, and tool-loop provider attempt", descriptor.Description, StringComparison.Ordinal);

        Assert.Contains("may use different configured providers or models", descriptor.Description, StringComparison.Ordinal);

    }

    [Fact]
    public void Covenant_descriptor_uses_shared_copy_and_links_known_provider_retention_documentation()
    {

        SettingDescriptor descriptor = CovenantDescriptor();

        Assert.Equal(CovenantExternalRetentionDisclosure.EnablementText, descriptor.Description);

        Assert.Equal(SettingHelpRoute.ConfiguredProviderRetention, descriptor.HelpRoute);

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings
                {
                    Name = "openai",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                },
            ]);

        Assert.Contains(
            targets,
            target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

    }

    [Fact]
    public void Unknown_or_self_hosted_provider_falls_back_to_providers_page_and_operator_guide()
    {

        ImmutableArray<CovenantRetentionHelpTarget> targets =
            CovenantExternalRetentionDisclosure.ResolveHelpTargets(
            [
                new ProviderSettings
                {
                    Name = "ollama",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "http://localhost:11434/v1",
                },
            ]);

        Assert.Contains(targets, target => target.Kind == CovenantRetentionHelpKind.ConfiguredProvidersPage);

        Assert.Contains(targets, target => target.Kind == CovenantRetentionHelpKind.OperatorGuide);

        Assert.DoesNotContain(
            targets,
            target => target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

    }

    [Fact]
    public void Covenant_disclosure_and_help_actions_render_before_the_enable_toggle()
    {

        using InMemoryConfigurationStore store = new(
            new ArcanumSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "openai",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://api.openai.com/v1",
                    },
                ],
            });

        ConfigurationViewModel root = new(
            store,
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance);

        GenericSettingsSectionView view = new()
        {
            Section = ConfigSection.Features,
            DataContext = root,
        };

        List<Control> ordered = view.GetLogicalDescendants().OfType<Control>().ToList();

        int disclosureIndex = ordered.FindIndex(control =>
            control is TextBlock text
            && string.Equals(text.Text, CovenantExternalRetentionDisclosure.EnablementText, StringComparison.Ordinal));

        int helpIndex = ordered.FindIndex(control =>
            control is Button button
            && button.Tag is CovenantRetentionHelpTarget);

        int toggleIndex = ordered.FindIndex(control =>
            control is LabeledToggle toggle
            && toggle.DataContext is GenericSettingFieldViewModel field
            && field.Descriptor.Key == "features.covenant");

        Assert.True(disclosureIndex >= 0, "The shared enablement disclosure was not rendered.");

        Assert.True(helpIndex >= 0, "No provider-retention help action was rendered.");

        Assert.True(toggleIndex >= 0, "The Covenant toggle was not rendered.");

        Assert.True(
            disclosureIndex < toggleIndex,
            "The disclosure must be constructed before the toggle it warns about.");

        Assert.True(
            helpIndex < toggleIndex,
            "The help actions must be constructed before the toggle they explain.");

    }

    /// <summary>
    /// Opening Features must survive a provider row whose credential variable is not a portable
    /// environment-variable name. The disclosure resolves its help targets from the configured
    /// providers during synchronous view construction, so a projection that validates on the way out
    /// turns "click Features" into an unhandled exception on the UI thread. No keystroke is needed to
    /// arm it: a hand-edited <c>arcanum.json</c> loads the offending value into the row verbatim.
    /// </summary>
    [Fact]
    public void Features_section_renders_when_a_provider_carries_a_non_portable_credential_variable()
    {

        using InMemoryConfigurationStore store = new(
            new ArcanumSettings
            {
                Providers =
                [
                    new ProviderSettings
                    {
                        Name = "openai",
                        Type = AiProviderKind.OpenAICompatible,
                        Endpoint = "https://api.openai.com/v1",
                        CredentialEnvironmentVariable = "MY-KEY",
                    },
                ],
            });

        ConfigurationViewModel root = new(
            store,
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance);

        GenericSettingsSectionView view = new()
        {
            Section = ConfigSection.Features,
            DataContext = root,
        };

        List<Control> ordered = view.GetLogicalDescendants().OfType<Control>().ToList();

        Assert.Contains(
            ordered,
            control => control is LabeledToggle toggle
                && toggle.DataContext is GenericSettingFieldViewModel field
                && field.Descriptor.Key == "features.covenant");

        // The endpoint still resolves the documented retention page, so degrading the projection must
        // not degrade the disclosure the operator reads before flipping the toggle.
        Assert.Contains(
            ordered,
            control => control is Button button
                && button.Tag is CovenantRetentionHelpTarget target
                && target.Kind == CovenantRetentionHelpKind.ProviderRetentionDocumentation);

    }

    /// <summary>
    /// Rendering must not silently accept the value the save path rejects. Building for persistence
    /// keeps validating, so the operator is still told the name is not portable when it matters.
    /// </summary>
    [Fact]
    public void Building_providers_for_persistence_still_rejects_a_non_portable_credential_variable()
    {

        ProvidersSectionViewModel section = new(new NoopDialogService());

        section.LoadFrom(
            [
                new ProviderSettings
                {
                    Name = "openai",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://api.openai.com/v1",
                    CredentialEnvironmentVariable = "MY-KEY",
                },
            ],
            defaultModel: null,
            fastModel: null);

        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() => section.BuildProviders());

        Assert.Contains("not portable", failure.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void Every_other_bool_descriptor_renders_without_a_help_route()
    {

        Assert.All(
            SettingDescriptors.All.Where(descriptor => descriptor.Key != "features.covenant"),
            descriptor => Assert.Null(descriptor.HelpRoute));

    }

    private static SettingDescriptor CovenantDescriptor() =>
        Assert.Single(SettingDescriptors.All, item => item.Key == "features.covenant");

    private sealed class InMemoryConfigurationStore(ArcanumSettings settings) : IArcanumConfigurationStore
    {

        public string ConfigurationFilePath => "memory://arcanum.json";

        public event EventHandler? ExternalChange
        {
            add { }
            remove { }
        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(settings);

        public Task<ConfigurationWriteResult> WriteAsync(
            ArcanumSettings updated,
            CancellationToken ct = default) =>
            Task.FromResult(new ConfigurationWriteResult(true, [], null));

        public void Dispose()
        {
        }

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") =>
            Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string accept = "Yes",
            string cancel = "No") =>
            Task.FromResult(true);

    }

}
