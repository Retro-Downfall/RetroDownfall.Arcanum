using System.Reflection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Who may derive a Campaign root identity from a claimed tuple, and where a display path may be
/// resolved a second time.
/// </summary>
/// <remarks>
/// Deriving an identity from a volume and file identifier pair is deriving it from bytes somebody
/// else wrote. That is exactly what post-restart marker reconciliation needs — the marker's own
/// self-binding tuple is the only evidence a restarted process still has — and exactly what nothing
/// else should be doing, because the digest it produces looks identical to one taken from a real
/// handle. These are inventory assertions over production source rather than behavior tests, because
/// the failure they prevent is a new call site, not a wrong result (§10.12).
/// </remarks>
public sealed class CampaignPathMarkerRootProofCallSiteTests
{

    private const string DerivationMethodName = "DeriveClaimedRootIdentityDigest";

    private const string OpenerFileName = "PhysicalCampaignRootOpener.MarkerCapabilities.cs";

    private const string RestartProofFileName = "CampaignPathMarkerLifecycle.RestartRootProof.cs";

    private const string LifecycleFileName = "CampaignPathMarkerLifecycle.cs";

    [Fact]
    public void One_internal_method_derives_an_identity_from_a_claimed_volume_and_file_pair()
    {

        MethodInfo[] derivations =
        [
            .. typeof(PhysicalCampaignRootOpener)
                .GetMethods(
                    BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(static method => method.IsAssembly)
                .Where(static method => method.ReturnType == typeof(CovenantDigest?))
                .Where(static method =>
                    method.GetParameters().Select(parameter => parameter.ParameterType)
                        .SequenceEqual([typeof(ulong), typeof(ulong)])),
        ];

        MethodInfo derivation = Assert.Single(derivations);

        // Named, not merely counted. A second derivation added under a different name would satisfy a
        // count of one only until the first was deleted, and the two would already have diverged.
        Assert.Equal(DerivationMethodName, derivation.Name);

    }

    [Fact]
    public void The_restart_root_proof_is_the_only_production_caller_of_that_derivation()
    {

        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    !source.Is(OpenerFileName)
                    && !source.Is(RestartProofFileName)
                    && source.Names(DerivationMethodName))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "An identity derived from a claimed tuple is indistinguishable from one taken through a "
            + "proven handle, and only the post-restart marker proof has a reason to compare the two. "
            + "Route these through it: "
            + string.Join(", ", offenders));

    }

    [Fact]
    public void The_in_process_marker_lifecycle_resolves_no_display_path()
    {

        // The retained-root half of the protocol has to be incapable of a second path resolution, not
        // merely uninterested in one. Keeping every reopen in its own file is what makes that
        // checkable: this file is reached only when no retained root exists.
        List<string> offenders =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Is(LifecycleFileName))
                .Where(static source =>
                    source.Names("TargetDisplayPath")
                    || source.Names("IdentifyExact")
                    || source.Names("CampaignPathMarkerRootAuthority.Instance"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            offenders.Count == 0,
            "The in-process marker path uses the root it retained when it opened one. A display path "
            + "resolved a second time can be redirected by a name that became a symlink in between, "
            + "which is the whole reason the root is retained: "
            + string.Join(", ", offenders));

    }

}
