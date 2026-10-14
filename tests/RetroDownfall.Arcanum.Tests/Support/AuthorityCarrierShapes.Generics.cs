namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class AuthorityCarrierShapes
{
    // A handle that flows into a type parameter is judged by what the callee or the member does with the type parameter, not by the
    // type the call site closed it over, and a container that is a subclass, an implementation or a constraint of a handle holder
    // holds the handle too.
    private static readonly Dictionary<string, Shape> GenericShapes = new(StringComparer.Ordinal)
    {
        ["generic-method-erases-a-lease-into-an-object-field"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                private void Keep<T>(T value)
                {
                    _slot = value;
                }

                public void Put(IGrimoireWorkLease lease) => Keep(lease);
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["closed-generic-static-field"] = new("""
            public static class Box<T>
            {
                public static T? Value;
            }

            public static class Closed
            {
                public static void Put(IGrimoireWorkLease lease)
                {
                    Box<IGrimoireWorkLease>.Value = lease;
                }
            }
            """,
            ["store|Fixture.Box.Value|IGrimoireWorkLease"]),

        ["closed-generic-static-property"] = new("""
            public static class Box<T>
            {
                public static T? Current { get; set; }
            }

            public static class Closed
            {
                public static void Put(IGrimoireExternalEffectGroup group)
                {
                    Box<IGrimoireExternalEffectGroup>.Current = group;
                }
            }
            """,
            ["store|Fixture.Box.Current|IGrimoireExternalEffectGroup"]),

        ["closed-generic-static-field-written-by-a-generic-setter"] = new("""
            public static class Box<T>
            {
                public static T? Value;

                public static void Set(T value) => Value = value;
            }

            public static class Closed
            {
                public static void Put(IGrimoireWorkLease lease) => Box<IGrimoireWorkLease>.Set(lease);
            }
            """,
            ["store|Fixture.Box.Value|IGrimoireWorkLease"]),

        ["generic-primary-constructor-closed-over-a-lease"] = new("""
            public sealed class Holder<T>(T value)
            {
                public override string ToString() => value!.ToString()!;
            }

            public static class Maker
            {
                public static string Put(IGrimoireWorkLease lease) => new Holder<IGrimoireWorkLease>(lease).ToString();
            }
            """,
            ["primary-constructor|Fixture.Holder(value)|IGrimoireWorkLease"]),

        ["generic-method-closing-over-its-type-parameter"] = new("""
            public static class Detached
            {
                private static Task Run<T>(T value) => Task.Run(() => value!.ToString());

                public static Task Put(IGrimoireWorkLease lease) => Run(lease);
            }
            """,
            ["closure|Fixture.Detached.Run{closure#0:value}|IGrimoireWorkLease"]),

        ["generic-method-returning-a-closure-over-an-explicit-type-argument"] = new("""
            public static class Later
            {
                private static Func<T> Defer<T>(T value) => () => value;

                private static object? s_kept;

                public static void Put(IGrimoireWorkLease lease)
                {
                    s_kept = Defer<object>(lease);
                }
            }
            """,
            ["closure|Fixture.Later.Defer{closure#0:value}|IGrimoireWorkLease"]),

        ["generic-method-returning-a-closure-over-an-inferred-type-argument"] = new("""
            public static class Later
            {
                private static Func<T> Defer<T>(T value) => () => value;

                private static object? s_kept;

                public static void Put(IGrimoireWorkLease lease)
                {
                    s_kept = Defer(lease);
                }
            }
            """,
            ["closure|Fixture.Later.Defer{closure#0:value}|IGrimoireWorkLease"]),

        ["anonymous-type-holding-a-lease-captured-by-a-lambda"] = new("""
            public static class Bagged
            {
                public static Task Put(IGrimoireWorkLease lease)
                {
                    var bag = new { Lease = lease };

                    return Task.Run(() => bag.Lease.DisposeAsync().AsTask());
                }
            }
            """,
            ["closure|Fixture.Bagged.Put{closure#0:bag}|IGrimoireWorkLease"]),

        ["subclass-of-a-generic-container-of-leases"] = new("""
            public sealed class Leases : List<IGrimoireWorkLease>
            {
            }

            public sealed class Shelf
            {
                private readonly Leases _all = new();

                public void Put(IGrimoireWorkLease lease) => _all.Add(lease);
            }
            """,
            ["field|Fixture.Shelf._all|IGrimoireWorkLease"]),

        ["subclass-of-a-generic-dictionary-of-groups"] = new("""
            public sealed class Groups : Dictionary<string, IGrimoireExternalEffectGroup>
            {
            }

            public sealed class Shelf
            {
                private Groups? _groups;
            }
            """,
            ["field|Fixture.Shelf._groups|IGrimoireExternalEffectGroup"]),

        ["implementation-of-a-generic-interface-of-leases"] = new("""
            public sealed class Source : IEnumerable<IGrimoireWorkLease>
            {
                public IEnumerator<IGrimoireWorkLease> GetEnumerator() => throw new NotSupportedException();

                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }

            public sealed class Shelf
            {
                private static Source? s_source;
            }
            """,
            ["field|Fixture.Shelf.s_source|IGrimoireWorkLease"]),

        ["type-parameter-constrained-to-a-lease"] = new("""
            public sealed class Constrained<T> where T : IGrimoireWorkLease
            {
                private T? _held;
            }
            """,
            ["field|Fixture.Constrained._held|IGrimoireWorkLease"]),

        ["type-parameter-constrained-to-a-group-property"] = new("""
            public sealed class Constrained<TGroup> where TGroup : class, IGrimoireExternalEffectGroup
            {
                public TGroup? Current { get; set; }
            }
            """,
            ["property|Fixture.Constrained.Current|IGrimoireExternalEffectGroup"]),

        // Negative twins.
        ["framework-list-of-leases-that-never-escapes"] = new("""
            public static class Local
            {
                public static async Task RunAsync(IGrimoireWorkLease lease)
                {
                    List<IGrimoireWorkLease> held = [];

                    held.Add(lease);

                    foreach (IGrimoireWorkLease each in held)
                    {
                        await each.DisposeAsync();
                    }
                }
            }
            """,
            []),

        ["generic-helper-that-only-reads"] = new("""
            public static class Reader
            {
                private static string Look<T>(T value) => value!.ToString() ?? string.Empty;

                public static string Put(IGrimoireWorkLease lease) => Look(lease);
            }
            """,
            []),

        ["type-parameter-constrained-to-an-unrelated-interface"] = new("""
            public sealed class Constrained<T> where T : IDisposable
            {
                private T? _held;
            }

            public sealed class Open<T>
            {
                private T? _held;
            }
            """,
            []),

        ["self-referential-generic-types-terminate"] = new("""
            public sealed class Node : List<Node>, IComparable<Node>, IEquatable<Node>
            {
                private Node? _head;

                public int CompareTo(Node? other) => 0;

                public bool Equals(Node? other) => ReferenceEquals(this, other);
            }

            public sealed class Ring<T> : List<Ring<T>> where T : IComparable<T>
            {
                private Ring<T>? _next;
            }
            """,
            []),
    };
}
