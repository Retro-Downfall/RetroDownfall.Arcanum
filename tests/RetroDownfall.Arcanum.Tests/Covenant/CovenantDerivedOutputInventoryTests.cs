using System.Reflection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The structural check that keeps the downstream-consumer inventory closed.
/// </summary>
/// <remarks>
/// This suite is the mechanism, not a description of one. It walks the shipping assemblies for types
/// that implement a labelled-write or protected-read contract and fails when one of them has no
/// declared policy, and it walks the inventory and fails when an entry names a type that no longer
/// exists. Either direction alone would rot: an inventory nobody adds to, or an inventory full of
/// guarantees nothing provides (§10.12).
/// </remarks>
public sealed class CovenantDerivedOutputInventoryTests
{

    /// <summary>
    /// The contracts whose implementations must appear in the inventory.
    /// </summary>
    /// <remarks>
    /// Adding a contract here is how a new class of sink is brought under the rule. Each of these
    /// either writes content that can be Covenant-derived or returns content that can be.
    /// </remarks>
    private static readonly Type[] GovernedContracts =
    [
        typeof(IArtifactSensitivityLedger),

        typeof(ISessionSummaryArtifactStore),

        typeof(ISessionTitleArtifactStore),

        typeof(IProtectedAssistantArtifactReader),
    ];

    private static readonly Assembly[] ShippingAssemblies =
    [
        typeof(CovenantDerivedOutputInventory).Assembly,

        typeof(ArtifactSensitivityLedger).Assembly,
    ];

    [Fact]
    public void Every_implementation_of_a_governed_contract_has_a_declared_policy()
    {

        List<string> undeclared = [];

        foreach (Type implementation in ShippingAssemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type is { IsClass: true, IsAbstract: false })
            .Where(static type => GovernedContracts.Any(contract => contract.IsAssignableFrom(type))))
        {

            if (CovenantDerivedOutputInventory.ForOwningType(implementation.FullName!).Count == 0)
            {

                undeclared.Add(implementation.FullName!);

            }

        }

        Assert.Empty(undeclared);

    }

    [Fact]
    public void Every_declared_entry_names_a_type_that_still_exists()
    {

        foreach (CovenantDerivedOutputConsumer consumer in CovenantDerivedOutputInventory.Consumers)
        {

            Type? owning = ShippingAssemblies
                .Select(assembly => assembly.GetType(consumer.OwningTypeName, throwOnError: false))
                .FirstOrDefault(static type => type is not null);

            Assert.True(
                owning is not null,
                $"The inventory declares '{consumer.Name}' against a type that no longer exists: {consumer.OwningTypeName}.");

        }

    }

    [Fact]
    public void Declared_names_are_unique_and_every_entry_states_why()
    {

        IReadOnlyList<CovenantDerivedOutputConsumer> consumers = CovenantDerivedOutputInventory.Consumers;

        Assert.Equal(
            consumers.Count,
            consumers.Select(static consumer => consumer.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (CovenantDerivedOutputConsumer consumer in consumers)
        {

            Assert.False(string.IsNullOrWhiteSpace(consumer.Name));

            // A policy without a stated reason is a policy the next change will quietly weaken.
            Assert.True(
                consumer.Rationale.Length > 40,
                $"The inventory entry '{consumer.Name}' does not say why its policy is what it is.");

            Assert.True(Enum.IsDefined(consumer.Policy));

        }

    }

    [Fact]
    public void Every_artifact_kind_that_a_producer_writes_declares_a_propagating_producer()
    {

        SensitiveArtifactKind[] producedHere =
        [
            SensitiveArtifactKind.AssistantEntry,

            SensitiveArtifactKind.Summary,

            SensitiveArtifactKind.SessionTitle,
        ];

        foreach (SensitiveArtifactKind kind in producedHere)
        {

            Assert.Contains(
                CovenantDerivedOutputInventory.Consumers,
                consumer => consumer.ArtifactKind == kind
                    && consumer.Policy is DerivedOutputPolicy.PropagateLabel);

        }

    }

    [Fact]
    public void The_content_free_log_scope_carries_no_digest_or_content_bearing_member()
    {

        string[] forbidden = ["Digest", "Content", "Text", "Key", "Fragment", "Bytes"];

        foreach (PropertyInfo property in typeof(CovenantProtectedLogScope)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {

            Assert.DoesNotContain(
                forbidden,
                name => property.Name.Contains(name, StringComparison.Ordinal));

        }

    }

    /// <summary>
    /// The head census travels to an operator holding no protected read authority, so it is held to
    /// the same content-free vocabulary the log scope is.
    /// </summary>
    /// <remarks>
    /// <c>RenderedBytes</c> is the stated exception, and only that name: a byte total is a length, and
    /// a length is what the per-section ceiling is compared against. Every other member of the census
    /// is a bucket label or a count, so a key, a fragment, or a digest appearing here would be content
    /// crossing a boundary that declares it carries none.
    /// </remarks>
    [Fact]
    public void The_scope_census_carries_no_key_content_or_digest_bearing_member()
    {

        string[] forbidden = ["Digest", "Content", "Text", "Key", "Fragment"];

        foreach (Type shape in (Type[])[typeof(CovenantScopeCensus), typeof(CovenantScopeCensusRow)])
        {

            foreach (PropertyInfo property in shape.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {

                Assert.DoesNotContain(
                    forbidden,
                    name => property.Name.Contains(name, StringComparison.Ordinal));

                Assert.True(
                    !property.Name.Contains("Bytes", StringComparison.Ordinal)
                        || property.Name.EndsWith("RenderedBytes", StringComparison.Ordinal),
                    $"{shape.Name}.{property.Name} carries bytes that are not the declared rendered total.");

                // The Campaign figures are maxima across Campaigns, so the name carries no identity —
                // but a member that could carry one would be a string or a Guid, and there is none.
                Assert.True(
                    property.PropertyType != typeof(string)
                        && property.PropertyType != typeof(Guid)
                        && property.PropertyType != typeof(Guid?),
                    $"{shape.Name}.{property.Name} could carry an identity or free text.");

            }

        }

    }

    [Fact]
    public void The_log_scope_renders_a_fixed_vocabulary_with_no_free_text()
    {

        CovenantProtectedLogScope tainted = CovenantProtectedLogScope.FromSensitivity(
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid(), Guid.NewGuid()]));

        Assert.Equal(
            "sensitivity=covenant-derived provenance=exact generations=2",
            tainted.ToString());

        Assert.Equal("sensitivity=none", CovenantProtectedLogScope.None.ToString());

    }

    [Fact]
    public void Reducing_a_label_to_a_log_scope_drops_every_digest_it_carried()
    {

        ArtifactSensitivityLabel label = new(
            Guid.NewGuid(),
            SensitiveArtifactKind.AssistantEntry,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            artifactRevision: 3,
            Digest(5),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()]),
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));

        CovenantProtectedLogScope scope = CovenantProtectedLogScope.FromLabel(label);

        Assert.Equal(ContentSensitivity.CovenantDerived, scope.Sensitivity);

        Assert.Equal(1, scope.GenerationCount);

        Assert.Equal(label.SessionId, scope.SessionId);

        // Nothing in the rendered form can be used to confirm a candidate fragment.
        Assert.DoesNotContain(label.ArtifactContentDigest.ToString(), scope.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain(label.LabelDigest.ToString(), scope.ToString(), StringComparison.Ordinal);

    }

    private static CovenantDigest Digest(byte seed)
    {

        byte[] bytes = new byte[32];

        for (int index = 0; index < bytes.Length; index++)
        {

            bytes[index] = (byte)(seed + index);

        }

        return new CovenantDigest(bytes);

    }

}
