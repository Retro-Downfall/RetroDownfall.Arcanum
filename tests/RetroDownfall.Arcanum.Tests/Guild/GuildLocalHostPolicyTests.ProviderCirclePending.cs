using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData("revoked", false)]
    [InlineData("revoked", true)]
    [InlineData("removed", false)]
    [InlineData("erased", false)]
    public async Task Circle_pending_invalid_source_is_withheld_without_starving_a_healthy_peer(string boundary, bool preparedContext)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, _, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 3);

        var sourceA = await BindCohortRunAsync(f, owner, members[0]);
        var sourceC = await BindCohortRunAsync(f, owner, members[2]);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        AppendSharedNoteRequest original = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Observation, null, "invalid source A canary", null);

        var posted = await cohorts.AppendNoteAsync(sourceA, NoteMutation(original), cohort.Id, original, default);

        Assert.True(posted.IsSuccess, posted.Error.Message);

        Guid originalInbox;

        await using (var connection = await f.OpenAsync())
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT i.Id FROM guild_inbox i JOIN guild_distribution_notifications n ON n.EnvelopeId=i.EnvelopeId
                WHERE n.ObjectId=$note AND i.RecipientQuestId=$recipient
                """;
            query.Parameters.AddWithValue("$note", posted.Value.Id.ToString("D").ToUpperInvariant());
            query.Parameters.AddWithValue("$recipient", members[1].Quest.Id.ToString("D").ToUpperInvariant());

            originalInbox = Guid.Parse((string)(await query.ExecuteScalarAsync())!);
        }

        GuildLocalInferenceExecution? activeExecution = null;
        GuildCircleInputBatch? retained = null;

        if (preparedContext)
        {
            var before = await f.Store.GetAsync(owner, members[1].Quest.Id, default);

            Assert.True(before.IsSuccess, before.Error.Message);

            activeExecution = await ToolExecutionAsync(f, owner, before.Value);

            var observed = await f.Store.PrepareCircleInputAsync(activeExecution, default);

            Assert.True(observed.IsSuccess, observed.Error.Message);

            retained = Assert.IsType<GuildCircleInputBatch>(observed.Value);
        }

        if (boundary == "revoked")
        {
            var author = await f.Store.GetAsync(owner, members[0].Quest.Id, default);

            Assert.True(author.IsSuccess, author.Error.Message);

            Guid key = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), author.Value.Quest.SignetId,
                new(key, author.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }
        else if (boundary == "removed")
        {
            current = await cohorts.GetAsync(issuer, cohort.Id, default);

            var removed = await cohorts.RemoveAsync(issuer, cohort.Id, posted.Value.AuthorMemberId!.Value,
                new(Guid.NewGuid(), current.Value.Revision), default);

            Assert.True(removed.IsSuccess, removed.Error.Message);
        }
        else
        {
            await using var connection = await f.OpenAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);

            await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, posted.Value.BodyRef!.ContentId.ToString("N"), default);

            await using var erase = connection.CreateCommand();

            erase.Transaction = transaction;
            erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$content";
            erase.Parameters.AddWithValue("$content", posted.Value.BodyRef.ContentId.ToString("D").ToUpperInvariant());

            Assert.Equal(1, await erase.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        current = await cohorts.GetAsync(issuer, cohort.Id, default);

        AppendSharedNoteRequest healthy = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Proposal, null, "healthy source C canary", null);

        var valid = await cohorts.AppendNoteAsync(sourceC, NoteMutation(healthy), cohort.Id, healthy, default);

        Assert.True(valid.IsSuccess, valid.Error.Message);

        var recipient = await f.Store.GetAsync(owner, members[1].Quest.Id, default);

        Assert.True(recipient.IsSuccess, recipient.Error.Message);

        var execution = activeExecution ?? await ToolExecutionAsync(f, owner, recipient.Value);
        var prepared = await f.Store.PrepareCircleInputAsync(execution, default);

        if (preparedContext)
        {
            Assert.True(prepared.IsFailure);

            Assert.Contains("invalid source A canary", retained!.Prompt, StringComparison.Ordinal);

            Assert.Equal(1L, await f.ScalarAsync($"SELECT DeliveryStateCode FROM guild_inbox WHERE Id='{originalInbox:D}' COLLATE NOCASE"));

            Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_portal_receipts WHERE InboxId='{originalInbox:D}' COLLATE NOCASE"));

            GC.KeepAlive(retained);
            return;
        }

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var batch = Assert.IsType<GuildCircleInputBatch>(prepared.Value);

        Assert.Single(batch.Items);

        Assert.DoesNotContain("invalid source A canary", batch.Prompt, StringComparison.Ordinal);

        Assert.Contains("healthy source C canary", batch.Prompt, StringComparison.Ordinal);

        Assert.Equal(3L, await f.ScalarAsync($"SELECT DeliveryStateCode FROM guild_inbox WHERE Id='{originalInbox:D}' COLLATE NOCASE"));

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_portal_receipts r JOIN guild_inbox i ON i.Id=r.InboxId JOIN guild_envelopes e ON e.Id=i.EnvelopeId WHERE i.Id='{originalInbox:D}' COLLATE NOCASE AND r.EffectKindCode=3 AND r.Digest=e.IntentDigest"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

        Assert.Equal(0L, await f.ScalarAsync($"SELECT count(*) FROM guild_inbox WHERE Id='{batch.Items[0].InboxId:D}' COLLATE NOCASE AND DeliveryStateCode<>1"));

        var repeated = await f.Store.PrepareCircleInputAsync(execution, default);

        Assert.True(repeated.IsSuccess, repeated.Error.Message);

        Assert.Equal(1L, await f.ScalarAsync($"SELECT count(*) FROM guild_portal_receipts WHERE InboxId='{originalInbox:D}' COLLATE NOCASE"));
    }

    [Fact]
    public async Task Circle_pending_invalid_source_transient_stream_preserves_backlog_and_finds_later_peer_beyond_one_page()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 3);

        var sourceA = await BindCohortRunAsync(f, owner, members[0]);
        var sourceC = await BindCohortRunAsync(f, owner, members[2]);
        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        AppendSharedNoteRequest first = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Observation, null, "transient A source", null);

        var posted = await cohorts.AppendNoteAsync(sourceA, NoteMutation(first), cohort.Id, first, default);

        Assert.True(posted.IsSuccess, posted.Error.Message);

        GuildPortalStore portal = new(f.Store, f.Authority, new EventDisclosurePolicy(f.Policy), TimeProvider.System);

        var filled = await f.Authority.InQuestTransactionAsync<bool>(root.Quest.Id,
            async (connection, transaction, signets, token) =>
            {
                for (int index = 0; index < 100; index++)
                {
                    var next = await portal.PublishWithinAsync(connection, transaction, signets, sourceA, sourceA.Execution!.QuestId,
                        new(Guid.NewGuid(), new(GuildCircleAddressKind.Cohort, null, cohort.Id, null), GuildEnvelopeKind.Progress,
                            "text/plain", "blocked successor " + index.ToString(System.Globalization.CultureInfo.InvariantCulture), null), token);

                    if (next.IsFailure) return next.Error;
                }

                var reference = posted.Value.BodyRef!;

                DerivedArtifactWrite label = new(SensitiveArtifactKind.GuildContent, reference.ContentId,
                    members[0].Quest.SessionId, members[0].Quest.CampaignId, null, (ulong)reference.Revision,
                    new CovenantDigest(Convert.FromHexString(reference.Digest)), ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()]));

                var protectedSource = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, label, token);

                return protectedSource.IsFailure ? protectedSource.Error : true;
            }, default);

        Assert.True(filled.IsSuccess, filled.Error.Message);

        current = await cohorts.GetAsync(issuer, cohort.Id, default);

        AppendSharedNoteRequest healthy = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Proposal, null, "healthy C beyond page", null);

        Assert.True((await cohorts.AppendNoteAsync(sourceC, NoteMutation(healthy), cohort.Id, healthy, default)).IsSuccess);

        var recipient = await f.Store.GetAsync(owner, members[1].Quest.Id, default);

        Assert.True(recipient.IsSuccess, recipient.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, recipient.Value);
        int writesBefore = f.Connections.Kinds.Count(kind => kind == GrimoireOrdinaryFreshConnectionKind.ReadWrite);

        var prepared = await f.Store.PrepareCircleInputAsync(execution, default);

        Assert.Equal(2, f.Connections.Kinds.Count(kind => kind == GrimoireOrdinaryFreshConnectionKind.ReadWrite) - writesBefore);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var batch = Assert.IsType<GuildCircleInputBatch>(prepared.Value);

        Assert.Single(batch.Items);

        Assert.Contains("healthy C beyond page", batch.Prompt, StringComparison.Ordinal);

        Assert.DoesNotContain("transient A source", batch.Prompt, StringComparison.Ordinal);

        Assert.DoesNotContain("blocked successor", batch.Prompt, StringComparison.Ordinal);

        Assert.Equal(101L, await f.ScalarAsync($"SELECT count(*) FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId WHERE i.RecipientQuestId='{members[1].Quest.Id:D}' COLLATE NOCASE AND e.QuestId='{members[0].Quest.Id:D}' COLLATE NOCASE AND i.DeliveryStateCode=1"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));
    }
}
