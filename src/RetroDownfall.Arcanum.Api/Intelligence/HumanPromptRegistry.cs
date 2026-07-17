using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// In-process registry: MCP <c>ask_human</c> and standard MCP elicitation await
/// <see cref="TrySubmitResponse"/> (HTTP or CLI) without blocking threads.
/// </summary>
public sealed class HumanPromptRegistry : IHumanPromptRegistry
{

    /// <summary>
    /// Soft cap on concurrent waiters. Excess registrations fail with
    /// <see cref="HumanPromptCapExceededException"/> rather than growing without bound.
    /// </summary>
    public const int MaxConcurrentWaiters = 64;

    /// <summary>
    /// Hard ceiling leak guard when the caller token never cancels. Prefer linked inference/stream
    /// cancellation first (default inference timeout is typically 10 minutes); this ceiling is only
    /// a backstop.
    /// </summary>
    public static readonly TimeSpan HardCeiling = TimeSpan.FromMinutes(30);

    /// <summary>Overridable in tests to exercise the hard-ceiling path without waiting 30 minutes.</summary>
    internal TimeSpan CeilingForTesting { get; set; } = HardCeiling;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<string> WaitForResponseAsync(string promptId, CancellationToken ct)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        if (_waiters.Count >= MaxConcurrentWaiters)
        {

            throw new HumanPromptCapExceededException();

        }

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_waiters.TryAdd(promptId, tcs))
        {

            throw new InvalidOperationException(
                $"A human prompt is already registered for promptId '{promptId}'. Use a new UUID for each ask_human call.");

        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        linkedCts.CancelAfter(CeilingForTesting);

        try
        {

            using (linkedCts.Token.Register(
                       () =>
                       {

                           if (_waiters.TryRemove(promptId, out TaskCompletionSource<string>? removed)
                               && ReferenceEquals(removed, tcs))
                           {

                               if (ct.IsCancellationRequested)
                               {

                                   _ = removed.TrySetCanceled(ct);

                               }
                               else
                               {

                                   _ = removed.TrySetException(new HumanPromptTimeoutException());

                               }

                           }

                       }))
            {

                return await tcs.Task.ConfigureAwait(false);

            }

        }
        catch (HumanPromptTimeoutException)
        {

            throw;

        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {

            if (!ct.IsCancellationRequested)
            {

                throw new HumanPromptTimeoutException();

            }

            throw;

        }
        finally
        {

            try
            {

                if (_waiters.TryRemove(promptId, out TaskCompletionSource<string>? left)
                    && ReferenceEquals(left, tcs)
                    && !left.Task.IsCompleted)
                {

                    _ = left.TrySetCanceled(CancellationToken.None);

                }

            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {

            }

        }

    }

    /// <inheritdoc />
    public bool TrySubmitResponse(string promptId, string response)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        if (!_waiters.TryRemove(promptId, out TaskCompletionSource<string>? tcs))
        {

            return false;

        }

        return tcs.TrySetResult(response);

    }

    /// <summary>Current waiter count for assertions in the test suite.</summary>
    internal int WaiterCountForTesting => _waiters.Count;

}
