using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views;
using RetroDownfall.Compendium.Ux.Views.Controls;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("AvaloniaBinding")]
public sealed class GenericSettingsSectionViewTests(AvaloniaBindingFixture ui)
{
    [Theory]
    [InlineData("100000.55")]
    [InlineData("500000.99")]
    [InlineData("0.7")]
    [InlineData("123456.7890123456789012345678")]
    [InlineData("0.0000000000000000000000000001")]
    public Task Money_text_edit_reaches_settings_as_the_exact_decimal(string text) =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = CreateRoot(store);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Cost,
                DataContext = root,
            };

            LabeledStepper stepper = StepperFor(view, "cost.budget.dailyLimitUsd");

            NumericUpDown numeric = NumericInput(stepper);

            decimal expected = decimal.Parse(text, CultureInfo.InvariantCulture);

            numeric.NumberFormat = CultureInfo.InvariantCulture.NumberFormat;

            var (entry, _) = ApplyNumericTemplate(numeric);

            entry.Text = text;

            numeric.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
            });

            GenericSettingFieldViewModel field = Assert.IsType<GenericSettingFieldViewModel>(stepper.DataContext);

            Assert.Equal(expected, numeric.Value);

            Assert.Equal(expected, Assert.IsType<decimal>(field.Value));

            Assert.False(field.HasError);

            Assert.Equal(expected, root.BuildSettings().Cost.Budget.DailyLimitUsd);
        });

    [Fact]
    public Task Loaded_money_stays_exact_when_the_cost_view_is_created_and_saved_unchanged() =>
        ui.RunAsync(() =>
        {
            const decimal amount = 123456.7890123456789012345678m;

            const decimal rate = 0.0000000000001234567890123456m;

            using InMemoryConfigurationStore store = new(new ArcanumSettings
            {
                Cost = new CostSettings
                {
                    Budget = new BudgetPolicySettings { DailyLimitUsd = amount },
                    Pricing = new PricingSettings
                    {
                        DefaultPricing = new ModelPricingEntry { InputPer1M = rate },
                    },
                },
            });

            ConfigurationViewModel root = CreateRoot(store);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Cost,
                DataContext = root,
            };

            Assert.False(root.LoadFailed);

            Assert.Equal(amount, NumericInput(StepperFor(view, "cost.budget.dailyLimitUsd")).Value);

            Assert.Equal(rate, NumericInput(StepperFor(view, "cost.pricing.defaultPricing.inputPer1M")).Value);

            ArcanumSettings built = root.BuildSettings();

            Assert.Equal(amount, built.Cost.Budget.DailyLimitUsd);

            Assert.Equal(rate, built.Cost.Pricing.DefaultPricing.InputPer1M);

            Assert.False(root.IsDirty);
        });

    [Fact]
    public Task Money_spin_uses_exact_cent_steps_without_erasing_existing_subcent_precision() =>
        ui.RunAsync(() =>
        {
            const decimal start = 0.7000000000000000000000000001m;

            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = CreateRoot(store);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Cost,
                DataContext = root,
            };

            LabeledStepper stepper = StepperFor(view, "cost.budget.dailyLimitUsd");

            NumericUpDown numeric = NumericInput(stepper);

            var (_, spinner) = ApplyNumericTemplate(numeric);

            Assert.Equal(0.01m, numeric.Increment);

            numeric.Value = start;

            SpinUp(spinner);

            SpinUp(spinner);

            SpinUp(spinner);

            const decimal expected = 0.7300000000000000000000000001m;

            Assert.Equal(expected, numeric.Value);

            Assert.Equal(expected, root.BuildSettings().Cost.Budget.DailyLimitUsd);
        });

    [Fact]
    public Task Nullable_money_checkbox_distinguishes_unset_from_explicit_zero() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = CreateRoot(store);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Cost,
                DataContext = root,
            };

            const string key = "cost.pricing.defaultPricing.reasoningPer1M";

            LabeledStepper stepper = StepperFor(view, key);

            GenericSettingFieldViewModel field = Assert.IsType<GenericSettingFieldViewModel>(stepper.DataContext);

            CheckBox enabled = Assert.Single(view.GetLogicalDescendants().OfType<CheckBox>(),
                checkbox => ReferenceEquals(checkbox.DataContext, field));

            Assert.False(field.IsSet);

            Assert.False(stepper.IsEnabled);

            Assert.Null(root.BuildSettings().Cost.Pricing.DefaultPricing.ReasoningPer1M);

            enabled.IsChecked = true;

            Assert.True(field.IsSet);

            Assert.True(stepper.IsEnabled);

            Assert.Equal(0m, root.BuildSettings().Cost.Pricing.DefaultPricing.ReasoningPer1M);

            NumericInput(stepper).Value = 0.0000000000001234567890123456m;

            Assert.Equal(0.0000000000001234567890123456m,
                root.BuildSettings().Cost.Pricing.DefaultPricing.ReasoningPer1M);

            enabled.IsChecked = false;

            Assert.False(field.IsSet);

            Assert.False(stepper.IsEnabled);

            Assert.Null(root.BuildSettings().Cost.Pricing.DefaultPricing.ReasoningPer1M);
        });

    [Fact]
    public Task Decimal_stepper_keeps_host_and_daemon_integer_bindings_two_way() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = CreateRoot(store);

            HostPage host = new() { DataContext = root };

            DaemonPage daemon = new() { DataContext = root };

            NumericUpDown port = NumericInput(StepperFor(host, "host.port"));

            NumericUpDown jobs = NumericInput(StepperFor(daemon, "daemon.maxConcurrentJobs"));

            port.Value = 6123m;

            jobs.Value = 7m;

            Assert.Equal(6123, root.Host.Port);

            Assert.Equal(7, root.Daemon.MaxConcurrentJobs);

            Assert.Equal(6123, root.BuildSettings().Host.Port);

            Assert.Equal(7, root.BuildSettings().Daemon.MaxConcurrentJobs);

            root.Host.Port = 6124;

            root.Daemon.MaxConcurrentJobs = 8;

            Assert.Equal(6124m, port.Value);

            Assert.Equal(8m, jobs.Value);

            port.Value = 6125m;

            Assert.Equal(6125, root.BuildSettings().Host.Port);
        });

    [Fact]
    public Task Generic_long_stepper_preserves_values_above_two_to_the_fifty_third() =>
        ui.RunAsync(() =>
        {
            const long original = 9_007_199_254_740_993;

            SettingDescriptor descriptor = new(
                "test.long",
                ConfigSection.Cost,
                "Long",
                "Exact integer binding fixture.",
                SettingKind.Long,
                Min: 0,
                Max: long.MaxValue,
                Increment: 1);

            GenericSettingFieldViewModel field = new(descriptor, original);

            LabeledStepper stepper = new()
            {
                Minimum = 0,
                Maximum = long.MaxValue,
                Increment = 1,
                DataContext = field,
            };

            using IDisposable binding = stepper.Bind(
                LabeledStepper.ValueProperty,
                new Binding(nameof(GenericSettingFieldViewModel.NumericValue)) { Mode = BindingMode.TwoWay });

            NumericUpDown numeric = NumericInput(stepper);

            Assert.Equal(9_007_199_254_740_993m, numeric.Value);

            numeric.Value = 9_007_199_254_740_995m;

            Assert.Equal(9_007_199_254_740_995L, Assert.IsType<long>(field.Value));

            field.Value = 9_007_199_254_740_997L;

            Assert.Equal(9_007_199_254_740_997m, numeric.Value);
        });

    [Fact]
    public Task Inbound_grant_precision_survives_generic_view_without_an_editable_money_control() =>
        ui.RunAsync(() =>
        {
            const string json = """
                {"integrations":{"a2A":{"inboundPeers":[{
                  "peerId":"configured-peer","enabled":true,"credentialEnvironmentVariable":"PRIVATE_PEER_TOKEN",
                  "campaignId":null,"grant":{"workspaces":[],"tools":[],"memory":[],
                  "budget":{"usd":{"mode":"finite","value":1.234567890123456789},"tokens":{"mode":"unlimited"}},
                  "delegation":["local"],"expiresAtUtc":null}}]}}}
                """;

            ArcanumSettings original = JsonSerializer.Deserialize(
                json, ConfigurationJsonContext.Default.ArcanumSettings)!;

            using InMemoryConfigurationStore store = new(original);

            ConfigurationViewModel root = CreateRoot(store);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Integrations,
                DataContext = root,
            };

            const string key = "integrations.a2A.inboundPeers.grant.budget.usd.value";

            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<LabeledStepper>(),
                stepper => stepper.Key == key);

            GenericSettingFieldViewModel field = Assert.Single(
                root.GetOrCreateGenericSection(ConfigSection.Integrations).Fields,
                candidate => candidate.Descriptor.Key == key);

            Assert.True(field.IsCollectionTemplate);

            field.NumericValue = 0;

            var peer = Assert.Single(root.BuildSettings().Integrations.A2A.InboundPeers);

            Assert.Equal(1.234567890123456789m, peer.Grant!.Budget!.Usd!.Value);

            Assert.Equal("PRIVATE_PEER_TOKEN", peer.CredentialEnvironmentVariable);
        });

    [Fact]
    public Task Provider_and_model_integer_steppers_keep_compiled_bindings_two_way() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new(new ArcanumSettings
            {
                Providers = [new ProviderSettings
                {
                    Name = "router",
                    Endpoint = "https://openrouter.ai/api/v1",
                    Models = [new ModelEntry { Name = "model" }],
                }],
            });

            ConfigurationViewModel root = CreateRoot(store);

            var provider = Assert.Single(root.Providers.Providers);

            ProvidersPage page = new() { DataContext = root };

            ItemsControl providers = Assert.Single(page.GetLogicalDescendants().OfType<ItemsControl>());

            Control row = providers.ItemTemplate!.Build(provider)!;

            row.DataContext = provider;

            NumericUpDown context = NumericInput(Assert.Single(row.GetLogicalDescendants().OfType<LabeledStepper>(),
                stepper => stepper.Label == "Context window limit"));

            context.Value = 32768m;

            Assert.Equal(32768, provider.ContextWindowLimit);

            provider.ContextWindowLimit = 65536;

            Assert.Equal(65536m, context.Value);

            ItemsControl models = Assert.Single(row.GetLogicalDescendants().OfType<ItemsControl>(), control => control.GetType() == typeof(ItemsControl));

            var model = Assert.Single(provider.Models);

            model.HasReasoning = true;

            model.HasReasoningMaxBudgetTokens = true;

            Control modelRow = models.ItemTemplate!.Build(model)!;

            modelRow.DataContext = model;

            NumericUpDown reasoning = NumericInput(Assert.Single(modelRow.GetLogicalDescendants().OfType<LabeledStepper>(),
                stepper => stepper.Label == "Maximum reasoning budget"));

            reasoning.Value = 1234m;

            Assert.Equal(1234, model.ReasoningMaxBudgetTokens);

            model.ReasoningMaxBudgetTokens = 4321;

            Assert.Equal(4321m, reasoning.Value);

            ProviderSettings built = Assert.Single(root.BuildSettings().Providers);

            Assert.Equal(65536, built.ContextWindowLimit);

            Assert.Equal(4321, Assert.Single(built.Models).Reasoning!.MaxBudgetTokens);
        });

    [Fact]
    public Task Existing_Guild_reconciliation_configuration_is_editable_in_the_provider_page() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new(new ArcanumSettings
            {
                Providers = [new ProviderSettings
                {
                    Name = "router",
                    Endpoint = "https://openrouter.ai/api/v1",
                    Models = ["model"],
                }],
            });

            ConfigurationViewModel root = CreateRoot(store);

            var provider = Assert.Single(root.Providers.Providers);

            ProvidersPage page = new() { DataContext = root };

            ItemsControl providers = Assert.Single(page.GetLogicalDescendants().OfType<ItemsControl>());

            Control row = providers.ItemTemplate!.Build(provider)!;

            row.DataContext = provider;

            LabeledPicker picker = Assert.Single(row.GetLogicalDescendants().OfType<LabeledPicker>(),
                control => control.Key == "providers.guildUsageReconciliation");

            Assert.Equal(GuildUsageReconciliationSource.None, picker.SelectedItem);

            picker.SelectedItem = GuildUsageReconciliationSource.OpenRouterGeneration;

            Assert.Equal(GuildUsageReconciliationSource.OpenRouterGeneration,
                Assert.Single(root.BuildSettings().Providers).GuildUsageReconciliation);

            provider.Type = AiProviderKind.CodexCli;

            Assert.False(picker.IsVisible);

            Assert.Equal(GuildUsageReconciliationSource.None,
                Assert.Single(root.BuildSettings().Providers).GuildUsageReconciliation);

            provider.Type = AiProviderKind.OpenAICompatible;

            Assert.Equal(GuildUsageReconciliationSource.None, picker.SelectedItem);
        });

    private static ConfigurationViewModel CreateRoot(IArcanumConfigurationStore store) =>
        new(
            store,
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance,
            ImmediateArcanumClientMutationBoundary.Instance);

    private static LabeledStepper StepperFor(Control view, string key) =>
        Assert.Single(view.GetLogicalDescendants().OfType<LabeledStepper>(),
            stepper => stepper.Key == key);

    private static NumericUpDown NumericInput(LabeledStepper stepper) =>
        Assert.IsType<NumericUpDown>(stepper.FindControl<NumericUpDown>("StepperControl"));

    private static (TextBox Entry, ButtonSpinner Spinner) ApplyNumericTemplate(NumericUpDown numeric)
    {
        TextBox? entry = null;

        ButtonSpinner? spinner = null;

        numeric.Template = new FuncControlTemplate<NumericUpDown>((_, scope) =>
        {
            entry = new TextBox { Name = "PART_TextBox" };

            spinner = new ButtonSpinner { Name = "PART_Spinner", Content = entry };

            scope.Register("PART_TextBox", entry);

            scope.Register("PART_Spinner", spinner);

            return spinner;
        });

        numeric.ApplyTemplate();

        return (Assert.IsType<TextBox>(entry), Assert.IsType<ButtonSpinner>(spinner));
    }

    private static void SpinUp(ButtonSpinner spinner) =>
        spinner.RaiseEvent(new SpinEventArgs(SpinDirection.Increase)
        {
            RoutedEvent = Spinner.SpinEvent,
        });

    [Fact]
    public Task DataContext_assigned_after_section_builds_the_selected_section() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = new(
                store,
                new NoopDialogService(),
                new SynchronousUiDispatcher(),
                NullLogger<ConfigurationViewModel>.Instance,
                ImmediateArcanumClientMutationBoundary.Instance);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Features,
            };

            view.DataContext = root;

            GenericSectionViewModel section = Assert.IsType<GenericSectionViewModel>(view.DataContext);

            Assert.Equal(ConfigSection.Features, section.Section);
            Assert.NotEmpty(section.Fields);
        });

    [Fact]
    public Task Configuration_loaded_after_view_creation_rebuilds_field_controls() =>
        ui.RunAsync(async () =>
        {
            using DeferredConfigurationStore store = new();
            QueuedUiDispatcher dispatcher = new();

            ConfigurationViewModel root = new(
                store,
                new NoopDialogService(),
                dispatcher,
                NullLogger<ConfigurationViewModel>.Instance,
                ImmediateArcanumClientMutationBoundary.Instance);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Features,
                DataContext = root,
            };

            Assert.False(WebBrowsingField(view).BoolValue);

            store.Complete(new ArcanumSettings
            {
                Features = new FeatureSettings
                {
                    WebBrowsing = true,
                },
            });

            await WaitForLoadAsync(root, dispatcher);

            Assert.True(WebBrowsingField(view).BoolValue);
        });

    /// <summary>
    /// Every control the generic editor hands an operator must address a path Save can write. A
    /// descriptor that names a field of a collection element (for example
    /// <c>integrations.a2A.skills.id</c>) has no per-element UI here, so rendering it as an ordinary
    /// box drops whatever is typed while the save still reports success.
    /// </summary>
    [Fact]
    public Task Every_input_control_the_generic_editor_renders_addresses_a_settable_path() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            List<string> unsettable = [];

            foreach (ConfigSection section in Enum.GetValues<ConfigSection>())
            {
                if (SectionDescriptors.IsPolished(section))
                {
                    continue;
                }

                ConfigurationViewModel root = new(
                    store,
                    new NoopDialogService(),
                    new SynchronousUiDispatcher(),
                    NullLogger<ConfigurationViewModel>.Instance,
                    ImmediateArcanumClientMutationBoundary.Instance);

                GenericSettingsSectionView view = new()
                {
                    Section = section,
                };

                view.DataContext = root;

                unsettable.AddRange(
                    InputFields(view)
                        .Select(static field => field.Descriptor.Key)
                        .Where(static key => GenericSettingsUpdater.ResolveValueType(key) is null));
            }

            Assert.True(
                unsettable.Count == 0,
                "The generic editor renders editable controls for paths Save cannot write, so operator input is"
                + $" silently discarded: {string.Join(", ", unsettable.Distinct().Order(StringComparer.Ordinal))}");
        });

    /// <summary>
    /// A value typed into a chips box but never committed with Enter or Add must not evaporate. The
    /// commit affordances are easy to miss, and the entry is the ordinary-looking box the operator
    /// types into, so leaving it — to press Save, to open another section, to close the window — has
    /// to take the value with it. On a deny list such as <c>security.ward.forbiddenArts</c> the
    /// dropped entry fails open: the operator believes a command is blocked and it is not.
    /// </summary>
    [Fact]
    public Task Pending_chip_text_is_committed_when_the_entry_loses_focus() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = new(
                store,
                new NoopDialogService(),
                new SynchronousUiDispatcher(),
                NullLogger<ConfigurationViewModel>.Instance,
                ImmediateArcanumClientMutationBoundary.Instance);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Security,
                DataContext = root,
            };

            ChipsEditor chips = ChipsEditorFor(view, "security.ward.forbiddenArts");

            TextBox entry = PendingEntry(chips);

            entry.Text = "rm -rf";

            entry.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

            Assert.Contains("rm -rf", chips.Text, StringComparison.Ordinal);

            GenericSettingFieldViewModel field =
                Assert.IsType<GenericSettingFieldViewModel>(chips.DataContext);

            Assert.Contains("rm -rf", field.StringValue, StringComparison.Ordinal);

            Assert.Empty(entry.Text);

            Assert.Contains(
                "rm -rf",
                root.BuildSettings().Security.Ward.ForbiddenArts);
        });

    /// <summary>
    /// Typing into a chips box has to count as an edit even before the value is committed. Otherwise
    /// Save stays greyed out with the operator's text sitting in front of them, and the close
    /// confirmation — which asks only when the editor is dirty — throws it away without a prompt.
    /// </summary>
    [Fact]
    public Task Typing_into_a_chip_entry_marks_the_editor_dirty() =>
        ui.RunAsync(() =>
        {
            using InMemoryConfigurationStore store = new();

            ConfigurationViewModel root = new(
                store,
                new NoopDialogService(),
                new SynchronousUiDispatcher(),
                NullLogger<ConfigurationViewModel>.Instance,
                ImmediateArcanumClientMutationBoundary.Instance);

            GenericSettingsSectionView view = new()
            {
                Section = ConfigSection.Security,
                DataContext = root,
            };

            Assert.False(root.IsDirty);

            PendingEntry(ChipsEditorFor(view, "security.spellWorkspaceRoots")).Text = "/home/me/projects";

            Assert.True(root.IsDirty);
        });

    private static ChipsEditor ChipsEditorFor(GenericSettingsSectionView view, string key) =>
        view.GetLogicalDescendants()
            .OfType<ChipsEditor>()
            .Single(chips => chips.Key == key);

    private static TextBox PendingEntry(ChipsEditor chips) =>
        Assert.IsType<TextBox>(chips.FindControl<TextBox>("NewItemEntry"));

    private static IEnumerable<GenericSettingFieldViewModel> InputFields(
        GenericSettingsSectionView view) =>
        view.GetLogicalDescendants()
            .Where(static control =>
                control is LabeledEntry
                    or LabeledToggle
                    or LabeledStepper
                    or LabeledPicker
                    or LabeledColorEntry
                    or ChipsEditor
                    or TextBox)
            .OfType<StyledElement>()
            .Select(static control => control.DataContext)
            .OfType<GenericSettingFieldViewModel>();

    private static GenericSettingFieldViewModel WebBrowsingField(
        GenericSettingsSectionView view) =>
        view.GetLogicalDescendants()
            .OfType<LabeledToggle>()
            .Select(static toggle => toggle.DataContext)
            .OfType<GenericSettingFieldViewModel>()
            .Single(static field => field.Descriptor.Key == "features.webBrowsing");

    private static async Task WaitForLoadAsync(
        ConfigurationViewModel root,
        QueuedUiDispatcher dispatcher)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));

        while (!root.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal))
        {
            await dispatcher.WaitForPostAsync(deadline.Token);

            dispatcher.Drain();
        }
    }

    private sealed class InMemoryConfigurationStore(ArcanumSettings? initial = null) : IArcanumConfigurationStore
    {
        public string ConfigurationFilePath => "memory://arcanum.json";

        public event EventHandler? ExternalChange
        {
            add { }
            remove { }
        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(initial ?? new ArcanumSettings());

        public Task<ConfigurationWriteResult> WriteAsync(
            ArcanumSettings settings,
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

    private sealed class DeferredConfigurationStore : IArcanumConfigurationStore
    {
        private readonly TaskCompletionSource<ArcanumSettings> _settings =
            new();

        public string ConfigurationFilePath => "memory://arcanum.json";

        public event EventHandler? ExternalChange
        {
            add { }
            remove { }
        }

        public void Complete(ArcanumSettings settings) => _settings.SetResult(settings);

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) =>
            _settings.Task.WaitAsync(ct);

        public Task<ConfigurationWriteResult> WriteAsync(
            ArcanumSettings settings,
            CancellationToken ct = default) =>
            Task.FromResult(new ConfigurationWriteResult(true, [], null));

        public void Dispose()
        {
        }
    }

    private sealed class QueuedUiDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _actions = new();

        private readonly object _gate = new();

        private TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForPostAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return _actions.Count > 0 ? Task.CompletedTask : _posted.Task.WaitAsync(cancellationToken);
            }
        }

        public void Post(Action action)
        {
            lock (_gate)
            {
                _actions.Enqueue(action);

                _posted.TrySetResult();
            }
        }

        public Task InvokeAsync(Action action)
        {
            TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Post(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            return completion.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            TaskCompletionSource<T> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Post(() =>
            {
                try
                {
                    completion.SetResult(func());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            return completion.Task;
        }

        public void Drain()
        {
            while (true)
            {
                Action? action;

                lock (_gate)
                {
                    action = _actions.Count == 0 ? null : _actions.Dequeue();

                    if (action is null)
                    {
                        _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                if (action is null)
                {
                    return;
                }

                action();
            }
        }
    }
}
