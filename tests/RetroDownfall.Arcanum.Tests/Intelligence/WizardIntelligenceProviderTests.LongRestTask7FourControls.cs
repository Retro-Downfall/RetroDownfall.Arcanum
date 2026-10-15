using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Fact]
    public async Task LongRestTask7_gate_off_preserves_real_nonempty_retrieval_and_exact_ordered_prompt_bytes()
    {
        await using SagaStoreHarness saga = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        string first = Guid.NewGuid().ToString("D");

        string second = Guid.NewGuid().ToString("D");

        float[] query = new float[64];

        query[0] = 1;

        float[] next = (float[])query.Clone();

        next[1] = 0.2f;

        const string firstText = "First retrieved observation preserves its exact newline.\n";

        const string secondText = "Second retrieved observation preserves café and its order.";

        await saga.Store.InsertAsync(first, firstText, new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), null, null,
            "extraction", query, CancellationToken.None);

        await saga.Store.InsertAsync(second, secondText, new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), null, null,
            "extraction", next, CancellationToken.None);

        ArcanumSettings settings = DefaultSettings();

        settings.Features.Embeddings = true;

        settings.Features.Saga = true;

        settings.Features.LongRest = false;

        settings.Features.CampaignRollups = false;

        DivinationService search = new(saga.Context, saga.VectorAccelerator, NullLogger<DivinationService>.Instance);

        DivinationResult[] expected = LongRestSweepHarness.Require(await search.SearchAsync(
            SagaStorageKeys.VectorTable, SagaStorageKeys.EmbeddingKeyColumn, SagaStorageKeys.EmbeddingColumn,
            new(query), 8, 0.72f, CancellationToken.None));

        Assert.Equal(new[] { first, second }, expected.Select(hit => hit.Id));

        Guid session = Guid.NewGuid();

        FakeGrimoireRepository baselineRepository = new() { Session = new Session { Id = session } };

        FakeGrimoireRepository currentRepository = new() { Session = new Session { Id = session } };

        CampaignRollupTurnSnapshotTests.ProbeStore rollups = new();

        CampaignWizardClaims claims = new();

        CampaignWizardMaintenance maintenance = new();

        CampaignRollupTurnPreparer disabled = new(rollups, new TestOptionsMonitor<ArcanumSettings>(settings),
            new LongRestTask7ScopeResolver(CanonicalCampaignContext.GlobalOnly), null!,
            UnreachedLongRestPorts.Coordinator, UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery, UnreachedLongRestPorts.Configuration, UnreachedLongRestPorts.RootEnricher,
            maintenance, claims,
            currentRepository, claims, new CampaignRollupTurnSnapshotTests.Authority(), new CampaignWizardLookup());

        ScriptingChatClient baselineChat = new();

        ScriptingChatClient currentChat = new();

        baselineChat.EnqueueText("answer");

        currentChat.EnqueueText("answer");

        Task7RecordingSearch baselineSearch = new(search);

        Task7RecordingSearch currentSearch = new(search);

        WizardIntelligenceProvider baseline = CreateWizard(baselineChat, settings, baselineRepository,
            weaveService: new FakeRagWeaveService { QueryVector = query }, divinationService: baselineSearch,
            sagaMemoryStore: saga.Store, db: saga.Context, ordinaryConnections: FixtureOrdinaryConnectionFactory.For(saga.Context));

        WizardIntelligenceProvider current = CreateWizard(currentChat, settings, currentRepository,
            weaveService: new FakeRagWeaveService { QueryVector = query }, divinationService: currentSearch,
            sagaMemoryStore: saga.Store, db: saga.Context, ordinaryConnections: FixtureOrdinaryConnectionFactory.For(saga.Context),
            campaignRollups: disabled, claimedBeginStore: claims, claims: claims);

        PingRequest request = BaseRequest() with
        {
            Prompt = "Recall these observations in their retrieval order.", SessionId = session,
            SkipSpellRouting = true, DisableAllTools = true,
        };

        ArcanumInvocationContext invocation = LongRestTask7Invocation(campaign: false);

        Assert.True((await baseline.ExecutePromptAsync(request, invocation, CancellationToken.None)).IsSuccess);

        Assert.True((await current.ExecutePromptAsync(request, invocation, CancellationToken.None)).IsSuccess);

        Assert.Equal(Task7HitBytes(expected), Task7HitBytes(Assert.Single(baselineSearch.Hits)));

        Assert.Equal(Task7HitBytes(expected), Task7HitBytes(Assert.Single(currentSearch.Hits)));

        IReadOnlyList<ChatMessage> original = Assert.Single(baselineChat.AllBufferedCalls);

        IReadOnlyList<ChatMessage> actual = Assert.Single(currentChat.AllBufferedCalls);

        string system = actual.Single(message => message.Role == ChatRole.System).Text!;

        Assert.Contains(firstText.TrimEnd('\n'), system, StringComparison.Ordinal);

        Assert.Contains(secondText, system, StringComparison.Ordinal);

        Assert.True(system.IndexOf(firstText.TrimEnd('\n'), StringComparison.Ordinal)
            < system.IndexOf(secondText, StringComparison.Ordinal));

        Assert.Equal(original.Count, actual.Count);

        for (int index = 0; index < original.Count; index++)
        {
            Assert.Equal(original[index].Role, actual[index].Role);

            Assert.Equal(Encoding.UTF8.GetBytes(original[index].Text!), Encoding.UTF8.GetBytes(actual[index].Text!));
        }

        IReadOnlyDictionary<string, SagaMemoryDto> retrieved = await saga.Store.GetByIdsAsync([first, second], CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetBytes(firstText), Encoding.UTF8.GetBytes(retrieved[first].Content));

        Assert.Equal(Encoding.UTF8.GetBytes(secondText), Encoding.UTF8.GetBytes(retrieved[second].Content));

        Assert.Empty(claims.Requests);

        Assert.Equal(0, claims.Begins);

        Assert.Equal(0, rollups.Reads);

        Assert.Equal(0, maintenance.SessionCalls + maintenance.CampaignCalls);

        Assert.Equal(0L, await Task7CountAsync((SqliteConnection)saga.Connection, "long_rest_sweeps"));
    }

    [Fact]
    public async Task LongRestTask7_real_protected_preparation_preserves_foreign_campaign_partition_debt()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await Task7ParkAsync(f);

        Guid foreignCampaign = Guid.NewGuid();

        Guid foreignSession = await f.H.SessionAsync(foreignCampaign);

        Guid foreignSource = Guid.NewGuid();

        await f.H.SeedAsync(foreignSource.ToString("D"), "Foreign protected Campaign debt remains pending.", foreignSession);

        CovenantDigest foreignDigest = await f.H.ProtectAsync(foreignSource, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        Assert.NotEqual(f.Partition.ProtectedPartitionDigest, foreignDigest);

        FixedCovenantConnectionSource connections = new(f.H.Connection);

        using CampaignRootIdentityKeyProvider rootKeys = new(new InMemoryOsCredentialStore());

        PhysicalCampaignRootOpener roots = new(rootKeys);

        CanonicalCampaignContextResolver resolver = new(new SessionCampaignBindingReader(connections),
            new CampaignPathIdentityReader(connections, roots), new CampaignAvailabilityReader(connections));

        CanonicalCampaignContext foreignScope = LongRestSweepHarness.Require(await resolver.ResolveAsync(
            new(foreignSession, foreignCampaign, null), CancellationToken.None));

        LongRestSweepScope foreignPartition = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Campaign, foreignCampaign), foreignDigest).Value;

        LongRunningOperationDto operation = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(foreignScope, foreignPartition, CancellationToken.None));

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(operation.Id, null, CancellationToken.None));

        Assert.Equal("long_rest.authorized_request_required", parked.DeferredReason);

        Assert.Equal(foreignPartition.ProtectedPartitionDigest, parked.ProtectedPartitionDigest);

        string[] before = await Task7ForeignDebtAsync(f, operation.Id, foreignSource);

        Assert.NotEmpty(before);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        Task7SuccessfulMaintenanceResponses(f, h);

        Result<CampaignRollupTurnSnapshot?> prepared = await h.Preparer.PrepareLiveAsync(h.Request, f.Invocation, false, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        Assert.NotNull(prepared.Value);

        await using CampaignRollupTurnSnapshot snapshot = prepared.Value!;

        Assert.NotNull(snapshot.ClaimLease);

        Assert.Equal(SessionTurnClaimState.PendingMaintenance, snapshot.ClaimLease!.Claim.State);

        Assert.Null(snapshot.Artifact);

        Assert.Equal(0, h.Rollups.Reads);

        Assert.True(f.Handler.SendCount > 0);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0, h.Begin.Begins);

        Assert.Contains(f.Partition, h.Coordinator.Partitions);

        Assert.DoesNotContain(foreignPartition, h.Coordinator.Partitions);

        Assert.Equal(0L, await Task7CountAsync(f.H.Connection, "Entries"));

        Assert.Equal(before, await Task7ForeignDebtAsync(f, operation.Id, foreignSource));

        Assert.NotEqual(LongRunningOperationState.Completed, (await f.Operations.GetAsync(operation.Id))!.State);
    }

    [Fact]
    public async Task LongRestTask7_wrong_session_accepted_key_refuses_without_materialization_send_begin_claim_or_debt_change()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await Task7ParkAsync(f);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        Guid wrongSession = await f.H.SessionAsync();

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(h.Request, ArcanumJsonContext.Default.PingRequest);

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(Convert.ToHexString(SHA256.HashData("Task7 accepted wrong-Session control"u8)),
                Convert.ToHexString(SHA256.HashData(body)));

            TurnIdempotencyRequestIdentity accepted = TurnIdempotencyAmbient.RequestIdentity!;

            CovenantAuthoritySnapshot origin = new CovenantAuthoritySnapshotProvider(f.Runtime).Current!;

            SessionTurnClaimInputSnapshot input = LongRestSweepHarness.Require(await h.Repository.ReadClaimInputAsync(h.Request.SessionId!.Value, CancellationToken.None));

            CovenantDigest requestDigest = CovenantDigests.SessionTurnRequest(new(accepted.Surface, CovenantProviderDispatchMode.Buffered,
                accepted.ClientTurnId, h.Request.SessionId, f.Invocation.ContextPolicy, accepted.Route, accepted.AcceptedBodyDigest,
                h.Request.CampaignId, null));

            SessionTurnClaimLease issued = LongRestSweepHarness.Require(await h.NativeClaims.AcquireAsync(
                new(Guid.Parse(origin.InstallationIdentity), 0, accepted.ClientTurnId, input.SessionId, accepted.Surface,
                    requestDigest, CampaignRollupTurnPreparer.ConfigurationDigest(f.Options.CurrentValue), input.HistoryWatermarkUtc,
                    input.HistoryRevision, input.SensitivityRevision), CancellationToken.None));

            Assert.Equal(SessionTurnClaimState.PendingMaintenance, issued.Claim.State);

            Assert.Equal(accepted.ClientTurnId, issued.Claim.ClientTurnId);

            Assert.NotEqual(wrongSession, issued.Claim.SessionId);

            Assert.Equal(issued.Claim, (await h.NativeClaims.FindAsync(issued.Claim.OriginInstallationId, accepted.ClientTurnId, CancellationToken.None)).Value);

            string[] before = await Task7AllStateAsync(f.H.Connection);

            ScriptingChatClient chat = new();

            chat.EnqueueText("This wrong Session must never dispatch.");

            WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
                new FakeGrimoireRepository { Session = new Session { Id = wrongSession } }, db: f.H.Saga.Context,
                ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
                claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository);

            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(h.Request with { SessionId = wrongSession }, f.Invocation, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Security.IdempotencyConflict, result.Error.Code);

            SessionTurnRequestIdentity attempted = Assert.Single(h.Claims.Acquisitions);

            Assert.Equal(issued.Claim.OriginInstallationId, attempted.OriginInstallationId);

            Assert.Equal(accepted.ClientTurnId, attempted.ClientTurnId);

            Assert.Equal(wrongSession, attempted.SessionId);

            Assert.Equal(accepted.Surface, attempted.Surface);

            Assert.Equal(0, chat.BufferedCallCount + chat.StreamingCallCount);

            Assert.Equal(0, f.Handler.SendCount);

            Assert.Equal(0, h.Begin.Begins);

            Assert.Equal(0, h.Maintenance.Capabilities);

            Assert.Equal(0, h.Coordinator.Resumes);

            Assert.Empty(h.Coordinator.Partitions);

            Assert.Equal(0, h.Coordinator.Requests);

            Assert.Equal(0, h.Discovery.ContentReads);

            Assert.Equal(before, await Task7AllStateAsync(f.H.Connection));

            Assert.Equal(0L, await Task7CountAsync(f.H.Connection, "external_disclosure_receipts"));

            Assert.Equal(0L, await Task7CountAsync(f.H.Connection, "Entries"));
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Fact]
    public async Task LongRestTask7_real_protected_tool_fallback_reuses_one_preparation_claim_and_paid_work()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await Task7ParkAsync(f);

        await using Task7ProtectedPreparation h = await Task7ProtectedPreparation.CreateAsync(f);

        Task7SuccessfulMaintenanceResponses(f, h);

        ScriptingChatClient chat = new();

        // The Lexicon preflight must consume its own response, leaving the tool refusal
        // for the actual assistant dispatch and its tool-free retry.
        chat.EnqueueText("{\"entities\":[]}");

        chat.EnqueueException(new InvalidOperationException("model does not support tools"));

        chat.EnqueueText("retried after real protected maintenance");

        List<string[]> paidAtAssistantSend = [];

        List<(int Claims, int Inputs, int Capabilities, int Resumes, int Sends)> preparationAtAssistantSend = [];

        chat.BeforeCall = () =>
        {
            if (!chat.LastBufferedMessages.Any(message => message.Role == ChatRole.System))
            {
                Assert.Equal(ChatRole.User, Assert.Single(chat.LastBufferedMessages).Role);

                return;
            }

            Assert.Equal(1, h.Begin.Begins);

            Assert.True(f.Handler.SendCount > 0);

            preparationAtAssistantSend.Add((h.Claims.Acquisitions.Count, h.Begin.Reads, h.Maintenance.Capabilities,
                h.Coordinator.Resumes, f.Handler.SendCount));

            paidAtAssistantSend.Add(Task7PaidRowsAsync(f.H.Connection).GetAwaiter().GetResult());
        };

        WizardIntelligenceProvider wizard = CreateWizard(chat, f.Options.CurrentValue,
            new FakeGrimoireRepository { Session = new Session { Id = h.Request.SessionId!.Value } }, db: f.H.Saga.Context,
            ordinaryConnections: FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), campaignRollups: h.Preparer,
            claimedBeginStore: h.Begin, claims: h.Claims, turnCommitter: h.Repository);

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(f.Invocation.Surface, f.Scope,
            f.Invocation.Attendance, f.Invocation.ContextPolicy, ToolPolicy.AllTools, f.Invocation.ReadAuthorityEpoch).Value;

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(h.Request with
        {
            DisableAllTools = false, DisableMcpTools = true,
        }, invocation, CancellationToken.None);

        Assert.Equal(1, h.Begin.Begins);

        Assert.Single(h.Claims.Acquisitions);

        Assert.True(f.Handler.SendCount > 0, "Real protected maintenance must finish before the assistant fallback begins.");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(3, chat.BufferedCallCount);

        Assert.Single(h.Claims.Acquisitions);

        Assert.Equal(1, h.Begin.Reads);

        Assert.Equal(1, h.Begin.Begins);

        Assert.Equal(1, h.Maintenance.Capabilities);

        Assert.True(h.Coordinator.Resumes > 0);

        Assert.Equal(h.Coordinator.ResumeIds.Count, h.Coordinator.ResumeIds.Distinct().Count());

        Assert.Equal(2, preparationAtAssistantSend.Count);

        Assert.Equal(preparationAtAssistantSend[0], preparationAtAssistantSend[1]);

        Assert.Equal(2, paidAtAssistantSend.Count);

        Assert.NotEmpty(paidAtAssistantSend[0]);

        Assert.Equal(paidAtAssistantSend[0], paidAtAssistantSend[1]);

        Assert.Equal(paidAtAssistantSend[1], await Task7PaidRowsAsync(f.H.Connection));

        Assert.True(f.Handler.SendCount > 0);

        Assert.Equal((long)f.Handler.SendCount, await Task7CountAsync(f.H.Connection, "BillableOperations"));

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(2L, await Task7CountAsync(f.H.Connection, "Entries"));

        Assert.Equal(SessionTurnClaimState.Committed,
            (await h.NativeClaims.FindAsync(h.Claims.Acquisitions[0].OriginInstallationId, h.Claims.Acquisitions[0].ClientTurnId, CancellationToken.None)).Value!.State);

        IReadOnlyList<ChatMessage>[] assistantCalls = chat.AllBufferedCalls
            .Where(messages => messages.Any(message => message.Role == ChatRole.System)).ToArray();

        Assert.Equal(2, assistantCalls.Length);

        Assert.All(assistantCalls, AssertLongRestTask7NoRollupMaterial);

        Assert.Equal(assistantCalls[0].Select(message => message.Text), assistantCalls[1].Select(message => message.Text));

        Assert.True(chat.LastChatOptions?.Tools is null or { Count: 0 });
    }

    private static byte[] Task7HitBytes(IEnumerable<DivinationResult> hits) => Encoding.UTF8.GetBytes(string.Join("\n",
        hits.Select(hit => hit.Id + ":" + BitConverter.SingleToInt32Bits(hit.Similarity).ToString(CultureInfo.InvariantCulture)
            + ":" + string.Join("|", hit.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)))));

    private static async Task Task7ParkAsync(LongRestMaintenanceTests.MaintenanceFixture f)
    {
        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal("long_rest.authorized_request_required", parked.DeferredReason);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await Task7CountAsync(f.H.Connection, "long_rest_discovery_leaves"));
    }

    private static void Task7SuccessfulMaintenanceResponses(LongRestMaintenanceTests.MaintenanceFixture f, Task7ProtectedPreparation h) =>
        f.Handler.InsideSend = async () =>
        {
            Assert.Equal(0L, await Task7CountAsync(f.H.Connection, "Entries"));

            Assert.Equal(0, h.Begin.Begins);

            Assert.Equal(1L, await Task7CountAsync(f.H.Connection, "session_turn_claims", "SessionId = $session AND StateCode = 1",
                ("$session", h.Request.SessionId!.Value.ToString("D").ToUpperInvariant())));

            Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))! > 0);

            f.Handler.ChatText = f.Handler.LastBody!.Contains("survivorVersionId", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";
        };

    private static async Task<long> Task7CountAsync(SqliteConnection connection, string table,
        string predicate = "1 = 1", params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT count(*) FROM \"{table}\" WHERE {predicate};";

        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task<string[]> Task7PaidRowsAsync(SqliteConnection connection) =>
        Task7RowsAsync(connection, ["BillableOperations", "InferenceRuns", "external_disclosure_receipts", "long_rest_provider_effects",
            "long_rest_accepted_summaries", "long_rest_adjudications"]);

    private static async Task<string[]> Task7AllStateAsync(SqliteConnection connection)
    {
        List<string> tables = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        return await Task7RowsAsync(connection, tables);
    }

    private static async Task<string[]> Task7ForeignDebtAsync(LongRestMaintenanceTests.MaintenanceFixture f,
        Guid operation, Guid source)
    {
        const string generationQuery = "SELECT GenerationId FROM long_rest_discovery_generations WHERE OperationId = $operation";

        string nodes = $"SELECT NodeId FROM long_rest_discovery_nodes WHERE GenerationId IN ({generationQuery})";

        string work = $"SELECT WorkItemId FROM long_rest_work_items WHERE GenerationId IN ({generationQuery})";

        string versions = "SELECT VersionId FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId = $source)";

        string receipts = $"SELECT ReceiptId FROM long_rest_receipts WHERE SurvivorVersionId IN ({versions}) OR RequestedSurvivorVersionId IN ({versions}) OR ReceiptId IN (SELECT ReceiptId FROM long_rest_receipt_inputs WHERE VersionId IN ({versions}))";

        List<string> rows = [];

        (string Name, object Value)[] parameters = [("$operation", operation.ToString("N")), ("$source", source.ToString("D")), ("$sourceKey", source.ToString("N")), ("$sagaKind", (long)SensitiveArtifactKind.Saga)];

        string[] tables = ["LongRunningOperations", "long_rest_discovery_generations", "long_rest_sweeps", "long_rest_discovery_leaves",
            "long_rest_discovery_nodes", "long_rest_clean_fts", "long_rest_protected_fts", "long_rest_discovery_embeddings",
            "long_rest_discovery_edges", "long_rest_accepted_summaries", "long_rest_work_items", "long_rest_work_inputs",
            "long_rest_provider_effects", "long_rest_adjudications", "saga_memories", "saga_memory_embeddings",
            "saga_memory_attachment_provenance", "long_rest_sources", "artifact_sensitivity", "annal_claims", "annal_heads",
            "annal_versions", "annal_dependencies", "annal_review_events", "annal_review_decision_receipts",
            "long_rest_receipts", "long_rest_receipt_inputs", "long_rest_suppressions", "long_rest_protected_receipt_inputs", "long_rest_protected_receipt_evidence"];

        foreach (string table in tables)
        {
            string predicate = table switch
            {
                "LongRunningOperations" => "Id = $operation",
                "long_rest_discovery_generations" or "long_rest_sweeps" => "OperationId = $operation",
                "long_rest_discovery_embeddings" => $"NodeId IN ({nodes})",
                "long_rest_work_inputs" or "long_rest_provider_effects" or "long_rest_adjudications" => $"WorkItemId IN ({work})",
                "saga_memories" => "Id = $source",
                "saga_memory_embeddings" or "saga_memory_attachment_provenance" or "long_rest_sources" => "MemoryId = $source",
                "artifact_sensitivity" => "ArtifactKindCode = $sagaKind AND lower(replace(ArtifactId, '-', '')) = $sourceKey",
                "annal_claims" => "SubjectStoreCode = 1 AND SubjectId = $source",
                "annal_heads" or "annal_versions" => "ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId = $source)",
                "annal_dependencies" => $"DependentVersionId IN ({versions}) OR DependencyVersionId IN ({versions})",
                "annal_review_events" => $"VersionId IN ({versions})",
                "annal_review_decision_receipts" => $"ReviewEventSequence IN (SELECT Sequence FROM annal_review_events WHERE VersionId IN ({versions}))",
                "long_rest_receipts" or "long_rest_receipt_inputs" or "long_rest_suppressions" or "long_rest_protected_receipt_inputs" or "long_rest_protected_receipt_evidence" => $"ReceiptId IN ({receipts})",
                _ => $"GenerationId IN ({generationQuery})",
            };

            rows.AddRange(await Task7RowsAsync(f.H.Connection, [table], predicate, parameters));
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string[]> Task7RowsAsync(SqliteConnection connection, IEnumerable<string> tables,
        string predicate = "1 = 1", params (string Name, object Value)[] parameters)
    {
        List<string> rows = [];

        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" WHERE {predicate};";

            foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                List<string> fields = [];

                for (int index = 0; index < reader.FieldCount; index++)
                {
                    fields.Add(reader.GetValue(index) switch
                    {
                        DBNull => "null",
                        byte[] bytes => "blob:" + Convert.ToHexString(bytes),
                        string text => "text:" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)),
                        long value => "integer:" + value.ToString(CultureInfo.InvariantCulture),
                        double value => "real:" + value.ToString("R", CultureInfo.InvariantCulture),
                        _ => throw new InvalidDataException("Unexpected native SQLite field type."),
                    });
                }

                rows.Add(table + ":" + string.Join("|", fields));
            }
        }

        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class Task7ProtectedPreparation : IAsyncDisposable
    {
        internal ServiceProvider Services = null!;

        internal CampaignRollupTurnPreparer Preparer = null!;

        internal CampaignRollupTurnSnapshotTests.ProbeStore Rollups = new();

        internal GrimoireRepository Repository = null!;

        internal SessionTurnClaimStore NativeClaims = null!;

        internal Task7ClaimProbe Claims = null!;

        internal Task7BeginProbe Begin = null!;

        internal Task7MaintenanceProbe Maintenance = null!;

        internal Task7CoordinatorProbe Coordinator = null!;

        internal Task7DiscoveryProbe Discovery = null!;

        internal PingRequest Request = null!;

        internal static async Task<Task7ProtectedPreparation> CreateAsync(LongRestMaintenanceTests.MaintenanceFixture f)
        {
            Task7ProtectedPreparation h = new();

            Guid session = await f.H.SessionAsync(f.CampaignId);

            h.NativeClaims = new(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), f.Epoch.Capture().Stamp.BootId);

            h.Claims = new(h.NativeClaims);

            h.Repository = new(f.H.Saga.Context, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
                new TestOptionsSnapshot<ArcanumSettings>(f.Options.CurrentValue), null, null,
                FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), FixtureLabeledArtifactGuard.For(f.H.Saga.Context));

            h.Begin = new(h.Repository);

            h.Maintenance = new(f.Maintenance);

            h.Coordinator = new(f.Coordinator);

            h.Discovery = new(f.H.Store);

            h.Services = new ServiceCollection()
                .AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Options)
                .AddSingleton<ICampaignRollupStore>(h.Rollups)
                .AddSingleton<ICanonicalCampaignContextResolver>(new LongRestTask7ScopeResolver(f.Scope))
                .AddSingleton<ICanonicalCampaignRootEnricher>(new PassThroughRootEnricher())
                .AddSingleton<ICovenantOperationGate>(f.Gate)
                .AddSingleton<ICampaignRollupMaintenance>(new CampaignRollupTurnSnapshotTests.RefusingMaintenance())
                .AddSingleton<ISessionTurnClaimBeginStore>(h.Begin)
                .AddSingleton<ISessionTurnBeginStore>(h.Begin)
                .AddSingleton<ISessionTurnClaimCoordinator>(h.Claims)
                .AddSingleton<ISessionTurnClaimLookup>(h.Claims)
                .AddSingleton<ISessionTurnClaimReplayStore>(h.Repository)
                .AddSingleton<ICovenantAuthoritySnapshotProvider>(new CovenantAuthoritySnapshotProvider(f.Runtime))
                .AddSingleton<ILongRestSweepCoordinator>(h.Coordinator)
                .AddSingleton<ILongRestMaintenance>(h.Maintenance)
                .AddSingleton<ILongRestConfigurationProvider>(f.Epoch)
                .AddSingleton<ILongRestSweepStore>(new LongRestSweepStore(f.H.Saga.Context, f.Clock))
                .AddSingleton<ILongRestDiscoveryStore>(h.Discovery)
                .AddSingleton<ILongRunningOperationStore>(f.Operations)
                .BuildServiceProvider();

            h.Preparer = ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(h.Services);

            h.Request = new("Continue after exact protected maintenance.", Model: "model", SessionId: session,
                CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true);

            return h;
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class Task7ClaimProbe(SessionTurnClaimStore inner) : ISessionTurnClaimCoordinator, ISessionTurnClaimLookup
    {
        internal List<SessionTurnRequestIdentity> Acquisitions { get; } = [];

        public ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken ct)
        {
            Acquisitions.Add(request);

            return inner.AcquireAsync(request, ct);
        }

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken ct) => inner.MarkBegunAsync(lease, begin, ct);

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken ct) => inner.CompleteAsync(lease, outcome, ct);

        public ValueTask<Result<bool>> HasClaimAsync(Guid client, CancellationToken ct) => inner.HasClaimAsync(client, ct);

        public ValueTask<Result<SessionTurnClaim?>> FindAsync(Guid installation, Guid client, CancellationToken ct) => inner.FindAsync(installation, client, ct);
    }

    private sealed class Task7BeginProbe(GrimoireRepository inner) : ISessionTurnClaimBeginStore, ISessionTurnBeginStore
    {
        internal int Reads;

        internal int Begins;

        public ValueTask<Result<SessionTurnClaimInputSnapshot>> ReadClaimInputAsync(Guid session, CancellationToken ct)
        {
            Reads++;

            return inner.ReadClaimInputAsync(session, ct);
        }

        public ValueTask<Result<AssistantReplyBeginReceipt>> BeginClaimedAssistantReplyAsync(SessionTurnClaimLease lease,
            CanonicalCampaignContext scope, string prompt, string model, CancellationToken ct)
        {
            Begins++;

            return inner.BeginClaimedAssistantReplyAsync(lease, scope, prompt, model, ct);
        }

        public ValueTask<Result<Guid>> CreateBoundSessionAsync(CanonicalCampaignContext scope, string title, CancellationToken ct) => inner.CreateBoundSessionAsync(scope, title, ct);

        public ValueTask<Result<AssistantReplyBeginReceipt>> BeginAssistantReplyAsync(Guid session, CanonicalCampaignContext scope,
            string prompt, string model, CancellationToken ct)
        {
            Begins++;

            return inner.BeginAssistantReplyAsync(session, scope, prompt, model, ct);
        }
    }

    private sealed class Task7MaintenanceProbe(ILongRestMaintenance inner) : ILongRestMaintenance
    {
        internal int Capabilities;

        public Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operation, ArcanumInvocationContext invocation,
            SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken ct)
        {
            Capabilities++;

            return inner.CreateCapabilityAsync(operation, invocation, claim, scope, lease, ct);
        }
    }

    private sealed class Task7CoordinatorProbe(ILongRestSweepCoordinator inner) : ILongRestSweepCoordinator
    {
        internal int Resumes;

        internal int Requests;

        internal List<Guid> ResumeIds { get; } = [];

        internal List<LongRestSweepScope> Partitions { get; } = [];

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            Requests++;

            return inner.RequestAsync(scope, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct)
        {
            Requests++;

            Partitions.Add(partition);

            return inner.RequestAsync(scope, partition, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct)
        {
            Requests++;

            Partitions.Add(partition);

            return inner.RequestAsync(scope, partition, authority, ct);
        }

        public Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operation, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            Resumes++;

            ResumeIds.Add(operation);

            return inner.ResumeAsync(operation, capability, ct);
        }
    }

    private sealed class Task7DiscoveryProbe(ILongRestDiscoveryStore inner) : ILongRestDiscoveryStore
    {
        internal int ContentReads;

        public Task<Result<CovenantDigest?>> ReadNextProtectedPartitionAsync(CanonicalCampaignContext scope, CovenantDigest? after, CancellationToken ct) => inner.ReadNextProtectedPartitionAsync(scope, after, ct);

        public Task<Result<LongRestDiscoveryGeneration?>> ReadGenerationMetadataAsync(Guid operation, CancellationToken ct) => inner.ReadGenerationMetadataAsync(operation, ct);

        public Task<Result<LongRestDiscoveryGeneration>> StartOrResumeAsync(Guid operation, LongRestSweepScope scope, LongRestSweepFence fence, LongRestRecipeIdentity recipe, CancellationToken ct) => inner.StartOrResumeAsync(operation, scope, fence, recipe, ct);

        public Task<Result<LongRestSourcePage>> CaptureNextSourcePageAsync(Guid generation, long revision, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            ContentReads++;

            return inner.CaptureNextSourcePageAsync(generation, revision, capability, ct);
        }

        public Task<Result<LongRestHierarchyWork?>> ReadNextFoldAsync(Guid generation, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            ContentReads++;

            return inner.ReadNextFoldAsync(generation, capability, ct);
        }

        public Task<Result<LongRestCandidateGroup[]>> DiscoverAsync(Guid generation, LongRestTarget anchor, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            ContentReads++;

            return inner.DiscoverAsync(generation, anchor, capability, ct);
        }

        public Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryAsync(LongRestAcceptedHierarchySummary summary, LongRestMaintenanceCapability? capability, CancellationToken ct) => inner.CommitAcceptedHierarchySummaryAsync(summary, capability, ct);

        public Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryAsync(LongRestAcceptedHierarchySummary summary, LongRestDispatchCompletion completion, string owner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false) => inner.CommitAcceptedHierarchySummaryAsync(summary, completion, owner, capability, ct, committedOnly);

        public Task<Result<LongRestDiscoveryGeneration>> CommitFoldAsync(LongRestHierarchyPublication publication, LongRestMaintenanceCapability? capability, CancellationToken ct) => inner.CommitFoldAsync(publication, capability, ct);

        public Task<Result<LongRestDiscoveryGeneration>> CommitFoldAsync(LongRestHierarchyPublication publication, LongRestDispatchCompletion completion, string owner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false) => inner.CommitFoldAsync(publication, completion, owner, capability, ct, committedOnly);

        public Task<Result<LongRestDiscoveryGeneration>> PublishAsync(Guid generation, long revision, LongRestSweepFence fence, LongRestMaintenanceCapability? capability, CancellationToken ct) => inner.PublishAsync(generation, revision, fence, capability, ct);
    }

    private sealed class Task7RecordingSearch(IDivinationService inner) : IDivinationService
    {
        internal List<DivinationResult[]> Hits { get; } = [];

        private async Task<Result<DivinationResult[]>> RecordAsync(Task<Result<DivinationResult[]>> task)
        {
            Result<DivinationResult[]> result = await task;

            if (result.IsSuccess) Hits.Add(result.Value);

            return result;
        }

        public Task<Result<DivinationResult[]>> SearchAsync(string table, string key, string column, Embedding<float> embedding,
            int max, float threshold, CancellationToken ct) => RecordAsync(inner.SearchAsync(table, key, column, embedding, max, threshold, ct));

        public Task<Result<DivinationResult[]>> SearchScopedAsync(string table, string key, string column, string ownerTable,
            string ownerKey, string scopeColumn, string scopeValue, Embedding<float> embedding, int max, float threshold, CancellationToken ct) =>
            RecordAsync(inner.SearchScopedAsync(table, key, column, ownerTable, ownerKey, scopeColumn, scopeValue, embedding, max, threshold, ct));

        public Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(string table, string key, string column,
            DivinationCampaignScope scope, Embedding<float> embedding, int max, float threshold, CancellationToken ct) =>
            RecordAsync(inner.SearchCampaignScopedAsync(table, key, column, scope, embedding, max, threshold, ct));
    }
}
