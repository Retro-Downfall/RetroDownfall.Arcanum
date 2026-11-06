using System.Collections;
using System.Reflection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildSignetAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quest_and_signet_share_commit_or_rollback(bool commit)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildSignetBinding binding = f.Binding();

        GuildAdmissionContext admission = f.Admission(f.Operator);

        bool entered = false;

        Result<bool> result = await f.Authority.InQuestTransactionAsync<bool>(binding.RootQuestId,
            async (connection, transaction, authority, ct) =>
            {
                entered = true;

                Result<GuildSignetRecord> issued = await authority.IssueAsync(admission, binding, null, null, ct);

                Assert.True(issued.IsSuccess, issued.Error.Message);

                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = "SELECT count(*) FROM guild_signets";

                Assert.Equal(1L, await command.ExecuteScalarAsync(ct));

                return commit ? Result<bool>.Success(true) : Result<bool>.Failure(new("fixture.rollback", "Roll back the admission."));
            }, default);

        Assert.True(entered);

        Assert.Equal(commit, result.IsSuccess);

        Assert.Equal(commit ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Transaction_participant_checks_live_parent_and_refuses_a_revoked_parent()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle parent = await f.RootAsync();

        GuildSignetBinding child = f.Binding(parent: parent);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Operator.PrincipalId!.Value, Guid.NewGuid(),
            f.Policy.InstallationGeneration, parent);

        GuildAdmissionContext admission = f.Admission(caller);

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Operator, new(key, Digest("revoke")), parent.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        Result<GuildSignetRecord> result = await f.Authority.InQuestTransactionAsync<GuildSignetRecord>(parent.RootQuestId,
            (connection, transaction, authority, ct) => authority.IssueAsync(admission, child, parent, new(), ct), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, result.Error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Transaction_participant_rejects_root_mismatch_for_issue_and_live_verification()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle parent = await f.RootAsync();

        GuildSignetBinding binding = f.Binding();

        Result<bool> result = await f.Authority.InQuestTransactionAsync<bool>(Guid.NewGuid(), async (_, _, scope, ct) =>
        {
            Result<GuildSignetRecord> issued = await scope.IssueAsync(f.Admission(f.Operator), binding, null, null, ct);

            Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, issued.Error.Code);

            Result<GuildGrant> verified = await scope.VerifyAsync(parent, ct);

            Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, verified.Error.Code);

            return true;
        }, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Transaction_participant_verifies_live_policy_and_refuses_a_deleted_owner()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, session, Guid.NewGuid());

        f.Policy.NextOwners = new(session, campaign, null);

        GuildExecutionHandle parent = await f.RootAsync();

        f.Policy.HostGrant = f.Grant with { Tools = [] };

        Result<GuildGrant> narrowed = await f.Authority.InQuestTransactionAsync<GuildGrant>(parent.RootQuestId,
            (_, _, scope, ct) => scope.VerifyAsync(parent, ct), default);

        Assert.True(narrowed.IsSuccess, narrowed.Error.Message);

        Assert.Empty(narrowed.Value.Tools);

        await f.DeleteSessionAsync(session);

        Result<GuildGrant> absent = await f.Authority.InQuestTransactionAsync<GuildGrant>(parent.RootQuestId,
            (_, _, scope, ct) => scope.VerifyAsync(parent, ct), default);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, absent.Error.Code);

        GuildSignetBinding orphan = f.Binding();

        Result<GuildSignetRecord> refused = await f.Authority.InQuestTransactionAsync<GuildSignetRecord>(orphan.RootQuestId,
            (_, _, scope, ct) => scope.IssueAsync(f.Admission(f.Operator), orphan, null, null, ct), default);

        Assert.Equal(ErrorCodes.Guild.OwnerUnavailable, refused.Error.Code);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Quest_credentials_become_usable_only_after_commit_and_scope_cannot_be_reused()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildSignetBinding binding = f.Binding();

        SignetAuthority.QuestTransaction? retained = null;

        IDictionary credentials = QuestCredentials(f.Authority);

        Result<GuildSignetRecord> result = await f.Authority.InQuestTransactionAsync<GuildSignetRecord>(binding.RootQuestId,
            async (_, _, scope, ct) =>
            {
                retained = scope;

                Result<GuildSignetRecord> issued = await scope.IssueAsync(f.Admission(f.Operator), binding, null, null, ct);

                Assert.True(issued.IsSuccess, issued.Error.Message);

                Assert.Empty(credentials);

                return issued;
            }, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        GuildExecutionHandle handle = Assert.Single(credentials.Keys.Cast<GuildExecutionHandle>());

        Assert.Equal(binding.SignetId, handle.SignetId);

        Assert.True((await f.Authority.VerifyAsync(handle, new(GuildAuthorityAction.Start, handle.QuestId), default)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, (await retained!.VerifyAsync(handle, default)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, (await retained.IssueAsync(f.Admission(f.Operator), f.Binding(), null, null, default)).Error.Code);
    }

    [Fact]
    public async Task Quest_child_attenuation_uses_the_live_parent_and_registers_after_commit()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle parent = await f.RootAsync();

        GuildSignetBinding binding = f.Binding(parent: parent);

        GuildCallerContext caller = new(GuildCallerKind.Run, f.Operator.PrincipalId!.Value, Guid.NewGuid(),
            f.Policy.InstallationGeneration, parent);

        Result<GuildSignetRecord> result = await f.Authority.InQuestTransactionAsync<GuildSignetRecord>(parent.RootQuestId,
            async (_, _, scope, ct) =>
            {
                Assert.True((await scope.VerifyAsync(parent, ct)).IsSuccess);

                Result<GuildSignetRecord> issued = await scope.IssueAsync(f.Admission(caller), binding, parent, new(), ct);

                Assert.True(issued.IsSuccess, issued.Error.Message);

                Assert.Single(QuestCredentials(f.Authority));

                return issued;
            }, default);

        Assert.True(result.IsSuccess, result.Error.Message);

        GuildExecutionHandle child = Assert.Single(QuestCredentials(f.Authority).Keys.Cast<GuildExecutionHandle>(),
            handle => handle.SignetId == binding.SignetId);

        Assert.True((await f.Authority.VerifyAsync(child, new(GuildAuthorityAction.Start, binding.QuestId), default)).IsSuccess);

        Assert.Equal(parent.SignetId, result.Value.Binding.IssuerSignetId);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task Quest_failure_disposes_pending_credentials_and_rolls_back_signet(string failure)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildSignetBinding binding = f.Binding();

        using CancellationTokenSource cancel = new();

        byte[]? raw = null;

        char[]? presentation = null;

        SignetAuthority.QuestTransaction? retained = null;

        async Task<Result<bool>> Run() => await f.Authority.InQuestTransactionAsync<bool>(binding.RootQuestId,
            async (_, _, scope, ct) =>
            {
                retained = scope;

                Assert.True((await scope.IssueAsync(f.Admission(f.Operator), binding, null, null, ct)).IsSuccess);

                Assert.Empty(QuestCredentials(f.Authority));

                (raw, presentation) = PendingQuestCredential(scope);

                Assert.Contains(raw, value => value != 0);

                if (failure == "throw") throw new InvalidOperationException("fixture interruption");

                if (failure == "cancel")
                {
                    cancel.Cancel();

                    return true;
                }

                return new Error("fixture.rollback", "Reject domain admission after Signet insert.");
            }, cancel.Token);

        if (failure == "throw") await Assert.ThrowsAsync<InvalidOperationException>(Run);
        else if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else Assert.True((await Run()).IsFailure);

        Assert.Empty(QuestCredentials(f.Authority));

        Assert.NotNull(raw);

        Assert.True(raw.All(value => value == 0), "Pending credential bytes were not cleared.");

        Assert.NotNull(presentation);

        Assert.True(presentation.All(value => value == '\0'), "Pending credential presentation was not cleared.");

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable,
            (await retained!.IssueAsync(f.Admission(f.Operator), binding, null, null, default)).Error.Code);
    }

    [Fact]
    public async Task Transaction_participant_cannot_issue_after_its_exact_transaction_is_disposed()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildSignetBinding binding = f.Binding();

        Result<GuildSignetRecord> result = await f.Authority.InQuestTransactionAsync<GuildSignetRecord>(binding.RootQuestId,
            async (_, transaction, scope, ct) =>
            {
                await transaction.DisposeAsync();

                return await scope.IssueAsync(f.Admission(f.Operator), binding, null, null, ct);
            }, default);

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, result.Error.Code);

        Assert.Empty(QuestCredentials(f.Authority));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));
    }

    [Fact]
    public async Task Transaction_target_eligibility_reuses_live_policy_without_minting_bearer_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildExecutionHandle target = await f.RootAsync();

        using SignetAuthority restarted = f.NewAuthority();

        SignetAuthority.QuestTransaction? retained = null;

        Result<GuildGrant> eligible = await restarted.InQuestTransactionAsync<GuildGrant>(target.RootQuestId,
            async (_, _, scope, ct) =>
            {
                retained = scope;

                Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, (await scope.VerifyAsync(target, ct)).Error.Code);

                return await scope.ValidateCurrentGrantAsync(target.QuestId, ct);
            }, default);

        Assert.True(eligible.IsSuccess, eligible.Error.Message);

        Assert.Empty(QuestCredentials(restarted));

        Assert.Equal(ErrorCodes.Guild.AuthorityUnavailable, (await retained!.ValidateCurrentGrantAsync(target.QuestId, default)).Error.Code);

        Result<GuildGrant> wrongRoot = await restarted.InQuestTransactionAsync<GuildGrant>(Guid.NewGuid(),
            (_, _, scope, ct) => scope.ValidateCurrentGrantAsync(target.QuestId, ct), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityMismatch, wrongRoot.Error.Code);

        Guid key = Guid.NewGuid();

        Assert.True((await f.Authority.RevokeAsync(f.Operator, new(key, Digest("revoke target")), target.SignetId,
            new(key, 1, GuildCancellationReason.OperatorRequested), default)).IsSuccess);

        Result<GuildGrant> revoked = await restarted.InQuestTransactionAsync<GuildGrant>(target.RootQuestId,
            (_, _, scope, ct) => scope.ValidateCurrentGrantAsync(target.QuestId, ct), default);

        Assert.Equal(ErrorCodes.Guild.AuthorityRevoked, revoked.Error.Code);

        Assert.Empty(QuestCredentials(restarted));
    }

    private static (byte[], char[]) PendingQuestCredential(SignetAuthority.QuestTransaction scope)
    {
        IEnumerable pending = Assert.IsAssignableFrom<IEnumerable>(typeof(SignetAuthority.QuestTransaction)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope));

        object item = Assert.Single(pending.Cast<object>());

        object credential = item.GetType().GetProperty("Credential")!.GetValue(item)!;

        return (Assert.IsType<byte[]>(credential.GetType().GetField("_raw", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(credential)),
            Assert.IsType<char[]>(credential.GetType().GetField("_presentation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(credential)));
    }

    private static IDictionary QuestCredentials(SignetAuthority authority) => Assert.IsAssignableFrom<IDictionary>(
        typeof(SignetAuthority).GetField("_credentials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(authority));

}
