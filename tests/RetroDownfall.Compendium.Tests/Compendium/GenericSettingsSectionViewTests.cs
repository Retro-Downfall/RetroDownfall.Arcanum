using Microsoft.Extensions.Logging.Abstractions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views;
using RetroDownfall.Compendium.Ux.Views.Controls;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("AvaloniaBinding")]
public sealed class GenericSettingsSectionViewTests
{
    [Fact]
    public void DataContext_assigned_after_section_builds_the_selected_section()
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
    }

    [Fact]
    public void Configuration_loaded_after_view_creation_rebuilds_field_controls()
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

        WaitForLoad(root, dispatcher);

        Assert.True(WebBrowsingField(view).BoolValue);
    }

    /// <summary>
    /// Every control the generic editor hands an operator must address a path Save can write. A
    /// descriptor that names a field of a collection element (for example
    /// <c>integrations.a2A.skills.id</c>) has no per-element UI here, so rendering it as an ordinary
    /// box drops whatever is typed while the save still reports success.
    /// </summary>
    [Fact]
    public void Every_input_control_the_generic_editor_renders_addresses_a_settable_path()
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
    }

    /// <summary>
    /// A value typed into a chips box but never committed with Enter or Add must not evaporate. The
    /// commit affordances are easy to miss, and the entry is the ordinary-looking box the operator
    /// types into, so leaving it — to press Save, to open another section, to close the window — has
    /// to take the value with it. On a deny list such as <c>security.ward.forbiddenArts</c> the
    /// dropped entry fails open: the operator believes a command is blocked and it is not.
    /// </summary>
    [Fact]
    public void Pending_chip_text_is_committed_when_the_entry_loses_focus()
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
    }

    /// <summary>
    /// Typing into a chips box has to count as an edit even before the value is committed. Otherwise
    /// Save stays greyed out with the operator's text sitting in front of them, and the close
    /// confirmation — which asks only when the editor is dirty — throws it away without a prompt.
    /// </summary>
    [Fact]
    public void Typing_into_a_chip_entry_marks_the_editor_dirty()
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
    }

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

    private static void WaitForLoad(
        ConfigurationViewModel root,
        QueuedUiDispatcher dispatcher)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            dispatcher.Drain();

            if (root.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal))
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"Timed out waiting for configuration load. Status={root.StatusMessage}");
    }

    private sealed class InMemoryConfigurationStore : IArcanumConfigurationStore
    {
        public string ConfigurationFilePath => "memory://arcanum.json";

        public event EventHandler? ExternalChange
        {
            add { }
            remove { }
        }

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new ArcanumSettings());

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

        public void Post(Action action)
        {
            lock (_gate)
            {
                _actions.Enqueue(action);
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
