namespace RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

/// <summary>
/// Carries the active campaign's network egress ward from the tool-execution pipeline to the native web
/// tools it invokes in-process.
/// </summary>
/// <remarks>
/// Sanctum validates the model-supplied <c>read_url</c> / <c>browse_web</c> target once, before the tool
/// runs. The reader then follows redirects, and those hops are outside the pipeline's reach — an
/// allowlisted host answering <c>302 Location: https://attacker.example/…</c> would leave the campaign's
/// containment boundary with no <c>SanctumBreach</c> recorded. The pipeline publishes the ward here so
/// the redirect loop can re-run the very same guard on every hop
/// (<c>docs/Arcanum.DESIGN.md</c> &#167;11.27).
/// </remarks>
public static class SanctumEgressWardAmbient
{

    private static readonly AsyncLocal<Func<Uri, CancellationToken, ValueTask<bool>>?> CurrentValue = new();

    /// <summary>The ward for the tool call running on this async flow, or <c>null</c> when unrestricted.</summary>
    public static Func<Uri, CancellationToken, ValueTask<bool>>? Current => CurrentValue.Value;

    public static IDisposable Begin(Func<Uri, CancellationToken, ValueTask<bool>> ward)
    {

        ArgumentNullException.ThrowIfNull(ward);

        Func<Uri, CancellationToken, ValueTask<bool>>? previous = CurrentValue.Value;

        CurrentValue.Value = ward;

        return new Scope(previous);

    }

    private sealed class Scope(Func<Uri, CancellationToken, ValueTask<bool>>? previous) : IDisposable
    {

        private bool _disposed;

        public void Dispose()
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            CurrentValue.Value = previous;

        }

    }

}
