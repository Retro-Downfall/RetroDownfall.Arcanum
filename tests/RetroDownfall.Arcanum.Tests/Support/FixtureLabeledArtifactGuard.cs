using Microsoft.Extensions.Logging.Abstractions;

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
/// <para>Every fixture database carries the Core label table, empty until something labels an artifact,
/// so the real guard answers success for suites whose subject never labels anything and they are
/// unaffected by being handed the genuine article. A fixture database whose label table cannot be read
/// is refused, as it is in production.</para>
/// </remarks>
internal static class FixtureLabeledArtifactGuard
{

    /// <summary>Builds the guard over the supplied fixture context.</summary>
    internal static ICovenantLabeledArtifactTransactionGuard For(ArcanumDbContext db)
    {

        CovenantConnectionSource connections = new(db, FixtureOrdinaryConnectionFactory.For(db));

        return new CovenantLabeledArtifactGuard(
            new ArtifactSensitivityLedger(connections),
            NullLogger<CovenantLabeledArtifactGuard>.Instance);

    }

}
