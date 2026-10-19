using System.Collections.Immutable;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class SagaRankingEvidenceReaderTests
{
    private static readonly CovenantDigest Digest = new(new byte[32]);

    [SkippableTheory]
    [InlineData(MemoryEvaluationOutcome.Benefit, 1, 0, 0)]
    [InlineData(MemoryEvaluationOutcome.Harm, 0, 1, 0)]
    [InlineData(MemoryEvaluationOutcome.NoMeasurableEffect, 0, 0, 1)]
    public async Task A_valid_single_source_report_contributes_once_to_its_current_exact_version(
        MemoryEvaluationOutcome outcome, int benefits, int harms, int noEffects)
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        MemoryEvaluationStoredEvidence report = await h.ReportAsync([source], outcome);

        await h.CommitUseAsync(source);

        SagaRankingEvidence evidence = Assert.Single(await h.ReadAsync()).Value;

        Assert.Equal(benefits, evidence.BenefitCount);

        Assert.Equal(harms, evidence.HarmCount);

        Assert.Equal(noEffects, evidence.NoEffectCount);

        Assert.Equal(1, evidence.SuccessfulUseCount);

        Assert.Equal(outcome == MemoryEvaluationOutcome.Benefit ? report.Report!.CompletedAtUtc : null, evidence.LastBenefitAtUtc);
    }

    [SkippableFact]
    public async Task Status_counts_measured_credit_by_exact_current_version_without_crediting_uncorroborated_benefit()
    {
        await using Harness h = await Harness.CreateAsync();

        h.Saga.Options.CurrentValue.Features.LongRest = true;

        MemoryEvaluationSourceCoordinate useful = await h.InsertAsync();

        await h.ReportAsync([useful]);

        await h.ReportAsync([useful]);

        await h.CommitUseAsync(useful);

        MemoryEvaluationSourceCoordinate harmful = await h.InsertAsync();

        await h.ReportAsync([harmful], MemoryEvaluationOutcome.Harm);

        MemoryEvaluationSourceCoordinate uncorroborated = await h.InsertAsync();

        await h.ReportAsync([uncorroborated]);

        MemoryEvaluationSourceCoordinate noEffect = await h.InsertAsync();

        await h.ReportAsync([noEffect], MemoryEvaluationOutcome.NoMeasurableEffect);

        await h.InsertAsync();

        SagaMemoryStore store = new(h.Saga.Context, h.Saga.VectorAccelerator, h.Saga.Options,
            MemoryErasureTestKeys.Isolated(), timeProvider: new Instant(h.AsOf));

        var status = (await store.ReadRankingStatusAsync(MemoryScope.Installation, null, CancellationToken.None))!;

        Assert.Equal(5, status.MemoryCount);

        Assert.Equal(5, status.VersionCount);

        Assert.Equal(1, status.PositiveCreditVersionCount);

        Assert.Equal(1, status.NegativeCreditVersionCount);

        Assert.Equal(1, status.UncorroboratedBenefitVersionCount);

        await h.Saga.LabelSensitiveAsync(Guid.Parse(useful.SubjectId));

        var afterProtection = (await store.ReadRankingStatusAsync(MemoryScope.Installation, null, CancellationToken.None))!;

        Assert.Equal(4, afterProtection.MemoryCount);

        Assert.Equal(4, afterProtection.VersionCount);

        Assert.Equal(0, afterProtection.PositiveCreditVersionCount);

        Assert.Equal(1, afterProtection.NegativeCreditVersionCount);

        Assert.Equal(1, afterProtection.UncorroboratedBenefitVersionCount);

        h.Saga.Options.CurrentValue.Features.LongRest = false;

        var disabled = (await store.ReadRankingStatusAsync(MemoryScope.Installation, null, CancellationToken.None))!;

        Assert.False(disabled.Enabled);

        Assert.Equal(4, disabled.VersionCount);

        Assert.Equal(0, disabled.PositiveCreditVersionCount);

        Assert.Equal(0, disabled.NegativeCreditVersionCount);

        Assert.Equal(0, disabled.UncorroboratedBenefitVersionCount);
    }

    [SkippableFact]
    public async Task Benefit_without_a_retained_native_committed_use_is_diagnostic_without_credit()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        SagaRankingEvidence evidence = Assert.Single(await h.ReadAsync()).Value;

        Assert.Equal(1, evidence.BenefitCount);

        Assert.Equal(0, evidence.SuccessfulUseCount);

        SagaRankingComponents components = SagaRankingPolicy.Evaluate(0.8f,
            new(source.VersionId, source.Origin, source.RecordedAtUtc!.Value, source.ValidFromUtc,
                source.ValidToUtc, false, false, false, evidence), h.AsOf, enabled: true);

        Assert.Equal(0f, components.UsefulnessModifier);
    }

    [SkippableFact]
    public async Task Deleting_the_report_immediately_removes_its_usefulness_effect()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        MemoryEvaluationStoredEvidence report = await h.ReportAsync([source]);

        await h.CommitUseAsync(source);

        Assert.Single(await h.ReadAsync());

        Require(await h.Store.DeleteAsync(new(report.Registration.EvaluationId, report.ReportDigest!.Value, true), CancellationToken.None));

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task A_correction_does_not_inherit_its_predecessors_report_or_successful_use()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        await h.CommitUseAsync(source);

        Assert.Single(await h.ReadAsync());

        SagaCurationOutcome correction = await h.Saga.Store.CorrectAsync(source.SubjectId, source.ContentDigest.Bytes,
            "the corrected observation", Vector(), h.AsOf.AddSeconds(-1), CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, correction.Kind);

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task A_later_sensitivity_label_removes_credit_before_content_is_read()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        await h.CommitUseAsync(source);

        await h.Saga.LabelSensitiveAsync(Guid.Parse(source.SubjectId));

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task A_multi_source_intervention_cannot_assign_its_outcome_to_each_memory()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate first = await h.InsertAsync();

        MemoryEvaluationSourceCoordinate second = await h.InsertAsync();

        await h.ReportAsync([first, second]);

        await h.CommitUseAsync(first);

        await h.CommitUseAsync(second);

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_corrupt_report_is_skipped_without_hiding_a_valid_sibling_report(bool invalidStorageType)
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        MemoryEvaluationStoredEvidence corrupt = await h.ReportAsync([source]);

        await h.ExecuteAsync("DROP TRIGGER memory_evaluation_reports_guard_update");

        await h.ExecuteAsync("UPDATE memory_evaluation_reports SET ReportDigest = $digest WHERE EvaluationId = $id",
            ("$digest", invalidStorageType ? new string('A', 32) : new byte[32]),
            ("$id", corrupt.Registration.EvaluationId.ToString("N")));

        Assert.Equal(1, Assert.Single(await h.ReadAsync()).Value.BenefitCount);
    }

    [SkippableFact]
    public async Task A_report_after_the_read_cut_does_not_affect_earlier_recall()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        MemoryEvaluationStoredEvidence report = await h.ReportAsync([source]);

        Assert.Empty(await SagaRankingEvidenceReader.ReadAsync(h.Connection, report.Report!.CompletedAtUtc.AddTicks(-1), CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_report_for_an_old_installed_dataset_cannot_transfer_credit()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source], dataset: Guid.NewGuid());

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task A_missing_current_dataset_refuses_unbound_report_credit()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        await h.ExecuteAsync("DROP TABLE covenant_state");

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task A_report_cannot_claim_a_source_cut_before_its_version_existed()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source], sourceCut: source.RecordedAtUtc!.Value.AddTicks(-1));

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task Campaign_credit_honors_the_same_UUID_across_retained_case_spellings()
    {
        await using Harness h = await Harness.CreateAsync();

        Guid session = await h.Saga.SessionBoundToNewCampaignAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync(session);

        await h.ReportAsync([source]);

        await h.CommitUseAsync(source);

        // Historical imports preserve their UUID spelling; authority is the typed identity.
        await h.ExecuteAsync("DROP TRIGGER annal_versions_guard_update");

        await h.ExecuteAsync("UPDATE annal_versions SET CampaignId = lower(CampaignId) WHERE VersionId = $version", ("$version", source.VersionId));

        await h.ExecuteAsync("DROP TRIGGER session_campaign_bindings_guard_update");

        await h.ExecuteAsync("DROP TRIGGER session_campaign_bindings_CampaignId_guard_identity_update");

        await h.ExecuteAsync("UPDATE session_campaign_bindings SET CampaignId = lower(CampaignId) WHERE SessionId = $session",
            ("$session", session.ToString("D").ToUpperInvariant()));

        SagaRankingEvidence evidence = Assert.Single(await h.ReadAsync()).Value;

        Assert.Equal(1, evidence.BenefitCount);

        Assert.Equal(1, evidence.SuccessfulUseCount);
    }

    [SkippableFact]
    public async Task An_unrelated_current_owner_cannot_inherit_an_earlier_owners_report()
    {
        await using Harness h = await Harness.CreateAsync();

        Guid owner = await h.Saga.SessionBoundToNewCampaignAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync(owner);

        await h.ReportAsync([source]);

        Guid unrelated = await h.Saga.SessionBoundToNewCampaignAsync();

        await h.ExecuteAsync("UPDATE saga_memories SET SessionId = $owner WHERE Id = $memory",
            ("$owner", unrelated.ToString("D").ToUpperInvariant()), ("$memory", source.SubjectId));

        Assert.Empty(await h.ReadAsync());
    }

    [SkippableFact]
    public async Task Retained_use_residue_without_its_entry_is_not_a_successful_outcome()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        Guid assistant = await h.CommitUseAsync(source);

        await h.ExecuteAsync("PRAGMA foreign_keys = OFF");

        await h.ExecuteAsync("DELETE FROM Entries WHERE Id = $assistant", ("$assistant", assistant.ToString("D").ToUpperInvariant()));

        Assert.Equal(0, Assert.Single(await h.ReadAsync()).Value.SuccessfulUseCount);
    }

    [SkippableFact]
    public async Task A_successful_use_timestamp_before_the_source_version_is_not_corroboration()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        Guid assistant = await h.CommitUseAsync(source);

        await h.ExecuteAsync("DROP TRIGGER assistant_entry_finalizations_guard_update");

        await h.ExecuteAsync("UPDATE assistant_entry_finalizations SET FinalizedAtUtc = $before WHERE AssistantEntryId = $assistant",
            ("$before", UtcInstantText.Format(source.RecordedAtUtc!.Value.AddTicks(-1))),
            ("$assistant", assistant.ToString("D").ToUpperInvariant()));

        Assert.Equal(0, Assert.Single(await h.ReadAsync()).Value.SuccessfulUseCount);
    }

    [SkippableFact]
    public async Task Credible_usefulness_reorders_before_top_k_without_changing_raw_threshold_or_gate_off_recall()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate useful = await h.InsertAsync(vector: Vector(0.8f, 0.6f));

        MemoryEvaluationSourceCoordinate fresh = await h.InsertAsync(vector: Vector(0.82f, MathF.Sqrt(1f - 0.82f * 0.82f)),
            createdAt: h.AsOf.AddDays(-1));

        await h.CommitUseAsync(useful);

        List<MemoryEvaluationStoredEvidence> reports = [];

        for (int index = 0; index < 4; index++)
        {
            reports.Add(await h.ReportAsync([useful]));
        }

        Assert.Equal(fresh.SubjectId, Assert.Single(await h.SearchAsync(0.5f)).Id);

        h.Saga.Options.CurrentValue.Features.LongRest = true;

        DivinationResult winner = Assert.Single(await h.SearchAsync(0.5f));

        Assert.Equal(useful.SubjectId, winner.Id);

        Assert.Equal(0.8f, winner.Similarity, 5);

        Assert.Equal(0.04f, winner.Ranking!.UsefulnessModifier, 6);

        Assert.Equal(fresh.SubjectId, Assert.Single(await h.SearchAsync(0.81f)).Id);

        foreach (MemoryEvaluationStoredEvidence report in reports)
        {
            Require(await h.Store.DeleteAsync(new(report.Registration.EvaluationId, report.ReportDigest!.Value, true), CancellationToken.None));
        }

        Assert.Equal(fresh.SubjectId, Assert.Single(await h.SearchAsync(0.5f)).Id);
    }

    [SkippableFact]
    public async Task Caller_owned_snapshot_remains_open_after_evidence_is_read()
    {
        await using Harness h = await Harness.CreateAsync();

        MemoryEvaluationSourceCoordinate source = await h.InsertAsync();

        await h.ReportAsync([source]);

        await h.ExecuteAsync("BEGIN DEFERRED");

        try
        {
            Assert.Single(await h.ReadAsync());

            await h.ExecuteAsync("COMMIT");
        }
        catch
        {
            await h.ExecuteAsync("ROLLBACK");

            throw;
        }
    }

    private static float[] Vector(float x = 1f, float y = 0f) => [x, y, .. new float[62]];

    private static T Require<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private sealed class Harness(SagaStoreHarness saga) : IAsyncDisposable
    {
        internal SagaStoreHarness Saga { get; } = saga;

        internal SqliteConnection Connection => (SqliteConnection)Saga.Connection;

        internal DateTimeOffset AsOf { get; } = DateTimeOffset.UtcNow.AddMinutes(10);

        internal MemoryEvaluationEvidenceStore Store => new(new FixedCovenantConnectionSource(Connection));

        internal static async Task<Harness> CreateAsync() => new(await SagaStoreHarness.CreateAsync(annalsEnabled: true));

        internal Task<IReadOnlyDictionary<string, SagaRankingEvidence>> ReadAsync() =>
            SagaRankingEvidenceReader.ReadAsync(Connection, AsOf, CancellationToken.None);

        internal async Task<DivinationResult[]> SearchAsync(float threshold)
        {
            DivinationService service = new(Saga.Context, Saga.VectorAccelerator, NullLogger<DivinationService>.Instance,
                Saga.Options, new Instant(AsOf));

            return Require(await service.SearchAsync("saga_memory_embeddings_vec", "MemoryId", "Embedding",
                new Embedding<float>(Vector()), 1, threshold, CancellationToken.None));
        }

        internal async Task<MemoryEvaluationSourceCoordinate> InsertAsync(Guid? owningSession = null, float[]? vector = null, DateTimeOffset? createdAt = null)
        {
            string id = Guid.NewGuid().ToString();

            Assert.Equal(SagaMemoryWriteOutcome.Written, await Saga.Store.InsertAsync(id, "the observation " + id,
                createdAt ?? AsOf.AddDays(-30), owningSession, null, null, vector ?? Vector(), CancellationToken.None));

            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = """
                SELECT c.ClaimId, v.VersionId, v.ContentHash, v.OriginCode, v.ValidFromUtc, v.ValidToUtc,
                    v.RecordedAtUtc, v.SourceSessionId, v.Revision, v.ScopeKindCode, v.CampaignId, m.SessionId
                FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId
                JOIN annal_versions v ON v.VersionId = h.CurrentVersionId
                JOIN saga_memories m ON m.Id = c.SubjectId
                WHERE c.SubjectStoreCode = 1 AND c.SubjectId = $id
                """;

            command.Parameters.AddWithValue("$id", id);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            return new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Saga,
                id, reader.GetString(0), reader.GetString(1), 1, new((byte[])reader[2]), null,
                (AnnalOrigin)reader.GetInt32(3), (MemoryEvaluationScopeKind)reader.GetInt32(9), reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                UtcInstantText.Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : UtcInstantText.Parse(reader.GetString(5)),
                UtcInstantText.Parse(reader.GetString(6)), null, MemoryEvaluationSourceLifecycle.Active,
                ContentSensitivity.None, reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)).ToString("D").ToUpperInvariant(),
                reader.IsDBNull(7) ? null : reader.GetString(7), Revision: reader.GetInt32(8));
        }

        internal async Task<MemoryEvaluationStoredEvidence> ReportAsync(
            ImmutableArray<MemoryEvaluationSourceCoordinate> sources,
            MemoryEvaluationOutcome outcome = MemoryEvaluationOutcome.Benefit,
            Guid? dataset = null,
            DateTimeOffset? sourceCut = null)
        {
            await using SqliteCommand state = Connection.CreateCommand();

            state.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1";

            Guid generation = dataset ?? new Guid((byte[])(await state.ExecuteScalarAsync())!);

            MemoryEvaluationRegistration registration = new(Guid.NewGuid(), Guid.NewGuid(), AsOf.AddMinutes(-1),
                new(generation, sources[0].Scope, sources[0].CampaignId), Digest, Digest, Digest, Digest, Digest,
                Digest, Digest, Digest, MemoryEvaluationDigests.SourceManifest(sources, []), 3, 1, SourceAsOfUtc: sourceCut ?? AsOf.AddMinutes(-2));

            Require(await Store.RegisterAsync(registration, sources, CancellationToken.None));

            int improved = outcome == MemoryEvaluationOutcome.Benefit ? 3 : 0;

            int regressed = outcome == MemoryEvaluationOutcome.Harm ? 3 : 0;

            int equivalent = outcome == MemoryEvaluationOutcome.NoMeasurableEffect ? 3 : 0;

            MemoryEvaluationTerminalReport report = new(registration, AsOf.AddSeconds(-10), MemoryEvaluationState.Completed,
                new(outcome, MemoryEvaluationReason.None), new(3, 3, 0, improved, regressed, equivalent, 0,
                    [new(0, MemoryEvaluationCheckKind.Regex, Digest, 0, improved, regressed, equivalent)], Digest),
                sources, 100, 30, 0m, 10);

            return Require(await Store.CompleteAsync(registration.EvaluationId, report, CancellationToken.None));
        }

        internal async Task<Guid> CommitUseAsync(MemoryEvaluationSourceCoordinate source)
        {
            Guid session = source.CanonicalOwnerSessionId is { } owner ? Guid.Parse(owner) : Guid.NewGuid();

            Guid assistant = Guid.NewGuid();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (source.CanonicalOwnerSessionId is null)
            {
                Saga.Context.Sessions.Add(new Session { Id = session, CreatedAt = now, UpdatedAt = now, Status = "active", Title = "use" });
            }

            Saga.Context.Entries.Add(new Entry { Id = Guid.NewGuid(), SessionId = session, Role = MessageRole.User,
                Content = "prompt", ModelUsed = "model", CreatedAt = now, Sequence = 1 });

            Saga.Context.Entries.Add(new Entry { Id = assistant, SessionId = session, Role = MessageRole.Assistant,
                Content = string.Empty, ModelUsed = "model", CreatedAt = now, Sequence = 2 });

            await Saga.Context.SaveChangesAsync();

            GrimoireRepository committer = new(Saga.Context, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
                new Snapshot(new ArcanumSettings()), null, null, FixtureOrdinaryConnectionFactory.For(Saga.Context), FixtureLabeledArtifactGuard.For(Saga.Context));

            Require(await committer.CommitTurnAsync(new(assistant, session, AssistantFinalizationOutcome.Committed,
                "answer", Digest, ContentSensitivity.None, GenerationProvenance.CreateExact([]),
                sagaUses: [new(source.SubjectId, source.ClaimId!, source.VersionId, source.HashFormat, source.ContentDigest)]), CancellationToken.None));

            return assistant;
        }

        internal async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using SqliteCommand command = Connection.CreateCommand();

            command.CommandText = sql;

            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => Saga.DisposeAsync();
    }

    private sealed class Snapshot(ArcanumSettings value) : IOptionsSnapshot<ArcanumSettings>
    {
        public ArcanumSettings Value => value;

        public ArcanumSettings Get(string? name) => value;
    }

    private sealed class Instant(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
