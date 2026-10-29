using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class CovenantLabeledRetentionRouteTests
{
    [SkippableTheory]
    [InlineData("entry", false)]
    [InlineData("saga", false)]
    [InlineData("lexicon", false)]
    [InlineData("session", false)]
    [InlineData("entry", true)]
    [InlineData("saga", true)]
    [InlineData("lexicon", true)]
    [InlineData("session", true)]
    public async Task Prune_preview_excludes_labelled_candidates_and_keeps_eligible_survivor(string family, bool enabled)
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Covenant = enabled;

                return settings;
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid artifact = Guid.NewGuid();

        Guid holder = Guid.NewGuid();

        Guid survivor = Guid.NewGuid();

        Guid survivorHolder = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            SqliteConnection connection = await OpenAsync(scope);

            await SeedDiagnosticArtifactAsync(connection, family, holder, artifact);

            await SeedSessionAsync(connection, survivorHolder);

            await SeedEntryAsync(connection, survivorHolder, survivor);

            await LabelAsync(scope, DiagnosticArtifactKind(family), artifact, family is "entry" or "session" ? holder : null);
        }

        await EnableDiagnosticRuleAsync(client, "entries");

        await EnableDiagnosticRuleAsync(client, DiagnosticRule(family));

        using HttpResponseMessage response = await client.PostAsync("/api/data/prune/plan", Json(
            new DataRetentionRequest(DataRetentionOperation.Prune), ArcanumJsonContext.Default.DataRetentionRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync();

        var envelope = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)!;

        DataRetentionPlan plan = envelope.Data!;

        Guid target = family == "session" ? holder : artifact;

        Assert.DoesNotContain(plan.CandidateIds, candidate => MatchesDiagnosticCandidate(candidate, family, target));

        Assert.Contains(plan.Blockers, blocker => blocker.ReasonCode == "Covenant.ForbiddenAuthority"
            && Guid.TryParse(blocker.ResourceId, out Guid id) && id == target);

        Assert.Contains(plan.CandidateIds, candidate => MatchesDiagnosticCandidate(candidate,
            family == "session" ? "session" : "entry", family == "session" ? survivorHolder : survivor));

        if (family == "session")
        {
            Assert.Contains(plan.Blockers, blocker => blocker.ReasonCode == "Covenant.ForbiddenAuthority"
                && blocker.HoldingSessionId == holder);
        }

        using HttpResponseMessage applied = await client.PostAsync("/api/data/prune", Json(
            new DataRetentionApplyRequest(plan.Request, plan.PlanId), ArcanumJsonContext.Default.DataRetentionApplyRequest));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        ApiResponse<DataRetentionApplyResult> result = await ReadAsync(applied);

        Assert.True(result.IsSuccess);

        Assert.Contains(result.Data!.Blockers, blocker => blocker.ReasonCode == "Covenant.ForbiddenAuthority");

        await using AsyncServiceScope verify = factory.Services.CreateAsyncScope();

        SqliteConnection current = await OpenAsync(verify);

        Assert.Equal(1, await CountAsync(current, DiagnosticTable(family), "Id", target));

        Assert.Equal(0, await CountAsync(current, "Entries", "Id", survivor));
    }

    [SkippableTheory]
    [InlineData("entry")]
    [InlineData("saga")]
    [InlineData("lexicon")]
    [InlineData("session")]
    public async Task Prune_labels_committed_after_fresh_plan_report_the_actual_preservation_cause(string family)
    {
        RequireSqlCipher();

        Guid artifact = Guid.NewGuid();

        Guid holder = Guid.NewGuid();

        int inserted = 0;

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                Func<IServiceProvider, object> real = services.Last(descriptor => descriptor.ServiceType == typeof(ILongRunningOperationStore))
                    .ImplementationFactory!;

                services.AddScoped<ILongRunningOperationStore>(sp => new HookedLongRunningOperationStore((ILongRunningOperationStore)real(sp))
                {
                    AfterCheckpointSaved = async (_, _) =>
                    {
                        if (Interlocked.Exchange(ref inserted, 1) != 0) return;

                        await using AsyncServiceScope scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();

                        await LabelAsync(scope, DiagnosticArtifactKind(family), artifact,
                            family is "entry" or "session" ? holder : null);
                    },
                });
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await SeedDiagnosticArtifactAsync(await OpenAsync(scope), family, holder, artifact);
        }

        await EnableDiagnosticRuleAsync(client, DiagnosticRule(family));

        using HttpResponseMessage applied = await client.PostAsync("/api/data/prune", Json(
            new DataRetentionApplyRequest(new(DataRetentionOperation.Prune)), ArcanumJsonContext.Default.DataRetentionApplyRequest));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        Assert.Equal(1, inserted);

        ApiResponse<DataRetentionApplyResult> result = await ReadAsync(applied);

        await using AsyncServiceScope verify = factory.Services.CreateAsyncScope();

        SqliteConnection current = await OpenAsync(verify);

        Assert.Equal(1, await CountAsync(current, DiagnosticTable(family), "Id", family == "session" ? holder : artifact));

        Assert.Equal(1, await CountAsync(current, "artifact_sensitivity", "ArtifactId", artifact));

        Assert.Contains(result.Data!.Conflicts, static conflict => conflict.Code == "Covenant.ForbiddenAuthority");

        Assert.DoesNotContain(result.Data.Conflicts, static conflict => conflict.Code == "Data.PlanChanged");
    }

    [SkippableFact]
    public async Task Prune_preview_keeps_unreadable_guard_unavailable_instead_of_claiming_a_label()
    {
        RequireSqlCipher();

        await using ArcanumWebApplicationFactory factory = HostWithUnreadableGuard();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid holder = Guid.NewGuid();

        Guid artifact = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await SeedDiagnosticArtifactAsync(await OpenAsync(scope), "entry", holder, artifact);
        }

        await EnableDiagnosticRuleAsync(client, "entries");

        using HttpResponseMessage response = await client.PostAsync("/api/data/prune/plan", Json(
            new DataRetentionRequest(DataRetentionOperation.Prune), ArcanumJsonContext.Default.DataRetentionRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)!;

        Assert.DoesNotContain(envelope.Data!.CandidateIds, candidate => MatchesDiagnosticCandidate(candidate, "entry", artifact));

        Assert.Contains(envelope.Data.Blockers, static blocker => blocker.ReasonCode == "Covenant.Unavailable");

        Assert.DoesNotContain(envelope.Data.Blockers, static blocker => blocker.ReasonCode == "Covenant.ForbiddenAuthority");
    }

    private static bool MatchesDiagnosticCandidate(string candidate, string family, Guid id)
    {
        string prefix = family + ":";

        return candidate.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(candidate[prefix.Length..], out Guid candidateId) && candidateId == id;
    }

    private static SensitiveArtifactKind DiagnosticArtifactKind(string family) => family switch
    {
        "saga" => SensitiveArtifactKind.Saga,
        "lexicon" => SensitiveArtifactKind.Lexicon,
        _ => SensitiveArtifactKind.AssistantEntry,
    };

    private static string DiagnosticTable(string family) => family switch
    {
        "saga" => "saga_memories",
        "lexicon" => "lexicon_entries",
        "session" => "Sessions",
        _ => "Entries",
    };

    private static string DiagnosticRule(string family) => family switch
    {
        "saga" => "saga-memories",
        "lexicon" => "lexicon-entries",
        "session" => "active-sessions",
        _ => "entries",
    };

    private static async Task SeedDiagnosticArtifactAsync(SqliteConnection connection, string family, Guid holder, Guid artifact)
    {
        if (family == "saga") await SeedSagaMemoryAsync(connection, artifact);
        else if (family == "lexicon") await SeedLexiconEntryAsync(connection, artifact);
        else
        {
            await SeedSessionAsync(connection, holder);

            await SeedEntryAsync(connection, holder, artifact);
        }
    }

    private static async Task EnableDiagnosticRuleAsync(HttpClient client, string rule)
    {
        using HttpResponseMessage response = await client.PutAsync("/api/data/retention", Json(
            new RetentionRuleUpdateRequest(rule, true, 1), ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Observe the new readonly eligibility question without inserting a label there. The competing
    // writer still runs immediately after the transaction guard's answer, where this control must
    // prove that the delete transaction excludes it. Exact question counts detect a moved/bypassed guard.
    private sealed class PrunePreviewIntrusionGuard(
        ICovenantLabeledArtifactTransactionGuard inner,
        LabelIntruder intruder,
        Action observePreview) : ICovenantLabeledArtifactTransactionGuard
    {
        private readonly LabelIntrusionGuard _transactionGuard = new(inner, intruder);

        public ValueTask<Result> EnsureUnlabeledAsync(
            SensitiveArtifactKind kind,
            Guid artifactId,
            CancellationToken cancellationToken = default)
        {
            observePreview();

            return inner.EnsureUnlabeledAsync(kind, artifactId, cancellationToken);
        }

        public ValueTask<Result> EnsureUnlabeledAsync(
            SensitiveArtifactKind kind,
            Guid artifactId,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            _transactionGuard.EnsureUnlabeledAsync(kind, artifactId, connection, transaction, cancellationToken);

        public ValueTask<Result> EnsureAllUnlabeledAsync(
            SensitiveArtifactKind kind,
            IReadOnlyCollection<Guid> artifactIds,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            _transactionGuard.EnsureAllUnlabeledAsync(kind, artifactIds, connection, transaction, cancellationToken);

        public ValueTask<Result> EnsureNoneLabeledAsync(
            SensitiveArtifactKind kind,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default) =>
            _transactionGuard.EnsureNoneLabeledAsync(kind, connection, transaction, cancellationToken);
    }
}
