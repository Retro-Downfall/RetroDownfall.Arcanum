using Microsoft.Extensions.Logging.Abstractions;
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
            NullLogger<ConfigurationViewModel>.Instance);

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
            NullLogger<ConfigurationViewModel>.Instance);

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
