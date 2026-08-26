using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Correcting one entry by naming the exact version it replaces.
/// </summary>
/// <remarks>
/// Every entry these tests correct is written first through <c>Set</c>, and every target they name is
/// read back off the head the write produced. Nothing asserted here was put there by the suite.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantCorrectionTests
{

    private static CancellationToken Token => CancellationToken.None;

    private const string Key = "preference.builds";

    [Fact]
    public async Task A_correction_naming_the_current_head_appends_a_version_the_next_read_returns()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts head = await ReadHeadAsync(harness);

        Result<CovenantMutationResultDto> committed = await CorrectAsync(
            harness,
            head,
            "Build from the tools directory.");

        Assert.True(committed.IsSuccess, committed.IsFailure ? committed.Error.Message : string.Empty);

        Assert.Equal(CovenantMutationOutcome.Applied, committed.Value.Outcome);

        Assert.Equal(head.Revision + 1, committed.Value.ResultingLaneRevision);

        HeadFacts corrected = await ReadHeadAsync(harness);

        Assert.NotEqual(head.RenderedHash, corrected.RenderedHash);

        // The version chain is a chain: the corrected head names the version it replaced.
        Assert.Equal(head.VersionId, corrected.PredecessorVersionId);

    }

    /// <summary>
    /// An older revision is a guess about what is current. A correction that applied over it would
    /// overwrite a version the operator never saw.
    /// </summary>
    [Fact]
    public async Task A_correction_naming_a_version_that_is_no_longer_the_head_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts first = await ReadHeadAsync(harness);

        _ = await CorrectAsync(harness, first, "Build from the tools directory.");

        // The operator's second correction still names the version they looked at, which has moved.
        Result<CovenantMutationPreflightDto> refused = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            "Build from somewhere else.",
            first.VersionId,
            first.RenderedHash,
            first.Revision,
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    /// <summary>
    /// The version identity is checked on its own terms, not as a proxy for the content.
    /// </summary>
    /// <remarks>
    /// Correcting away from a value and back to it produces a later version whose compiled hash equals
    /// an earlier one's. Naming that earlier version beside the <i>current</i> revision and the current
    /// hash is the one shape where only the identity comparison can refuse — and severing that
    /// comparison left every other correction test green, which is what this case exists to stop.
    /// </remarks>
    [Fact]
    public async Task A_correction_naming_an_earlier_version_with_the_same_content_is_still_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts original = await ReadHeadAsync(harness);

        _ = await CorrectAsync(harness, original, "Build from the tools directory.");

        HeadFacts changed = await ReadHeadAsync(harness);

        _ = await CorrectAsync(harness, changed, "Build from the root.");

        HeadFacts restored = await ReadHeadAsync(harness);

        // The content came back, so the hash matches the version the operator first read.
        Assert.Equal(original.RenderedHash, restored.RenderedHash);

        Assert.NotEqual(original.VersionId, restored.VersionId);

        Result<CovenantMutationPreflightDto> refused = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            "Build from somewhere else.",
            original.VersionId,
            restored.RenderedHash,
            restored.Revision,
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    /// <summary>
    /// The revision half of the same comparison, isolated: the right version named beside the wrong
    /// revision is refused before a token is issued rather than by the kernel's compare-and-swap after
    /// the operator has already approved.
    /// </summary>
    [Fact]
    public async Task A_correction_naming_the_head_version_with_a_wrong_revision_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts head = await ReadHeadAsync(harness);

        Result<CovenantMutationPreflightDto> refused = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            "Build from the tools directory.",
            head.VersionId,
            head.RenderedHash,
            head.Revision + 1,
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    /// <summary>
    /// The rendered hash is what a revision number cannot be: proof the operator saw this content.
    /// </summary>
    [Fact]
    public async Task A_correction_whose_rendered_hash_disagrees_with_the_head_is_refused_as_a_guess()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts head = await ReadHeadAsync(harness);

        Result<CovenantMutationPreflightDto> refused = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            "Build from the tools directory.",
            head.VersionId,
            new string('a', 64),
            head.Revision,
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, refused.Error.Code);

    }

    /// <summary>
    /// Reinstating a retired key is a different sentence from correcting a live one, and the refusal
    /// says which one the operator wanted.
    /// </summary>
    [Fact]
    public async Task A_correction_of_a_retired_head_is_refused_and_names_reactivation_instead()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts live = await ReadHeadAsync(harness);

        await harness.RetireAsync(CovenantScope.Global, null, Key, live.Revision, Token);

        HeadFacts tombstone = await ReadHeadAsync(harness);

        Result<CovenantMutationPreflightDto> refused = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            "Build from the tools directory.",
            tombstone.VersionId,
            live.RenderedHash,
            tombstone.Revision,
            Token);

        Assert.True(refused.IsFailure);

        Assert.Contains("reactivate", refused.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// A correction naming the Proposed lane is refused by the request itself, because an operator
    /// authors the Confirmed lane and only the Confirmed lane.
    /// </summary>
    [Fact]
    public void A_correction_naming_the_Proposed_branch_refuses_itself()
    {

        Result validated = new CovenantCorrectPrepareRequest(
            CovenantScope.Campaign,
            Guid.CreateVersion7(),
            Key,
            "Build from the tools directory.",
            Guid.CreateVersion7(),
            CovenantLane.Proposed,
            ExpectedRevision: 1,
            new string('a', 64),
            Guid.CreateVersion7()).Validate();

        Assert.True(validated.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, validated.Error.Code);

    }

    /// <summary>
    /// Three-way equality. Enforcing only the token against live state would let a commit name one
    /// target, succeed against another, and report success to a client that believes it corrected the
    /// first — which is the split between "what you were shown" and "what you committed" that the
    /// two-step protocol exists to close.
    /// </summary>
    [Fact]
    public async Task A_commit_naming_one_target_with_a_token_bound_to_another_is_refused()
    {

        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Build from the root.", Token);

        HeadFacts head = await ReadHeadAsync(harness);

        Guid mutationId = Guid.CreateVersion7();

        Result<CovenantMutationPreflightDto> prepared = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            "Build from the tools directory.",
            head.VersionId,
            head.RenderedHash,
            head.Revision,
            Token,
            mutationId);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

        Result<CovenantMutationResultDto> refused = await harness.CommitCorrectAsync(
            new CovenantCorrectRequest(
                CovenantScope.Global,
                null,
                Key,
                "Build from the tools directory.",
                Guid.CreateVersion7(),
                CovenantLane.Confirmed,
                head.Revision,
                head.RenderedHash,
                mutationId,
                prepared.Value.PreflightToken),
            Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

    }

    private static async Task<Result<CovenantMutationResultDto>> CorrectAsync(
        CovenantServiceHarness harness,
        HeadFacts head,
        string content)
    {

        Guid mutationId = Guid.CreateVersion7();

        Result<CovenantMutationPreflightDto> prepared = await harness.PrepareCorrectAsync(
            CovenantScope.Global,
            null,
            Key,
            content,
            head.VersionId,
            head.RenderedHash,
            head.Revision,
            Token,
            mutationId);

        if (prepared.IsFailure)
        {

            return prepared.Error;

        }

        return await harness.CommitCorrectAsync(
            new CovenantCorrectRequest(
                CovenantScope.Global,
                null,
                Key,
                content,
                head.VersionId,
                CovenantLane.Confirmed,
                head.Revision,
                head.RenderedHash,
                mutationId,
                prepared.Value.PreflightToken),
            Token);

    }

    /// <summary>
    /// Reads the live Confirmed head straight out of canonical storage, the way an operator reads it
    /// off <c>show</c> before naming it as a correction target.
    /// </summary>
    private static async Task<HeadFacts> ReadHeadAsync(CovenantServiceHarness harness)
    {

        await using Microsoft.Data.Sqlite.SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = """
            SELECT h.CurrentVersionId, h.CurrentLaneRevision, v.RenderedHash, v.PredecessorVersionId
            FROM covenant_heads h
            JOIN covenant_versions v ON v.VersionId = h.CurrentVersionId
            WHERE h.CampaignId IS NULL AND h.NormalizedKey = $key AND h.LaneCode = 1;
            """;

        _ = command.Parameters.AddWithValue("$key", Key);

        await using Microsoft.Data.Sqlite.SqliteDataReader reader =
            await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token), "No Confirmed head exists for that key.");

        return new HeadFacts(
            Guid.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? string.Empty : Convert.ToHexStringLower((byte[])reader.GetValue(2)),
            reader.IsDBNull(3)
                ? null
                : Guid.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture));

    }

    private sealed record HeadFacts(
        Guid VersionId,
        long Revision,
        string RenderedHash,
        Guid? PredecessorVersionId);

}
