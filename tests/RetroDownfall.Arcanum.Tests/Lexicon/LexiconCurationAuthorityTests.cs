using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
public sealed class LexiconCurationAuthorityTests(GrimoireFixture fixture)
{
    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Declared_protected_mutation_without_authority_is_refused_before_any_store_read(string operation)
    {
        await using CorrectionFixture owner = new(fixture);

        await owner.SeedAsync();

        await owner.ProtectAsync();

        LexiconEntryDetail detail = await owner.ShowProtectedAsync();

        List<string> statements = [];

        raw.sqlite3_trace(owner.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        Result<LexiconCurationResult> result = await MutateAsync(owner, operation, detail.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.ProtectedMutationRefused, result.Error.Code);

        Assert.Empty(statements);
    }

    [Theory]
    [InlineData("correct")]
    [InlineData("retire")]
    [InlineData("reinstate")]
    [InlineData("pin")]
    [InlineData("unpin")]
    public async Task Label_added_after_unprotected_target_is_refused_before_reading_protected_facts(string operation)
    {
        await using CorrectionFixture owner = new(fixture);

        LexiconEntryDetail detail = await owner.SeedAsync();

        await owner.ProtectAsync();

        string[] before = await owner.SnapshotAsync();

        List<string> statements = [];

        raw.sqlite3_trace(owner.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        Result<LexiconCurationResult> result = await MutateAsync(owner, operation, detail.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, result.Error.Code);

        Assert.DoesNotContain(statements, sql => sql.Contains("FactsJson", StringComparison.Ordinal));

        raw.sqlite3_trace(owner.Connection.Handle, (strdelegate_trace)null!, null);

        Assert.Equal(before, await owner.SnapshotAsync());
    }

    [Fact]
    public async Task Effective_global_inspection_accepts_scoped_global_authority()
    {
        await using CorrectionFixture owner = new(fixture);

        await owner.SeedAsync();

        await owner.ProtectAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Read);

        await using CovenantReadLease lease = new(registration);

        var result = await owner.Service.ShowEffectiveAsync(LexiconCorrectionTests.Global, "entity", lease);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ContainsProtectedContent);

        Assert.Equal(["alpha", "beta"], result.Value.Value.Entry.Facts);
    }

    [Theory]
    [InlineData("exact", "campaign", true)]
    [InlineData("exact", "global", false)]
    [InlineData("exact", "other", false)]
    [InlineData("exact", "installation", false)]
    [InlineData("effective", "campaign", false)]
    [InlineData("effective", "global", false)]
    [InlineData("effective", "installation", true)]
    [InlineData("list", "campaign", false)]
    [InlineData("list", "global", false)]
    [InlineData("list", "installation", true)]
    public async Task Protected_Campaign_projection_requires_exact_or_installation_coverage_for_its_read_shape(
        string shape, string authority, bool accepted)
    {
        await using CorrectionFixture owner = new(fixture);

        Guid campaign = Guid.NewGuid();

        var seed = await owner.Concrete.UpsertAsync("Entity", "Person", ["campaign secret"], LexiconScope.ForCampaign(campaign));

        Assert.True(seed.IsSuccess, seed.Error.Message);

        LexiconCurationScope exact = new(LexiconScopeKind.Campaign, campaign);

        DerivedArtifactWrite write = new(SensitiveArtifactKind.Lexicon, seed.Value.Id, null, campaign, null, 1,
            DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(
                LexiconValueNormalizer.NormalizeCorrection("Entity", "Person", ["campaign secret"]).Value)),
            ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        await using (var transaction = owner.Connection.BeginTransaction())
        {
            var labeled = await ArtifactSensitivityLedger.WriteWithinAsync(owner.Connection, transaction, write, CancellationToken.None);

            Assert.True(labeled.IsSuccess, labeled.Error.Message);

            await transaction.CommitAsync();
        }

        bool installation = authority == "installation";

        using LeaseRegistration registration = new(installation ? CovenantLeaseKind.InstallationRead : CovenantLeaseKind.Read)
        {
            Snapshot = new(Guid.NewGuid(), 1, installation ? CovenantLeaseKind.InstallationRead : CovenantLeaseKind.Read,
                installation ? CovenantLeaseCoverage.Installation : CovenantLeaseCoverage.Scoped,
                installation ? null : authority == "global" ? CovenantOperationScope.Global
                    : CovenantOperationScope.ForCampaign(authority == "campaign" ? campaign : Guid.NewGuid()),
                null, 1, 1, 0, null, null, null, null, null, false),
        };

        await using ICovenantSnapshotReadLease lease = installation
            ? new CovenantInstallationReadLease(registration) : new CovenantReadLease(registration);

        List<string> statements = [];

        raw.sqlite3_trace(owner.Connection.Handle, (object _, string sql) => statements.Add(sql), null);

        if (shape == "list")
        {
            var result = await owner.Service.ListInspectionAsync(lease);

            Assert.Equal(accepted, result.IsSuccess);

            if (accepted)
            {
                Assert.True(result.Value.ContainsProtectedContent);

                Assert.Equal(["campaign secret"], Assert.Single(result.Value.Value).Facts);
            }
            else
            {
                Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);
            }
        }
        else
        {
            var result = shape == "exact" ? await owner.Service.ShowExactAsync(exact, "Entity", lease)
                : await owner.Service.ShowEffectiveAsync(exact, "Entity", lease);

            Assert.Equal(accepted, result.IsSuccess);

            if (accepted)
            {
                Assert.True(result.Value.ContainsProtectedContent);

                Assert.Equal(exact, result.Value.Value.Scope);

                Assert.Equal(["campaign secret"], result.Value.Value.Entry.Facts);
            }
            else
            {
                Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, result.Error.Code);
            }
        }

        if (!accepted)
        {
            Assert.Empty(statements);
        }
    }

    internal static Task<Result<LexiconCurationResult>> MutateAsync(
        CorrectionFixture owner, string operation, LexiconCurationTarget target, CovenantWriteLease? lease) => operation switch
        {
            "correct" => owner.Service.CorrectAsync(target, new("Person", ["replacement"]), lease),
            "retire" => owner.Service.RetireAsync(target, lease),
            "reinstate" => owner.Service.ReinstateAsync(target, lease),
            "pin" => owner.Service.PinAsync(target, lease),
            _ => owner.Service.UnpinAsync(target, lease),
        };
}
