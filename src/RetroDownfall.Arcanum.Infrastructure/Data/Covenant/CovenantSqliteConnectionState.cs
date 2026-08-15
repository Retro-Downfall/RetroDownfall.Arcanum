namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Per-connection authorization counters.
/// </summary>
/// <remarks>
/// Held in a <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}" /> keyed
/// by connection, so state cannot outlive its connection and a reopened pooled connection starts
/// from a fresh, fully denied state.
/// </remarks>
internal sealed class CovenantSqliteConnectionState
{

    private readonly int[] _depth = new int[Enum.GetValues<CovenantSqliteAuthorizationKind>().Length];

    private readonly Lock _gate = new();

    /// <summary>
    /// Whether the SQL scalar function for <paramref name="kind" /> should currently return true.
    /// </summary>
    internal bool IsAuthorized(CovenantSqliteAuthorizationKind kind)
    {

        lock (_gate)
        {

            return _depth[(int)kind] > 0;

        }

    }

    internal void Acquire(CovenantSqliteAuthorizationKind kind)
    {

        lock (_gate)
        {

            _depth[(int)kind]++;

        }

    }

    internal void Release(CovenantSqliteAuthorizationKind kind)
    {

        lock (_gate)
        {

            if (_depth[(int)kind] > 0)
            {

                _depth[(int)kind]--;

            }

        }

    }

    /// <summary>
    /// Returns every counter to denied. Used when a connection is (re)initialized, so a pooled
    /// handle can never carry an authorization into its next logical use.
    /// </summary>
    internal void Reset()
    {

        lock (_gate)
        {

            Array.Clear(_depth);

        }

    }

}
