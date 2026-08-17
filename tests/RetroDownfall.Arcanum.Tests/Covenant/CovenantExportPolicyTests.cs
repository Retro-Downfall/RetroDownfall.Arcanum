using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #114 — the one reader both plaintext export routes ask before they emit a byte.
/// </summary>
/// <remarks>
/// The interesting cases are the refusals. A policy that answered "clean" for a Session whose labels
/// were purged, or that counted another Campaign's rows into this Campaign's exclusion report, would
/// let an operator believe an export carried less than it did — and an export is nonrevocable once
/// the file exists (§10.19.11).
/// </remarks>
public sealed class CovenantExportPolicyTests
{

    private static readonly Guid Session = Guid.Parse("0A1B2C3D-4E5F-4A6B-8C9D-0E1F2A3B4C5D");

    private static readonly Guid OtherSession = Guid.Parse("6B7C8D9E-0F10-4A2B-8C3D-4E5F60718293");

    private static readonly Guid Campaign = Guid.Parse("1B2C3D4E-5F60-4B7C-8D9E-1F2A3B4C5D6E");

    private static readonly Guid OtherCampaign = Guid.Parse("2C3D4E5F-6071-4C8D-9EAF-2A3B4C5D6E7F");

    private static readonly Guid Generation = Guid.Parse("3D4E5F60-7182-4D9E-8FA0-3B4C5D6E7F80");

    /// <summary>
    /// A Session that has never produced a derived artifact has no label row and no projection row.
    /// Absence is the honest clean answer, not a missing measurement.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_label_and_no_projection_is_clean()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        Result<CovenantSessionExportSensitivity> decision = await fixture.Policy.InspectSessionAsync(
            Session,
            fixture.InstallationLease,
            CancellationToken.None);

        Assert.True(decision.IsSuccess);

        Assert.False(decision.Value.IsRefused);

        Assert.Equal(0, decision.Value.TaintedArtifactCount);

        Assert.Equal(ContentSensitivity.None, decision.Value.MaximumSensitivity);

    }

    /// <summary>
    /// Every artifact kind the acceptance criterion names, one at a time. A refusal that only covered
    /// Entries would let a tainted title or a tainted Saga ride out inside an otherwise clean export.
    /// </summary>
    [Theory]
    [InlineData(SensitiveArtifactKind.AssistantEntry)]
    [InlineData(SensitiveArtifactKind.ToolArtifact)]
    [InlineData(SensitiveArtifactKind.Summary)]
    [InlineData(SensitiveArtifactKind.SessionTitle)]
    [InlineData(SensitiveArtifactKind.Saga)]
    [InlineData(SensitiveArtifactKind.Lexicon)]
    [InlineData(SensitiveArtifactKind.ManagedWorkspaceFile)]
    [InlineData(SensitiveArtifactKind.SearchProjection)]
    public async Task A_session_carrying_any_tainted_artifact_kind_is_refused(SensitiveArtifactKind kind)
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        await fixture.LabelAsync(kind, Session);

        Result<CovenantSessionExportSensitivity> decision = await fixture.Policy.InspectSessionAsync(
            Session,
            fixture.InstallationLease,
            CancellationToken.None);

        Assert.True(decision.IsSuccess);

        Assert.True(decision.Value.IsRefused);

        Assert.Equal(1, decision.Value.TaintedArtifactCount);

        Assert.Equal(ContentSensitivity.CovenantDerived, decision.Value.MaximumSensitivity);

    }

    /// <summary>
    /// Another Session's taint is not this Session's. A policy that read the label ledger without its
    /// owner would refuse every export on any installation that had ever held Covenant content.
    /// </summary>
    [Fact]
    public async Task Another_sessions_taint_does_not_refuse_this_session()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        await fixture.LabelAsync(SensitiveArtifactKind.AssistantEntry, OtherSession);

        Result<CovenantSessionExportSensitivity> decision = await fixture.Policy.InspectSessionAsync(
            Session,
            fixture.InstallationLease,
            CancellationToken.None);

        Assert.True(decision.IsSuccess);

        Assert.False(decision.Value.IsRefused);

    }

    /// <summary>
    /// The projection is conservative in one direction only, and this is that direction. Purged taint
    /// still bars a plaintext export for the same reason it still bars a cached replay: the Session
    /// held Covenant content, and removing the artifact does not unmake that.
    /// </summary>
    [Fact]
    public async Task A_session_whose_labels_were_purged_is_still_refused_by_its_projection()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        await fixture.LabelAsync(SensitiveArtifactKind.AssistantEntry, Session);

        await fixture.DeleteLabelsAsync();

        Result<CovenantSessionExportSensitivity> decision = await fixture.Policy.InspectSessionAsync(
            Session,
            fixture.InstallationLease,
            CancellationToken.None);

        Assert.True(decision.IsSuccess);

        Assert.True(decision.Value.IsRefused);

        Assert.Equal(ContentSensitivity.CovenantDerived, decision.Value.MaximumSensitivity);

    }

    /// <summary>
    /// The two counts answer two questions: what Covenant memory this Campaign holds, and what
    /// artifacts of its own are Covenant-derived. Folding them into one total would tell an operator
    /// a number that cannot be acted on.
    /// </summary>
    [Fact]
    public async Task Campaign_exclusions_count_covenant_entries_and_tainted_artifacts_separately()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        await fixture.InsertCampaignCovenantEntryAsync(Campaign, "alpha");

        await fixture.InsertCampaignCovenantEntryAsync(Campaign, "beta");

        await fixture.LabelAsync(SensitiveArtifactKind.Saga, Session);

        Result<CovenantCampaignExportExclusions> exclusions = await fixture.Policy
            .InventoryCampaignExclusionsAsync(Campaign, fixture.CampaignLease, CancellationToken.None);

        Assert.True(exclusions.IsSuccess);

        Assert.Equal(2, exclusions.Value.CovenantEntryCount);

        Assert.Equal(1, exclusions.Value.TaintedArtifactCount);

    }

    /// <summary>
    /// A Global Covenant entry and another Campaign's rows were never this export's to carry, so
    /// counting them would overstate what this export left behind.
    /// </summary>
    [Fact]
    public async Task Campaign_exclusions_ignore_global_entries_and_another_campaigns_rows()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        await fixture.InsertGlobalCovenantEntryAsync("global");

        await fixture.InsertCampaignCovenantEntryAsync(OtherCampaign, "elsewhere");

        await fixture.LabelAsync(SensitiveArtifactKind.Saga, Session, OtherCampaign);

        Result<CovenantCampaignExportExclusions> exclusions = await fixture.Policy
            .InventoryCampaignExclusionsAsync(Campaign, fixture.CampaignLease, CancellationToken.None);

        Assert.True(exclusions.IsSuccess);

        Assert.Equal(0, exclusions.Value.CovenantEntryCount);

        Assert.Equal(0, exclusions.Value.TaintedArtifactCount);

    }

    /// <summary>
    /// A Session's labels can name any Campaign, so nothing narrower than an installation read covers
    /// the question. An under-scoped lease fails before SQL rather than being supplemented by a second
    /// acquisition.
    /// </summary>
    [Fact]
    public async Task A_scoped_lease_cannot_cover_a_session_inspection()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        Result<CovenantSessionExportSensitivity> decision = await fixture.Policy.InspectSessionAsync(
            Session,
            fixture.CampaignLease,
            CancellationToken.None);

        Assert.True(decision.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, decision.Error.Code);

    }

    /// <summary>
    /// A lease taken over one Campaign does not cover the inventory of another.
    /// </summary>
    [Fact]
    public async Task A_lease_over_another_campaign_cannot_cover_this_inventory()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        Result<CovenantCampaignExportExclusions> exclusions = await fixture.Policy
            .InventoryCampaignExclusionsAsync(OtherCampaign, fixture.CampaignLease, CancellationToken.None);

        Assert.True(exclusions.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, exclusions.Error.Code);

    }

    /// <summary>
    /// An installation read covers every Campaign, so it covers one.
    /// </summary>
    [Fact]
    public async Task An_installation_lease_covers_a_campaign_inventory()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        await fixture.InsertCampaignCovenantEntryAsync(Campaign, "alpha");

        Result<CovenantCampaignExportExclusions> exclusions = await fixture.Policy
            .InventoryCampaignExclusionsAsync(Campaign, fixture.InstallationLease, CancellationToken.None);

        Assert.True(exclusions.IsSuccess);

        Assert.Equal(1, exclusions.Value.CovenantEntryCount);

    }

    /// <summary>
    /// With the feature off there is no arm at all, and the route that asked runs exactly as it did
    /// before the Covenant tier existed. An absent arm is a real answer rather than a failure.
    /// </summary>
    [Fact]
    public async Task With_the_feature_disabled_the_conditional_arm_is_absent()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync(featureEnabled: false);

        Result<CovenantExportAdmission> admission = await fixture.Policy
            .AcquireConditionalReadAsync(scope: null, CancellationToken.None);

        Assert.True(admission.IsSuccess);

        Assert.False(admission.Value.IsProtected);

        Assert.Null(admission.Value.ReadLease);

    }

    /// <summary>
    /// With the feature on the arm takes exactly one lease, and the caller owns it.
    /// </summary>
    [Fact]
    public async Task With_the_feature_enabled_the_conditional_arm_takes_one_lease()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        int before = fixture.Gate.LiveRegistrationCount;

        Result<CovenantExportAdmission> admission = await fixture.Policy
            .AcquireConditionalReadAsync(scope: null, CancellationToken.None);

        Assert.True(admission.IsSuccess);

        Assert.True(admission.Value.IsProtected);

        Assert.Equal(
            CovenantLeaseCoverage.Installation,
            admission.Value.ReadLease!.Snapshot.Coverage);

        // Exactly one, and it is the caller's. A port that acquired a second for its own reads would
        // answer from a snapshot the response was never bound to.
        Assert.Equal(before + 1, fixture.Gate.LiveRegistrationCount);

        await admission.Value.ReadLease.DisposeAsync();

        Assert.Equal(before, fixture.Gate.LiveRegistrationCount);

    }

    /// <summary>
    /// An enabled feature whose canonical tier cannot be read fails closed. "Cannot prove clean" is
    /// the only reading a plaintext export may take from an unreadable label ledger.
    /// </summary>
    [Fact]
    public async Task An_enabled_feature_over_an_unhealthy_tier_fails_closed()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        fixture.Availability.Mutate(static current => current with
        {
            Canonical = CovenantCapabilityState.Unavailable,
        });

        Result<CovenantExportAdmission> admission = await fixture.Policy
            .AcquireConditionalReadAsync(scope: null, CancellationToken.None);

        Assert.True(admission.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, admission.Error.Code);

    }

    /// <summary>
    /// A Campaign export names its Campaign in the route, so its arm takes the exact scoped lease
    /// rather than an installation-wide one it does not need.
    /// </summary>
    [Fact]
    public async Task A_campaign_scope_takes_the_exact_scoped_lease()
    {

        await using ExportPolicyFixture fixture = await ExportPolicyFixture.CreateAsync();

        Result<CovenantExportAdmission> admission = await fixture.Policy.AcquireConditionalReadAsync(
            CovenantOperationScope.ForCampaign(Campaign),
            CancellationToken.None);

        Assert.True(admission.IsSuccess);

        Assert.Equal(CovenantLeaseCoverage.Scoped, admission.Value.ReadLease!.Snapshot.Coverage);

        Assert.Equal(Campaign, admission.Value.ReadLease.Snapshot.Scope!.Value.CampaignId);

        await admission.Value.ReadLease.DisposeAsync();

    }

    /// <summary>
    /// A scratch Grimoire carrying the canonical tier and the four core objects the export policy
    /// reads, plus a real gate so an acquisition test proves a lease rather than a stub.
    /// </summary>
    private sealed class ExportPolicyFixture : IAsyncDisposable
    {

        private readonly CovenantSchemaScratchDatabase _database;

        private readonly ArtifactSensitivityLedger _ledger;

        private ExportPolicyFixture(
            CovenantSchemaScratchDatabase database,
            FakeCovenantAvailability availability,
            CovenantOperationGate gate,
            CovenantInstallationReadLease installationLease,
            CovenantReadLease campaignLease)
        {

            _database = database;

            _ledger = new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(database.Connection));

            Availability = availability;

            Gate = gate;

            InstallationLease = installationLease;

            CampaignLease = campaignLease;

            Policy = new CovenantExportPolicy(
                availability,
                gate,
                new FixedCovenantConnectionSource(database.Connection));

        }

        internal FakeCovenantAvailability Availability { get; }

        internal CovenantOperationGate Gate { get; }

        internal CovenantExportPolicy Policy { get; }

        internal CovenantInstallationReadLease InstallationLease { get; }

        internal CovenantReadLease CampaignLease { get; }

        internal static async Task<ExportPolicyFixture> CreateAsync(bool featureEnabled = true)
        {

            CovenantSchemaScratchDatabase database = await CovenantSchemaScratchDatabase
                .CreateAsync(CancellationToken.None);

            try
            {

                await database.InstallCanonicalAsync(CancellationToken.None);

                await database.InstallCoreObjectsAsync(
                    ["Campaigns", "Sessions", "artifact_sensitivity", "session_sensitivity_state"],
                    CancellationToken.None);

                await SeedSessionAsync(database, Session);

                await SeedSessionAsync(database, OtherSession);

                FakeCovenantAvailability availability = new();

                if (!featureEnabled)
                {

                    availability.Mutate(static current => current with { FeatureEnabled = false });

                }

                CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

                CovenantInstallationReadLease installationLease =
                    (await gate.AcquireInstallationReadAsync(CancellationToken.None)).Value;

                CovenantReadLease campaignLease = (await gate.AcquireReadAsync(
                    CovenantOperationScope.ForCampaign(Campaign),
                    CancellationToken.None)).Value;

                return new ExportPolicyFixture(
                    database,
                    availability,
                    gate,
                    installationLease,
                    campaignLease);

            }
            catch
            {

                await database.DisposeAsync();

                throw;

            }

        }

        internal async Task LabelAsync(
            SensitiveArtifactKind kind,
            Guid sessionId,
            Guid? campaignId = null)
        {

            Result<LabeledArtifactWriteReceipt> receipt = await _ledger.LabelAsync(
                new DerivedArtifactWrite(
                    kind,
                    Guid.NewGuid(),
                    sessionId,
                    campaignId ?? Campaign,
                    null,
                    1,
                    Digest(3),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Generation])),
                CancellationToken.None);

            Assert.True(receipt.IsSuccess);

        }

        internal Task DeleteLabelsAsync() =>
            _database.ExecuteAsync("DELETE FROM artifact_sensitivity;", CancellationToken.None);

        internal Task InsertGlobalCovenantEntryAsync(string key) =>
            InsertCovenantEntryAsync(scopeCode: 1, campaignId: null, key);

        internal Task InsertCampaignCovenantEntryAsync(Guid campaignId, string key) =>
            InsertCovenantEntryAsync(scopeCode: 2, campaignId, key);

        public async ValueTask DisposeAsync()
        {

            await InstallationLease.DisposeAsync();

            await CampaignLease.DisposeAsync();

            await _database.DisposeAsync();

        }

        private static CovenantDigest Digest(byte seed)
        {

            byte[] bytes = new byte[32];

            for (int index = 0; index < bytes.Length; index++)
            {

                bytes[index] = unchecked((byte)(seed + index));

            }

            return new CovenantDigest(bytes);

        }

        private static async Task SeedSessionAsync(CovenantSchemaScratchDatabase database, Guid sessionId)
        {

            await using SqliteCommand seed = database.Connection.CreateCommand();

            seed.CommandText = """
                INSERT INTO "Sessions" ("Id", "Title", "CreatedAt", "UpdatedAt")
                VALUES ($sessionId, 'export', $now, $now);
                """;

            _ = seed.Parameters.AddWithValue("$sessionId", sessionId.ToString().ToUpperInvariant());

            _ = seed.Parameters.AddWithValue("$now", "2026-08-17T00:00:00.0000000+00:00");

            _ = await seed.ExecuteNonQueryAsync(CancellationToken.None);

        }

        private async Task InsertCovenantEntryAsync(long scopeCode, Guid? campaignId, string key)
        {

            await using SqliteCommand insert = _database.Connection.CreateCommand();

            insert.CommandText = """
                INSERT INTO covenant_entries (
                    EntryId, ScopeCode, CampaignId, AuthoredKey, NormalizedKey, CreatedAtUtc)
                VALUES ($entryId, $scopeCode, $campaignId, $key, $key, $now);
                """;

            // Exactly how CovenantMutationKernel writes them. The canonical family and the label
            // ledger disagree about case, so a fixture that "tidied" this would pass against a policy
            // that could never match a real row.
            _ = insert.Parameters.AddWithValue("$entryId", Guid.NewGuid().ToString("D"));

            _ = insert.Parameters.AddWithValue("$scopeCode", scopeCode);

            _ = insert.Parameters.AddWithValue(
                "$campaignId",
                campaignId is { } value ? value.ToString("D") : DBNull.Value);

            _ = insert.Parameters.AddWithValue("$key", key);

            _ = insert.Parameters.AddWithValue("$now", "2026-08-17T00:00:00.0000000+00:00");

            _ = await insert.ExecuteNonQueryAsync(CancellationToken.None);

        }

    }

}
