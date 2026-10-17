using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The oracle for Seams-and-Steps. A step re-proves the generation row, the fence, the set-based metadata question, the exact inputs it
/// acts on and one rotating window of leaves; a seam re-proves every leaf. This matrix mutates the corpus a sweep rests on, by every route
/// that can change it, between two units, once on a leaf the next unit uses and once on a leaf it does not, for a clean sweep and a
/// protected one. The contract has two halves. Wherever a step acts on what changed, because a trigger, the set-based question, the fence or
/// the generation row sees it, or because the leaf is one the unit's request derives from, the next step's verdict equals the full recompute's
/// and no send follows. For the one class nothing but a seam sees, a write-guarded row changed with its guard bypassed on a leaf the unit does
/// not act on, the step admits and the exposure is bounded instead: the unit sends nothing built from that leaf, the rotating window reaches it
/// within ceil(leaves / window) revisions, and publish and completion refuse it. <c>LongRestMutationMatrixTests.Hierarchy.cs</c> runs the same
/// oracle in the middle of the hierarchy build.
/// </summary>
/// <remarks>
/// <para>Each row names the mechanism that refuses it, and the test proves the name rather than trusting it: a <see cref="Mechanism.Trigger"/>
/// row is refused by the next step on a leaf the rotating window does not cover, so only the trigger can have done it; a
/// <see cref="Mechanism.Seam"/> row is the single place a step is allowed to admit what a seam refuses, and then only for a leaf the unit
/// does not act on, which the window reaches within ceil(leaves / window) revisions of the sweep and the completion seam refuses at once.
/// A row nothing refuses is a failure of the design, never an exemption: it is closed by a trigger or by a step check.</para>
/// <para>The corpus is larger than the window on purpose. With fewer leaves than the window a step re-proves them all, which would
/// make every row look covered whatever its mechanism.</para>
/// </remarks>
[Collection("Grimoire")]
public sealed partial class LongRestMutationMatrixTests
{
    internal const int Leaves = 40;

    private const int Windows = (Leaves + LongRestDiscoveryStore.ScrubWindowLeaves - 1) / LongRestDiscoveryStore.ScrubWindowLeaves;

    /// <summary>What refuses a mutation before a send.</summary>
    internal enum Mechanism
    {
        /// <summary>A trigger on the written table advances the source epoch and flips the generation, which deletes the rows the next step reads.</summary>
        Trigger,

        /// <summary>The write is to the generation's own rows, which every step reads first.</summary>
        GenerationRow,

        /// <summary>The process fence: dataset, configuration, authority, capability or claim.</summary>
        Fence,

        /// <summary>The set-based question every step asks of every leaf: owner, scope, label count and partition, embedding width, epoch.</summary>
        AntiJoin,

        /// <summary>
        /// The receipts and suppressions that set a projection flag on a leaf are the sweep's own writes, so no trigger can tell a foreign one
        /// from them. Every step asks, in one set-based statement, that each such row belong to one of the generation's own work items.
        /// </summary>
        Attribution,

        /// <summary>No trigger sees it. A step re-proves the leaves it acts on, the window reaches the rest, and the next seam refuses it.</summary>
        Seam,

        /// <summary>The full recompute does not look at it either: it can only change which neighbours are offered, and a neighbour is re-read in full before it counts.</summary>
        NotVerified,

        /// <summary>The generation's own forest. A leaf pass never reads it; the next discovery scan reads every summary and its whole subtree in full.</summary>
        Scan,

        /// <summary>The unit's own records: its inputs as queued. The attempt gate and the next read compare them leaf by leaf with the live corpus.</summary>
        Unit,
    }

    internal sealed record Row(string Name, Mechanism Mechanism, Func<Scenario, string, Task> Apply, bool CleanOnly = false, bool ProtectedOnly = false, bool EveryLeaf = false, bool AttemptOnly = false,
        Func<Scenario, Task>? Prepare = null);

    internal static readonly Row[] Rows =
    [
        new("content", Mechanism.Trigger, static (s, m) => s.SqlAsync("UPDATE saga_memories SET Content = 'altered content' WHERE Id = $m;", ("$m", m))),
        new("correction", Mechanism.Trigger, static async (s, m) =>
        {
            string content = (string)(await s.F.H.ScalarAsync($"SELECT Content FROM saga_memories WHERE Id = '{m}';"))!;

            SagaCurationOutcome outcome = await s.F.H.Saga.Store.CorrectAsync(m, AnnalContentDigest.ForSagaMemory(content), "a corrected observation",
                s.F.H.Saga.Embedding(), DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal(SagaCurationOutcomeKind.Applied, outcome.Kind);
        }),
        new("retire", Mechanism.Trigger, static async (s, m) =>
        {
            string content = (string)(await s.F.H.ScalarAsync($"SELECT Content FROM saga_memories WHERE Id = '{m}';"))!;

            Assert.Equal(SagaCurationOutcomeKind.Applied, (await s.F.H.Saga.Store.RetireAsync(m, AnnalContentDigest.ForSagaMemory(content),
                DateTimeOffset.UtcNow, CancellationToken.None)).Kind);
        }),
        new("pin", Mechanism.Trigger, static async (s, m) =>
            Assert.Equal(SagaCurationOutcomeKind.Applied, (await s.F.H.Saga.Store.SetPinAsync(m, true, DateTimeOffset.UtcNow, CancellationToken.None)).Kind)),
        new("owner-session", Mechanism.Trigger, static (s, m) => s.SqlAsync("UPDATE saga_memories SET SessionId = NULL WHERE Id = $m;", ("$m", m))),
        new("embedding-delete", Mechanism.Trigger, static (s, m) => s.SqlAsync("DELETE FROM saga_memory_embeddings WHERE MemoryId = $m;", ("$m", m))),
        new("embedding-update", Mechanism.Trigger, static (s, m) => s.F.H.SetEmbeddingAsync(m, Enumerable.Range(0, Leaves).Select(static i => i == 3 ? 1f : 0f).ToArray())),
        new("source-delete", Mechanism.Trigger, static (s, m) => s.SqlAsync("DELETE FROM long_rest_sources WHERE MemoryId = $m;", ("$m", m))),
        new("source-epoch", Mechanism.Trigger, static (s, m) => s.SqlAsync("UPDATE long_rest_sources SET MutationEpoch = MutationEpoch + 1 WHERE MemoryId = $m;", ("$m", m))),
        new("attachment-provenance", Mechanism.Trigger, static (s, m) => s.SqlAsync(
            "INSERT INTO saga_memory_attachment_provenance VALUES ($m, 'session', 'AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA', 'key', 1, 'hash', '2026-10-10T00:00:00.0000000Z', 'text');", ("$m", m))),
        new("attachment-update", Mechanism.Trigger, static (s, m) => s.SqlAsync("UPDATE saga_memory_attachment_provenance SET ContentHash = 'changed' WHERE MemoryId = $m;", ("$m", m)),
            Prepare: static s => s.AttachAllAsync()),
        new("attachment-delete", Mechanism.Trigger, static (s, m) => s.SqlAsync("DELETE FROM saga_memory_attachment_provenance WHERE MemoryId = $m;", ("$m", m)),
            Prepare: static s => s.AttachAllAsync()),
        new("head-delete", Mechanism.Trigger, static (s, m) => s.SqlAsync(
            "DELETE FROM annal_heads WHERE ClaimId = (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId = $m);", ("$m", m))),
        new("dependency", Mechanism.Trigger, static async (s, m) =>
        {
            (string Version, long Sequence) mine = await s.VersionOfAsync(m);

            (string Version, long Sequence) other = await s.VersionOfAsync(s.Bystander == m ? s.Inputs[0] : s.Bystander);

            (string Version, long Sequence) dependent = mine.Sequence > other.Sequence ? mine : other;

            (string Version, long Sequence) dependency = mine.Sequence > other.Sequence ? other : mine;

            await s.SqlAsync("""
                INSERT INTO annal_dependencies (DependentVersionId, DependentSequence, DependencyVersionId, DependencySequence, RelationCode, Ordinal, CreatedAtUtc)
                VALUES ($dependent, $dependentSequence, $dependency, $dependencySequence, 3, 1, '2026-10-10T00:00:00.0000000Z');
                """, ("$dependent", dependent.Version), ("$dependentSequence", dependent.Sequence), ("$dependency", dependency.Version), ("$dependencySequence", dependency.Sequence));
        }),
        new("label-insert", Mechanism.Trigger, static async (s, m) => _ = await s.F.H.ProtectAsync(Guid.Parse(m), s.F.Provenance), CleanOnly: true),
        new("label-digest", Mechanism.AntiJoin, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

            await s.SqlAsync("UPDATE artifact_sensitivity SET SensitivityDigest = zeroblob(32) WHERE ArtifactKindCode = 6 AND lower(replace(ArtifactId, '-', '')) = lower(replace($m, '-', ''));", ("$m", m));
        }, ProtectedOnly: true),
        new("label-content-digest", Mechanism.Seam, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

            await s.SqlAsync("UPDATE artifact_sensitivity SET ArtifactContentDigest = zeroblob(32) WHERE ArtifactKindCode = 6 AND lower(replace(ArtifactId, '-', '')) = lower(replace($m, '-', ''));", ("$m", m));
        }, ProtectedOnly: true),
        new("hash-bypass", Mechanism.Seam, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER annal_versions_guard_update;");

            await s.SqlAsync("UPDATE annal_versions SET ContentHash = zeroblob(32) WHERE VersionId = (SELECT h.CurrentVersionId FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId WHERE c.SubjectStoreCode = 1 AND c.SubjectId = $m);", ("$m", m));
        }),
        new("node-delete", Mechanism.Trigger, static (s, m) => s.SqlAsync("DELETE FROM long_rest_discovery_nodes WHERE GenerationId = $g AND Layer = 0 AND MemoryId = $m;", ("$g", s.GenerationText), ("$m", m))),
        new("generation-phase", Mechanism.GenerationRow, static (s, _) => s.SqlAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 7 WHERE GenerationId = $g;", ("$g", s.GenerationText)), EveryLeaf: true),
        new("generation-fence", Mechanism.GenerationRow, static (s, _) => s.SqlAsync("UPDATE long_rest_discovery_generations SET InsertionHighWatermark = InsertionHighWatermark + 1 WHERE GenerationId = $g;", ("$g", s.GenerationText)), EveryLeaf: true),
        new("dataset-generation", Mechanism.Fence, static (s, _) => { s.F.Availability.PublishCommittedDataset(Guid.NewGuid()); return Task.CompletedTask; }, EveryLeaf: true),
        new("owner-binding", Mechanism.AntiJoin, static async (s, _) =>
        {
            using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(s.F.H.Connection, CovenantSqliteAuthorizationKind.OwnerCleanup);

            await s.SqlAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $session;", ("$session", s.F.SourceSession.ToString("D").ToUpperInvariant()));
        }, EveryLeaf: true),
        new("label-delete", Mechanism.Trigger, static async (s, m) =>
        {
            using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance.Authorize(s.F.H.Connection, CovenantSqliteAuthorizationKind.OwnerCleanup);

            await s.SqlAsync("DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = 6 AND lower(replace(ArtifactId, '-', '')) = lower(replace($m, '-', ''));", ("$m", m));
        }, ProtectedOnly: true),
        new("foreign-suppression", Mechanism.Attribution, static async (s, m) =>
        {
            string partner = s.Partner(m);

            Result<LongRestReceipt> applied = await new SagaLongRestService(s.F.H.Saga.Context, TimeProvider.System).ApplyAsync(
                new(LongRestTransformationKind.Supersession, [await s.F.H.TargetAsync(m), await s.F.H.TargetAsync(partner)], (await s.F.H.TargetAsync(partner)).ExpectedVersionId),
                CancellationToken.None);

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.ToString() : "");
        }, CleanOnly: true),
        new("forged-suppression", Mechanism.Attribution, static async (s, m) =>
        {
            // The write guard that checks a suppression against its receipt and its protected evidence is bypassed, as a restore or a rebuild would bypass it.
            await s.SqlAsync("DROP TRIGGER long_rest_suppressions_validate_insert;");

            (string Version, long Sequence) source = await s.VersionOfAsync(m);

            (string Version, long Sequence) survivor = await s.VersionOfAsync(s.Partner(m));

            await s.SqlAsync("""
                INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
                VALUES ('forged-receipt', 1, 2, 1, 0, $hash, $hash, $survivorMemory, $survivor, '2026-10-10T00:00:00.0000000Z');
                INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES ($source, $survivor, 'forged-receipt');
                """, ("$hash", new string('a', 64)), ("$survivorMemory", s.Partner(m)), ("$survivor", survivor.Version), ("$source", source.Version));
        }),
        // An ordinary operation, not a bypass: the memory an earlier consolidation suppressed is deleted, pruned or erased. The erasure plan removes the
        // consolidation's receipt with it, so the survivor stops being a transformation output without anything having written the survivor.
        new("consolidated-source-delete", Mechanism.Attribution, static async (s, m) =>
            Assert.True(await s.F.H.Saga.Store.DeleteAsync(await s.ConsolidatedSourceOfAsync(m), CancellationToken.None)),
            CleanOnly: true, Prepare: static s => s.ConsolidateAsync()),
        new("consolidated-source-prune", Mechanism.Attribution, static async (s, m) =>
        {
            string source = await s.ConsolidatedSourceOfAsync(m);

            // Only the suppressed source is old enough for the Saga retention rule; every other memory is fresh.
            await s.SqlAsync("UPDATE saga_memories SET CreatedAt = $fresh WHERE Id <> $source;", ("$fresh", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)), ("$source", source));

            await s.SqlAsync("UPDATE saga_memories SET CreatedAt = '2000-01-01T00:00:00.0000000+00:00' WHERE Id = $source;", ("$source", source));

            await using Retention retention = new(s.F, SagaPruneSettings());

            DataRetentionRequest request = new(DataRetentionOperation.Prune);

            DataRetentionPlan plan = await retention.Service.PlanAsync(request);

            Assert.Single(plan.CandidateIds);

            Result<DataRetentionApplyResult> applied = await retention.Service.ApplyAsync(new(request, plan.PlanId));

            Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : "");

            Assert.Equal(0L, await s.F.H.ScalarAsync($"SELECT count(*) FROM saga_memories WHERE Id = '{source}';"));
        }, CleanOnly: true, Prepare: static s => s.ConsolidateAsync()),
        new("consolidated-source-erase", Mechanism.Attribution, static async (s, m) => await ErasureKernelAsync(s, await s.ConsolidatedSourceOfAsync(m)),
            ProtectedOnly: true, Prepare: static s => s.ConsolidateAsync()),
        new("leaf-snapshot", Mechanism.Seam, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER long_rest_discovery_leaves_guard_update;");

            await s.SqlAsync("UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.revision', 99) WHERE GenerationId = $g AND MemoryId = $m;", ("$g", s.GenerationText), ("$m", m));
        }, AttemptOnly: true),

        // A frozen flag no row explains. The rows a flag projects from are the sweep's own writes, so the flag a leaf was frozen with is asked of the
        // rows themselves, in the set-based question every gate asks.
        new("frozen-flag-consolidated", Mechanism.AntiJoin, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER long_rest_discovery_leaves_guard_update;");

            await s.SqlAsync("UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.isConsolidated', json('true')) WHERE GenerationId = $g AND MemoryId = $m;", ("$g", s.GenerationText), ("$m", m));
        }),
        new("frozen-flag-output", Mechanism.AntiJoin, static async (s, m) =>
        {
            await s.SqlAsync("DROP TRIGGER long_rest_discovery_leaves_guard_update;");

            await s.SqlAsync("UPDATE long_rest_discovery_leaves SET SnapshotJson = json_set(SnapshotJson, '$.isTransformationOutput', json('true')) WHERE GenerationId = $g AND MemoryId = $m;", ("$g", s.GenerationText), ("$m", m));
        }),
        new("summary-text", Mechanism.Scan, static (s, _) => s.SqlAsync("""
            UPDATE long_rest_discovery_nodes SET Summary = 'tampered summary'
            WHERE GenerationId = $g AND NodeId = (SELECT min(NodeId) FROM long_rest_discovery_nodes WHERE GenerationId = $g AND Layer = 1);
            """, ("$g", s.GenerationText)), EveryLeaf: true),
        new("summary-vector", Mechanism.Scan, static (s, _) => s.SqlAsync("""
            UPDATE long_rest_discovery_embeddings SET Vector = zeroblob(length(Vector))
            WHERE NodeId = (SELECT min(NodeId) FROM long_rest_discovery_nodes WHERE GenerationId = $g AND Layer = 1);
            """, ("$g", s.GenerationText)), EveryLeaf: true),
        new("edge-delete", Mechanism.Scan, static (s, _) => s.SqlAsync("""
            DELETE FROM long_rest_discovery_edges WHERE GenerationId = $g AND rowid = (SELECT max(rowid) FROM long_rest_discovery_edges WHERE GenerationId = $g);
            """, ("$g", s.GenerationText)), EveryLeaf: true),
        new("work-inputs", Mechanism.Unit, static (s, _) => s.SqlAsync("""
            UPDATE long_rest_work_inputs SET SourceEvidenceJson = json_set(SourceEvidenceJson, '$.snapshot.revision', 99)
            WHERE WorkItemId = $unit AND Ordinal = 1;
            """, ("$unit", s.Unit)), EveryLeaf: true),
        new("fts-rows", Mechanism.NotVerified, static (s, m) => s.SqlAsync("DELETE FROM long_rest_clean_fts WHERE GenerationId = $g; DELETE FROM long_rest_protected_fts WHERE GenerationId = $g;", ("$g", s.GenerationText)), EveryLeaf: true),
    ];

    public static IEnumerable<object[]> RowCases()
    {
        foreach (Row row in Rows)
        {
            foreach (bool isProtected in new[] { false, true })
            {
                if ((isProtected && row.CleanOnly) || (!isProtected && row.ProtectedOnly))
                {
                    continue;
                }

                foreach (bool input in row.EveryLeaf ? new[] { true } : new[] { true, false })
                {
                    yield return [row.Name, isProtected, input];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(RowCases))]
    public async Task The_next_step_refuses_what_the_full_recompute_refuses(string name, bool isProtected, bool input)
    {
        Row row = Rows.Single(candidate => candidate.Name == name);

        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves, prepare: row.Prepare);

        string target = input ? s.Inputs[0] : s.Bystander;

        await row.Apply(s, target);

        Verdicts v = await s.ProbeAsync();

        // A step is never stricter than the full recompute: it refuses nothing the corpus has not earned.
        Assert.True(!v.EnsureStep || v.EnsureSeam, $"{name}: a step refused what the full recompute admits.");

        Assert.True(!v.AttemptStep || v.AttemptSeam, $"{name}: an attempt step refused what the full recompute admits.");

        switch (row.Mechanism)
        {
            case Mechanism.NotVerified:
                // The full recompute does not look at it either, so a step admitting it is equality, not a gap.
                Assert.False(v.EnsureSeam);

                Assert.False(v.EnsureStep);

                Assert.False(v.AttemptSeam);

                Assert.False(v.AttemptStep);

                break;

            case Mechanism.Scan:
                Assert.False(v.EnsureStep);

                Assert.Equal(v.EnsureSeam, v.EnsureStep);

                Assert.True(v.ScanRefused, $"{name}: the discovery scan admitted a forest the next unit would be built from.");

                break;

            case Mechanism.Unit:
                Assert.True(v.AttemptSeam, $"{name}: the full recompute admitted a tampered unit.");

                Assert.True(v.AttemptStep, $"{name}: the attempt gate admitted a tampered unit.");

                Assert.True(v.NextWorkRefused, $"{name}: the next read admitted a tampered unit.");

                break;

            case Mechanism.Seam when row.AttemptOnly:
                // The leaf pass compares live rows with the captured digest and never reads the stored snapshot row; the metadata pass does.
                Assert.False(v.EnsureSeam);

                Assert.False(v.EnsureStep);

                Assert.True(v.AttemptSeam, $"{name}: the metadata recompute admitted a tampered snapshot row.");

                if (input)
                {
                    Assert.True(v.AttemptStep, $"{name}: the attempt gate admitted a tampered snapshot of a leaf the unit acts on.");
                }
                else
                {
                    Assert.False(v.AttemptStep);

                    await AssertAttemptWindowReachesAsync(s, name);
                }

                break;

            case Mechanism.Seam when !input:
                await AssertExposureIsBoundedAsync(s, name, v);

                break;

            default:
                // A trigger, the fence, the generation row or the set-based question refuses it, on any leaf; and a leaf the unit acts on is refused whatever the route.
                Assert.True(v.EnsureSeam, $"{name}: the full recompute admitted the mutation, so this row proves nothing.");

                Assert.True(v.EnsureStep, $"{name} ({row.Mechanism}): the next step admitted what the full recompute refuses.");

                if (row.Mechanism != Mechanism.Fence)
                {
                    Assert.True(v.NextWorkRefused, $"{name}: the next unit's read admitted a corpus the full recompute refuses.");

                    Assert.True(v.AttemptSeam, $"{name}: the attempt gate's full recompute admitted the mutation.");

                    Assert.True(v.AttemptStep, $"{name}: the attempt gate admitted what its full recompute refuses.");
                }

                break;
        }
    }

    /// <summary>
    /// The single place a step admits what a seam refuses: a divergence no trigger saw, on a leaf the unit does not act on. It is bounded:
    /// the full recompute refuses it now, and the rotating window reaches the leaf within ceil(leaves / window) revisions of the sweep, at
    /// the fence reader and at the attempt gate alike.
    /// </summary>
    private static async Task AssertExposureIsBoundedAsync(Scenario s, string name, Verdicts v, bool attemptSees = true)
    {
        Assert.True(v.EnsureSeam, $"{name}: the full recompute admitted the mutation, so this row proves nothing.");

        Assert.False(v.EnsureStep, $"{name}: expected a step to admit a divergence only the seam sees, on a leaf outside the window.");

        bool reached = false;

        for (int revision = 0; revision < Windows && !reached; revision++)
        {
            await s.AdvanceSweepRevisionAsync();

            reached = (await s.EnsureAsync(LongRestVerificationDepth.Step, null)).IsFailure;
        }

        Assert.True(reached, $"{name}: the rotating window never reached the divergence within {Windows} revisions.");

        if (attemptSees)
        {
            await AssertAttemptWindowReachesAsync(s, name);
        }
    }

    private static async Task AssertAttemptWindowReachesAsync(Scenario s, string name)
    {
        bool reached = false;

        for (int revision = 0; revision < Windows && !reached; revision++)
        {
            await s.AdvanceSweepRevisionAsync();

            reached = await s.AttemptStepRefusedAsync();
        }

        Assert.True(reached, $"{name}: the attempt gate's window never reached the divergence within {Windows} revisions.");
    }

    [Theory]
    [MemberData(nameof(RowCases))]
    public async Task A_unit_that_meets_a_refused_mutation_sends_nothing(string name, bool isProtected, bool input)
    {
        Row row = Rows.Single(candidate => candidate.Name == name);

        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves, prepare: row.Prepare);

        string target = input ? s.Inputs[0] : s.Bystander;

        await row.Apply(s, target);

        int sends = s.F.Handler.SendCount;

        _ = await Record.ExceptionAsync(() => s.F.ProcessAsync(s.F.Operation, "fixture-worker", s.Capability, CancellationToken.None));

        bool refusedBeforeSend = row.Mechanism switch
        {
            Mechanism.NotVerified or Mechanism.Scan => false,
            Mechanism.Seam => input,
            _ => true,
        };

        if (refusedBeforeSend)
        {
            Assert.Equal(sends, s.F.Handler.SendCount);
        }
        else
        {
            // Admitted, so the request it sent was built from the unit's own verified leaves and never from the one that diverged.
            Assert.Equal(sends + 1, s.F.Handler.SendCount);

            if (!row.EveryLeaf)
            {
                Assert.DoesNotContain(target, s.F.Payloads.Last().Inputs.Select(leaf => leaf.Snapshot.MemoryId));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_completion_seam_passes_a_sweep_nothing_has_touched(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves, queueUnit: false);

        await s.AdvanceDiscoveryToTheEndAsync();

        Assert.True((await s.Sweeps.ReadNextWorkAsync(await s.CurrentSweepAsync(), s.Capability, CancellationToken.None)).IsSuccess);

        Assert.Equal((long)(int)LongRestSweepPhase.Completed, await s.F.H.ScalarAsync("SELECT PhaseCode FROM long_rest_discovery_generations;"));
    }

    /// <summary>
    /// The negative control. With the trigger that watches a memory's text gone, nothing flips the generation when the text changes, and
    /// the steps that do not scan admit the divergent leaf: this is the exposure window, stated as a test. The sweep still cannot close on
    /// it, because completion re-proves every leaf in full in the transaction that completes the sweep.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_its_trigger_a_step_proceeds_and_the_completion_seam_refuses(bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves, queueUnit: false);

        await s.SqlAsync("DROP TRIGGER long_rest_saga_update;");

        await s.SqlAsync("UPDATE saga_memories SET Content = 'altered content' WHERE Id = $m;", ("$m", s.Bystander));

        Assert.True((await s.EnsureAsync(LongRestVerificationDepth.Step, null)).IsSuccess, "A step outside the window admits a divergence no trigger saw.");

        Assert.True((await s.EnsureAsync(LongRestVerificationDepth.Seam, null)).IsFailure, "The full recompute refuses it.");

        await s.AdvanceDiscoveryToTheEndAsync();

        Assert.True((await s.Sweeps.ReadNextWorkAsync(await s.CurrentSweepAsync(), s.Capability, CancellationToken.None)).IsFailure,
            "The completion seam must refuse a corpus that has diverged.");

        Assert.NotEqual((long)(int)LongRestSweepPhase.Completed, await s.F.H.ScalarAsync("SELECT PhaseCode FROM long_rest_discovery_generations;"));
    }

    public static IEnumerable<object[]> BypassCases()
    {
        foreach (bool isProtected in new[] { false, true })
        {
            foreach (string bypass in new[] { "embedding-reset", "restore-sanitation", "factory-reset", "erasure-kernel", "schema-rebuild" })
            {
                if ((isProtected && bypass == "embedding-reset") || (!isProtected && bypass == "erasure-kernel"))
                {
                    continue;
                }

                yield return [bypass, isProtected];
            }
        }
    }

    /// <summary>
    /// The routes that change the corpus without the triggers' help. The first four close the generation themselves, by deleting its rows
    /// in the same statement sequence that removes the sources, so the row every step reads first is gone. A table rebuild leaves the generation
    /// standing and changes what it froze: nothing sees that until a seam, which is the resume entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(BypassCases))]
    public async Task A_bypass_path_leaves_no_generation_a_step_would_admit(string bypass, bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves);

        await ApplyBypassAsync(s, bypass);

        Verdicts v = await s.ProbeAsync();

        if (bypass == "schema-rebuild")
        {
            Assert.Equal(1L, await s.F.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));

            // The text of a leaf is read only in full; the attempt gate proves metadata, so it is the fence reader's window that reaches this.
            await AssertExposureIsBoundedAsync(s, bypass, v, attemptSees: false);

            return;
        }

        Assert.Equal(0L, await s.F.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));

        Assert.True(v.EnsureSeam && v.EnsureStep && v.NextWorkRefused && v.AttemptSeam && v.AttemptStep, $"{bypass}: a gate admitted a corpus whose generation was erased.");

        int sends = s.F.Handler.SendCount;

        _ = await Record.ExceptionAsync(() => s.F.ProcessAsync(s.F.Operation, "fixture-worker", s.Capability, CancellationToken.None));

        Assert.Equal(sends, s.F.Handler.SendCount);
    }

    [Theory]
    [MemberData(nameof(BypassCases))]
    public async Task A_parked_sweep_readmitted_after_a_bypass_sends_nothing(string bypass, bool isProtected)
    {
        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves);

        string code = await s.ParkAsync();

        Assert.Equal(isProtected ? LongRestParkCodes.AuthorizedRequestRequired : LongRestParkCodes.Interrupted, code);

        await ApplyBypassAsync(s, bypass);

        int sends = s.F.Handler.SendCount;

        long billed = (long)(await s.F.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"))!;

        _ = await Record.ExceptionAsync(s.ReadmitAsync);

        Assert.Equal(sends, s.F.Handler.SendCount);

        Assert.Equal(billed, await s.F.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        LongRunningOperation? operation = await s.F.Operations.GetAsync(s.F.Operation);

        Assert.True(operation is null || operation.State != LongRunningOperationState.Completed, "A sweep whose footing was erased or changed must not complete on it.");

        if (bypass == "schema-rebuild")
        {
            // The resume entry is the seam that caught it. A clean sweep whose generation can no longer hold is retired for a fresh request to
            // replace; a protected one is refused by the first unit under its capability and stays parked until the next request that carries
            // live authority, which finds that its frozen sources can no longer be proved and retires it as stale input (see
            // LongRestMaintenanceTests.FreshAuthorityRetire).
            Assert.Equal(isProtected ? LongRunningOperationState.ReconciliationRequired : LongRunningOperationState.Abandoned, operation!.State);

            Assert.Equal(isProtected ? LongRestParkCodes.AuthorizedRequestRequired : ErrorCodes.LongRest.StaleInput, operation.TerminalErrorCode);
        }
    }

    /// <summary>
    /// The same ordinary erasure while the sweep is parked: nothing is running to see it, and the footing the sweep resumes on has lost the receipt
    /// that made a survivor a transformation output. Readmission is a seam, so it re-proves every leaf before the first send.
    /// </summary>
    [Theory]
    [InlineData(false, "consolidated-source-delete")]
    [InlineData(true, "consolidated-source-erase")]
    public async Task A_parked_sweep_readmitted_after_a_consolidated_source_is_removed_sends_nothing(bool isProtected, string name)
    {
        Row row = Rows.Single(candidate => candidate.Name == name);

        await using Scenario s = await Scenario.CreateAsync(isProtected, Leaves, prepare: row.Prepare);

        string code = await s.ParkAsync();

        Assert.Equal(isProtected ? LongRestParkCodes.AuthorizedRequestRequired : LongRestParkCodes.Interrupted, code);

        await row.Apply(s, s.Bystander);

        int sends = s.F.Handler.SendCount;

        long billed = (long)(await s.F.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"))!;

        _ = await Record.ExceptionAsync(s.ReadmitAsync);

        Assert.Equal(sends, s.F.Handler.SendCount);

        Assert.Equal(billed, await s.F.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        LongRunningOperation? operation = await s.F.Operations.GetAsync(s.F.Operation);

        Assert.True(operation is null || operation.State != LongRunningOperationState.Completed, "A sweep whose footing changed must not complete on it.");
    }

    private static async Task ApplyBypassAsync(Scenario s, string bypass)
    {
        switch (bypass)
        {
            case "embedding-reset":
                await s.SqlAsync("PRAGMA foreign_keys = OFF;");

                ServiceCollection services = [];

                services.AddSingleton<IGrimoireOrdinaryConnectionFactory>(FixtureOrdinaryConnectionFactory.For(s.F.H.Saga.Context));

                services.AddSingleton(FixtureLabeledArtifactGuard.For(s.F.H.Saga.Context));

                using (ServiceProvider provider = services.BuildServiceProvider())
                {
                    EmbeddingsResetResult applied = await new EmbeddingsResetService(s.F.H.Saga.Context, provider).ResetAsync(EmbeddingsResetScope.Saga, CancellationToken.None);

                    Assert.True(applied.DeletedRowCounts["saga_memories"] > 0);
                }

                break;

            case "restore-sanitation":
                Assert.True((await BackupRestoreLongRestSanitation.SanitizeStagedAsync(s.F.H.Connection, CancellationToken.None)).IsSuccess);

                break;

            case "factory-reset":
                await using (Retention retention = new(s.F))
                {
                    // A factory reset refuses to run beside an active operation, so the sweep's operation is first ended the way an operator ends it.
                    // Its generation, and everything the sweep froze, is still in the Grimoire for the reset to remove.
                    await s.SqlAsync("UPDATE LongRunningOperations SET State = $abandoned, LeaseOwner = NULL, LeaseExpiresAt = NULL, CompletedAt = '2026-10-10T00:00:00.0000000Z' WHERE Id = $operation;",
                        ("$abandoned", (int)LongRunningOperationState.Abandoned), ("$operation", s.F.Operation.ToString("N")));

                    // Applied the way a restart applies an interrupted factory reset: the registered version-zero launch rebuilds its own
                    // preview and executes it, so nothing about the reset is stubbed.
                    DateTimeOffset crashed = DateTimeOffset.UtcNow.AddMinutes(-10);

                    LongRunningOperation launch = await s.F.Operations.CreateAsync(new(LongRunningOperationKinds.DataRetentionFactoryReset,
                        LongRunningOperationRecoveryPolicy.RestartIdempotently, "Interrupted factory reset.", crashed));

                    Assert.True((await s.F.Operations.TryAcquireLeaseAsync(launch.Id, "crashed-factory-reset", crashed, crashed.AddMinutes(1))).Acquired);

                    LongRunningOperationReconciler reconciler = new(s.F.Operations, [new DataRetentionFactoryResetRecoveryHandler(retention.Service)],
                        TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

                    LongRunningOperationReconciliationSummary recovered = await reconciler.ReconcileNowAsync("matrix-factory-reset", 10, 1, CancellationToken.None);

                    LongRunningOperation reset = (await s.F.Operations.GetAsync(launch.Id))!;

                    Assert.True(recovered.Completed == 1, $"completed {recovered.Completed}, attention {recovered.RequiresAttention}, state {reset.State}, code {reset.TerminalErrorCode}");
                }

                break;

            case "erasure-kernel":
                await ErasureKernelAsync(s, s.Bystander);

                break;

            case "schema-rebuild":
                await RebuildWindowAsync(s);

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(bypass));
        }
    }

    /// <summary>
    /// The Covenant erasure kernel removes one protected leaf's artifact, its source and every generation that froze it. Its exclusive
    /// lease leaves the scope closed unless it is given a disposition, so a caller that takes a turn lease afterwards asks for the scope to be
    /// reopened without a commit, which moves no dataset generation.
    /// </summary>
    internal static async Task ErasureKernelAsync(Scenario s, string memory, bool reopenScope = false)
    {
        using CovenantConnectionSource connections = new(s.F.H.Saga.Context, FixtureOrdinaryConnectionFactory.For(s.F.H.Saga.Context));

        ArtifactSensitivityLabel label = LongRestSweepHarness.Require(await new ArtifactSensitivityLedger(connections)
            .TryReadLabelAsync(SensitiveArtifactKind.Saga, Guid.Parse(memory), CancellationToken.None))!;

        await s.F.Authority.DisposeAsync();

        await using CovenantExclusiveLease lease = LongRestSweepHarness.Require(await s.F.Gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize), CancellationToken.None));

        CovenantArtifactErasureAuthority authority = LongRestSweepHarness.Require(CovenantArtifactErasureAuthority.ForExclusive(
            lease, CovenantExclusiveOperation.CovenantFamilyReinitialize));

        CovenantProtectedArtifactErasureKernel kernel = new(connections, CovenantSqliteConnectionInitializer.Instance, TimeProvider.System);

        Result<CovenantArtifactErasureProgress> erased = await kernel.ErasePageAsync(new(
            lease.Snapshot.DatasetGeneration!.Value,
            [new(label.ArtifactId, label.ArtifactKind, label.SessionId, label.LabelId, label, label.ArtifactContentDigest, label.ArtifactRevision)]),
            authority, CancellationToken.None);

        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.Message : "");

        Assert.Equal(1UL, erased.Value.ErasedCount);

        if (reopenScope)
        {
            Result reopened = await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, CancellationToken.None);

            Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : "");
        }
    }

    /// <summary>
    /// A table rebuild drops the triggers that were attached to the table, and a later step of the same rebuild restores them. Whatever
    /// changes the table in between is invisible to them; this is that window, with a leaf's text changed inside it.
    /// </summary>
    private static async Task RebuildWindowAsync(Scenario s)
    {
        List<(string Name, string Sql)> triggers = [];

        await using (SqliteCommand read = s.F.H.Connection.CreateCommand())
        {
            read.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'saga_memories' ORDER BY name;";

            await using SqliteDataReader reader = await read.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.Contains(triggers, trigger => trigger.Name == "long_rest_saga_update");

        foreach ((string name, _) in triggers)
        {
            await s.SqlAsync($"DROP TRIGGER \"{name}\";");
        }

        await s.SqlAsync("UPDATE saga_memories SET Content = 'altered content' WHERE Id = $m;", ("$m", s.Bystander));

        foreach ((_, string sql) in triggers)
        {
            await s.SqlAsync(sql);
        }
    }

    /// <summary>Only the Saga memory rule is on, so a prune reaches nothing else the corpus rests on.</summary>
    private static ArcanumSettings SagaPruneSettings()
    {
        static RetentionRuleSettings Off() => new() { Enabled = false, Days = 30 };

        return new()
        {
            Retention = new RetentionSettings
            {
                AutomaticSweepsEnabled = false,
                ActiveSessions = Off(),
                ArchivedSessions = Off(),
                Entries = Off(),
                Attachments = Off(),
                UploadedFiles = Off(),
                CompletedBatches = Off(),
                SagaMemories = new() { Enabled = true, Days = 1 },
                LexiconEntries = Off(),
                WorkspaceIndexes = Off(),
                SessionEntryEmbeddings = Off(),
                AuditLogs = Off(),
                GuardrailLogs = Off(),
                IdempotencyClaims = Off(),
                Accounting = Off(),
                LongRunningOperations = Off(),
                SanctumBreaches = Off(),
                DaemonHistory = Off(),
            },
        };
    }

    private sealed class Retention : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-matrix-reset-" + Guid.NewGuid().ToString("N"));

        internal Retention(LongRestMaintenanceTests.MaintenanceFixture f, ArcanumSettings? settings = null)
        {
            Directory.CreateDirectory(_root);

            foreach (string child in new[] { "attachments", "files", "logs" })
            {
                Directory.CreateDirectory(Path.Combine(_root, child));
            }

            Service = new(f.H.Saga.Context, new TestOptionsMonitor<ArcanumSettings>(settings ?? f.Options.CurrentValue), f.Operations,
                TimeProvider.System, NullLogger<DataRetentionService>.Instance, FixtureLabeledArtifactGuard.For(f.H.Saga.Context),
                Path.Combine(_root, "attachments"), Path.Combine(_root, "files"), Path.Combine(_root, "logs"));
        }

        internal DataRetentionService Service { get; }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, recursive: true);

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>What one piece of work verified, from the counters of a measurement scope.</summary>
    internal sealed record Cost(long CorpusPasses, long ScrubWindows, long FullLeafReads, long MetadataLeafReads, long AntiJoinPasses, long Unit)
    {
        internal static Cost Of(LongRestVerificationTally tally, long unit = 0) => new(tally.CorpusPasses, tally.ScrubWindows, tally.FullLeafReads,
            tally.MetadataLeafReads, tally.AntiJoinPasses, unit);
    }

    /// <summary>The verdicts of every gate on the mutated corpus, from a step and from the full recompute.</summary>
    internal sealed record Verdicts(bool EnsureSeam, bool EnsureStep, bool AttemptSeam, bool AttemptStep, bool NextWorkRefused, bool ScanRefused);

    internal sealed class Scenario : IAsyncDisposable
    {
        private readonly LongRestSweepStore _sweeps;

        private readonly LongRestAttemptStore _attempts;

        private LongRestSweep _sweep = null!;

        private LongRestDispatchAttempt _attempt = null!;

        private string _unit = "";

        private Scenario(LongRestMaintenanceTests.MaintenanceFixture fixture)
        {
            F = fixture;

            _sweeps = new(fixture.H.Saga.Context, fixture.Clock);

            _attempts = new(fixture.H.Saga.Context, fixture.Clock);
        }

        internal LongRestMaintenanceTests.MaintenanceFixture F { get; }

        internal LongRestMaintenanceCapability? Capability { get; private set; }

        internal LongRestDiscoveryGeneration Generation { get; private set; } = null!;

        internal string GenerationText => Generation.GenerationId.ToString("D").ToUpperInvariant();

        internal string[] Inputs { get; private set; } = [];

        internal string Unit => _unit;

        /// <summary>Every unit that built the generation, with the phase the sweep stood in afterwards and what the unit verified.</summary>
        internal List<(LongRestSweepPhase Phase, Cost Cost)> BuildUnits { get; } = [];

        /// <summary>What the first read of work cost: the anchor's scan of the corpus and the pack it queued.</summary>
        internal Cost FirstRead { get; private set; } = null!;

        internal static async Task<Cost> MeasureAsync(Func<Task> work)
        {
            using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

            await work();

            return Cost.Of(tally);
        }

        internal LongRestSweepStore Sweeps => _sweeps;

        internal async Task<LongRestSweep> CurrentSweepAsync() =>
            LongRestSweepHarness.Require(await _sweeps.BeginOrResumeAsync(F.Operation, F.Partition, F.Epoch.Capture().Stamp, CancellationToken.None));

        /// <summary>Moves the discovery cursor past the last leaf, so that the next read has nothing left to scan and can only complete the sweep.</summary>
        internal Task AdvanceDiscoveryToTheEndAsync() => F.H.ExecuteAsync("""
            UPDATE long_rest_sweeps SET DiscoveryCursor = (SELECT max(MemoryId) FROM long_rest_discovery_leaves WHERE GenerationId = long_rest_sweeps.GenerationId)
            WHERE OperationId = $operation;
            """, ("$operation", F.Operation.ToString("N")));

        private string[] _memories = [];

        /// <summary>A second leaf that is neither an input of the unit nor the leaf under test, for a mutation that names a pair.</summary>
        internal string Partner(string memory) => _memories.Skip(2).Last(candidate => candidate != memory && candidate != Bystander);

        internal string Bystander { get; set; } = "";

        internal bool IsProtected { get; private set; }

        /// <summary>A leaf a prepared corpus needs the bystander to be, because it is the one that was given a history before capture.</summary>
        private string? _fixedBystander;

        internal string Memory(int index) => _memories[index];

        /// <summary>The survivor of the consolidation a prepared corpus was given, if it was given one.</summary>
        internal string? FixedSurvivor => _fixedBystander;

        /// <summary>
        /// Gives two leaves a history before anything is captured: the first input and one leaf outside the window are each the survivor of a
        /// consolidation whose source is another memory of the corpus. The sources are suppressed, so they are not captured; the survivors are
        /// captured as transformation outputs.
        /// </summary>
        internal async Task ConsolidateAsync()
        {
            _fixedBystander = Memory(_memories.Length - 5);

            await ConsolidateAsync(Memory(3), Memory(0));

            await ConsolidateAsync(Memory(5), _fixedBystander);
        }

        private async Task ConsolidateAsync(string source, string survivor)
        {
            LongRestTarget consumed = await F.H.TargetAsync(source);

            LongRestTarget kept = await F.H.TargetAsync(survivor);

            if (!IsProtected)
            {
                Result<LongRestReceipt> applied = await new SagaLongRestService(F.H.Saga.Context, TimeProvider.System).ApplyAsync(
                    new(LongRestTransformationKind.Supersession, [consumed, kept], kept.ExpectedVersionId), CancellationToken.None);

                Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.ToString() : "");

                return;
            }

            // A protected consolidation is written only under a protected sweep's own capability, which this corpus has none of before it is
            // captured. The write guard is lifted for the one insert and restored whole, so the rows are the ones a protected sweep would have left.
            string guard = (string)(await F.H.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'long_rest_suppressions_validate_insert';"))!;

            await SqlAsync("DROP TRIGGER long_rest_suppressions_validate_insert;");

            string receipt = "protected-history-" + source;

            await SqlAsync("""
                INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, RequestedSurvivorVersionId, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
                VALUES ($receipt, 1, 2, 1, 0, $hash, $hash, $survivor, $survivorMemory, $survivor, '2026-10-10T00:00:00.0000000Z');
                INSERT INTO long_rest_receipt_inputs (ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode, ContentHash, SnapshotHash, SnapshotJson)
                VALUES ($receipt, 1, $sourceMemory, $source, 1, zeroblob(32), $hash, '{}'), ($receipt, 2, $survivorMemory, $survivor, 1, zeroblob(32), $hash, '{}');
                INSERT INTO long_rest_suppressions (SourceVersionId, SurvivorVersionId, ReceiptId) VALUES ($source, $survivor, $receipt);
                """, ("$receipt", receipt), ("$hash", new string('a', 64)), ("$survivor", kept.ExpectedVersionId), ("$survivorMemory", survivor),
                ("$source", consumed.ExpectedVersionId), ("$sourceMemory", source));

            await SqlAsync(guard);
        }

        /// <summary>The memory an earlier consolidation suppressed in favour of this one.</summary>
        internal async Task<string> ConsolidatedSourceOfAsync(string survivor) => (string)(await F.H.ScalarAsync($"""
            SELECT claim.SubjectId FROM long_rest_suppressions suppression JOIN annal_versions version ON version.VersionId = suppression.SourceVersionId
            JOIN annal_claims claim ON claim.ClaimId = version.ClaimId
            WHERE suppression.SurvivorVersionId = (SELECT head.CurrentVersionId FROM annal_claims owner JOIN annal_heads head ON head.ClaimId = owner.ClaimId
                WHERE owner.SubjectStoreCode = 1 AND owner.SubjectId = '{survivor}');
            """))!;

        internal LongRestSweepWork Work { get; private set; } = null!;

        internal static Guid Id(int index) => new("a0000000-0000-0000-0000-" + index.ToString("D12", CultureInfo.InvariantCulture));

        internal static async Task<Scenario> CreateAsync(bool isProtected, int leaves, bool queueUnit = true, bool related = true, Func<Scenario, Task>? prepare = null,
            Func<int, float[]>? vectorOf = null, Func<Scenario, Task<bool>>? stopWhen = null)
        {
            LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: true,
                protectedSources: isProtected, sourceCount: leaves, sourceId: Id,
                sourceText: index => related && index < 2 ? $"orchard apples harvest ledger entry {index}" : $"observation about topic{index} with detail{index}");

            Scenario s = new(f) { IsProtected = isProtected };

            string[] memories = [.. Enumerable.Range(0, leaves).Select(index => Id(index).ToString("D"))];

            s._memories = memories;

            for (int index = 0; index < leaves; index++)
            {
                float[] vector;

                if (vectorOf is null)
                {
                    vector = new float[leaves];

                    vector[related && index < 2 ? 0 : index] = 1;
                }
                else
                {
                    vector = vectorOf(index);
                }

                await f.H.SetEmbeddingAsync(memories[index], vector);
            }

            if (prepare is not null)
            {
                // Before anything is captured, so that what a row later changes or removes is already part of the corpus the generation freezes.
                await prepare(s);
            }

            if (isProtected)
            {
                s.Capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

                Assert.True((await s.Capability.ConsumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None)).IsSuccess);
            }

            int sends = 0;

            f.Handler.VectorFor = body =>
            {
                float[] vector = new float[64];

                vector[int.Parse(Regex.Match(body, @"Summary (\d+)").Groups[1].Value, CultureInfo.InvariantCulture) % 64] = 1;

                return vector;
            };

            f.Handler.InsideSend = () =>
            {
                f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                    ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Summary " + ++sends + "\"}";

                return Task.CompletedTask;
            };

            LongRestSweepProgress progress;

            int guard = 0;

            do
            {
                using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

                progress = LongRestSweepHarness.Require(await f.ProcessAsync(f.Operation, "fixture-worker", s.Capability, CancellationToken.None));

                s.BuildUnits.Add((progress.Phase, Cost.Of(tally)));

                if (stopWhen is not null && await stopWhen(s))
                {
                    // Handed over in the middle of the hierarchy build, between two units.
                    s.Generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

                    return s;
                }
            } while (progress.Phase != LongRestSweepPhase.Discover && ++guard < 200);

            Assert.Equal(LongRestSweepPhase.Discover, progress.Phase);

            s.Generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

            s._sweep = LongRestSweepHarness.Require(await s._sweeps.BeginOrResumeAsync(f.Operation, f.Partition, f.Epoch.Capture().Stamp, CancellationToken.None));

            if (queueUnit)
            {
                using (LongRestVerificationTally first = LongRestVerificationCounters.Measure())
                {
                    s.Work = LongRestSweepHarness.Require(await s._sweeps.ReadNextWorkAsync(s._sweep, s.Capability, CancellationToken.None))!;

                    s.FirstRead = Cost.Of(first);
                }

                Assert.Equal([memories[0], memories[1]], s.Work.Inputs.Select(input => input.Snapshot.MemoryId).Order());

                s.Inputs = [.. s.Work.Inputs.Select(input => input.Snapshot.MemoryId).Order()];

                CovenantDigest payload = new(SHA256.HashData("matrix payload"u8));

                s._attempt = LongRestSweepHarness.Require(await s._attempts.PrepareAttemptAsync(s.Work.Identity, LongRestProviderPayloadKind.Adjudication,
                    payload, s.Generation.Recipe.SummaryDestinationDigest, CancellationToken.None));

                s._unit = (string)(await f.H.ScalarAsync($"SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId = '{s.GenerationText}' AND KindCode = 3 AND DispositionCode = 1;"))!;
            }

            // The bystander is a leaf the unit does not act on and the rotating window does not cover. A prepared corpus names it, and the window is
            // turned until it is outside it: the window follows the sweep's revision and nothing else.
            List<string> window = await s.WindowAsync();

            if (s._fixedBystander is { } fixedBystander)
            {
                for (int turn = 0; turn < Windows && window.Contains(fixedBystander); turn++)
                {
                    await s.AdvanceSweepRevisionAsync();

                    window = await s.WindowAsync();
                }

                Assert.DoesNotContain(fixedBystander, window);

                s.Bystander = fixedBystander;

                return s;
            }

            s.Bystander = memories.Skip(2).Last(memory => !window.Contains(memory));

            return s;
        }

        internal async Task<List<string>> WindowAsync()
        {
            await using SqliteTransaction transaction = F.H.Connection.BeginTransaction(deferred: true);

            return await LongRestDiscoveryStore.ReadScrubWindowAsync(F.H.Connection, transaction, Generation, CancellationToken.None);
        }

        internal async Task<(string Version, long Sequence)> VersionOfAsync(string memory)
        {
            await using SqliteCommand command = F.H.Connection.CreateCommand();

            command.CommandText = """
                SELECT v.VersionId, v.Sequence FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId
                JOIN annal_versions v ON v.VersionId = h.CurrentVersionId WHERE c.SubjectStoreCode = 1 AND c.SubjectId = $memory;
                """;

            command.Parameters.AddWithValue("$memory", memory);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            return (reader.GetString(0), reader.GetInt64(1));
        }

        /// <summary>Parks the sweep the way recovery leaves it after its worker is gone, and returns the code it was parked under.</summary>
        internal async Task<string> ParkAsync()
        {
            await F.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
                ("$operation", F.Operation.ToString("N")));

            LongRunningOperationReconciler reconciler = new(F.Operations, [new LongRestSweepRecoveryHandler()], F.Clock,
                NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

            LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("matrix-recovery");

            Assert.Equal(1, summary.RequiresAttention);

            LongRunningOperation parked = (await F.Operations.GetAsync(F.Operation))!;

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

            return parked.TerminalErrorCode!;
        }

        /// <summary>Offers the parked sweep to the authority class that owns it: the hosted worker for a clean one, a fresh Session capability for a protected one.</summary>
        internal async Task<Result<LongRestSweepProgress>> ReadmitAsync()
        {
            if (Capability is null)
            {
                return await F.ResumeAdmittedAsync(F.Operation, CancellationToken.None);
            }

            Result<LongRestMaintenanceCapability> fresh = await F.Maintenance.CreateCapabilityAsync(F.Operation, F.Invocation, F.Claim, F.Scope, F.Authority, CancellationToken.None);

            return fresh.IsFailure ? Result<LongRestSweepProgress>.Failure(fresh.Error) : await F.Coordinator.ResumeAsync(F.Operation, fresh.Value, CancellationToken.None);
        }

        /// <summary>Gives every leaf an attachment provenance row, so that a row can later update or remove one.</summary>
        internal async Task AttachAllAsync()
        {
            foreach (string memory in _memories)
            {
                await SqlAsync("INSERT INTO saga_memory_attachment_provenance VALUES ($m, 'session', 'AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA', 'key', 1, 'hash', '2026-10-10T00:00:00.0000000Z', 'text');", ("$m", memory));
            }
        }

        internal Task SqlAsync(string sql, params (string Name, object? Value)[] parameters) => F.H.ExecuteAsync(sql, parameters);

        internal Task<Result> EnsureAsync(LongRestVerificationDepth depth, IReadOnlyCollection<string>? inputs) =>
            F.Facts.ValidateGenerationAsync(Generation, Capability, DateTimeOffset.UtcNow, depth, inputs, CancellationToken.None);

        internal Task AdvanceSweepRevisionAsync() => F.H.ExecuteAsync("UPDATE long_rest_sweeps SET Revision = Revision + 1 WHERE OperationId = $operation;", ("$operation", F.Operation.ToString("N")));

        internal async Task<Verdicts> ProbeAsync()
        {
            bool ensureSeam = (await EnsureAsync(LongRestVerificationDepth.Seam, null)).IsFailure;

            bool ensureStep = (await EnsureAsync(LongRestVerificationDepth.Step, Inputs)).IsFailure;

            bool attemptStep = (await _attempts.ValidateAttemptAsync(_attempt, CancellationToken.None)).IsFailure;

            bool attemptSeam = await AttemptSeamRefusedAsync();

            bool next = (await _sweeps.ReadNextWorkAsync(_sweep, Capability, CancellationToken.None)).IsFailure;

            bool scan = (await F.H.Store.DiscoverAsync(Generation.GenerationId, Work.Inputs[0].Target, Capability, CancellationToken.None)).IsFailure;

            return new(ensureSeam, ensureStep, attemptSeam, attemptStep, next, scan);
        }

        internal Task<bool> AttemptSeamAsync() => AttemptSeamRefusedAsync();

        internal async Task<bool> AttemptStepRefusedAsync() => (await _attempts.ValidateAttemptAsync(_attempt, CancellationToken.None)).IsFailure;

        private async Task<bool> AttemptSeamRefusedAsync()
        {
            try
            {
                await using SqliteTransaction transaction = F.H.Connection.BeginTransaction(deferred: true);

                await LongRestDiscoveryStore.ValidateAttemptSourceMetadataAsync(F.H.Connection, transaction, Work.Identity, _unit,
                    LongRestVerificationDepth.Seam, CancellationToken.None);

                return false;
            }
            catch (InvalidDataException)
            {
                return true;
            }
        }

        public async ValueTask DisposeAsync() => await F.DisposeAsync();
    }
}
