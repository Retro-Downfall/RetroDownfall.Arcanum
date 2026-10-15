using System.Reflection;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The four Long Rest ports <c>CampaignRollupTurnPreparer</c> now requires, for fixtures that never enable Long Rest. Reaching any
/// member fails the test loudly: a fixture that needs Long Rest supplies its own real or scripted port instead.
/// </summary>
internal static class UnreachedLongRestPorts
{
    internal static ILongRestSweepCoordinator Coordinator => Unreached<ILongRestSweepCoordinator>();

    internal static ILongRestMaintenance Maintenance => Unreached<ILongRestMaintenance>();

    internal static ILongRestDiscoveryStore Discovery => Unreached<ILongRestDiscoveryStore>();

    /// <summary>A configuration view that reports Long Rest disabled, which is what a fixture without the feature observes.</summary>
    internal static ILongRestConfigurationProvider Configuration => new DisabledConfiguration();

    private static T Unreached<T>() where T : class => DispatchProxy.Create<T, UnreachedLongRestProxy>();

    private sealed class DisabledConfiguration : ILongRestConfigurationProvider
    {
        public LongRestSweepConfiguration Capture() => new(
            new LongRestConfigurationStamp(Guid.NewGuid(), 0, new RetroDownfall.Arcanum.Core.Covenant.CovenantDigest(new byte[32])),
            false, false,
            Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.Unavailable, "Long Rest is not enabled in this fixture.")));

        public bool IsCurrent(LongRestConfigurationStamp stamp) => false;
    }
}

/// <summary>Throws on every call; a proxy type must itself be public for <see cref="DispatchProxy"/>.</summary>
public class UnreachedLongRestProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new InvalidOperationException($"{targetMethod?.DeclaringType?.Name}.{targetMethod?.Name} was reached by a fixture that does not enable Long Rest.");
}
