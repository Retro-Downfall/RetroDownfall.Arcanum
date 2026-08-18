using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Issue #118 — the sole producer of the effect digest a Covenant reset and a healthy-catalog
/// factory erasure bind their exclusive owner to.
/// </summary>
/// <remarks>
/// One producer, because the digest is the identity every later stage compares against: the gate
/// acquisition, the durable checkpoint, the request-identity row, and recovery's adoption check. Two
/// implementations that agreed on the day they were written would, at the first divergence, let a
/// retry with a changed plan resume a half-finished erasure (§10.20.3).
/// </remarks>
public sealed class CovenantErasureEffectDigestTests
{

    private static readonly Guid Dataset = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly ICovenantErasureEffectDigestCalculator Calculator =
        new CovenantErasureEffectDigestCalculator();

    private static CovenantErasureEffectDigestInput Input(
        CovenantExclusiveOperation operation = CovenantExclusiveOperation.CovenantReset) =>
        new(
            operation,
            PlanId: "plan-0123456789abcdef",
            Dataset,
            Rows: 512,
            ManagedFiles: 12,
            LocalArtifacts: 3,
            AffectedSessions: 41,
            PossibleDisclosures: 7,
            CovenantDisclosureCountKind.LowerBound);

    [Fact]
    public void The_two_domains_are_pinned_ascii_constants()
    {

        Assert.Equal(
            "Arcanum.Covenant.Reset.Effect.v1",
            CovenantErasureEffectDigestCalculator.ResetDomain);

        Assert.Equal(
            "Arcanum.Covenant.HealthyCatalogFactoryErasure.Effect.v1",
            CovenantErasureEffectDigestCalculator.HealthyCatalogFactoryErasureDomain);

    }

    [Fact]
    public void The_same_plan_always_produces_the_same_thirty_two_byte_digest()
    {

        Result<CovenantDigest> first = Calculator.Compute(Input());

        Result<CovenantDigest> second = Calculator.Compute(Input());

        Assert.True(first.IsSuccess);

        Assert.True(first.Value.IsValid);

        Assert.Equal(32, first.Value.Bytes.Length);

        Assert.Equal(first.Value, second.Value);

    }

    /// <summary>
    /// A reset and a factory erasure over an identical inventory are different destructive plans, so
    /// they must never share an owner: one preserves the schema, authority taint, and disclosure
    /// evidence the other does not.
    /// </summary>
    [Fact]
    public void The_two_operations_are_domain_separated()
    {

        Assert.NotEqual(
            Calculator.Compute(Input()).Value,
            Calculator.Compute(Input(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)).Value);

    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CampaignPathMutation)]
    [InlineData(CovenantExclusiveOperation.CampaignDelete)]
    [InlineData(CovenantExclusiveOperation.ProtectedSessionTransfer)]
    [InlineData(CovenantExclusiveOperation.SchemaRepair)]
    [InlineData(CovenantExclusiveOperation.BackupRestore)]
    [InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize)]
    public void No_other_exclusive_operation_has_an_erasure_effect_digest(
        CovenantExclusiveOperation operation)
    {

        Result<CovenantDigest> computed = Calculator.Compute(Input(operation));

        Assert.True(computed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, computed.Error.Code);

    }

    [Fact]
    public void Every_field_of_the_authenticated_plan_changes_the_digest()
    {

        CovenantDigest baseline = Calculator.Compute(Input()).Value;

        CovenantErasureEffectDigestInput[] variants =
        [
            Input() with { PlanId = "plan-0123456789abcdee" },
            Input() with { DatasetGeneration = Guid.NewGuid() },
            Input() with { Rows = 513 },
            Input() with { ManagedFiles = 13 },
            Input() with { LocalArtifacts = 4 },
            Input() with { AffectedSessions = 42 },
            Input() with { PossibleDisclosures = 8 },
            Input() with { DisclosureCountKind = CovenantDisclosureCountKind.Exact },
        ];

        foreach (CovenantErasureEffectDigestInput variant in variants)
        {

            Assert.NotEqual(baseline, Calculator.Compute(variant).Value);

        }

    }

    [Fact]
    public void A_plan_with_no_identity_is_refused_rather_than_hashed()
    {

        Assert.True(Calculator.Compute(Input() with { PlanId = "   " }).IsFailure);

        Assert.True(Calculator.Compute(Input() with { DatasetGeneration = Guid.Empty }).IsFailure);

    }

    /// <summary>
    /// Counts are inventory, and a negative inventory is a measurement bug rather than a plan.
    /// Hashing it would mint a stable owner for a plan nobody could have produced.
    /// </summary>
    [Fact]
    public void A_negative_count_is_refused()
    {

        Assert.True(Calculator.Compute(Input() with { Rows = -1 }).IsFailure);

        Assert.True(Calculator.Compute(Input() with { PossibleDisclosures = -1 }).IsFailure);

    }

}
