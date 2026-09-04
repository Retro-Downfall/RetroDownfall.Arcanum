using System.Collections;
using System.Reflection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// What a Covenant erasure is allowed to write down: identities, digests, counters, and a phase —
/// never a live lease, an opened handle, or any other live capability.
/// </summary>
/// <remarks>
/// Structural rather than behavioural, because the failure it prevents is a new field rather than a
/// wrong result. A checkpoint or work item that carried a live capability would be a promise the
/// process could keep only until it stopped: recovery reads these shapes back in a later process,
/// where a lease, a connection, or a file descriptor is at best meaningless and at worst a
/// capability nobody is holding any more (§10.20.5).
/// </remarks>
public sealed class CovenantErasureDurableStateTests
{

    /// <summary>Every durable shape a Covenant erasure writes, or resumes from.</summary>
    public static TheoryData<Type> DurableShapes =>
    [
        typeof(CovenantOfflineTransitionEpochsV1),
        typeof(CovenantOfflineTransitionLaunchV4),
        typeof(DataRetentionFactoryTransitionLaunchV2),
        typeof(CovenantResetOfflineTransitionPayloadV1),
        typeof(HealthyCatalogFactoryErasureOfflineTransitionPayloadV1),
        typeof(LocalErasureWorkItemRow),
    ];

    /// <summary>
    /// The value shapes a durable member may be built from, transitively.
    /// </summary>
    /// <remarks>
    /// A closed list rather than a "does it look serializable" heuristic. Every one of these is a
    /// value with no ambient authority attached, so a member built from them is meaningful in a
    /// process that did not create it — which is the only property that matters here.
    /// </remarks>
    private static readonly Type[] AllowedLeafTypes =
    [
        typeof(bool),
        typeof(byte),
        typeof(int),
        typeof(long),
        typeof(uint),
        typeof(ulong),
        typeof(string),
        typeof(Guid),
        typeof(DateTimeOffset),
        typeof(CovenantDigest),
    ];

    [Theory]
    [MemberData(nameof(DurableShapes))]
    public void No_durable_erasure_shape_carries_a_live_capability(Type shape)
    {

        List<string> offenders = [];

        Walk(shape, shape.Name, offenders, []);

        Assert.Empty(offenders);

    }

    private static void Walk(Type type, string path, List<string> offenders, HashSet<Type> seen)
    {

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {

            // A record's compiler-generated equality contract is not durable state.
            if (string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
            {

                continue;

            }

            Inspect(property.PropertyType, $"{path}.{property.Name}", offenders, seen);

        }

    }

    private static void Inspect(Type type, string path, List<string> offenders, HashSet<Type> seen)
    {

        Type unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        if (unwrapped.IsEnum || AllowedLeafTypes.Contains(unwrapped))
        {

            return;

        }

        // A disposable member is a live capability by definition: something has to release it, and a
        // durable row has nobody left to do that.
        if (typeof(IDisposable).IsAssignableFrom(unwrapped)
            || typeof(IAsyncDisposable).IsAssignableFrom(unwrapped)
            || typeof(Delegate).IsAssignableFrom(unwrapped)
            || typeof(Task).IsAssignableFrom(unwrapped))
        {

            offenders.Add(path);

            return;

        }

        if (unwrapped.IsGenericType
            && unwrapped.GetGenericArguments() is [Type element]
            && typeof(IEnumerable).IsAssignableFrom(unwrapped))
        {

            Inspect(element, $"{path}[]", offenders, seen);

            return;

        }

        // A composite of allowed values is allowed, and is checked rather than trusted: an evidence
        // record that grew a handle is exactly the change this suite exists to report.
        if (unwrapped.IsClass || (unwrapped.IsValueType && !unwrapped.IsPrimitive))
        {

            if (!seen.Add(unwrapped))
            {

                return;

            }

            Walk(unwrapped, path, offenders, seen);

            return;

        }

        offenders.Add(path);

    }

}
