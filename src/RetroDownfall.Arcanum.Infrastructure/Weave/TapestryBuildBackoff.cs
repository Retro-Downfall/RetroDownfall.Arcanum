using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Remembers which Tapestry builds failed, so a build that keeps failing is not paid for again on every
/// sweep (DESIGN §21.11).
/// </summary>
/// <remarks>
/// A failed build is abandoned, so the summaries it had already paid for are discarded, and the corpus
/// fingerprint has not moved, so the next sweep starts the same build again and repeats the same paid
/// prefix — forever, when the failure is deterministic. This keeps one record per scope: the identity of
/// the build that failed (the corpus, the tree-shaping settings, and the summary model), how many times
/// in a row that identity has failed, and the earliest moment it may be tried again. The wait doubles
/// with each consecutive failure, starting at one sweep interval, and is capped at a day (or at one
/// interval when the interval is longer than that), so a failure is retried on the next sweep, then
/// every other sweep, then every fourth, and a cause the operator has fixed is picked up within a day.
///
/// <para>A different identity starts over: an edited corpus, a changed setting, or another summary model
/// is a different build, and the failure of the old one says nothing about it. A published or up-to-date
/// scope forgets its record, a completed sweep forgets the records of scopes that no longer exist, and a
/// reset that drops the trees forgets them all. Only a build that paid for something counts — an
/// unavailable embedding provider or an unconfigured summary model fails before any call is made, and a
/// failure that arrives while the sweep is being cancelled is the host stopping, so none of those is
/// recorded here.</para>
///
/// <para>State is in memory and registered as a singleton, because the weaver is created per sweep. It
/// is lost on restart, which at worst costs one repeated build.</para>
/// </remarks>
internal sealed class TapestryBuildBackoff
{
    /// <summary>The longest wait, unless one sweep interval is longer still.</summary>
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromHours(24);

    /// <summary>Stops the doubling well before the arithmetic could overflow a <see cref="TimeSpan"/>.</summary>
    private const int MaximumDoublings = 16;

    private readonly Lock _gate = new();

    private readonly Dictionary<TapestryScope, Record> _records = [];

    /// <summary>Whether this exact build failed recently enough that it should not be started again yet.</summary>
    internal bool IsBackingOff(TapestryScope scope, string buildIdentity, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _records.TryGetValue(scope, out Record? record)
                && string.Equals(record.BuildIdentity, buildIdentity, StringComparison.Ordinal)
                && now < record.RetryAfter;
        }
    }

    /// <summary>Records one more failure of this build and returns when it may be tried again.</summary>
    internal DateTimeOffset RecordFailure(
        TapestryScope scope,
        string buildIdentity,
        DateTimeOffset now,
        TimeSpan sweepInterval)
    {
        lock (_gate)
        {
            int failures = _records.TryGetValue(scope, out Record? previous)
                && string.Equals(previous.BuildIdentity, buildIdentity, StringComparison.Ordinal)
                    ? previous.ConsecutiveFailures + 1
                    : 1;

            DateTimeOffset retryAfter = now + WaitAfter(failures, sweepInterval);

            _records[scope] = new Record(buildIdentity, failures, retryAfter);

            return retryAfter;
        }
    }

    /// <summary>Forgets a scope whose build was published or found already current.</summary>
    internal void RecordSuccess(TapestryScope scope)
    {
        lock (_gate)
        {
            _ = _records.Remove(scope);
        }
    }

    /// <summary>
    /// Forgets every record. A reset that drops the trees is how an operator asks for them to be rebuilt,
    /// and the corpus, settings and model are unchanged, so without this the rebuild would still be told
    /// to back off for up to a day.
    /// </summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }
    }

    /// <summary>
    /// Forgets the record of every scope that is not in <paramref name="live"/>. A scope that has gone — a
    /// deleted Session, a workspace no longer indexed — is never swept again, so nothing else would ever
    /// clear its record.
    /// </summary>
    internal void RetainOnly(IReadOnlyCollection<TapestryScope> live)
    {
        ArgumentNullException.ThrowIfNull(live);

        HashSet<TapestryScope> keep = [.. live];

        lock (_gate)
        {
            foreach (TapestryScope scope in _records.Keys.Where(scope => !keep.Contains(scope)).ToList())
            {
                _ = _records.Remove(scope);
            }
        }
    }

    /// <summary>The wait after the <paramref name="consecutiveFailures"/>th failure of one build.</summary>
    internal static TimeSpan WaitAfter(int consecutiveFailures, TimeSpan sweepInterval)
    {
        TimeSpan ceiling = sweepInterval > MaximumWait ? sweepInterval : MaximumWait;

        int doublings = Math.Clamp(consecutiveFailures - 1, 0, MaximumDoublings);

        TimeSpan wait = TimeSpan.FromTicks(sweepInterval.Ticks * (1L << doublings));

        return wait > ceiling ? ceiling : wait;
    }

    private sealed record Record(string BuildIdentity, int ConsecutiveFailures, DateTimeOffset RetryAfter);
}
