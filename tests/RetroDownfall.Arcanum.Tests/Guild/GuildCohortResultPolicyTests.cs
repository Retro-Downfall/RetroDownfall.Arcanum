using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildCohortResultPolicyTests
{
    [Theory]
    [InlineData("text/plain", " answer\n", "6d901fd562436468ea0ebbf4079a51b0ce479d7a30cad99e29d45e149a2fb41d")]
    [InlineData("application/json", "{\"a\":1}", "9a75cfdcb8c0706f676f56945ae6ae91ad7e4606c8e2014a6f5b79a09b5a8d58")]
    public void Accepted_result_digest_binds_exact_mime_and_bytes(string mime, string value, string digest)
    {
        var result = GuildCohortResultPolicy.ValidateResult(new(mime), mime, Encoding.UTF8.GetBytes(value));

        Assert.True(result.IsSuccess);

        Assert.Equal(digest, result.Value);
    }

    [Theory]
    [InlineData("text/plain", "")]
    [InlineData("text/plain", " \t\r\n\u2003")]
    [InlineData("application/json", "true")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "123")]
    [InlineData("application/json", "\"answer\"")]
    [InlineData("application/json", "{} []")]
    [InlineData("application/json", "{\"a\":1,\"a\":2}")]
    [InlineData("application/json", "{\"a\":1,\"\\u0061\":2}")]
    [InlineData("application/json", "[{\"nested\":{\"a\":1,\"a\":2}}]")]
    [InlineData("application/json", "{\"a\":1,}")]
    [InlineData("application/json", "{/*comment*/}")]
    [InlineData("application/json", "{\"a\":\"\\uD800\"}")]
    [InlineData("application/json", "{\"\\uD800\":1}")]
    public void Invalid_result_shape_is_terminal_nonacceptance(string mime, string value)
    {
        Assert.True(GuildCohortResultPolicy.ValidateResult(new(mime), mime, Encoding.UTF8.GetBytes(value)).IsFailure);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    public void Malformed_utf8_is_never_replaced_before_acceptance(string mime)
    {
        Assert.True(GuildCohortResultPolicy.ValidateResult(new(mime), mime, new byte[] { 0xC0, 0xAF }).IsFailure);

        Assert.True(GuildCohortResultPolicy.ValidateResult(new(mime), mime, new byte[] { 0xED, 0xA0, 0x80 }).IsFailure);
    }

    [Theory]
    [InlineData("text/plain; charset=utf-8", "text/plain", 1)]
    [InlineData("text/plain", "application/json", 1)]
    [InlineData("text/plain", "text/plain", 0)]
    [InlineData("text/plain", "text/plain", 2)]
    public void Contract_requires_exact_supported_mime_and_one_designated_result(string contract, string output, int count)
    {
        Assert.True(GuildCohortResultPolicy.ValidateResult(new(contract), output, "{}"u8.ToArray(), count).IsFailure);
    }

    [Fact]
    public void Valid_json_has_no_arbitrary_depth_or_object_work_cap()
    {
        string nested = new string('[', 2048) + "{}" + new string(']', 2048);

        Assert.True(GuildCohortResultPolicy.ValidateResult(new("application/json"), "application/json", Encoding.UTF8.GetBytes(nested)).IsSuccess);

        string objects = "[" + string.Join(',', Enumerable.Repeat("{\"a\":1}", 4096)) + "]";

        Assert.True(GuildCohortResultPolicy.ValidateResult(new("application/json"), "application/json", Encoding.UTF8.GetBytes(objects)).IsSuccess);
    }

    [Fact]
    public void Duplicate_property_at_depth_beyond_default_parser_limit_is_rejected()
    {
        string nested = new string('[', 2048) + "{\"a\":1,\"\\u0061\":2}" + new string(']', 2048);

        Assert.True(GuildCohortResultPolicy.ValidateResult(new("application/json"), "application/json", Encoding.UTF8.GetBytes(nested)).IsFailure);
    }

    [Fact]
    public void Exact_json_whitespace_and_property_order_remain_distinct()
    {
        string[] values = ["{\"a\":1,\"b\":2}", "{ \"a\":1,\"b\":2}", "{\"b\":2,\"a\":1}"];

        var digests = values.Select(value => GuildCohortResultPolicy.ValidateResult(new("application/json"), "application/json", Encoding.UTF8.GetBytes(value)).Value);

        Assert.Equal(3, digests.Distinct().Count());
    }

    [Fact]
    public void All_selects_in_admission_order_without_requiring_agreement()
    {
        GuildCohortVote early = Accepted(1, 9, "one");

        GuildCohortVote late = Accepted(2, 3, "two");

        var decision = Evaluate(GuildConvergenceKind.All, [late, early]);

        Assert.Equal(GuildConvergenceDecision.Success, decision.Decision);

        Assert.Equal<Guid>([early.MemberId, late.MemberId], decision.SelectedMemberIds);
    }

    [Fact]
    public void All_waits_for_pending_and_fails_on_terminal_nonacceptance()
    {
        Assert.Null(Evaluate(GuildConvergenceKind.All, [Accepted(1, 1), Pending(2)]).Decision);

        Assert.Equal(GuildConvergenceDecision.Impossible, Evaluate(GuildConvergenceKind.All, [Rejected(1, 1), Pending(2)]).Decision);
    }

    [Fact]
    public void First_selects_earliest_durable_result_sequence()
    {
        GuildCohortVote earlyResult = Accepted(3, 2);

        var decision = Evaluate(GuildConvergenceKind.First, [Accepted(1, 9), Pending(2), earlyResult]);

        Assert.Equal(GuildConvergenceDecision.Success, decision.Decision);

        Assert.Equal<Guid>([earlyResult.MemberId], decision.SelectedMemberIds);

        Assert.Equal(1, decision.PendingMemberCount);
    }

    [Fact]
    public void First_is_impossible_only_after_all_members_are_nonacceptable()
    {
        Assert.Null(Evaluate(GuildConvergenceKind.First, [Rejected(1, 1), Pending(2)]).Decision);

        Assert.Equal(GuildConvergenceDecision.Impossible, Evaluate(GuildConvergenceKind.First, [Rejected(1, 1), Rejected(2, 2)]).Decision);
    }

    [Fact]
    public void Quorum_replays_preclose_buckets_in_durable_sequence_order()
    {
        GuildCohortVote a1 = Accepted(1, 1, "a");

        GuildCohortVote b1 = Accepted(2, 2, "b");

        GuildCohortVote b2 = Accepted(3, 3, "b");

        GuildCohortVote a2 = Accepted(4, 4, "a");

        var decision = Evaluate(GuildConvergenceKind.Quorum, [a2, a1, b2, b1], 2);

        Assert.Equal(GuildConvergenceDecision.Success, decision.Decision);

        Assert.Equal<Guid>([b1.MemberId, b2.MemberId], decision.SelectedMemberIds);
    }

    [Theory]
    [InlineData("x", "y")]
    [InlineData("x", "xx")]
    public void Equal_digest_never_equates_different_bytes_or_lengths(string first, string second)
    {
        GuildCohortVote a = Accepted(1, 1, first) with { AgreementDigest = "forced-collision" };

        GuildCohortVote b = Accepted(2, 2, second) with { AgreementDigest = "forced-collision" };

        Assert.Equal(GuildConvergenceDecision.Impossible, Evaluate(GuildConvergenceKind.Quorum, [a, b], 2).Decision);
    }

    [Fact]
    public void Quorum_accounts_for_largest_bucket_and_pending_including_unseen_bucket()
    {
        Assert.Null(Evaluate(GuildConvergenceKind.Quorum, [Pending(1), Pending(2)], 2).Decision);

        Assert.Null(Evaluate(GuildConvergenceKind.Quorum, [Accepted(1, 1, "a"), Accepted(2, 2, "b"), Pending(3)], 2).Decision);

        Assert.Equal(GuildConvergenceDecision.Impossible, Evaluate(GuildConvergenceKind.Quorum,
            [Accepted(1, 1, "a"), Accepted(2, 2, "b"), Rejected(3, 3), Pending(4)], 3).Decision);
    }

    [Theory]
    [InlineData(GuildConvergenceKind.All, null)]
    [InlineData(GuildConvergenceKind.First, null)]
    [InlineData(GuildConvergenceKind.Quorum, 1)]
    public void Open_membership_never_selects_or_proves_impossibility(GuildConvergenceKind kind, int? quorum)
    {
        Assert.Null(GuildCohortResultPolicy.Evaluate(false, new(kind, quorum), [Accepted(1, 1)]).Value.Decision);

        Assert.Null(GuildCohortResultPolicy.Evaluate(false, new(kind, quorum), [Rejected(1, 1)]).Value.Decision);

        Assert.True(GuildCohortResultPolicy.Evaluate(false, new(kind, quorum), []).IsSuccess);
    }

    [Theory]
    [InlineData(GuildConvergenceKind.Quorum, null)]
    [InlineData(GuildConvergenceKind.Quorum, 0)]
    [InlineData(GuildConvergenceKind.Quorum, -1)]
    [InlineData(GuildConvergenceKind.Quorum, 3)]
    [InlineData(GuildConvergenceKind.All, 1)]
    [InlineData(GuildConvergenceKind.First, 1)]
    [InlineData((GuildConvergenceKind)999, null)]
    public void Invalid_policy_is_refused(GuildConvergenceKind kind, int? quorum)
    {
        Assert.True(GuildCohortResultPolicy.Evaluate(true, new(kind, quorum), [Pending(1), Pending(2)]).IsFailure);
    }

    [Fact]
    public void Close_rejects_empty_electorate_but_open_quorum_can_wait_for_members()
    {
        Assert.True(GuildCohortResultPolicy.Evaluate(true, new(GuildConvergenceKind.First, null), []).IsFailure);

        Assert.True(GuildCohortResultPolicy.Evaluate(false, new(GuildConvergenceKind.Quorum, 5), [Pending(1)]).IsSuccess);
    }

    [Fact]
    public void Duplicate_member_admission_or_result_sequence_cannot_cast_multiple_votes()
    {
        GuildCohortVote one = Accepted(1, 1);

        Assert.True(GuildCohortResultPolicy.Evaluate(true, new(GuildConvergenceKind.All, null), [one, one]).IsFailure);

        Assert.True(GuildCohortResultPolicy.Evaluate(true, new(GuildConvergenceKind.All, null), [one, Accepted(2, 2) with { AdmissionSequence = 1 }]).IsFailure);

        Assert.True(GuildCohortResultPolicy.Evaluate(true, new(GuildConvergenceKind.All, null), [one, Accepted(2, 1)]).IsFailure);
    }

    private static GuildCohortVote Accepted(int member, long sequence, string value = "answer") =>
        new(new Guid(member, 0, 0, new byte[8]), member, sequence, GuildContributionOutcome.Accepted,
            "host-digest-" + value, Encoding.UTF8.GetBytes(value));

    private static GuildCohortVote Pending(int member) =>
        new(new Guid(member, 0, 0, new byte[8]), member, null, null, null, default);

    private static GuildCohortVote Rejected(int member, long sequence) =>
        new(new Guid(member, 0, 0, new byte[8]), member, sequence, GuildContributionOutcome.Rejected, null, default);

    private static GuildCohortPolicyDecision Evaluate(GuildConvergenceKind kind, ImmutableArray<GuildCohortVote> members, int? quorum = null) =>
        GuildCohortResultPolicy.Evaluate(true, new(kind, quorum), members).Value;
}
