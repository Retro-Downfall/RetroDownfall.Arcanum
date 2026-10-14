namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class AuthorityCarrierShapes
{
    // The ways a handle widens to a supertype and then escapes: patterns, loops, deconstruction, element and return flow, an
    // indexer argument, virtual dispatch, a primary-constructor base argument, a record property, `this`, a Bind argument and
    // a long loop-carried copy chain.
    private static readonly Dictionary<string, Shape> FlowShapes = new(StringComparer.Ordinal)
    {
        ["pattern-declaration-widening-into-a-field"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    if (lease is IAsyncDisposable disposable)
                    {
                        _slot = disposable;
                    }
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["switch-expression-arm-capture"] = new("""
            public static class Switching
            {
                public static Task Put(object state) => state switch
                {
                    IAsyncDisposable disposable => Task.Run(() => disposable.DisposeAsync().AsTask()),
                    _ => Task.CompletedTask,
                };

                public static Task Go(IGrimoireWorkLease lease) => Put(lease);
            }
            """,
            ["closure|Fixture.Switching.Put{closure#0:disposable}|IGrimoireWorkLease"]),

        ["switch-statement-case-widening-into-a-field"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireExternalEffectGroup group)
                {
                    switch (group)
                    {
                        case IAsyncDisposable disposable:
                            _slot = disposable;

                            break;
                    }
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireExternalEffectGroup"]),

        ["foreach-iteration-variable"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    foreach (object each in new object[] { lease })
                    {
                        _slot = each;
                    }
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["deconstruction-into-locals-then-capture"] = new("""
            public static class Deconstructed
            {
                public static Task Put(IGrimoireWorkLease lease)
                {
                    (object kept, int count) = (lease, 1);

                    return Task.Run(() => kept.ToString());
                }
            }
            """,
            ["closure|Fixture.Deconstructed.Put{closure#0:kept}|IGrimoireWorkLease"]),

        ["array-element-read-of-a-carrier"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    object[] items = [lease];

                    _slot = items[0];
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["tuple-element-read-of-a-carrier"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    (object, int) pair = (lease, 1);

                    _slot = pair.Item1;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["list-indexer-read-of-a-carrier"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireExternalEffectGroup group)
                {
                    List<object> items = [group];

                    _slot = items[0];
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireExternalEffectGroup"]),

        ["collection-call-returning-the-element-type"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    Queue<object> items = new();

                    items.Enqueue(lease);

                    _slot = items.Dequeue();
                }
            }
            """,
            [
                "sink|Fixture.Stash.Put->System.Collections.Generic.Queue<T>.Enqueue|IGrimoireWorkLease",
                "store|Fixture.Stash._slot|IGrimoireWorkLease",
            ]),

        ["return-flow-through-a-helper-that-erases-the-type"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                private static object Erase(IGrimoireWorkLease lease) => lease;

                public void Put(IGrimoireWorkLease lease)
                {
                    _slot = Erase(lease);
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["return-flow-through-an-async-helper"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                private static async Task<object> EraseAsync(IGrimoireExternalEffectGroup group)
                {
                    await Task.Yield();

                    return group;
                }

                public async Task PutAsync(IGrimoireExternalEffectGroup group)
                {
                    _slot = await EraseAsync(group);
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireExternalEffectGroup"]),

        ["return-flow-through-a-local-function"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    _slot = Erase();

                    object Erase() => lease;
                }
            }
            """,
            [
                "closure|Fixture.Stash.Put{closure#0:lease}|IGrimoireWorkLease",
                "store|Fixture.Stash._slot|IGrimoireWorkLease",
            ]),

        ["lease-as-an-indexer-argument"] = new("""
            public sealed class Seen
            {
                private readonly Dictionary<object, int> _seen = [];

                public void Put(IGrimoireWorkLease lease)
                {
                    _seen[lease] = 1;
                }
            }
            """,
            ["sink|Fixture.Seen.Put->System.Collections.Generic.Dictionary<TKey, TValue>.this[]|IGrimoireWorkLease"]),

        ["virtual-dispatch-to-an-override-that-stores"] = new("""
            public class Base
            {
                public virtual void Take(object value)
                {
                }
            }

            public sealed class Derived : Base
            {
                private static object? s_kept;

                public override void Take(object value)
                {
                    s_kept = value;
                }
            }

            public static class Dispatch
            {
                public static void Put(Base target, IGrimoireWorkLease lease) => target.Take(lease);
            }
            """,
            ["store|Fixture.Derived.s_kept|IGrimoireWorkLease"]),

        ["interface-call-to-an-implementation-that-stores"] = new("""
            public interface ISink
            {
                void Take(object value);
            }

            public sealed class KeepingSink : ISink
            {
                private static object? s_kept;

                public void Take(object value)
                {
                    s_kept = value;
                }
            }

            public static class Dispatch
            {
                public static void Put(ISink sink, IGrimoireExternalEffectGroup group) => sink.Take(group);
            }
            """,
            ["store|Fixture.KeepingSink.s_kept|IGrimoireExternalEffectGroup"]),

        ["primary-constructor-base-argument"] = new("""
            public class Keeper(object kept)
            {
                private readonly object _kept = kept;
            }

            public sealed class Forwarder(object value) : Keeper(value)
            {
                public int Count => 1;
            }

            public static class Maker
            {
                public static object Put(IGrimoireWorkLease lease) => new Forwarder(lease);
            }
            """,
            ["store|Fixture.Keeper._kept|IGrimoireWorkLease"]),

        ["record-positional-property-with-a-body"] = new("""
            public sealed record Wrap(object Value)
            {
                public int Count => 1;
            }

            public static class Maker
            {
                private static object? s_kept;

                public static void Put(IGrimoireWorkLease lease)
                {
                    s_kept = new Wrap(lease);
                }
            }
            """,
            ["store|Fixture.Wrap.Value|IGrimoireWorkLease"]),

        ["record-positional-property-without-a-body"] = new("""
            public sealed record Wrap(object Value);

            public static class Maker
            {
                public static object Put(IGrimoireExternalEffectGroup group) => new Wrap(group);
            }
            """,
            ["store|Fixture.Wrap.Value|IGrimoireExternalEffectGroup"]),

        ["lambda-capturing-this-of-a-lease-implementation"] = new("""
            public sealed class Decorator : IGrimoireWorkLease
            {
                public long Generation => 0;

                public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group)
                {
                    group = null;

                    return false;
                }

                public ValueTask DisposeAsync() => default;

                public Task Later() => Task.Run(() => this.DisposeAsync().AsTask());

                public Task LaterImplicit() => Task.Run(() => DisposeAsync().AsTask());
            }
            """,
            [
                "closure|Fixture.Decorator.Later{closure#0:this}|IGrimoireWorkLease",
                "closure|Fixture.Decorator.LaterImplicit{closure#0:this}|IGrimoireWorkLease",
            ]),

        ["linq-closure-capture"] = new("""
            public static class Lazily
            {
                public static IEnumerable<object> Put(IGrimoireWorkLease lease, int[] values) => values.Select(value => (object)lease);
            }
            """,
            ["closure|Fixture.Lazily.Put{closure#0:lease}|IGrimoireWorkLease"]),

        ["extension-method-group-bound-to-a-lease"] = new("""
            public static class Extensions
            {
                public static ValueTask Release(this IGrimoireWorkLease lease) => lease.DisposeAsync();
            }

            public sealed class Hooks
            {
                private Func<ValueTask>? _release;

                public void Put(IGrimoireWorkLease lease)
                {
                    _release = lease.Release;
                }
            }
            """,
            ["delegate-receiver|Fixture.Hooks.Put|IGrimoireWorkLease"]),

        ["bind-taking-an-object-called-with-a-lease"] = new("""
            public sealed class Binder
            {
                public void Bind(object state)
                {
                }

                public void Put(IGrimoireWorkLease lease) => Bind(lease);
            }
            """,
            ["bind|Fixture.Binder.Bind|IGrimoireWorkLease"]),

        ["generic-bind-called-with-a-group"] = new("""
            public sealed class Binder
            {
                public void Bind<T>(T state)
                {
                }

                public void Put(IGrimoireExternalEffectGroup group) => Bind(group);
            }
            """,
            ["bind|Fixture.Binder.Bind|IGrimoireExternalEffectGroup"]),

        ["bind-property-of-a-delegate-type-taking-a-lease"] = new("""
            public sealed class Hooks
            {
                public Func<IGrimoireWorkLease, IDisposable>? Bind { get; set; }
            }
            """,
            ["bind|Fixture.Hooks.Bind|IGrimoireWorkLease"]),

        ["bind-field-of-a-delegate-type-taking-a-group"] = new("""
            public sealed class Hooks
            {
                public Action<IGrimoireExternalEffectGroup>? Bind;
            }
            """,
            ["bind|Fixture.Hooks.Bind|IGrimoireExternalEffectGroup"]),

        ["bind-event-of-a-delegate-type-taking-a-lease"] = new("""
            public sealed class Hooks
            {
                public event Action<IGrimoireWorkLease>? Bind;
            }
            """,
            ["bind|Fixture.Hooks.Bind|IGrimoireWorkLease"]),

        ["long-loop-carried-copy-chain"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease, int count)
                {
                    object? a0 = null, a1 = null, a2 = null, a3 = null, a4 = null, a5 = null, a6 = null, a7 = null, a8 = null, a9 = null, a10 = null;

                    for (int i = 0; i < count; i++)
                    {
                        a10 = a9;
                        a9 = a8;
                        a8 = a7;
                        a7 = a6;
                        a6 = a5;
                        a5 = a4;
                        a4 = a3;
                        a3 = a2;
                        a2 = a1;
                        a1 = a0;
                        a0 = lease;
                    }

                    _slot = a10;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["iterator-yielding-a-lease-as-an-object"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                private static IEnumerable<object> Iterate(IGrimoireWorkLease lease)
                {
                    yield return lease;
                }

                public void Put(IGrimoireWorkLease lease)
                {
                    _slot = Iterate(lease);
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["return-flow-through-a-generic-identity"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                private static T Identity<T>(T value) => value;

                public void Put(IGrimoireExternalEffectGroup group)
                {
                    _slot = Identity(group);
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireExternalEffectGroup"]),

        ["extension-method-called-on-a-lease-stores-its-receiver"] = new("""
            public static class Extensions
            {
                private static object? s_kept;

                public static void Keep(this object value)
                {
                    s_kept = value;
                }
            }

            public static class Use
            {
                public static void Put(IGrimoireWorkLease lease) => lease.Keep();
            }
            """,
            ["store|Fixture.Extensions.s_kept|IGrimoireWorkLease"]),

        ["out-variable-of-a-carrier-dictionary"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    Dictionary<string, object> items = new();

                    items["lease"] = lease;

                    if (items.TryGetValue("lease", out object? found))
                    {
                        _slot = found;
                    }
                }
            }
            """,
            [
                "store|Fixture.Stash.Put->System.Collections.Generic.Dictionary.this[]|IGrimoireWorkLease",
                "store|Fixture.Stash._slot|IGrimoireWorkLease",
            ]),

        ["dynamic-call-handed-a-lease"] = new("""
            public static class Dynamically
            {
                public static void Put(dynamic target, IGrimoireWorkLease lease)
                {
                    target.Take(lease);
                }
            }
            """,
            ["sink|Fixture.Dynamically.Put->dynamic|IGrimoireWorkLease"]),

        ["dynamic-construction-handed-a-group"] = new("""
            public static class Dynamically
            {
                public static object Put(IGrimoireExternalEffectGroup group)
                {
                    dynamic argument = group;

                    return new Constructed(argument);
                }
            }

            public sealed class Constructed
            {
                public Constructed(object value)
                {
                }
            }
            """,
            ["sink|Fixture.Dynamically.Put->dynamic|IGrimoireExternalEffectGroup"]),

        // Negative twins.
        ["pattern-and-loop-variables-used-only-locally"] = new("""
            public static class Local
            {
                public static async Task RunAsync(IGrimoireWorkLease lease, IGrimoireExternalEffectGroup group)
                {
                    if (lease is IAsyncDisposable disposable)
                    {
                        await disposable.DisposeAsync();
                    }

                    switch (group)
                    {
                        case IAsyncDisposable other:
                            await other.DisposeAsync();

                            break;
                    }

                    foreach (object each in new object[] { lease })
                    {
                        _ = each.GetHashCode();
                    }

                    (object kept, int count) = (lease, 1);

                    _ = kept.GetHashCode() + count;
                }
            }
            """,
            []),

        ["numeric-reads-of-a-carrier-are-not-carriers"] = new("""
            public sealed class Counter
            {
                private int _count;

                private long _generation;

                public void Put(IGrimoireWorkLease lease)
                {
                    (object, int) pair = (lease, 1);

                    _count = pair.Item2;

                    object[] items = [lease];

                    _count += items.Length;

                    _generation = lease.Generation;
                }
            }
            """,
            []),

        ["a-helper-result-used-only-locally"] = new("""
            public static class Local
            {
                private static object Erase(IGrimoireWorkLease lease) => lease;

                public static string Put(IGrimoireWorkLease lease)
                {
                    object erased = Erase(lease);

                    return erased.ToString() ?? string.Empty;
                }
            }
            """,
            []),

        ["virtual-dispatch-to-overrides-that-only-look"] = new("""
            public class Base
            {
                public virtual void Take(object value) => _ = value.ToString();
            }

            public sealed class Derived : Base
            {
                public override void Take(object value) => _ = value.GetHashCode();
            }

            public static class Dispatch
            {
                public static void Put(Base target, IGrimoireWorkLease lease) => target.Take(lease);
            }
            """,
            []),

        ["primary-constructor-base-argument-that-is-only-looked-at"] = new("""
            public class Looker(object seen)
            {
                public int Hash { get; } = seen.GetHashCode();
            }

            public sealed class Forwarder(object value) : Looker(value)
            {
                public int Count => 1;
            }

            public static class Maker
            {
                public static int Put(IGrimoireWorkLease lease) => new Forwarder(lease).Hash;
            }
            """,
            []),

        ["a-record-property-that-is-not-a-handle-is-not-a-store"] = new("""
            public sealed record Counted(int Count, string Name);

            public static class Maker
            {
                public static Counted Put(IGrimoireWorkLease lease) => new Counted((int)lease.Generation, "x");
            }
            """,
            []),

        ["bind-members-that-take-no-handle"] = new("""
            public sealed class Plain
            {
                public Func<string, IDisposable>? Bind { get; set; }
            }

            public sealed class Binder
            {
                public void Bind(string name, int value)
                {
                }

                public void Put(IGrimoireWorkLease lease)
                {
                    Bind("generation", (int)lease.Generation);
                }
            }
            """,
            []),

        ["lambda-capturing-this-of-an-ordinary-class"] = new("""
            public sealed class Ordinary
            {
                private int _count;

                public Task Later() => Task.Run(() => this._count + Count());

                private int Count() => 1;
            }
            """,
            []),
    };
}
