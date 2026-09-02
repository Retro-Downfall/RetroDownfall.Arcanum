using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The real labelled-artifact guard, reading one fixture database's own label table.
/// </summary>
/// <remarks>
/// The production guard rather than a stand-in, because a stand-in is the failure this dependency
/// exists to prevent. A double that answers success to every call turns each suite that constructs
/// the repository by hand into a suite that cannot observe a refusal, which is indistinguishable from
/// the null the composed hosts were passing — and that null survived because every test around it
/// looked green.
///
/// <para>On a fixture database with no Covenant tier installed the real guard answers success anyway:
/// it treats a missing label table as "nothing protected exists here", so suites whose subject never
/// labels anything are unaffected by being handed the genuine article.</para>
/// </remarks>
internal static class FixtureLabeledArtifactGuard
{

    /// <summary>Builds the guard over the supplied fixture context.</summary>
    internal static ICovenantLabeledArtifactGuard For(ArcanumDbContext db)
    {

        CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

        return new CovenantLabeledArtifactGuard(new ArtifactSensitivityLedger(connections), connections);

    }

}
