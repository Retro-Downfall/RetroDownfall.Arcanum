using System.Collections.Immutable;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildSignetAuthorityTests
{
    [Fact]
    public void Signet_authority_has_a_host_owned_runtime_implementation()
    {
        Type[] implementations = typeof(GuildStore).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ISignetAuthority).IsAssignableFrom(type)).ToArray();

        Assert.Single(implementations);

        Assert.False(implementations[0].IsPublic);
    }

    [Fact]
    public async Task Issuance_stores_only_a_digest_and_a_copied_handle_cannot_authenticate()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        Assert.True((await f.Authority.VerifyToolAsync(execution, Fixture.Tool, true, default)).IsSuccess);

        GuildExecutionHandle copy = new(execution.SignetId, execution.AgentId, execution.RunId, execution.QuestId,
            execution.RootQuestId, execution.CredentialGeneration, execution.RevocationGeneration);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch,
            (await f.Authority.VerifyToolAsync(copy, Fixture.Tool, true, default)).Error.Code);

        Assert.Equal(32L, await f.ScalarAsync("SELECT length(TokenDigest) FROM guild_signets"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT CredentialGeneration FROM guild_signets"));
    }

    [Fact]
    public async Task Unrecognized_admission_cannot_turn_an_operator_label_into_root_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildSignetBinding binding = f.Binding();

        GuildAdmissionContext untrusted = new(f.Operator, Guid.NewGuid(), new(Guid.NewGuid(), "intent"));

        Result<GuildExecutionHandle> result = await f.Authority.MintAsync(untrusted, binding, default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Intermediate_revocation_denies_its_subtree_and_preserves_siblings()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        GuildExecutionHandle child = await f.ChildAsync(root);

        GuildExecutionHandle grandchild = await f.ChildAsync(child);

        GuildExecutionHandle sibling = await f.ChildAsync(root);

        Guid key = Guid.NewGuid();

        GuildRevokeSignetRequest request = new(key, 1, GuildCancellationReason.OperatorRequested);

        Result<SignetRevocationDto> revoked = await f.Authority.RevokeAsync(f.Operator, new(key, Digest("revoke-child")), child.SignetId, request, default);

        Assert.True(revoked.IsSuccess, revoked.Error.Message);

        Assert.True(revoked.Value.CancellationRequested);

        Assert.False(revoked.Value.CancellationConverged);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await f.Authority.VerifyToolAsync(grandchild, Fixture.Tool, true, default)).Error.Code);

        Assert.True((await f.Authority.VerifyToolAsync(sibling, Fixture.Tool, true, default)).IsSuccess);

        Result<SignetRevocationDto> replay = await f.Authority.RevokeAsync(f.Operator, new(key, Digest("revoke-child")), child.SignetId, request, default);

        Assert.Equal(revoked.Value, replay.Value);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict,
            (await f.Authority.RevokeAsync(f.Operator, new(key, Digest("different-intent")), child.SignetId, request, default)).Error.Code);
    }

    [Fact]
    public async Task Live_policy_removal_and_expiry_refuse_previously_retained_execution()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync(f.Grant with { ExpiresAtUtc = f.Time.GetUtcNow().AddMinutes(1) });

        f.Policy.HostGrant = f.Grant with { Tools = [] };

        Assert.Equal(ErrorCodes.Guild.ToolNotGranted, (await f.Authority.VerifyToolAsync(execution, Fixture.Tool, true, default)).Error.Code);

        f.Policy.HostGrant = f.Grant;

        f.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ErrorCodes.Guild.AuthorityExpired, (await f.Authority.VerifyToolAsync(execution, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Recovery_rotates_the_same_binding_once_and_invalidates_retained_credentials()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle original = await f.RootAsync();

        GuildRecoveryContext recovery = new(original.QuestId, Guid.NewGuid(), 1);

        f.Policy.Recoveries.Add(recovery);

        using SignetAuthority restarted = f.NewAuthority();

        Result<GuildExecutionHandle> result = await restarted.RebindAsync(recovery, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(original.RunId, result.Value.RunId);

        Assert.Equal(original.QuestId, result.Value.QuestId);

        Assert.Equal(2, result.Value.CredentialGeneration);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, (await f.Authority.VerifyToolAsync(original, Fixture.Tool, true, default)).Error.Code);

        Assert.True((await restarted.VerifyToolAsync(result.Value, Fixture.Tool, true, default)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, (await restarted.RebindAsync(recovery, default)).Error.Code);
    }

    [Fact]
    public async Task Restored_inert_authority_cannot_verify_or_rebind()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        await f.ExecuteAsync("UPDATE guild_signets SET RestoredInert=1,Revision=Revision+1");

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await f.Authority.VerifyToolAsync(execution, Fixture.Tool, true, default)).Error.Code);

        GuildRecoveryContext recovery = new(execution.QuestId, Guid.NewGuid(), 2);

        f.Policy.Recoveries.Add(recovery);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await f.Authority.RebindAsync(recovery, default)).Error.Code);
    }

    [Fact]
    public async Task Revocation_waits_for_an_admitted_handoff_and_no_later_handoff_runs()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<int>> admitted = f.Authority.AdmitToolAsync(execution, Fixture.Tool, true, async (_, _) =>
        {
            entered.SetResult();

            await release.Task;

            return Result<int>.Success(17);
        }, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Guid key = Guid.NewGuid();

        Task<Result<SignetRevocationDto>> revoke = f.Authority.RevokeAsync(f.Operator, new(key, Digest("revoke")), execution.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default);

        Assert.False(revoke.IsCompleted);

        release.SetResult();

        Assert.Equal(17, (await admitted).Value);

        Assert.True((await revoke).IsSuccess);

        bool ran = false;

        Result<int> refused = await f.Authority.AdmitToolAsync(execution, Fixture.Tool, true, (_, _) =>
        {
            ran = true;

            return Task.FromResult(Result<int>.Success(1));
        }, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, refused.Error.Code);

        Assert.False(ran);
    }

    [Fact]
    public async Task Exact_capability_identity_and_empty_tool_grants_cannot_be_repaired_by_host_eligibility()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        GuildToolGrant[] other =
        [
            Fixture.Tool with { SourceKind = GuildToolSourceKind.InternalMcp },
            Fixture.Tool with { SourceRegistrationId = "another-server" },
            Fixture.Tool with { Name = "Test_read" },
        ];

        foreach (GuildToolGrant tool in other)
        {
            Assert.Equal(ErrorCodes.Guild.ToolNotGranted, (await f.Authority.VerifyToolAsync(execution, tool, true, default)).Error.Code);
        }

        GuildExecutionHandle empty = await f.RootAsync(f.Grant with { Tools = [] });

        Assert.Equal(ErrorCodes.Guild.ToolNotGranted, (await f.Authority.VerifyToolAsync(empty, Fixture.Tool, true, default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.ToolBoundaryUnavailable, (await f.Authority.VerifyToolAsync(execution, Fixture.Tool, false, default)).Error.Code);
    }

    [Fact]
    public async Task Retained_tool_wrapper_rechecks_real_revocation_and_current_grant_before_unavailable_boundary()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        int effects = 0;

        AIFunction retained = new GuildToolInvocationContext(execution, f.Authority, _ => true)
            .Bind(new TestFunction(() => effects++), Fixture.Tool);

        f.Policy.HostGrant = f.Grant with { Tools = [] };

        Assert.Contains(ErrorCodes.Guild.ToolNotGranted, Assert.IsType<string>(await retained.InvokeAsync(new AIFunctionArguments(), default)));

        f.Policy.HostGrant = f.Grant;

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Operator, new(key, Digest("revoke-retained")), execution.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        Assert.Contains(ErrorCodes.Guild.AuthorityRevoked, Assert.IsType<string>(await retained.InvokeAsync(new AIFunctionArguments(), default)));

        Assert.Equal(0, effects);
    }

    private static string Digest(string intent) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent)));

    [Fact]
    public async Task Remote_child_uses_the_host_bound_remote_edge_without_borrowing_local_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildGrant remote = f.Grant with { Delegation = [GuildDelegationKind.Remote] };

        f.Policy.HostGrant = remote;

        GuildExecutionHandle root = await f.RootAsync(remote);

        GuildExecutionHandle child = await f.ChildAsync(root, remote, GuildSignetIssuanceEdge.Remote);

        Assert.True((await f.Authority.VerifyToolAsync(child, Fixture.Tool, true, default)).IsSuccess);
    }

    [Fact]
    public async Task Cohort_edge_requires_cohort_and_the_exact_transport_right()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildGrant grant = f.Grant with { Delegation = [GuildDelegationKind.Local] };

        GuildExecutionHandle root = await f.RootAsync(grant);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Operator.PrincipalId!.Value, Guid.NewGuid(), f.Policy.InstallationGeneration, root);

        GuildAdmissionContext admission = f.Admission(caller);

        f.Policy.NextEdge = GuildSignetIssuanceEdge.LocalCohort;

        Result<GuildExecutionHandle> refused = await f.Authority.AttenuateAsync(admission, root, f.Binding(grant, root), new(), default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, refused.Error.Code);
    }

    [Fact]
    public async Task Distinct_child_session_owners_reopen_under_the_same_canonical_campaign()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid parentSession = Guid.NewGuid();

        Guid childSession = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, parentSession, childSession);

        f.Policy.NextOwners = new(parentSession, campaign, null);

        GuildExecutionHandle root = await f.RootAsync();

        f.Policy.NextOwners = new(childSession, campaign, null);

        GuildExecutionHandle child = await f.ChildAsync(root);

        await using (SqliteConnection reopened = await f.OpenAsync())
        {
            var result = await GrimoireSchemaTestInstaller.InstallAsync(reopened, 768, default);

            Assert.True(result.GuildCanonical.IsHealthy);
        }

        Assert.True((await f.Authority.VerifyToolAsync(child, Fixture.Tool, true, default)).IsSuccess);

        await f.DeleteSessionAsync(childSession);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await f.Authority.VerifyToolAsync(child, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Stored_unknown_grant_constraints_fail_closed_instead_of_being_ignored()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        string grant = Assert.IsType<string>(await f.ScalarAsync("SELECT GrantJson FROM guild_signets"));

        await f.ReplaceStoredGrantAsync(root.SignetId, grant[..^1] + ",\"unrecognizedRestriction\":true}");

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Stored_oversize_grant_is_refused_before_its_json_is_materialized()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        string grant = Assert.IsType<string>(await f.ScalarAsync("SELECT GrantJson FROM guild_signets"));

        await f.ReplaceStoredGrantAsync(root.SignetId, grant[..^1] + ",\"padding\":\"" + new string('x', 70_000) + "\"}");

        Assert.Equal(ErrorCodes.Guild.PayloadTooLarge, (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Same_process_rotation_clears_the_retired_raw_credential_buffer()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        IDictionary registry = Assert.IsAssignableFrom<IDictionary>(typeof(SignetAuthority)
            .GetField("_credentials", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Authority));

        object credential = registry[root]!;

        byte[] retiredBuffer = Assert.IsType<byte[]>(credential.GetType().GetField("_raw", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(credential));

        Assert.Equal(32, retiredBuffer.Length);

        GuildRecoveryContext recovery = new(root.QuestId, Guid.NewGuid(), 1);

        f.Policy.Recoveries.Add(recovery);

        Assert.True((await f.Authority.RebindAsync(recovery, default)).IsSuccess);

        Assert.True(retiredBuffer.All(value => value == 0), "The retired raw credential must be cleared after successful rotation.");

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Noncanonical_mutation_digest_is_refused_before_any_revocation_write()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        Guid key = Guid.NewGuid();

        Result<SignetRevocationDto> refused = await f.Authority.RevokeAsync(f.Operator, new(key, "not-a-sha256-digest"), root.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, refused.Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT Revoked FROM guild_signets"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_signet_revocations"));
    }

    [Fact]
    public async Task Current_policy_intersects_budget_memory_and_delegation_without_reinterpreting_global()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildMemoryGrant global = new(GuildMemoryStore.Saga, GuildMemoryOperation.Read, new(GuildMemoryScopeKind.Global));

        GuildGrant issued = f.Grant with
        {
            Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)),
            Memory = [global],
            Delegation = [GuildDelegationKind.Local, GuildDelegationKind.Remote],
        };

        f.Policy.HostGrant = issued;

        GuildExecutionHandle root = await f.RootAsync(issued);

        f.Policy.HostGrant = issued with
        {
            Budget = new(new(GuildBudgetMode.Finite, 1.25m), new(GuildBudgetMode.Finite, 0)),
            Memory = [],
            Delegation = [GuildDelegationKind.Remote],
        };

        Result<GuildVerifiedAuthority> verified = await f.Authority.VerifyAsync(root, new(GuildAuthorityAction.Start, root.QuestId), default);

        Assert.True(verified.IsSuccess, verified.Error.Message);

        Assert.Equal(1.25m, verified.Value.EffectiveGrant!.Budget.Usd.Value);

        Assert.Equal(0L, verified.Value.EffectiveGrant.Budget.Tokens.Value);

        Assert.Empty(verified.Value.EffectiveGrant.Memory);

        Assert.Equal(new[] { GuildDelegationKind.Remote }, verified.Value.EffectiveGrant.Delegation.ToArray());
    }

    [Fact]
    public async Task Owner_tombstone_denies_a_still_present_owner_before_cleanup_and_recovery()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        GuildExecutionHandle root = await f.RootAsync();

        await f.ExecuteAsync($"INSERT INTO owner_deletion_events(OwnerKindCode,OwnerId,DeletedAtUtc) VALUES(2,'{session.ToString("D").ToUpperInvariant()}','2026-11-05T00:00:00.0000000Z')");

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);

        GuildRecoveryContext recovery = new(root.QuestId, Guid.NewGuid(), 1);

        f.Policy.Recoveries.Add(recovery);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, (await f.Authority.RebindAsync(recovery, default)).Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT Revoked FROM guild_signets"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM Sessions"));
    }

    [Fact]
    public async Task Authority_epoch_rotation_invalidates_live_handles_and_recovery_without_row_flags()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        await f.ExecuteAsync($"UPDATE guild_signet_authority_state SET Epoch='{Guid.NewGuid().ToString("D").ToUpperInvariant()}' WHERE StateKey=1");

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);

        GuildRecoveryContext recovery = new(root.QuestId, Guid.NewGuid(), 1);

        f.Policy.Recoveries.Add(recovery);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await f.Authority.RebindAsync(recovery, default)).Error.Code);

        Assert.Equal(0L, await f.ScalarAsync("SELECT RestoredInert+Revoked FROM guild_signets"));
    }

    [Fact]
    public async Task Oversized_peer_metadata_is_refused_before_managed_materialization()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        await f.ReplaceStoredPeerAsync(root.SignetId, new string('p', 70_000));

        Assert.Equal(ErrorCodes.Guild.PayloadTooLarge, (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_fixed_authority_blobs_are_rejected_before_their_managed_getters(bool credentialDigest)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        // A valid catalog does not prove that imported rows respected its CHECK constraints.
        await f.ReplaceStoredBlobAsync(root.SignetId, credentialDigest);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable,
            (await f.Authority.VerifyToolAsync(root, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Corrupt_ancestor_generation_cannot_supply_live_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        GuildExecutionHandle child = await f.ChildAsync(root);

        await f.ReplaceStoredRevocationGenerationAsync(root.SignetId, -1);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked,
            (await f.Authority.VerifyToolAsync(child, Fixture.Tool, true, default)).Error.Code);
    }

    [Fact]
    public async Task Concurrent_recovery_claims_rotate_once_and_return_no_losing_execution()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle original = await f.RootAsync();

        GuildRecoveryContext firstClaim = new(original.QuestId, Guid.NewGuid(), 1);

        GuildRecoveryContext secondClaim = new(original.QuestId, Guid.NewGuid(), 1);

        f.Policy.Recoveries.UnionWith([firstClaim, secondClaim]);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        f.Policy.RecoveryAdmissionHook = async (claim, _, token) =>
        {
            if (ReferenceEquals(claim, firstClaim))
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);
            }
        };

        Task<Result<GuildExecutionHandle>> first = f.Authority.RebindAsync(firstClaim, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<Result<GuildExecutionHandle>> second;

        try
        {
            second = f.Authority.RebindAsync(secondClaim, default);

            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Result<GuildExecutionHandle>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(results[0].IsSuccess, results[0].Error.Message);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, results[1].Error.Code);

        Assert.Equal(2, results[0].Value.CredentialGeneration);

        Assert.Equal(2L, await f.ScalarAsync("SELECT CredentialGeneration FROM guild_signets"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT Revision FROM guild_signets"));

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, (await f.Authority.VerifyToolAsync(original, Fixture.Tool, true, default)).Error.Code);

        Assert.True((await f.Authority.VerifyToolAsync(results[0].Value, Fixture.Tool, true, default)).IsSuccess);
    }

    [Fact]
    public async Task Concurrent_revocations_commit_one_generation_and_one_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int claims = 0;

        f.Policy.RevocationAdmissionHook = async (_, _, token) =>
        {
            if (Interlocked.Increment(ref claims) == 1)
            {
                entered.TrySetResult();

                await release.Task.WaitAsync(token);
            }
        };

        Guid firstKey = Guid.NewGuid();

        Guid secondKey = Guid.NewGuid();

        Task<Result<SignetRevocationDto>> first = f.Authority.RevokeAsync(f.Operator, new(firstKey, Digest("first-revoke")), execution.SignetId,
            new(firstKey, 1, GuildCancellationReason.OperatorRequested), default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<Result<SignetRevocationDto>> second;

        try
        {
            second = f.Authority.RevokeAsync(f.Operator, new(secondKey, Digest("second-revoke")), execution.SignetId,
                new(secondKey, 1, GuildCancellationReason.OperatorRequested), default);

            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        Result<SignetRevocationDto>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(results[0].IsSuccess, results[0].Error.Message);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, results[1].Error.Code);

        Assert.Equal(1, results[0].Value.Generation);

        Assert.Equal(1L, await f.ScalarAsync("SELECT RevocationGeneration FROM guild_signets"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT Revision FROM guild_signets"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signet_revocations"));

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, (await f.Authority.VerifyToolAsync(execution, Fixture.Tool, true, default)).Error.Code);
    }

    [Theory]
    [InlineData("RunId")]
    [InlineData("QuestId")]
    [InlineData("IssuerSignetId")]
    public async Task Persisted_execution_binding_corruption_refuses_the_actual_effect_callback(string axis)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle root = await f.RootAsync();

        GuildExecutionHandle child = await f.ChildAsync(root);

        Guid replacement = axis == "IssuerSignetId" ? (await f.ChildAsync(root)).SignetId : Guid.NewGuid();

        await f.ReplaceStoredBindingAsync(child.SignetId, axis, replacement);

        bool called = false;

        Result<int> result = await f.Authority.AdmitToolAsync(child, Fixture.Tool, true, (_, _) =>
        {
            called = true;

            return Task.FromResult(Result<int>.Success(1));
        }, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, result.Error.Code);

        Assert.False(called);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unhealthy_Guild_tier_refuses_before_the_effect_callback(bool missing)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        await f.ExecuteAsync(missing ? "DROP TABLE guild_signet_authority_state"
            : "UPDATE grimoire_feature_schemas SET HealthCode=2 WHERE FamilyCode=2 AND TransactionTierCode=3");

        bool called = false;

        Result<int> result = await f.Authority.AdmitToolAsync(execution, Fixture.Tool, true, (_, _) =>
        {
            called = true;

            return Task.FromResult(Result<int>.Success(1));
        }, default);

        Assert.Equal(ErrorCodes.Guild.Unavailable, result.Error.Code);

        Assert.False(called);
    }

    [Fact]
    public async Task The_private_credential_canary_is_hash_only_in_real_durable_authority_fields()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle execution = await f.RootAsync();

        IDictionary registry = Assert.IsAssignableFrom<IDictionary>(typeof(SignetAuthority)
            .GetField("_credentials", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Authority));

        object credential = registry[execution]!;

        byte[] canary = Assert.IsType<byte[]>(credential.GetType().GetField("_raw", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(credential)).ToArray();

        char[] presentation = Assert.IsType<char[]>(credential.GetType().GetField("_presentation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(credential)).ToArray();

        Assert.Equal(32, canary.Length);

        Assert.Equal(43, presentation.Length);

        Assert.True(presentation.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
            "The private presentation must use the canonical base64url alphabet without padding.");

        byte[] digest = Assert.IsType<byte[]>(await f.ScalarAsync("SELECT TokenDigest FROM guild_signets"));

        Assert.Equal(SHA256.HashData(canary), digest);

        Assert.False(canary.AsSpan().SequenceEqual(digest), "The durable digest must not contain the raw credential.");

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Operator, new(key, Digest("canary-revocation")), execution.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        await f.AssertNoDurableCredentialAsync(canary, presentation);

        CryptographicOperations.ZeroMemory(canary);

        Array.Clear(presentation);
    }

    private sealed class TestFunction(Action effect) : AIFunction
    {
        public override string Name => "test_read";

        public override JsonElement JsonSchema
        {
            get
            {
                using JsonDocument document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");

                return document.RootElement.Clone();
            }
        }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            effect();

            return ValueTask.FromResult<object?>("unexpected-effect");
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal static readonly GuildToolGrant Tool = new(GuildToolSourceKind.BuiltIn, "arcanum", "test_read");

        private readonly EvolutionScratchDatabase _file;

        internal string ConnectionString => _file.ConnectionString;

        private readonly FixtureOrdinaryConnectionFactory _connections;

        private readonly GrimoireConnectionAdmissionGate _gate;

        internal IGrimoireConnectionAdmissionGate Gate => _gate;

        internal readonly FakeTimeProvider Time = new();

        internal GuildGrant Grant { get; } = new([], [Tool], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null);

        internal HostPolicy Policy { get; }

        internal GuildCallerContext Operator { get; }

        internal SignetAuthority Authority { get; }

        private Fixture(EvolutionScratchDatabase file)
        {
            _file = file;

            _connections = new(file.ConnectionString);

            _gate = new(Time);

            Policy = new(Grant);

            Operator = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), Policy.InstallationGeneration);

            Policy.Operator = Operator;

            Authority = NewAuthority();
        }

        internal SignetAuthority NewAuthority() => new(new(_connections), _gate, Policy, Time);

        internal static async Task<Fixture> CreateAsync()
        {
            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(default);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

            return new(file);
        }

        internal GuildSignetBinding Binding(GuildGrant? grant = null, GuildExecutionHandle? parent = null)
        {
            Guid quest = Guid.NewGuid();

            return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quest, parent?.RootQuestId ?? quest,
                parent?.SignetId, grant ?? Grant, Time.GetUtcNow(), Operator.PrincipalId!.Value);
        }

        internal async Task<GuildExecutionHandle> RootAsync(GuildGrant? grant = null)
        {
            GuildSignetBinding binding = Binding(grant);

            GuildAdmissionContext admission = Admission(Operator);

            Result<GuildExecutionHandle> result = await Authority.MintAsync(admission, binding, default);

            Assert.True(result.IsSuccess, result.Error.Message);

            return result.Value;
        }

        internal async Task<GuildExecutionHandle> ChildAsync(GuildExecutionHandle parent, GuildGrant? grant = null,
            GuildSignetIssuanceEdge edge = GuildSignetIssuanceEdge.Local)
        {
            GuildCallerContext caller = new(GuildCallerKind.Run, Operator.PrincipalId!.Value, Guid.NewGuid(), Policy.InstallationGeneration, parent);

            Policy.NextEdge = edge;

            Result<GuildExecutionHandle> result = await Authority.AttenuateAsync(Admission(caller), parent, Binding(grant, parent), new(), default);

            Assert.True(result.IsSuccess, result.Error.Message);

            return result.Value;
        }

        internal GuildAdmissionContext Admission(GuildCallerContext caller)
        {
            GuildAdmissionContext context = new(caller, Guid.NewGuid(), new(Guid.NewGuid(), Digest("intent")));

            Policy.Admissions.Add(context);

            return context;
        }

        internal Task<SqliteConnection> OpenAsync() => _file.OpenAsync(default);

        internal async Task SeedOwnersAsync(Guid campaign, Guid first, Guid second)
        {
            await using SqliteConnection connection = await OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt)
                VALUES($campaign,'Guild','guild','/fixture-guild',0,'{}',$now,$now);
                INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES($first,$campaign,$now,$now),($second,$campaign,$now,$now);
                """;

            command.Parameters.AddWithValue("$campaign", campaign.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$first", first.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$second", second.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$now", UtcInstantText.Format(Time.GetUtcNow()));

            _ = await command.ExecuteNonQueryAsync();
        }

        internal Task ReplaceStoredGrantAsync(Guid signet, string grant) => ReplaceStoredAsync(signet,
            "UPDATE guild_signets SET GrantJson=$value WHERE SignetId=$id", grant);

        internal Task ReplaceStoredPeerAsync(Guid signet, string peer) => ReplaceStoredAsync(signet,
            "UPDATE guild_signets SET PeerId=$value,PeerGrantRevision='AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' WHERE SignetId=$id", peer);

        internal Task ReplaceStoredBlobAsync(Guid signet, bool credentialDigest) => ReplaceStoredAsync(signet,
            credentialDigest ? "UPDATE guild_signets SET TokenDigest=$value WHERE SignetId=$id"
                : "UPDATE guild_signets SET WorkspaceRootIdentity=$value WHERE SignetId=$id", new byte[70_000], ignoreChecks: true);

        internal Task ReplaceStoredRevocationGenerationAsync(Guid signet, long generation) => ReplaceStoredAsync(signet,
            "UPDATE guild_signets SET RevocationGeneration=$value WHERE SignetId=$id", generation, ignoreChecks: true);

        internal Task ReplaceStoredBindingAsync(Guid signet, string axis, Guid replacement) => ReplaceStoredAsync(signet,
            axis switch
            {
                "RunId" => "UPDATE guild_signets SET RunId=$value WHERE SignetId=$id",
                "QuestId" => "UPDATE guild_signets SET QuestId=$value WHERE SignetId=$id",
                "IssuerSignetId" => "UPDATE guild_signets SET IssuerSignetId=$value WHERE SignetId=$id",
                _ => throw new ArgumentOutOfRangeException(nameof(axis)),
            }, replacement.ToString("D").ToUpperInvariant());

        private async Task ReplaceStoredAsync(Guid signet, string mutation, object value, bool ignoreChecks = false)
        {
            await using SqliteConnection connection = await OpenAsync();

            await using SqliteTransaction transaction = connection.BeginTransaction();

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            if (ignoreChecks)
            {
                command.CommandText = "PRAGMA ignore_check_constraints=ON";

                _ = await command.ExecuteNonQueryAsync();
            }

            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='guild_signets_guard_update'";

            string trigger = Assert.IsType<string>(await command.ExecuteScalarAsync());

            command.CommandText = "DROP TRIGGER guild_signets_guard_update";

            _ = await command.ExecuteNonQueryAsync();

            command.CommandText = mutation;

            command.Parameters.AddWithValue("$value", value);

            command.Parameters.AddWithValue("$id", signet.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();

            command.CommandText = trigger;

            command.Parameters.Clear();

            _ = await command.ExecuteNonQueryAsync();

            if (ignoreChecks)
            {
                command.CommandText = "PRAGMA ignore_check_constraints=OFF";

                _ = await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        internal async Task DeleteSessionAsync(Guid session)
        {
            await using SqliteConnection connection = await OpenAsync();

            using CovenantSqliteAuthorizationScope deletion = CovenantSqliteConnectionInitializer.Instance.Authorize(connection,
                CovenantSqliteAuthorizationKind.SessionRetention);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "DELETE FROM Sessions WHERE Id=$id";

            command.Parameters.AddWithValue("$id", session.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync();
        }

        internal async Task<object?> ScalarAsync(string sql)
        {
            await using SqliteConnection connection = await _file.OpenAsync(default);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            return await command.ExecuteScalarAsync();
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteConnection connection = await _file.OpenAsync(default);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        internal async Task AssertNoDurableCredentialAsync(byte[] raw, char[] presentation)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(presentation);

            await using SqliteConnection connection = await OpenAsync();

            foreach (string table in new[] { "guild_signets", "guild_signet_revocations", "guild_content" })
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = $"SELECT * FROM {table}";

                await using SqliteDataReader reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    for (int column = 0; column < reader.FieldCount; column++)
                    {
                        byte[]? bytes = reader.GetValue(column) switch
                        {
                            byte[] blob => blob,
                            string text => Encoding.UTF8.GetBytes(text),
                            _ => null,
                        };

                        Assert.True(bytes is null || bytes.AsSpan().IndexOf(raw) < 0,
                            "A durable authority field must not contain the raw credential canary.");

                        Assert.True(bytes is null || bytes.AsSpan().IndexOf(encoded) < 0,
                            "A durable authority field must not contain the base64url credential canary.");
                    }
                }
            }

            CryptographicOperations.ZeroMemory(encoded);
        }

        public ValueTask DisposeAsync()
        {
            Authority.Dispose();

            _file.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    internal sealed class HostPolicy(GuildGrant grant) : IGuildSignetHostPolicy
    {
        public Guid InstallationGeneration { get; } = Guid.NewGuid();

        internal GuildGrant HostGrant { get; set; } = grant;

        internal GuildCallerContext? Operator { get; set; }

        internal HashSet<GuildAdmissionContext> Admissions { get; } = [];

        internal HashSet<GuildRecoveryContext> Recoveries { get; } = [];

        internal GuildSignetIssuanceEdge NextEdge { get; set; } = GuildSignetIssuanceEdge.Local;

        internal GuildSignetOwnerBinding NextOwners { get; set; } = new(null, null, null);

        internal Func<GuildRecoveryContext, GuildSignetRecord, CancellationToken, Task>? RecoveryAdmissionHook { get; set; }

        internal Func<GuildCallerContext, GuildSignetRecord, CancellationToken, Task>? RevocationAdmissionHook { get; set; }

        public Task<Result<GuildSignetAdmissionEvidence>> AuthorizeAdmissionAsync(GuildAdmissionContext admission, GuildSignetBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult(Admissions.Contains(admission) ? Result<GuildSignetAdmissionEvidence>.Success(new(NextOwners,
                    binding.IssuerSignetId is null ? GuildSignetIssuanceEdge.Root : NextEdge))
                : Result<GuildSignetAdmissionEvidence>.Failure(new(ErrorCodes.Guild.Forbidden, "Unrecognized host admission.")));

        public Task<Result<GuildSignetPolicyEvidence>> ResolveCurrentAsync(ImmutableArray<GuildSignetRecord> lineage, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GuildSignetPolicyEvidence>.Success(new(HostGrant, new([], []))));

        public async Task<Result> AuthorizeRevocationAsync(GuildCallerContext caller, GuildSignetRecord target, CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(caller, Operator))
            {
                return new Error(ErrorCodes.Guild.Forbidden, "Unrecognized operator.");
            }

            if (RevocationAdmissionHook is { } hook)
            {
                await hook(caller, target, cancellationToken);
            }

            return Result.Success();
        }

        public async Task<Result> AuthorizeRecoveryAsync(GuildRecoveryContext recovery, GuildSignetRecord target, CancellationToken cancellationToken)
        {
            if (!Recoveries.Contains(recovery))
            {
                return new Error(ErrorCodes.Guild.Forbidden, "Unrecognized recovery owner.");
            }

            if (RecoveryAdmissionHook is { } hook)
            {
                await hook(recovery, target, cancellationToken);
            }

            return Result.Success();
        }
    }
}
