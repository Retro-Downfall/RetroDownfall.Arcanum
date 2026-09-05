using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

namespace RetroDownfall.Arcanum.Tests.GrimoireTransitions;

/// <summary>
/// The digest that ties an outer installation reset to the nested transition it launched.
/// </summary>
/// <remarks>
/// The value has to be produced twice from two different sources at two different times: once at
/// journal open, from the launch and the outer record's claim, and once in the reconciliation suffix,
/// from the completion receipt that was read back out of the outer record. Equality between those two
/// is the proof. If either side could derive its value from the other, the comparison would assert
/// nothing, which is the failure mode this whole seam exists to avoid.
/// </remarks>
public sealed class GrimoireOfflineTransitionParentReceiptTests
{

    private static readonly Guid OuterOperation =
        Guid.Parse("1a2b3c4d-5e6f-4071-8213-243546576879");

    private static readonly Guid NestedOperation =
        Guid.Parse("9f8e7d6c-5b4a-4938-8271-605f4e3d2c1b");

    private static readonly Guid Other =
        Guid.Parse("0c1d2e3f-4a5b-4c6d-8e7f-8091a2b3c4d5");

    [Fact]
    public void The_binding_digest_covers_every_member_and_is_domain_separated()
    {

        CovenantDigest effect = Digest(0x11);

        CovenantDigest baseline = Value(
            GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                NestedOperation,
                effect));

        Assert.True(baseline.IsValid);

        Assert.Equal(
            baseline,
            Value(GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                NestedOperation,
                effect)));

        // Every member is load-bearing: two transitions differing only in which operation launched
        // them, which operation was launched, or what effect was launched must not share a binding,
        // or one nested transition's receipt would satisfy another's journal.
        CovenantDigest[] distinct =
        [
            Value(GrimoireOfflineTransitionParentReceipt.BindingDigest(
                Other,
                NestedOperation,
                effect)),
            Value(GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                Other,
                effect)),
            Value(GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                NestedOperation,
                Digest(0x12))),
        ];

        Assert.Equal(distinct.Length, distinct.Distinct().Count());

        Assert.DoesNotContain(baseline, distinct);

    }

    [Fact]
    public void The_binding_digest_refuses_an_absent_identity_or_an_invalid_effect()
    {

        Assert.True(
            GrimoireOfflineTransitionParentReceipt.BindingDigest(
                Guid.Empty,
                NestedOperation,
                Digest(0x11)).IsFailure);

        Assert.True(
            GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                Guid.Empty,
                Digest(0x11)).IsFailure);

        Assert.True(
            GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                NestedOperation,
                default).IsFailure);

        // An outer workflow that launched itself is not a nesting. Admitting it would let one record
        // stand as both authorities, which is the substitution the two-record split forbids.
        Assert.True(
            GrimoireOfflineTransitionParentReceipt.BindingDigest(
                OuterOperation,
                OuterOperation,
                Digest(0x11)).IsFailure);

    }

    private static CovenantDigest Digest(byte first) =>
        new([.. Enumerable.Range(first, 32).Select(static value => (byte)value)]);

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        return result.Value;

    }

}
