using Avalonia.Threading;
using Xunit;

/// <summary>
/// Serializes the tests that build real Avalonia controls and let them create bindings.
///
/// Avalonia's binding-expression compiler keeps process-wide mutable state:
/// <c>ExpressionNodeFactory.CreateFromAst</c> enumerates a shared node list while another thread can
/// still be adding to it, which surfaces as
/// <c>InvalidOperationException: Collection was modified; enumeration operation may not execute</c>
/// inside <c>AvaloniaObject.Bind</c>. The throw lands on whichever view test happened to be running,
/// so it reads as that one test being flaky when the cause is two classes binding at once.
///
/// Every Compendium test that constructs a view — anything reaching <c>GenericSettingsSectionView</c>,
/// <c>PresetsPage</c>, or the <c>Labeled*</c> controls — belongs here. View-model-only tests do not.
/// </summary>
[CollectionDefinition(AvaloniaBindingCollectionName.Value, DisableParallelization = true)]
public sealed class AvaloniaBindingCollection : ICollectionFixture<AvaloniaBindingFixture>
{
}

internal static class AvaloniaBindingCollectionName
{

    internal const string Value = "AvaloniaBinding";

}

/// <summary>
/// Owns the dispatcher used by control tests. Serial scheduling alone does not keep xUnit cases on
/// one thread; all setup, binding updates, assertions and disposal run on this live UI loop.
/// </summary>
public sealed class AvaloniaBindingFixture : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private readonly Thread _thread;

    private readonly Dispatcher _dispatcher;

    private Exception? _loopFailure;

    public AvaloniaBindingFixture()
    {
        TaskCompletionSource<Dispatcher> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken stopping = _stop.Token;

        _thread = new Thread(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(dispatcher, DispatcherPriority.Normal));

                started.SetResult(dispatcher);

                dispatcher.MainLoop(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                _loopFailure = error;

                started.TrySetException(error);
            }
        }) { IsBackground = true, Name = "Compendium test UI" };

        _thread.Start();

        try
        {
            _dispatcher = started.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception startupFailure)
        {
            _stop.Cancel();

            if (!_thread.Join(TimeSpan.FromSeconds(10)))
            {
                throw new AggregateException("The Compendium test UI startup failed and its thread did not stop.",
                    startupFailure, new TimeoutException("The Compendium test UI loop did not stop."));
            }

            _stop.Dispose();

            throw;
        }
    }

    public Task RunAsync(Action action) => RunAsync(() =>
    {
        action();

        return Task.CompletedTask;
    });

    public async Task RunAsync(Func<Task> action)
    {
        await _dispatcher.InvokeAsync(action);
    }

    public void Dispose()
    {
        _stop.Cancel();

        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The Compendium test UI loop did not stop.");
        }

        _stop.Dispose();

        if (_loopFailure is not null)
        {
            throw new InvalidOperationException("The Compendium test UI loop failed.", _loopFailure);
        }
    }
}
