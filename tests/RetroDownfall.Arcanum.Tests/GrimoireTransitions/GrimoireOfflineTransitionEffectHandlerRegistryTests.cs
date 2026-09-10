using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// The second closed table over the journal's key space.
/// </summary>
/// <remarks>
/// Two tables keyed the same way is a hazard as well as a design: it introduces the possibility of one
/// knowing a kind the other has never heard of, which would present as a journal that decodes
/// perfectly and then cannot be run — or worse, as an effect that runs under a payload nothing can
/// read back. The closure assertion below is the only thing that rules that out, and it is the reason
/// the second table was allowed to exist at all.
/// </remarks>
public sealed class GrimoireOfflineTransitionEffectHandlerRegistryTests
{

    /// <summary>
    /// The effect table and the payload table answer for exactly the same pairs.
    /// </summary>
    [Fact]
    public void The_effect_table_is_closed_over_the_same_keys_as_the_payload_table()
    {

        GrimoireOfflineTransitionEffectHandlerRegistry effects = Production();

        // Every declared kind, at the one version this build ships, and nothing else. Stated as the
        // literal expectation rather than derived from the enum, so adding a kind is a deliberate edit
        // here as well as a registration there.
        (GrimoireOfflineTransitionKind Kind, byte Version)[] expected =
        [
            (GrimoireOfflineTransitionKind.CovenantReset, 1),
            (GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure, 1),
        ];

        (GrimoireOfflineTransitionKind Kind, byte Version)[] actual =
            [.. effects.Keys.OrderBy(static key => key.Kind)];

        Assert.Equal(expected, actual);

        foreach ((GrimoireOfflineTransitionKind kind, byte version) in expected)
        {

            Assert.True(
                GrimoireOfflineTransitionHandlerRegistry.Production.Resolve(kind, version).IsSuccess,
                $"The payload table has no handler for the pair the effect table answers for: {kind}.");

        }

    }

    /// <summary>Each registered kind is the one durable operation it is allowed to be.</summary>
    /// <remarks>
    /// This is the restriction the journal-driven entry gate imposes, and it exists here rather than
    /// only at that gate because the table is what the gate believes. A table that mapped a reset kind
    /// onto a factory erasure would make the gate agree to a scope adoption nothing had authorised.
    /// </remarks>
    [Theory]
    [InlineData(
        GrimoireOfflineTransitionKind.CovenantReset,
        CovenantExclusiveOperation.CovenantReset,
        false)]
    [InlineData(
        GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
        CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
        true)]
    internal void Each_kind_names_its_operation_and_whether_it_owes_ordinary_work(
        GrimoireOfflineTransitionKind kind,
        CovenantExclusiveOperation operation,
        bool ordinaryContinuation)
    {

        Result<IGrimoireOfflineTransitionEffectHandler> resolved = Production().Resolve(kind, 1);

        Assert.True(resolved.IsSuccess);

        Assert.Equal(operation, resolved.Value.Operation);

        Assert.Equal(ordinaryContinuation, resolved.Value.RequiresOrdinaryContinuation);

    }

    /// <summary>
    /// A key nothing registered is refused, and the refusal never names the key.
    /// </summary>
    /// <remarks>
    /// The kind and the version are not something an operator chose or can act on, and putting them in
    /// a message would only invite somebody to try composing the missing registration by hand. What is
    /// actionable is that this build does not run the transition.
    /// </remarks>
    [Theory]
    [InlineData(GrimoireOfflineTransitionKind.CovenantReset, (byte)0)]
    [InlineData(GrimoireOfflineTransitionKind.CovenantReset, (byte)2)]
    [InlineData((GrimoireOfflineTransitionKind)7, (byte)1)]
    internal void An_unregistered_key_is_refused_without_being_named(
        GrimoireOfflineTransitionKind kind,
        byte version)
    {

        Result<IGrimoireOfflineTransitionEffectHandler> resolved = Production().Resolve(kind, version);

        Assert.True(resolved.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, resolved.Error.Code);

        Assert.DoesNotContain(kind.ToString(), resolved.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// A composition that would make a lookup ambiguous or unanswerable is refused at build time.
    /// </summary>
    /// <remarks>
    /// All four together, because they are one property: a table that cannot answer a question with
    /// exactly one handler is not a table. A duplicate is two answers, a zero version and an undeclared
    /// kind are keys nothing may legitimately ask for, and an empty table would refuse every transition
    /// while looking composed — which is the failure that reaches an operator rather than a developer.
    /// </remarks>
    [Fact]
    public void A_table_that_cannot_answer_with_exactly_one_handler_is_refused()
    {

        Assert.True(
            GrimoireOfflineTransitionEffectHandlerRegistry.Create([]).IsFailure,
            "An empty effect table was composed.");

        Assert.True(
            GrimoireOfflineTransitionEffectHandlerRegistry.Create(
            [
                new CovenantOfflineTransitionEffectHandler(
                    GrimoireOfflineTransitionKind.CovenantReset,
                    CovenantExclusiveOperation.CovenantReset,
                    requiresOrdinaryContinuation: false),
                new CovenantOfflineTransitionEffectHandler(
                    GrimoireOfflineTransitionKind.CovenantReset,
                    CovenantExclusiveOperation.CovenantReset,
                    requiresOrdinaryContinuation: true),
            ]).IsFailure,
            "Two handlers were registered for one pair.");

        Assert.True(
            GrimoireOfflineTransitionEffectHandlerRegistry.Create(
            [
                new CovenantOfflineTransitionEffectHandler(
                    (GrimoireOfflineTransitionKind)9,
                    CovenantExclusiveOperation.CovenantReset,
                    requiresOrdinaryContinuation: false),
            ]).IsFailure,
            "A handler for an undeclared kind was registered.");

        Assert.True(
            GrimoireOfflineTransitionEffectHandlerRegistry.Create(
            [
                new CovenantOfflineTransitionEffectHandler(
                    GrimoireOfflineTransitionKind.CovenantReset,
                    (CovenantExclusiveOperation)31,
                    requiresOrdinaryContinuation: false),
            ]).IsFailure,
            "A handler naming an undeclared operation was registered.");

    }

    private static GrimoireOfflineTransitionEffectHandlerRegistry Production()
    {

        Result<GrimoireOfflineTransitionEffectHandlerRegistry> created =
            GrimoireOfflineTransitionEffectHandlerRegistry.Create(
                GrimoireOfflineTransitionEffectHandlerRegistry.Declared);

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);

        return created.Value;

    }

}
