using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using RetroDownfall.Arcanum.Tests.Collections;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// Carried Admission passes one work lease by value from its mint to every use. Outside the admission gate nothing
/// stores that lease or the external-effect group begun on it, captures either in a closure, binds either to a delegate,
/// or hands either to a callee that could keep it. This is also what backs the hosted-producer inventory's documented limits
/// on a handle written to heap storage (a group stashed in an instance field and read back through a closed key, a static
/// alias through a local or a reference, a delegate that carries it): the inventory cannot follow those shapes, so
/// production is not allowed to contain them.
/// </summary>
[Collection(HostedProducerAnalysisCollection.Name)]
[Trait("Category", "HostedProducerAnalysis")]
public sealed class AuthorityCarrierBanTests(ITestOutputHelper output)
{
    private const string Data = AuthorityCarrierScanner.Namespace;

    private const string Store = Data + ".LongRest.LongRestDiscoveryStore";

    private const string Coordinator = Data + ".CovenantErasureCoordinator";

    /// <summary>
    /// Every carrier of a gate-minted handle in production, outside the gate itself, reviewed one by one.
    /// </summary>
    /// <remarks>
    /// The request lease, the closing owner and the exclusive closed lease are kept where the request and the erasure close
    /// have to carry them across their own lifetime: the request scope holds the lease it admitted until the container
    /// disposes the scope, and the erasure closure and the closed-period authority hold the closing owner and the closed
    /// lease the whole closed period. None of them is a work lease or an external-effect group.
    ///
    /// <para>The two Long Rest store closures are the one place a work lease is captured. Each is the async body of
    /// <c>SqliteBusyRetry.ExecuteAsync</c>, awaited in the statement that creates it, so the lease is lent for the call and
    /// never outlives it: <see cref="The_pinned_closure_captures_of_the_work_lease_are_awaited_inline_by_the_busy_retry"/> pins that shape,
    /// and any other capture of a work lease, or a second closure in either method, is a new finding.</para>
    /// </remarks>
    internal static readonly string[] ReviewedCensus =
    [
        "closure|" + Store + ".ReadmitCleanAsync{closure#0:admission}|IGrimoireWorkLease",
        "closure|" + Store + ".ReconcileCompletedCheckpointAsync{closure#0:admission}|IGrimoireWorkLease",
        "field|" + Data + ".Covenant.CovenantClosedPeriodAuthority._closed|IGrimoireExclusiveClosedLease",
        "field|" + Data + ".GrimoireRequestAdmissionScope._lease|IGrimoireRequestLease",
        "primary-constructor|" + Coordinator + ".CovenantGrimoireClosure(closed)|IGrimoireExclusiveClosedLease",
        "primary-constructor|" + Coordinator + ".CovenantGrimoireClosure(closingOwner)|IGrimoireClosingOwner",
        "property|" + Coordinator + ".CovenantGrimoireClosure.Closed|IGrimoireExclusiveClosedLease",
        "property|" + Coordinator + ".GrimoireClosureSlot.ClosedLease|IGrimoireExclusiveClosedLease",
        "property|" + Coordinator + ".GrimoireClosureSlot.ClosingOwner|IGrimoireClosingOwner",
        "property|" + Data + ".GrimoireRequestAdmissionScope.Lease|IGrimoireRequestLease",
    ];

    [Fact]
    public void Production_carries_no_authority_handle_outside_the_gate_except_the_reviewed_census()
    {
        string[] actual = [.. new AuthorityCarrierScanner([.. HostedGrimoireProducerInventory.ProductionCompilations]).Scan().Select(static finding => finding.ToString())];

        foreach (string line in actual)
        {
            output.WriteLine("AUTHORITY_CARRIER " + line);
        }

        string[] unreviewed = [.. actual.Except(ReviewedCensus, StringComparer.Ordinal)];

        string[] stale = [.. ReviewedCensus.Except(actual, StringComparer.Ordinal)];

        Assert.True(unreviewed.Length == 0 && stale.Length == 0,
            "An authority handle is kept, captured or lent where it is not reviewed.\nUnreviewed:\n" + string.Join('\n', unreviewed)
            + "\nReviewed but gone:\n" + string.Join('\n', stale));

        Assert.Equal(ReviewedCensus.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void The_reviewed_census_never_excuses_a_work_lease_or_a_group_except_the_two_pinned_inline_closures()
    {
        foreach (string entry in ReviewedCensus)
        {
            string authority = entry[(entry.LastIndexOf('|') + 1)..];

            if (AuthorityCarrierScanner.BannedAuthorities.Contains(authority, StringComparer.Ordinal))
            {
                Assert.StartsWith("closure|" + Store + ".", entry, StringComparison.Ordinal);

                Assert.Contains(entry.Split('|')[1], new[]
                {
                    Store + ".ReadmitCleanAsync{closure#0:admission}",
                    Store + ".ReconcileCompletedCheckpointAsync{closure#0:admission}",
                });
            }
            else
            {
                Assert.Contains(authority, AuthorityCarrierScanner.ReviewedAuthorities);
            }
        }

        Assert.Equal(2, ReviewedCensus.Count(static entry => entry.EndsWith("|IGrimoireWorkLease", StringComparison.Ordinal)));

        Assert.DoesNotContain(ReviewedCensus, static entry => entry.EndsWith("|IGrimoireExternalEffectGroup", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pinned_closure_captures_of_the_work_lease_are_awaited_inline_by_the_busy_retry()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        INamedTypeSymbol store = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(Store));

        foreach (string name in new[] { "ReadmitCleanAsync", "ReconcileCompletedCheckpointAsync" })
        {
            IMethodSymbol method = Assert.Single(store.GetMembers(name).OfType<IMethodSymbol>());

            MethodDeclarationSyntax syntax = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());

            SemanticModel model = infrastructure.GetSemanticModel(syntax.SyntaxTree);

            ParenthesizedLambdaExpressionSyntax closure = Assert.Single(syntax.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>());

            Assert.Contains(closure.Modifiers, static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));

            // The closure is the first argument of the busy retry, and the call is awaited and returned by the same statement.
            ArgumentSyntax argument = Assert.IsType<ArgumentSyntax>(closure.Parent);

            InvocationExpressionSyntax retry = Assert.IsType<InvocationExpressionSyntax>(argument.Parent!.Parent);

            Assert.Same(closure, retry.ArgumentList.Arguments[0].Expression);

            IMethodSymbol called = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(retry).Symbol);

            Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.SqliteBusyRetry.ExecuteAsync", called.ContainingType.ToDisplayString() + "." + called.Name);

            MemberAccessExpressionSyntax configure = Assert.IsType<MemberAccessExpressionSyntax>(retry.Parent);

            Assert.Equal("ConfigureAwait", configure.Name.Identifier.ValueText);

            AwaitExpressionSyntax awaited = Assert.IsType<AwaitExpressionSyntax>(Assert.IsType<InvocationExpressionSyntax>(configure.Parent).Parent);

            Assert.IsType<ReturnStatementSyntax>(awaited.Parent);

            // The retry runs the closure and nothing else: the delegate it is given is only ever invoked.
            INamedTypeSymbol retryType = Assert.IsAssignableFrom<INamedTypeSymbol>(called.ContainingType);

            foreach (IMethodSymbol runner in retryType.GetMembers("ExecuteAsync").OfType<IMethodSymbol>())
            {
                MethodDeclarationSyntax body = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(runner.DeclaringSyntaxReferences).GetSyntax());

                SemanticModel runnerModel = infrastructure.GetSemanticModel(body.SyntaxTree);

                IParameterSymbol operation = runner.Parameters[0];

                foreach (IdentifierNameSyntax use in body.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(name => SymbolEqualityComparer.Default.Equals(runnerModel.GetSymbolInfo(name).Symbol, operation)))
                {
                    InvocationExpressionSyntax invocation = Assert.IsType<InvocationExpressionSyntax>(use.Parent);

                    Assert.Same(use, invocation.Expression);
                }
            }
        }
    }

    [Fact]
    public void Gate_minted_handles_are_the_two_banned_handles_and_the_three_reviewed_ones()
    {
        CSharpCompilation infrastructure = Assert.Single(HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        foreach (string name in AuthorityCarrierScanner.BannedAuthorities.Concat(AuthorityCarrierScanner.ReviewedAuthorities))
        {
            INamedTypeSymbol handle = Assert.IsAssignableFrom<INamedTypeSymbol>(infrastructure.GetTypeByMetadataName(Data + "." + name));

            Assert.Equal(TypeKind.Interface, handle.TypeKind);

            Assert.Contains(handle.AllInterfaces, static contract => contract.ToDisplayString() == "System.IAsyncDisposable");
        }

        Assert.NotNull(infrastructure.GetTypeByMetadataName(AuthorityCarrierScanner.GateType));
    }

    // The protocol and the gate of the synthetic assembly. The gate's own nested types keep every handle they mint, exactly as the
    // real gate does, and none of that may be reported.
    private const string Protocol = """
        using System;
        using System.Collections.Generic;
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
    private sealed record Shape(string Source, string[] Expected);

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
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

    public static TheoryData<string> ShapeNames()
    {
        TheoryData<string> names = [];

        foreach (string name in Shapes.Keys)
        {
            names.Add(name);
        }

        return names;
    }

    private static readonly Lazy<IReadOnlyList<MetadataReference>> PlatformReferences = new(static () =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path))]);

    private static CSharpCompilation Synthetic(string source)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "SyntheticHolderAssembly",
            [CSharpSyntaxTree.ParseText("#nullable enable\n" + Protocol + "\nnamespace Fixture\n{\n" + source + "\n}\n", path: "src/Fixture.cs")],
            PlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static string[] Scan(CSharpCompilation compilation) =>
        [.. new AuthorityCarrierScanner([compilation]).Scan().Select(static finding => finding.ToString())];

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void The_scan_reports_exactly_the_carriers_of_a_synthetic_shape(string name)
    {
        Shape shape = Shapes[name];

        string[] actual = Scan(Synthetic(shape.Source));

        foreach (string line in actual)
        {
            output.WriteLine("SYNTHETIC_CARRIER shape=" + name + " " + line);
        }

        Assert.Equal(shape.Expected.Order(StringComparer.Ordinal), actual);
    }

    // A compilation sees another compilation's members through a retargeting layer, so the formal a call site reports is not the
    // symbol the callee's body is read with. The handle must still be followed into a callee another compilation owns.
    [Fact]
    public void A_handle_handed_to_a_callee_another_compilation_owns_is_followed_into_it()
    {
        CSharpParseOptions parse = CSharpParseOptions.Default;

        CSharpCompilation lower = CSharpCompilation.Create(
            "SyntheticLower",
            [CSharpSyntaxTree.ParseText("#nullable enable\n" + Protocol + """

                namespace Fixture
                {
                    public static class Keep
                    {
                        private static readonly object[] s_items = new object[1];

                        public static void Park(object kept)
                        {
                            s_items[0] = kept;
                        }

                        public static void Look(object seen)
                        {
                            _ = seen.ToString();
                        }
                    }

                    public sealed class Keeper(object kept)
                    {
                        public override string ToString() => kept.ToString() ?? string.Empty;
                    }
                }
                """, parse, path: "src/Lower.cs")],
            PlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpCompilation upper = CSharpCompilation.Create(
            "SyntheticUpper",
            [CSharpSyntaxTree.ParseText("""
                #nullable enable
                using RetroDownfall.Arcanum.Infrastructure.Data;

                namespace Fixture.Upper
                {
                    public static class Caller
                    {
                        public static void Go(IGrimoireWorkLease lease) => Fixture.Keep.Park(lease);

                        public static void Peek(IGrimoireWorkLease lease) => Fixture.Keep.Look(lease);

                        public static object Hold(IGrimoireWorkLease lease) => new Fixture.Keeper(lease);
                    }
                }
                """, parse, path: "src/Upper.cs")],
            [.. PlatformReferences.Value, lower.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.DoesNotContain(lower.GetDiagnostics().Concat(upper.GetDiagnostics()), static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        // The callees that keep the handle are reported once each, from the compilation that owns them; the one that only looks at it is not.
        Assert.Equal(
            ["primary-constructor|Fixture.Keeper(kept)|IGrimoireWorkLease", "store|Fixture.Keep.Park.[]|IGrimoireWorkLease"],
            new AuthorityCarrierScanner([lower, upper]).Scan().Select(static finding => finding.ToString()));
    }

    [Fact]
    public void Every_shape_row_is_either_a_positive_or_a_named_negative_twin()
    {
        string[] negatives = [.. Shapes.Where(static pair => pair.Value.Expected.Length == 0).Select(static pair => pair.Key)];

        Assert.Equal(
            new[]
            {
                "a-lease-passed-by-parameter-only",
                "a-primary-constructor-parameter-used-only-to-initialise",
                "acquisition-out-parameter-of-the-handle-type",
                "reviewed-pass-throughs",
                "a-minted-lease-held-by-await-using",
            }.Order(StringComparer.Ordinal),
            negatives.Order(StringComparer.Ordinal));

        // Every kind the scan can report is exercised by at least one positive row.
        string[] kinds = [.. Shapes.Values.SelectMany(static shape => shape.Expected).Select(static line => line[..line.IndexOf('|')]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.Equal(
            new[] { "bind", "closure", "delegate-receiver", "field", "primary-constructor", "property", "ref-store", "service", "sink", "store" },
            kinds);
    }

    [Fact]
    public void The_gate_nested_types_are_the_only_exception_and_the_exception_is_real()
    {
        // The protocol alone contains the gate with every kind of holder inside it, and reports nothing.
        Assert.Empty(Scan(Synthetic("")));

        // The same gate, renamed, is not the gate: every holder inside it is reported.
        CSharpCompilation renamed = CSharpCompilation.Create(
            "SyntheticHolderAssembly",
            [CSharpSyntaxTree.ParseText("#nullable enable\n" + Protocol
                .Replace("class GrimoireConnectionAdmissionGate", "class SomeOtherGate", StringComparison.Ordinal)
                .Replace("(GrimoireConnectionAdmissionGate gate)", "(SomeOtherGate gate)", StringComparison.Ordinal), path: "src/Fixture.cs")],
            PlatformReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        string[] reported = Scan(renamed);

        Assert.NotEmpty(reported);

        Assert.Contains(reported, static line => line.StartsWith("field|" + Data + ".SomeOtherGate._leases|", StringComparison.Ordinal));

        Assert.Contains(reported, static line => line.StartsWith("field|" + Data + ".SomeOtherGate.WorkLease._active|", StringComparison.Ordinal));
    }

    [Fact]
    public void The_census_comparison_refuses_an_unreviewed_carrier_and_a_stale_entry()
    {
        string[] reviewed = ["field|Fixture.RequestScope._lease|IGrimoireRequestLease"];

        string[] actual = Scan(Synthetic(Shapes["request-family-field"].Source));

        Assert.Equal(reviewed, actual);

        string[] extra = Scan(Synthetic(Shapes["request-family-field"].Source + Shapes["static-field"].Source));

        Assert.Equal(
            ["field|Fixture.Ambient.s_held|IGrimoireWorkLease"],
            extra.Except(reviewed, StringComparer.Ordinal));

        Assert.Equal(
            reviewed,
            reviewed.Except(Scan(Synthetic(Shapes["a-lease-passed-by-parameter-only"].Source)), StringComparer.Ordinal));
    }
}
