namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// One request's Grimoire admission, held for the whole lifetime of that request's service scope.
/// </summary>
/// <remarks>
/// It lives in Infrastructure rather than beside the middleware that populates it, because the two
/// things that read it afterwards are here: the container that disposes it, and the erasure
/// coordinator that promotes the initiating request out of its own drain. An Api-owned carrier would
/// have to be reached back into from Infrastructure, which is the dependency direction this codebase
/// does not have.
///
/// <para>It acquires nothing in its constructor, and that is a safety property rather than a style
/// choice. Ninety-odd child scopes exist under <c>src</c> — background sweeps, hosted services,
/// startup recovery — and any of them can resolve this type transitively. A holder that took a lease
/// when it was constructed would mint an <i>HTTP request</i> lease from a timer tick, put it in the
/// set stage one waits on, and account a request that never existed.</para>
///
/// <para>It releases only on disposal, and disposal belongs to the request service scope rather than
/// to a middleware <c>finally</c>. A <c>finally</c> after <c>next()</c> runs before the response's
/// completion callbacks, and two of those callbacks write to the Grimoire — the idempotency claim
/// persist among them. Releasing there would leave those writes with no live finisher lifetime, and a
/// closing gate would refuse them silently.</para>
/// </remarks>
/// <remarks>
/// <para>It is disposable both ways on purpose. ASP.NET Core disposes the request scope
/// asynchronously, which is the path that matters; but a container scope disposed <i>synchronously</i>
/// throws outright on a service that implements only <see cref="IAsyncDisposable"/>, so a holder with
/// one disposal would turn any future background scope that resolved it — directly or through
/// something that depends on it — into a crash at teardown. Releasing a request lease is synchronous
/// work in any case, which is what makes the second implementation honest rather than a wrapper around
/// a blocking wait.</para>
/// </remarks>
internal sealed class GrimoireRequestAdmissionScope(IGrimoireConnectionAdmissionGate gate)
    : IAsyncDisposable, IDisposable
{

    private IGrimoireRequestLease? _lease;

    private int _disposed;

    /// <summary>The admitted lease, or <see langword="null"/> for a scope no request populated.</summary>
    internal IGrimoireRequestLease? Lease => _lease;

    /// <summary>
    /// Takes this request's lease, reporting whether ordinary admission is open.
    /// </summary>
    /// <remarks>
    /// Idempotent by design: a second call on an admitted scope reports the first admission rather
    /// than taking a second lease, so a pipeline that reaches admission twice cannot leave a lease
    /// nothing disposes.
    ///
    /// <para>A disposed scope admits nothing. That ordering cannot happen in the request pipeline and
    /// can happen in a fault — a scope torn down by an unwinding container, then reached by something
    /// that outlived it — and admitting there would take a lease no disposal will ever release, which
    /// stage one would then wait out in full on a request that no longer exists.</para>
    /// </remarks>
    internal bool TryAdmit(GrimoireRequestKind kind)
    {

        if (Volatile.Read(ref _disposed) != 0)
        {

            return false;

        }

        if (_lease is not null)
        {

            return true;

        }

        if (!gate.TryAcquireRequestLease(kind, out IGrimoireRequestLease? admitted))
        {

            return false;

        }

        _lease = admitted;

        return true;

    }

    public ValueTask DisposeAsync() => ReleaseAsync();

    public void Dispose()
    {

        ValueTask released = ReleaseAsync();

        if (released.IsCompleted)
        {

            // Observes a fault rather than discarding one; a completed release has nothing to wait on.
            released.GetAwaiter().GetResult();

            return;

        }

        released.AsTask().GetAwaiter().GetResult();

    }

    private ValueTask ReleaseAsync()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return ValueTask.CompletedTask;

        }

        IGrimoireRequestLease? held = _lease;

        _lease = null;

        return held?.DisposeAsync() ?? ValueTask.CompletedTask;

    }

}
