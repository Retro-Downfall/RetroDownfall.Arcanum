using System.Reflection;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Pins the shape the hosted-producer checker depends on: the gate's attestation and the lease's revocation getter are
/// abstract, and the admission contracts declare no default (non-abstract) instance member beyond one pure forward.
/// </summary>
/// <remarks>
/// A default interface body would compile cleanly, because every implementation now states both members explicitly.
/// The checker would then walk lifetime on that default body while traversing the DI-bound override for effects. A
/// default slot whose override disposes the lease therefore keeps the acquired Work across a later send, a false grant
/// held back only by an unresolved-disposal diagnostic (and, for an asynchronous override, an unproven-callback one).
/// The LeaseArgumentAttestation default-slot rows document that contrast.
/// Tracked follow-up for the checker workstreams: resolve lifetime on the bound override instead of the default body,
/// after which abstractness stops being the only guard.
/// </remarks>
public sealed class GrimoireConnectionAdmissionContractShapeTests
{
    private const BindingFlags DeclaredInstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void Gate_attestation_has_no_default_body()
    {
        MethodInfo attestation = Assert.Single(
            typeof(IGrimoireConnectionAdmissionGate).GetMethods(DeclaredInstanceMembers),
            static method => method.Name == nameof(IGrimoireConnectionAdmissionGate.AttestDrainedMaintenance));

        Assert.True(attestation.IsAbstract);
    }

    [Fact]
    public void Lease_revocation_pending_getter_has_no_default_body()
    {
        PropertyInfo pending = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(IGrimoireWorkLease).GetProperty(nameof(IGrimoireWorkLease.MaintenanceRevocationPending), DeclaredInstanceMembers));

        MethodInfo getter = Assert.IsAssignableFrom<MethodInfo>(pending.GetMethod);

        Assert.True(getter.IsAbstract);

        Assert.Null(pending.SetMethod);
    }

    // The gate contract has exactly one default body: a pure forward to the abstract WaitForNextOpenGenerationAsync. It
    // takes no lease and has no lifetime or effect of its own, so it cannot stand in for an attestation. The set is
    // pinned exactly: a new default slot fails here, and so does dropping this one without updating the pin.
    [Fact]
    public void Gate_contract_default_members_are_exactly_the_pure_generation_wait() =>
        AssertDefaultInstanceMembers(typeof(IGrimoireConnectionAdmissionGate), nameof(IGrimoireConnectionAdmissionGate.WaitForOpenGenerationAfterRefusalAsync));

    [Fact]
    public void Work_lease_contract_has_no_default_members() =>
        AssertDefaultInstanceMembers(typeof(IGrimoireWorkLease));

    private static void AssertDefaultInstanceMembers(Type contract, params string[] expected)
    {
        Assert.True(contract.IsInterface);

        MethodInfo[] members = contract.GetMethods(DeclaredInstanceMembers);

        // Property and event accessors are methods too, so a default getter, setter or event body is caught here.
        Assert.NotEmpty(members);

        string[] defaults = members.Where(static member => !member.IsAbstract).Select(static member => member.Name)
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(), defaults);
    }
}
