using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The release port on its own, over a catalog the erase routes could never have written evidence into.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryErasureReleaseServiceTests
{
    private static CancellationToken Token => CancellationToken.None;

    /// <summary>
    /// Below Core version 13 there is no evidence to release, and the release says the feature is not
    /// ready yet rather than reporting that nothing was fingerprinted. It never asks for the key.
    /// </summary>
    [SkippableFact]
    public async Task Release_below_core_thirteen_is_unavailable_and_touches_no_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(Token);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionTwelveFixture.ChainSet(),
            64,
            Token);

        Assert.Equal(12, installed.Core.SchemaVersion);

        await using ArcanumDbContext db = SagaMemoryMidUpgradeWriteTests.CreateContext(file);

        CountingOsCredentialStore counting = new(new InMemoryOsCredentialStore());

        using MemoryErasureKeyring keys = MemoryErasureTestKeys.Isolated(counting);

        MemoryErasureRelease release = new(db, keys, NullLogger<MemoryErasureRelease>.Instance);

        Assert.Equal(
            ErrorCodes.MemoryErasure.Unavailable,
            (await release.ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, "x"), Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.MemoryErasure.Unavailable,
            (await release.ReleaseLexiconAsync(new(new LexiconCurationScope(LexiconScopeKind.Global, null), "x"), Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.MemoryErasure.Unavailable,
            (await release.ReleaseCovenantAsync(new(CovenantScope.Global, null, "preference.x"), Token)).Error.Code);

        Assert.Equal(0, counting.Calls);
    }
}
