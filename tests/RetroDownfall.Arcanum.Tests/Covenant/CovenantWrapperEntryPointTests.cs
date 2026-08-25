using System.Reflection;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Which entry point a Covenant suite is allowed to reach the dispatch gate through.
/// </summary>
/// <remarks>
/// Every member of <see cref="CovenantDispatchGate"/> pinned below has exactly one production caller,
/// and that caller is a private method on <see cref="WizardIntelligenceProvider"/> which adds
/// something the gate does not do for itself — an early return, a fail-closed comparison, or the
/// measurement the argument is supposed to carry. A suite that calls the gate member instead runs the
/// gate's half of the journey and none of the wrapper's, so it stays green over a wrapper that returns
/// early on every fresh installation, or that hands the gate an argument production would never build.
/// That is not a hypothetical: the unbootstrappable-feature defect was a passing test standing over
/// exactly that gap, with a working revert-proof attached.
///
/// <para>The wrappers are private, so no test can call one. Entering through the wrapper therefore
/// means entering through the provider's public turn surface, which is the point — the wrapper's guard
/// only runs on the path production actually takes.</para>
///
/// <para>These are inventory assertions over authored source rather than behavior tests, because the
/// failure they prevent is a new call site, not a wrong result.</para>
/// </remarks>
public sealed class CovenantWrapperEntryPointTests
{

    private const string GateSourceFileName = "CovenantDispatchGate.cs";

    private const string WrapperSourceFileName = "WizardIntelligenceProvider.cs";

    /// <summary>
    /// The suites of this file's own inventory. Excluded from the tests-tree scan because the pair
    /// table below names every pinned member, so without this the rule would report itself.
    /// </summary>
    private const string ThisSourceFileName = "CovenantWrapperEntryPointTests.cs";

    /// <summary>
    /// The gate members whose production caller is a guarded private wrapper, and the wrapper.
    /// </summary>
    /// <remarks>
    /// <c>BeginTurnAsync</c> is deliberately absent, and its absence is a compromise rather than a
    /// judgement that it does not belong. It is wrapped the same way — <c>BeginCovenantTurnAsync</c>
    /// substitutes the inert scope for a host that composed no Covenant arm — but its simple name is
    /// also the name of <c>ICovenantContextProvider.BeginTurnAsync</c> one layer below, which several
    /// suites call legitimately and directly. A source scan cannot tell the two apart without
    /// resolving the receiver's type, so pinning it would report every context-provider suite as an
    /// offender. The layer below it, <c>ICovenantContextProvider.BeginTurnAsync</c> reached past
    /// <c>CovenantDispatchGate</c>, is unpinnable here for the same reason.
    /// </remarks>
    public static TheoryData<string, string> GuardedGateMembers => new()
    {
        // Freezes the envelope, refuses a transcript whose system message has drifted from the frozen
        // prompt, and returns success without a receipt on a turn that can neither inject nor stage.
        { "AcknowledgeDispatchAsync", "AcknowledgeCovenantDispatchAsync" },

        // Supplies the headroom and both token measurers. A caller that invents them plans against a
        // budget no turn ever has, so every "the section fitted" assertion is about arithmetic the
        // test wrote itself.
        { "PlanDispatch", "ResolveCovenantAdmission" },

        // Only ever resolved from a scope and a plan the wrapper already built, immediately before the
        // envelope that carries the label is frozen.
        { "ResolveSensitivity", "AcknowledgeCovenantDispatchAsync" },
    };

    [Theory]

    [MemberData(nameof(GuardedGateMembers))]
    public void Each_guarded_gate_member_is_still_reached_through_a_private_provider_wrapper(
        string gateMember,
        string wrapperMember)
    {

        Assert.Contains(
            typeof(CovenantDispatchGate).GetMethods(DeclaredMembers),
            method => method.Name == gateMember);

        MethodInfo[] wrappers =
        [
            .. typeof(WizardIntelligenceProvider)
                .GetMethods(DeclaredMembers)
                .Where(method => method.Name == wrapperMember),
        ];

        MethodInfo wrapper = Assert.Single(wrappers);

        // Private, and asserted rather than assumed. A wrapper that became visible would give a suite
        // a way to call it that production never takes, and the rule below would start exempting
        // tests that had reached the guard without reaching the turn the guard exists to protect.
        Assert.True(
            wrapper.IsPrivate,
            $"WizardIntelligenceProvider.{wrapperMember} is no longer private, so a test can now "
            + "invoke the guard without going through the turn that invokes it in production.");

    }

    /// <summary>
    /// Simple-name matching is only sound while the name means one thing. A second declaration of it
    /// anywhere on the Covenant or intelligence surface would make every assertion below ambiguous —
    /// silently, and in the direction that stops reporting offenders.
    /// </summary>
    [Theory]

    [MemberData(nameof(GuardedGateMembers))]
    public void Each_guarded_gate_member_name_means_one_thing_across_the_covenant_surface(
        string gateMember,
        string wrapperMember)
    {

        _ = wrapperMember;

        List<string> declarations =
        [
            .. CovenantSurfaceTypes()
                .SelectMany(static type => type.GetMethods(DeclaredMembers))
                .Where(method => method.Name == gateMember)
                .Select(static method => $"{method.DeclaringType?.FullName}.{method.Name}"),
        ];

        Assert.True(
            declarations.Distinct(StringComparer.Ordinal).Count() == 1,
            $"'{gateMember}' is declared more than once on the Covenant and intelligence surface, so "
            + "a source scan can no longer attribute a call to the gate. Rename one of them, or pin "
            + "the member by its qualified call form: "
            + string.Join(", ", declarations.Distinct(StringComparer.Ordinal)));

    }

    [Theory]

    [MemberData(nameof(GuardedGateMembers))]
    public void The_provider_wrapper_is_the_only_production_caller_of_a_guarded_gate_member(
        string gateMember,
        string wrapperMember)
    {

        _ = wrapperMember;

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => !source.Is(GateSourceFileName)
                    && !source.Is(WrapperSourceFileName)
                    && source.Names(gateMember))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            $"A production file other than the gate and its wrapper calls '{gateMember}', so the "
            + "guard the wrapper adds is no longer the only way in and the rule the suites are held "
            + "to below no longer describes production: "
            + string.Join(", ", offenders));

    }

    /// <summary>
    /// The discriminator is derived from the type under test rather than listed by hand: the gate's own
    /// unit-test class is <c>CovenantDispatchGate</c> plus the suffix this repository gives every such
    /// class, and it is the one suite whose subject genuinely is the inner method. Every other suite is
    /// testing a journey, and a journey runs the wrapper.
    /// </summary>
    /// <remarks>
    /// The exemption is a whole file name, not a prefix. <c>CovenantDispatchGateTestsFixture.cs</c>
    /// would be a second suite sharing the exemption by accident, which is how an exemption written for
    /// one file stops reporting the file it was written for.
    /// </remarks>
    [Theory]

    [MemberData(nameof(GuardedGateMembers))]
    public void Only_the_gate_s_own_unit_tests_call_a_guarded_gate_member_directly(
        string gateMember,
        string wrapperMember)
    {

        _ = wrapperMember;

        string ownUnitTests = $"{nameof(CovenantDispatchGate)}Tests.cs";

        List<string> offenders =
        [
            .. ProductionSourceInventory.TestSuiteSources()
                .Where(source => !source.Is(ownUnitTests)
                    && !source.Is(ThisSourceFileName)
                    && source.Names(gateMember))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            $"A suite calls CovenantDispatchGate.{gateMember} directly, skipping "
            + $"WizardIntelligenceProvider.{wrapperMember} — the private wrapper that is the only "
            + "production caller, and that holds the guard. Whatever this suite proves about the gate, "
            + "it proves nothing about the turn, and it will stay green over a wrapper that returns "
            + "early or hands the gate something no turn would build. Enter through the provider's "
            + "turn surface instead: "
            + string.Join(", ", offenders));

    }

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> CovenantSurfaceTypes() =>
        CovenantProductionSurface.Types();

}
