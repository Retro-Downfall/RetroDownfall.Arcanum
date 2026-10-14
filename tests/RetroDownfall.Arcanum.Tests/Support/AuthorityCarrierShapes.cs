namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The synthetic assemblies of the Authority Carrier Ban: a copy of the admission protocol and gate, and one shape per row with
/// exactly the carriers the scan must report for it. It is plain data, so the tests and a scratch harness read the same rows.
/// </summary>
internal static partial class AuthorityCarrierShapes
{
    // The protocol and the gate of the synthetic assembly. The gate's own nested types keep every handle they mint, exactly as the
    // real gate does, and none of that may be reported.
    internal const string Protocol = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Channels;
        using System.Threading.Tasks;
        using RetroDownfall.Arcanum.Infrastructure.Data;

        namespace RetroDownfall.Arcanum.Infrastructure.Data
        {
            public interface IGrimoireExternalEffectGroup : IAsyncDisposable
            {
            }

            public interface IGrimoireWorkLease : IAsyncDisposable
            {
                long Generation { get; }

                bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group);
            }

            public interface IGrimoireRequestLease : IAsyncDisposable
            {
            }

            public interface IGrimoireClosingOwner : IAsyncDisposable
            {
            }

            public interface IGrimoireExclusiveClosedLease : IAsyncDisposable
            {
            }

            public interface IGrimoireConnectionAdmissionGate
            {
                bool TryAcquireWorkLease(int kind, out IGrimoireWorkLease? lease);
            }

            public sealed class GrimoireConnectionAdmissionGate : IGrimoireConnectionAdmissionGate
            {
                private readonly HashSet<WorkLease> _leases = [];

                private WorkLease? _current;

                private IGrimoireExternalEffectGroup? _group;

                public bool TryAcquireWorkLease(int kind, out IGrimoireWorkLease? lease)
                {
                    WorkLease made = new(this);

                    _current = made;

                    _leases.Add(made);

                    lease = made;

                    return true;
                }

                private sealed class WorkLease(GrimoireConnectionAdmissionGate gate) : IGrimoireWorkLease
                {
                    private IGrimoireExternalEffectGroup? _active;

                    private Func<ValueTask>? _release;

                    public long Generation => 1;

                    public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group)
                    {
                        IGrimoireExternalEffectGroup made = new Group(this);

                        _active = made;

                        _release = () => made.DisposeAsync();

                        gate._group = made;

                        group = made;

                        return true;
                    }

                    public ValueTask DisposeAsync() => default;
                }

                private sealed class Group(WorkLease owner) : IGrimoireExternalEffectGroup
                {
                    public ValueTask DisposeAsync() => default;
                }
            }
        }

        """;

    // One synthetic shape per row: the classes it declares, and exactly the findings the scan must report for them.
    internal sealed record Shape(string Source, string[] Expected);

    /// <summary>Every row: the basic holders and sinks, the generic shapes and the data-flow shapes, merged by name.</summary>
    internal static IReadOnlyDictionary<string, Shape> Shapes => AllShapes.Value;

    // Lazy, because the parts live in other files of this partial class and static field initializers do not run in a fixed order across files.
    private static readonly Lazy<Dictionary<string, Shape>> AllShapes = new(MergeAll);

    private static Dictionary<string, Shape> MergeAll() => Merge(BasicShapes, GenericShapes, FlowShapes);

    private static Dictionary<string, Shape> Merge(params Dictionary<string, Shape>[] parts)
    {
        Dictionary<string, Shape> merged = new(StringComparer.Ordinal);

        foreach (Dictionary<string, Shape> part in parts)
        {
            foreach ((string name, Shape shape) in part)
            {
                merged.Add(name, shape);
            }
        }

        return merged;
    }

    private static readonly Dictionary<string, Shape> BasicShapes = new(StringComparer.Ordinal)
    {
        // The deleted holder: a field and a property keep the lease, and Bind hands it in.
        ["holder-field-property-and-bind"] = new("""
            public sealed class LongRestWorkAdmission
            {
                private IGrimoireWorkLease? current;

                public IGrimoireWorkLease Lease => current ?? throw new InvalidOperationException();

                public IDisposable Bind(IGrimoireWorkLease lease)
                {
                    current = lease;

                    return new Token();
                }

                private sealed class Token : IDisposable
                {
                    public void Dispose()
                    {
                    }
                }
            }
            """,
            [
                "bind|Fixture.LongRestWorkAdmission.Bind|IGrimoireWorkLease",
                "field|Fixture.LongRestWorkAdmission.current|IGrimoireWorkLease",
                "property|Fixture.LongRestWorkAdmission.Lease|IGrimoireWorkLease",
            ]),

        ["bind-extension-taking-a-group"] = new("""
            public sealed class Receiver
            {
            }

            public static class Holders
            {
                public static void Bind(this Receiver receiver, IGrimoireExternalEffectGroup group)
                {
                }
            }
            """,
            ["bind|Fixture.Holders.Bind|IGrimoireExternalEffectGroup"]),

        ["static-field"] = new("""
            public static class Ambient
            {
                private static IGrimoireWorkLease? s_held;
            }
            """,
            ["field|Fixture.Ambient.s_held|IGrimoireWorkLease"]),

        ["static-property"] = new("""
            public static class Ambient
            {
                public static IGrimoireExternalEffectGroup? Kept { get; set; }
            }
            """,
            ["property|Fixture.Ambient.Kept|IGrimoireExternalEffectGroup"]),

        ["record-positional-property"] = new("""
            public sealed record Positional(IGrimoireWorkLease Lease);
            """,
            ["property|Fixture.Positional.Lease|IGrimoireWorkLease"]),

        ["array-field"] = new("""
            public sealed class Shelf
            {
                private readonly IGrimoireExternalEffectGroup[] _groups = new IGrimoireExternalEffectGroup[4];
            }
            """,
            ["field|Fixture.Shelf._groups|IGrimoireExternalEffectGroup"]),

        // A group stashed in an instance field and read back through a closed key.
        ["instance-dictionary-of-groups-read-back-by-key"] = new("""
            public sealed class Registry
            {
                private readonly Dictionary<string, IGrimoireExternalEffectGroup> _byKey = [];

                public void Put(IGrimoireExternalEffectGroup group) => _byKey["slot"] = group;

                public IGrimoireExternalEffectGroup? Take() => _byKey.GetValueOrDefault("slot");
            }
            """,
            ["field|Fixture.Registry._byKey|IGrimoireExternalEffectGroup"]),

        ["tuple-and-task-fields"] = new("""
            public sealed class Pending
            {
                private (IGrimoireWorkLease Lease, int Count) _pair;

                private Task<IGrimoireWorkLease>? _pending;
            }
            """,
            [
                "field|Fixture.Pending._pair|IGrimoireWorkLease",
                "field|Fixture.Pending._pending|IGrimoireWorkLease",
            ]),

        ["request-family-field"] = new("""
            public sealed class RequestScope
            {
                private IGrimoireRequestLease? _lease;
            }
            """,
            ["field|Fixture.RequestScope._lease|IGrimoireRequestLease"]),

        // An object-typed member hides the handle behind a supertype: the store is the finding.
        ["object-field-assigned-from-a-lease"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    _slot = lease;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["async-disposable-field-assigned-from-a-group"] = new("""
            public sealed class Stash
            {
                private IAsyncDisposable? _slot;

                public void Put(IGrimoireExternalEffectGroup group)
                {
                    _slot = group;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireExternalEffectGroup"]),

        ["generic-field-assigned-from-a-lease"] = new("""
            public sealed class Box<T>
            {
                private T? _value;

                public void Put(IGrimoireWorkLease lease)
                {
                    _value = (T)(object)lease;
                }
            }
            """,
            ["store|Fixture.Box._value|IGrimoireWorkLease"]),

        ["object-property-assigned-from-a-lease"] = new("""
            public sealed class PropertySlot
            {
                public object? Slot { get; set; }

                public void Put(IGrimoireWorkLease lease)
                {
                    Slot = lease;
                }
            }
            """,
            ["store|Fixture.PropertySlot.Slot|IGrimoireWorkLease"]),

        ["object-initializer-member"] = new("""
            public sealed class Target
            {
                public object? Value { get; set; }
            }

            public static class Maker
            {
                public static Target Make(IGrimoireWorkLease lease) => new Target { Value = lease };
            }
            """,
            ["store|Fixture.Target.Value|IGrimoireWorkLease"]),

        ["coalesce-assignment"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease)
                {
                    _slot ??= lease;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["conditional-assignment"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireWorkLease lease, bool keep)
                {
                    _slot = keep ? lease : null;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["deconstruction-assignment"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                private int _count;

                public void Put(IGrimoireWorkLease lease)
                {
                    (_slot, _count) = (lease, 1);
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireWorkLease"]),

        ["copy-chain-into-a-field"] = new("""
            public sealed class Stash
            {
                private object? _slot;

                public void Put(IGrimoireExternalEffectGroup group)
                {
                    object first = group;

                    object second = first;

                    _slot = second;
                }
            }
            """,
            ["store|Fixture.Stash._slot|IGrimoireExternalEffectGroup"]),

        ["array-literal-copied-into-a-field"] = new("""
            public sealed class Stash
            {
                private object[]? _slots;

                public void Put(IGrimoireWorkLease lease)
                {
                    object[] items = [lease];

                    _slots = items;
                }
            }
            """,
            ["store|Fixture.Stash._slots|IGrimoireWorkLease"]),

        ["static-array-element"] = new("""
            public static class Stash
            {
                private static readonly object[] s_items = new object[4];

                public static void Put(IGrimoireExternalEffectGroup group)
                {
                    s_items[0] = group;
                }
            }
            """,
            ["store|Fixture.Stash.Put.[]|IGrimoireExternalEffectGroup"]),

        ["indexer-of-a-framework-map"] = new("""
            public sealed class ClosedKey
            {
                private readonly Dictionary<int, object> _map = [];

                public void Put(IGrimoireExternalEffectGroup group)
                {
                    _map[3] = group;
                }
            }
            """,
            ["store|Fixture.ClosedKey.Put->System.Collections.Generic.Dictionary.this[]|IGrimoireExternalEffectGroup"]),

        // A static alias through a local or a reference.
        ["ref-local-alias-of-a-static"] = new("""
            public static class Alias
            {
                private static object? s_slot;

                public static void Put(IGrimoireExternalEffectGroup group)
                {
                    ref object? alias = ref s_slot;

                    alias = group;
                }
            }
            """,
            ["ref-store|Fixture.Alias.Put|IGrimoireExternalEffectGroup"]),

        ["ref-parameter-store"] = new("""
            public static class Alias
            {
                public static void Put(IGrimoireWorkLease lease, ref object? slot)
                {
                    slot = lease;
                }
            }
            """,
            ["ref-store|Fixture.Alias.Put|IGrimoireWorkLease"]),

        ["out-parameter-of-a-supertype"] = new("""
            public static class Alias
            {
                public static void Put(IGrimoireWorkLease lease, out object? slot)
                {
                    slot = lease;
                }
            }
            """,
            ["ref-store|Fixture.Alias.Put|IGrimoireWorkLease"]),

        ["ref-return-store"] = new("""
            public static class Alias
            {
                private static object? s_slot;

                private static ref object? Slot() => ref s_slot;

                public static void Put(IGrimoireWorkLease lease)
                {
                    Slot() = lease;
                }
            }
            """,
            ["ref-store|Fixture.Alias.Put|IGrimoireWorkLease"]),

        // A handle handed to an authored callee that keeps it under a supertype is found in the callee.
        ["authored-callee-stores-it-in-a-static-array"] = new("""
            public static class Relay
            {
                private static readonly object[] s_items = new object[4];

                public static void Forward(IGrimoireExternalEffectGroup group) => Park(group);

                private static void Park(object kept)
                {
                    s_items[0] = kept;
                }
            }
            """,
            ["store|Fixture.Relay.Park.[]|IGrimoireExternalEffectGroup"]),

        ["authored-callee-keeps-it-in-a-primary-constructor-capture"] = new("""
            public sealed class Keeper(object kept)
            {
                public override string ToString() => kept.ToString() ?? string.Empty;
            }

            public static class Maker
            {
                public static Keeper Make(IGrimoireWorkLease lease) => new Keeper(lease);
            }
            """,
            ["primary-constructor|Fixture.Keeper(kept)|IGrimoireWorkLease"]),

        ["primary-constructor-initializer-copy"] = new("""
            public sealed class Init(IGrimoireWorkLease lease)
            {
                private readonly object _kept = lease;
            }
            """,
            ["store|Fixture.Init._kept|IGrimoireWorkLease"]),

        ["primary-constructor-capture"] = new("""
            public sealed class Captured(IGrimoireWorkLease lease)
            {
                public ValueTask Release() => lease.DisposeAsync();
            }
            """,
            ["primary-constructor|Fixture.Captured(lease)|IGrimoireWorkLease"]),

        ["primary-constructor-capture-of-a-group-through-a-local-function"] = new("""
            public sealed class Captured(IGrimoireExternalEffectGroup group)
            {
                public Task Release()
                {
                    return Run();

                    Task Run() => group.DisposeAsync().AsTask();
                }
            }
            """,
            [
                "closure|Fixture.Captured.Release{closure#0:group}|IGrimoireExternalEffectGroup",
                "primary-constructor|Fixture.Captured(group)|IGrimoireExternalEffectGroup",
            ]),

        // Closures: a lambda, an anonymous method, a local function and an event subscriber each keep what they capture.
        ["lambda-capture"] = new("""
            public static class Lambdas
            {
                public static Task Detach(IGrimoireWorkLease lease) => Task.Run(() => lease.DisposeAsync().AsTask());
            }
            """,
            ["closure|Fixture.Lambdas.Detach{closure#0:lease}|IGrimoireWorkLease"]),

        ["async-lambda-capture"] = new("""
            public static class Lambdas
            {
                public static Task Detach(IGrimoireExternalEffectGroup group) => Task.Run(async () => await group.DisposeAsync());
            }
            """,
            ["closure|Fixture.Lambdas.Detach{closure#0:group}|IGrimoireExternalEffectGroup"]),

        ["anonymous-method-capture"] = new("""
            public static class Lambdas
            {
                public static Task Detach(IGrimoireWorkLease lease) => Task.Run(delegate { return lease.DisposeAsync().AsTask(); });
            }
            """,
            ["closure|Fixture.Lambdas.Detach{closure#0:lease}|IGrimoireWorkLease"]),

        ["local-function-capture"] = new("""
            public static class Lambdas
            {
                public static Task Detach(IGrimoireWorkLease lease)
                {
                    return Run();

                    Task Run() => lease.DisposeAsync().AsTask();
                }
            }
            """,
            ["closure|Fixture.Lambdas.Detach{closure#0:lease}|IGrimoireWorkLease"]),

        ["captured-local-copy-of-a-supertype"] = new("""
            public static class Lambdas
            {
                public static Task Detach(IGrimoireWorkLease lease)
                {
                    object alias = lease;

                    return Task.Run(() => alias.ToString());
                }
            }
            """,
            ["closure|Fixture.Lambdas.Detach{closure#0:alias}|IGrimoireWorkLease"]),

        ["event-subscriber-closure"] = new("""
            public sealed class Hooks
            {
                public event Func<Task>? Released;

                public void Subscribe(IGrimoireWorkLease lease)
                {
                    Released += () => lease.DisposeAsync().AsTask();
                }
            }
            """,
            ["closure|Fixture.Hooks.Subscribe{closure#0:lease}|IGrimoireWorkLease"]),

        // A busy retry that takes the attempt as a delegate and awaits it where it invokes it lends the lease for the call and no longer. That is
        // still a closure over a work lease, and a work lease may be captured by a closure nowhere, whatever awaits the closure.
        ["busy-retry-delegate-capture-of-a-lease"] = new("""
            public static class DelegatedRetry
            {
                public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
                {
                    while (true)
                    {
                        try
                        {
                            return await action().ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            await Task.Delay(1, ct).ConfigureAwait(false);
                        }
                    }
                }
            }

            public sealed class Store
            {
                public async Task<long> ReadmitAsync(IGrimoireWorkLease admission, CancellationToken ct)
                {
                    return await DelegatedRetry.ExecuteAsync(async () =>
                    {
                        await Task.Delay(1, ct).ConfigureAwait(false);

                        return admission.Generation;
                    }, ct).ConfigureAwait(false);
                }
            }
            """,
            ["closure|Fixture.Store.ReadmitAsync{closure#0:admission}|IGrimoireWorkLease"]),

        // Delegate carriers: a method group bound to the handle keeps it alive as the delegate's target.
        ["method-group-bound-to-a-lease"] = new("""
            public sealed class Hooks
            {
                private Func<ValueTask>? _release;

                public void Keep(IGrimoireWorkLease lease)
                {
                    _release = lease.DisposeAsync;
                }
            }
            """,
            ["delegate-receiver|Fixture.Hooks.Keep|IGrimoireWorkLease"]),

        ["event-subscribed-with-a-method-group-of-a-group"] = new("""
            public sealed class Hooks
            {
                public event Func<ValueTask>? Released;

                public void Subscribe(IGrimoireExternalEffectGroup group)
                {
                    Released += group.DisposeAsync;
                }
            }
            """,
            ["delegate-receiver|Fixture.Hooks.Subscribe|IGrimoireExternalEffectGroup"]),

        // Callees that could keep the handle: a framework container or channel, a pinned exchange, a keep-alive, an opaque port.
        ["framework-list-add"] = new("""
            public static class Sinks
            {
                public static void Keep(List<object> items, IGrimoireWorkLease lease)
                {
                    items.Add(lease);
                }
            }
            """,
            ["sink|Fixture.Sinks.Keep->System.Collections.Generic.List<T>.Add|IGrimoireWorkLease"]),

        ["channel-write-of-a-group"] = new("""
            public static class Sinks
            {
                public static void Forward(Channel<object> channel, IGrimoireExternalEffectGroup group)
                {
                    channel.Writer.TryWrite(group);
                }
            }
            """,
            ["sink|Fixture.Sinks.Forward->System.Threading.Channels.ChannelWriter<T>.TryWrite|IGrimoireExternalEffectGroup"]),

        ["interlocked-exchange-into-a-field"] = new("""
            public sealed class Sinks
            {
                private object? _slot;

                public void Keep(IGrimoireWorkLease lease)
                {
                    Interlocked.Exchange(ref _slot, lease);
                }
            }
            """,
            ["sink|Fixture.Sinks.Keep->System.Threading.Interlocked.Exchange|IGrimoireWorkLease"]),

        ["gc-keep-alive"] = new("""
            public static class Sinks
            {
                public static void Keep(IGrimoireWorkLease lease)
                {
                    GC.KeepAlive(lease);
                }
            }
            """,
            ["sink|Fixture.Sinks.Keep->System.GC.KeepAlive|IGrimoireWorkLease"]),

        ["string-format-boxes-the-handle"] = new("""
            public static class Sinks
            {
                public static string Describe(IGrimoireWorkLease lease) => string.Format("{0}", lease);
            }
            """,
            ["sink|Fixture.Sinks.Describe->string.Format|IGrimoireWorkLease"]),

        // A handle registered as a service is kept by the container.
        ["service-registration-by-type-argument"] = new("""
            public static class Composition
            {
                public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
                {
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IGrimoireWorkLease>(services, static provider => throw new InvalidOperationException());
                }
            }
            """,
            ["service|Fixture.Composition.Configure|IGrimoireWorkLease"]),

        ["service-registration-by-type-object"] = new("""
            public static class Composition
            {
                public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
                {
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(services, typeof(IGrimoireExternalEffectGroup), static provider => throw new InvalidOperationException());
                }
            }
            """,
            ["service|Fixture.Composition.Configure|IGrimoireExternalEffectGroup"]),

        ["interface-property-and-indexer-returning-a-lease"] = new("""
            public interface IHolder
            {
                IGrimoireWorkLease Lease { get; }

                IGrimoireExternalEffectGroup this[int slot] { get; }
            }
            """,
            [
                "property|Fixture.IHolder.Lease|IGrimoireWorkLease",
                "property|Fixture.IHolder.this[]|IGrimoireExternalEffectGroup",
            ]),

        ["static-async-local-of-a-lease"] = new("""
            public static class Ambient
            {
                private static readonly AsyncLocal<IGrimoireWorkLease?> s_current = new();
            }
            """,
            ["field|Fixture.Ambient.s_current|IGrimoireWorkLease"]),

        ["primary-constructor-parameter-captured-by-an-initializer-lambda"] = new("""
            public sealed class Deferred(IGrimoireWorkLease lease)
            {
                private readonly Func<Task> _release = () => lease.DisposeAsync().AsTask();
            }
            """,
            ["closure|Fixture.Deferred._release{closure#0:lease}|IGrimoireWorkLease"]),

        ["opaque-authored-port-taking-a-supertype"] = new("""
            public interface ISink
            {
                void Take(object value);
            }

            public static class Sinks
            {
                public static void Keep(ISink sink, IGrimoireExternalEffectGroup group)
                {
                    sink.Take(group);
                }
            }
            """,
            ["sink|Fixture.Sinks.Keep->Fixture.ISink.Take|IGrimoireExternalEffectGroup"]),

        // Lookalikes: the exemption belongs to the real gate type only.
        ["lookalike-gate-in-another-namespace"] = new("""
            namespace Other
            {
                public sealed class GrimoireConnectionAdmissionGate
                {
                    private IGrimoireWorkLease? _held;
                }
            }
            """,
            ["field|Fixture.Other.GrimoireConnectionAdmissionGate._held|IGrimoireWorkLease"]),

        ["a-copy-of-the-gate-nested-lease-outside-the-gate"] = new("""
            public sealed class NotTheGate
            {
                private readonly HashSet<IGrimoireWorkLease> _leases = [];

                private IGrimoireExternalEffectGroup? _group;
            }
            """,
            [
                "field|Fixture.NotTheGate._group|IGrimoireExternalEffectGroup",
                "field|Fixture.NotTheGate._leases|IGrimoireWorkLease",
            ]),

        // Negative twins: handles passed by parameter, typed, derived data, the real gate and the reviewed pass-throughs.
        ["a-lease-passed-by-parameter-only"] = new("""
            public sealed class Pure
            {
                private long _generation;

                private CancellationToken _token;

                public async Task RunAsync(IGrimoireWorkLease lease, CancellationToken ct)
                {
                    if (!lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
                    {
                        return;
                    }

                    await using IGrimoireExternalEffectGroup owned = group;

                    await PaidAsync(lease, ct);

                    IGrimoireWorkLease alias = lease;

                    long generation = alias.Generation;

                    _ = Task.Run(() => generation);

                    Func<Task> pause = async () => await Task.Delay(1, ct);

                    await pause();

                    _generation = generation;

                    _token = ct;
                }

                private static Task PaidAsync(IGrimoireWorkLease lease, CancellationToken ct) => lease.Generation > 0 ? Task.CompletedTask : Task.FromCanceled(ct);

                public async Task ThroughATypedDelegate(Func<IGrimoireWorkLease, Task> observe, IGrimoireWorkLease lease)
                {
                    await observe(lease);
                }
            }
            """,
            []),

        // The retry cursor is the same retry without the closure: the method writes the attempt loop around its own lease parameter and the cursor
        // only waits, so nothing keeps, captures or lends the lease.
        ["retry-cursor-loop-around-the-lease-parameter"] = new("""
            public struct RetryCursor
            {
                private int _attempt;

                public Task WaitAsync(Exception busy, CancellationToken ct)
                {
                    _attempt++;

                    return Task.Delay(_attempt, ct);
                }
            }

            public sealed class Store
            {
                public async Task<long> ReadmitAsync(IGrimoireWorkLease admission, CancellationToken ct)
                {
                    RetryCursor retry = new();

                    while (true)
                    {
                        try
                        {
                            await Task.Delay(1, ct).ConfigureAwait(false);

                            return admission.Generation;
                        }
                        catch (InvalidOperationException busy)
                        {
                            await retry.WaitAsync(busy, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            """,
            []),

        ["acquisition-out-parameter-of-the-handle-type"] = new("""
            public sealed class Minting
            {
                public bool TryAcquire(IGrimoireConnectionAdmissionGate gate, out IGrimoireWorkLease? lease)
                {
                    return gate.TryAcquireWorkLease(17, out lease);
                }
            }
            """,
            []),

        ["a-primary-constructor-parameter-used-only-to-initialise"] = new("""
            public sealed class Initial(IGrimoireWorkLease lease)
            {
                public long Generation { get; } = lease.Generation;
            }
            """,
            []),

        ["reviewed-pass-throughs"] = new("""
            public static class Guards
            {
                public static void Check(IGrimoireWorkLease lease, IGrimoireWorkLease other)
                {
                    ArgumentNullException.ThrowIfNull(lease);

                    if (ReferenceEquals(lease, other))
                    {
                        throw new InvalidOperationException();
                    }
                }
            }
            """,
            []),

        ["a-minted-lease-held-by-await-using"] = new("""
            public static class Minting
            {
                public static async Task<long> RunAsync(IGrimoireConnectionAdmissionGate gate)
                {
                    if (!gate.TryAcquireWorkLease(17, out IGrimoireWorkLease? admitted))
                    {
                        return 0;
                    }

                    await using IGrimoireWorkLease lease = admitted!;

                    return lease.Generation;
                }
            }
            """,
            []),
    };
}
