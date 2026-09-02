using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Compendium.Ux;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// <c>OnAboutClick</c> is <c>async void</c>: an exception it does not catch is not thrown back to the
/// native-menu caller, it is posted to whatever <see cref="SynchronizationContext"/> was current when
/// the handler started and rethrown there — on the real app that is the Avalonia dispatcher, and an
/// unhandled rethrow on the dispatcher crashes the process. <see cref="RecordingSynchronizationContext"/>
/// stands in for that dispatcher so the test can observe the rethrow without one.
/// </summary>
public sealed class AppAboutDialogTests
{

    [Fact]
    public void OnAboutClick_WhenTheDialogServiceThrows_DoesNotCrashTheDispatcher()
    {

        SynchronizationContext? previous = SynchronizationContext.Current;

        RecordingSynchronizationContext recorder = new();

        SynchronizationContext.SetSynchronizationContext(recorder);

        try
        {

            ServiceCollection serviceCollection = new();

            serviceCollection.AddSingleton<IDialogService>(new ThrowingDialogService());

            using ServiceProvider provider = serviceCollection.BuildServiceProvider();

            App app = new();

            App.ConfigureServices(provider);

            app.OnAboutClick(sender: null, EventArgs.Empty);

        }
        finally
        {

            SynchronizationContext.SetSynchronizationContext(previous);

        }

        Assert.Null(recorder.Observed);

    }

    [Fact]
    public void OnAboutClick_WhenTheProviderIsAlreadyDisposed_DoesNotCrashTheDispatcher()
    {

        SynchronizationContext? previous = SynchronizationContext.Current;

        RecordingSynchronizationContext recorder = new();

        SynchronizationContext.SetSynchronizationContext(recorder);

        try
        {

            ServiceCollection serviceCollection = new();

            serviceCollection.AddSingleton<IDialogService>(new NoopDialogService());

            ServiceProvider provider = serviceCollection.BuildServiceProvider();

            // Disposed before the click — GetRequiredService<IDialogService>() itself throws
            // ObjectDisposedException here, before ShowAlertAsync is ever reached. This is the most
            // reachable real trigger: macOS keeps the native menu bar (and its Click handlers) live
            // during teardown, after DI has already been disposed.
            provider.Dispose();

            App app = new();

            App.ConfigureServices(provider);

            app.OnAboutClick(sender: null, EventArgs.Empty);

        }
        finally
        {

            SynchronizationContext.SetSynchronizationContext(previous);

        }

        Assert.Null(recorder.Observed);

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No") =>
            Task.FromResult(true);

    }

    private sealed class ThrowingDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") =>
            Task.FromException(new InvalidOperationException("No owner window; the visual root was disposed."));

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No") =>
            Task.FromResult(true);

    }

    /// <summary>
    /// Runs posted callbacks inline and captures anything they throw instead of letting it escape onto
    /// the test thread — mirroring how a real dispatcher's Post loop would carry the rethrow into its
    /// own message pump rather than back to this call site.
    /// </summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {

        public Exception? Observed { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {

            try
            {

                d(state);

            }
            catch (Exception ex)
            {

                Observed = ex;

            }

        }

        public override void Send(SendOrPostCallback d, object? state) => Post(d, state);

    }

}
