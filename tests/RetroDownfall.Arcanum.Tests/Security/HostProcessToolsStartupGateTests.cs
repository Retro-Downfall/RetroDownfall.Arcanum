using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Cli;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The startup classification that decides whether this process may open Covenant at all.
/// </summary>
/// <remarks>
/// The gate is the only place the two markers are compared before anything else in the host exists,
/// so these tests assert the ordering as well as the outcomes: an unreadable operating-system marker
/// has to block before the database is opened, and no disposition other than a proven clean pair may
/// leave Covenant permitted.
/// </remarks>
public sealed class HostProcessToolsStartupGateTests
{

    private static readonly Guid Transition = Guid.Parse("3E5A7C90-1B2D-4F6A-8C0E-9D1F3A5B7C90");

    [Fact]
    public async Task A_clean_installation_with_no_marker_starts_normally()
    {

        Harness harness = Harness.Create();

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.Clean, result.Value.Disposition);

        Assert.True(harness.Policy.CovenantPermitted);

        Assert.False(harness.Policy.HostProcessToolsPermitted);

    }

    [Fact]
    public async Task The_escape_hatch_environment_without_a_completed_transition_blocks_startup()
    {

        Harness harness = Harness.Create();

        harness.Environment.EscapeHatchOptIn = true;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.HostToolsTransitionRequired, result.Error.Code);

        Assert.Contains(
            HostProcessToolsStartupGate.EscapeHatchRemediation,
            result.Error.Message,
            StringComparison.Ordinal);

        Assert.False(harness.Policy.CovenantPermitted);

        // The blocker is what lets the host tell "clean but armed" apart from "evidence disagrees":
        // only the first is survivable, and only the first has a remedy the operator can act on.
        Assert.Equal(
            HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition,
            harness.Policy.Blocker);

    }

    [Fact]
    public async Task Every_block_that_evidence_could_not_produce_on_its_own_is_distinguishable()
    {

        Harness pending = Harness.Create();

        _ = await pending.Authority.CommitPendingAsync(
            pending.Authority.Row,
            Transition,
            CancellationToken.None);

        _ = await pending.Gate.ClassifyAndPublishAsync(CancellationToken.None);

        Assert.Equal(HostProcessToolsStartupBlocker.PendingTransition, pending.Policy.Blocker);

        Harness stray = Harness.Create();

        stray.Markers.SeedForeignMarker();

        _ = await stray.Gate.ClassifyAndPublishAsync(CancellationToken.None);

        Assert.Equal(HostProcessToolsStartupBlocker.MarkerMismatch, stray.Policy.Blocker);

        Harness unreadable = Harness.Create();

        unreadable.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable;

        _ = await unreadable.Gate.ClassifyAndPublishAsync(CancellationToken.None);

        Assert.Equal(HostProcessToolsStartupBlocker.MarkerMismatch, unreadable.Policy.Blocker);

    }

    [Fact]
    public async Task A_matching_tainted_pair_starts_in_permanent_no_covenant_mode()
    {

        Harness harness = Harness.Create();

        harness.Taint();

        harness.Environment.EscapeHatchOptIn = true;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.TaintedMatched, result.Value.Disposition);

        Assert.False(harness.Policy.CovenantPermitted);

        Assert.True(harness.Policy.HostProcessToolsPermitted);

    }

    [Fact]
    public async Task Removing_the_opt_in_suppresses_the_tool_and_never_restores_covenant()
    {

        Harness harness = Harness.Create();

        harness.Taint();

        harness.Environment.EscapeHatchOptIn = false;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(harness.Policy.HostProcessToolsPermitted);

        Assert.False(harness.Policy.CovenantPermitted);

        harness.Environment.Edition = ArcanumEdition.Local;

        Result<HostProcessToolsStartupDecision> again = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(again.IsSuccess);

        Assert.False(harness.Policy.CovenantPermitted);

    }

    [Fact]
    public async Task A_pending_transition_always_blocks()
    {

        Harness harness = Harness.Create();

        _ = await harness.Authority.CommitPendingAsync(
            harness.Authority.Row,
            Transition,
            CancellationToken.None);

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.HostToolsTransitionRequired, result.Error.Code);

        Assert.Equal(HostProcessToolsMarkerPairDisposition.PendingBlocked, harness.Policy.Disposition);

    }

    [Fact]
    public async Task A_tainted_row_without_a_marker_and_a_clean_row_with_one_both_block()
    {

        Harness taintedWithoutMarker = Harness.Create();

        taintedWithoutMarker.Taint();

        _ = taintedWithoutMarker.Markers.ClearStoredForTest(TakeMarker(taintedWithoutMarker));

        Assert.True((await taintedWithoutMarker.Gate.ClassifyAndPublishAsync(CancellationToken.None)).IsFailure);

        Harness cleanWithMarker = Harness.Create();

        cleanWithMarker.Markers.SeedForeignMarker();

        Assert.True((await cleanWithMarker.Gate.ClassifyAndPublishAsync(CancellationToken.None)).IsFailure);

        Assert.False(cleanWithMarker.Policy.CovenantPermitted);

    }

    [Fact]
    public async Task A_malformed_marker_blocks_before_the_database_is_opened()
    {

        Harness harness = Harness.Create();

        harness.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Malformed;

        Result<HostProcessToolsStartupDecision> result = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.HostToolsTransitionRequired, result.Error.Code);

        Assert.Equal(0, harness.Authority.ReadCount);

    }

    [Fact]
    public async Task An_unreachable_credential_backend_is_never_read_as_an_absent_marker()
    {

        Harness harness = Harness.Create();

        harness.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable;

        Assert.True((await harness.Gate.ClassifyAndPublishAsync(CancellationToken.None)).IsFailure);

        Assert.False(harness.Policy.CovenantPermitted);

    }

    [Fact]
    public void An_unpublished_policy_permits_nothing()
    {

        HostProcessToolsRuntimePolicy policy = new();

        Assert.False(policy.IsPublished);

        Assert.False(policy.CovenantPermitted);

        Assert.False(policy.HostProcessToolsPermitted);

        Assert.Null(policy.Disposition);

    }

    [Fact]
    public void A_tainted_policy_refuses_to_be_republished_as_covenant_permitting()
    {

        HostProcessToolsRuntimePolicy policy = new();

        Assert.True(policy.Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.TaintedMatched,
            CovenantPermitted: false,
            HostProcessToolsPermitted: true)).IsSuccess);

        Result restored = policy.Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.Clean,
            CovenantPermitted: true,
            HostProcessToolsPermitted: false));

        Assert.True(restored.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, restored.Error.Code);

        Assert.False(policy.CovenantPermitted);

    }

    /// <summary>
    /// Evidence that could not be read is not evidence that disagrees.
    /// </summary>
    /// <remarks>
    /// Three of the six hard stops are read or validation failures rather than disagreements: a
    /// credential store that is locked or unreachable, an authority row that will not read, and an
    /// authority row that does not describe a valid host-tools state. The first needs no transition
    /// to have been attempted at all, which makes it the most reachable block in the gate — and
    /// telling that operator the recorded evidence disagrees with itself sends them hunting for a
    /// corruption that is not there instead of unlocking the keychain.
    /// </remarks>
    [Fact]
    public async Task A_read_failure_is_not_reported_as_evidence_that_disagrees_with_itself()
    {

        Harness unreadableMarker = Harness.Create();

        unreadableMarker.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable;

        Result<HostProcessToolsStartupDecision> marker = await unreadableMarker.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(marker.IsFailure);

        Assert.DoesNotContain("disagrees", marker.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("credential store", marker.Error.Message, StringComparison.OrdinalIgnoreCase);

        Harness unreadableRow = Harness.Create();

        unreadableRow.Authority.TryReadFailure = true;

        Result<HostProcessToolsStartupDecision> row = await unreadableRow.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(row.IsFailure);

        Assert.DoesNotContain("disagrees", row.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Grimoire", row.Error.Message, StringComparison.Ordinal);

        Harness invalidRow = Harness.Create();

        invalidRow.Authority.InvalidRow = true;

        Result<HostProcessToolsStartupDecision> invalid = await invalidRow.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        Assert.True(invalid.IsFailure);

        Assert.DoesNotContain("disagrees", invalid.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Grimoire", invalid.Error.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The denial a tool call hands back has to describe the process the operator is actually in.
    /// </summary>
    /// <remarks>
    /// <see cref="HostProcessToolPolicy.DeniedMessage"/> used to tell them that setting Development
    /// edition plus <c>ARCANUM_ALLOW_HOST_PROCESS_TOOLS</c> enables these tools and that
    /// <c>GET /api/health</c> would then report Degraded. Both became the startup gate's decision.
    /// On an installation with no completed transition that exact pair is what the gate refuses to
    /// start on - asserted here through the gate's own entry point rather than assumed - so the
    /// message was directing the operator into the one state that stops their host, and promising a
    /// health status only the permitted path ever produces. The remedy it gives now is the gate's
    /// own, held to the same sentence so the two cannot drift apart.
    /// </remarks>
    [Fact]
    public async Task The_tool_denial_gives_the_same_remedy_the_startup_gate_gives()
    {

        const string Remedy =
            "Clear the ARCANUM_ALLOW_HOST_PROCESS_TOOLS environment variable and start the host again.";

        Harness harness = Harness.Create();

        harness.Environment.EscapeHatchOptIn = true;

        Result<HostProcessToolsStartupDecision> blocked = await harness.Gate
            .ClassifyAndPublishAsync(CancellationToken.None);

        // The premise: the pair the old denial called the way to turn these tools on is the pair
        // this gate refuses to start with, and it publishes that refusal rather than permitting.
        Assert.True(blocked.IsFailure);

        Assert.True(harness.Policy.IsPublished);

        Assert.False(harness.Policy.HostProcessToolsPermitted);

        Assert.EndsWith(Remedy, HostProcessToolsStartupGate.EscapeHatchRemediation, StringComparison.Ordinal);

        Assert.Contains(
            HostProcessToolsStartupGate.EscapeHatchRemediation,
            blocked.Error.Message,
            StringComparison.Ordinal);

        // The denial an operator reads from a refused tool call has to end on that same remedy.
        Assert.EndsWith(Remedy, HostProcessToolPolicy.DeniedMessage, StringComparison.Ordinal);

        // It may not offer the pair as a way to enable anything, and it may not promise a health
        // status that only the permitted path produces.
        Assert.DoesNotContain(
            "to enable",
            HostProcessToolPolicy.DeniedMessage,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "Degraded",
            HostProcessToolPolicy.DeniedMessage,
            StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Every command a refused startup tells the operator to run has to be one the CLI has.
    /// </summary>
    /// <remarks>
    /// The remediation instruction is the only thing the operator of a host that will not start can
    /// act on, and a host that spends it on a command the parser rejects has told them nothing. The
    /// projected surface is the same tree the parser walks, so a message and the shipped verbs
    /// cannot drift apart without this failing.
    /// </remarks>
    [Fact]
    public async Task No_refusal_message_names_a_command_the_cli_does_not_have()
    {

        // The control on the extractor: it has to find both a real chain and a fictional one, or a
        // message it silently stopped matching would satisfy the assertion below by finding nothing.
        Assert.Equal(
            ["ward resolve"],
            CommandChains("Run `arcanum ward resolve --allow` while the host is stopped."));

        Assert.Equal(
            ["security host-process-tools enable"],
            CommandChains("Run `arcanum security host-process-tools enable --yes`."));

        HashSet<string> paths =
        [
            .. CliSurfaceTests.Walk(CliSurfaceTests.BuildMap())
                .Select(static command => command.Path),
        ];

        Assert.Contains("ward resolve", paths);

        // Six messages: every blocked disposition the gate can produce, each proven to have failed
        // and to carry its own remedy before its command literals are read out of it.
        string[] named =
        [
            .. (await RefusalMessagesAsync()).SelectMany(CommandChains),
        ];

        // The positive control. Without it the assertion below is satisfied by a corpus in which no
        // message names a command at all, which is indistinguishable from one in which every command
        // resolves. The read-failure remedy names a real verb, so a live chain has to resolve here.
        Assert.Contains("doctor", named, StringComparer.Ordinal);

        List<string> offenders = [.. named.Where(chain => !paths.Contains(chain))];

        Assert.True(
            offenders.Count == 0,
            "A blocked startup may only name a command the CLI actually registers: "
            + string.Join("; ", offenders));

    }

    /// <summary>One refusal message per blocked disposition the gate can produce.</summary>
    /// <remarks>
    /// Every message is taken through <see cref="RefusalMessage"/>, which asserts the classification
    /// actually failed. Reading <c>Error.Message</c> off an unchecked result is how this helper would
    /// go quiet on the regression it exists to catch: a disposition that stopped blocking returns
    /// <c>Error.None</c>, whose message is empty, so the caller would scan nothing and pass.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> RefusalMessagesAsync()
    {

        List<string> messages = [];

        Harness escapeHatch = Harness.Create();

        escapeHatch.Environment.EscapeHatchOptIn = true;

        messages.Add(RefusalMessage(
            await escapeHatch.Gate.ClassifyAndPublishAsync(CancellationToken.None),
            HostProcessToolsStartupGate.EscapeHatchRemediation));

        Harness pending = Harness.Create();

        _ = await pending.Authority.CommitPendingAsync(
            pending.Authority.Row,
            Transition,
            CancellationToken.None);

        messages.Add(RefusalMessage(
            await pending.Gate.ClassifyAndPublishAsync(CancellationToken.None),
            HostProcessToolsStartupGate.EvidenceMismatchRemediation));

        Harness stray = Harness.Create();

        stray.Markers.SeedForeignMarker();

        messages.Add(RefusalMessage(
            await stray.Gate.ClassifyAndPublishAsync(CancellationToken.None),
            HostProcessToolsStartupGate.EvidenceMismatchRemediation));

        Harness unreadable = Harness.Create();

        unreadable.Markers.ReadStatusOverride = HostProcessToolsMarkerReadStatus.Unavailable;

        messages.Add(RefusalMessage(
            await unreadable.Gate.ClassifyAndPublishAsync(CancellationToken.None),
            HostProcessToolsStartupGate.MarkerUnreadableRemediation));

        Harness unreadableRow = Harness.Create();

        unreadableRow.Authority.TryReadFailure = true;

        messages.Add(RefusalMessage(
            await unreadableRow.Gate.ClassifyAndPublishAsync(CancellationToken.None),
            HostProcessToolsStartupGate.AuthorityUnreadableRemediation));

        Harness invalidRow = Harness.Create();

        invalidRow.Authority.InvalidRow = true;

        messages.Add(RefusalMessage(
            await invalidRow.Gate.ClassifyAndPublishAsync(CancellationToken.None),
            HostProcessToolsStartupGate.AuthorityUnreadableRemediation));

        return messages;

    }

    /// <summary>
    /// The message of a classification that must have failed, carrying the remedy for its own site.
    /// </summary>
    /// <remarks>
    /// Both assertions are about vacuity rather than about the message text. A result nobody checked
    /// yields an empty string on success, and a site whose remedy silently became a different one
    /// would keep every caller scanning a message that no longer says what the caller assumed.
    /// </remarks>
    private static string RefusalMessage(
        Result<HostProcessToolsStartupDecision> result,
        string expectedRemediation)
    {

        Assert.True(result.IsFailure);

        Assert.Contains(expectedRemediation, result.Error.Message, StringComparison.Ordinal);

        return result.Error.Message;

    }

    /// <summary>The verb chain of every backtick-quoted <c>arcanum</c> invocation in a message.</summary>
    private static IReadOnlyList<string> CommandChains(string message)
    {

        List<string> chains = [];

        foreach (Match quoted in Regex.Matches(message, "`arcanum ([^`]+)`"))
        {

            string[] verbs =
            [
                .. quoted.Groups[1].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .TakeWhile(static token => !token.StartsWith('-')),
            ];

            if (verbs.Length > 0)
            {

                chains.Add(string.Join(' ', verbs));

            }

        }

        return chains;

    }

    private static HostProcessToolsOsMarkerEvidence TakeMarker(Harness harness) =>
        harness.Markers.Read().Marker!;

    private sealed class Harness
    {

        private Harness(
            HostProcessToolsStartupGate gate,
            CountingHostProcessToolsAuthorityStore authority,
            FakeHostProcessToolsMarkerStore markers,
            FakeHostProcessToolsEnvironmentProbe environment,
            HostProcessToolsRuntimePolicy policy)
        {

            Gate = gate;

            Authority = authority;

            Markers = markers;

            Environment = environment;

            Policy = policy;

        }

        internal HostProcessToolsStartupGate Gate { get; }

        internal CountingHostProcessToolsAuthorityStore Authority { get; }

        internal FakeHostProcessToolsMarkerStore Markers { get; }

        internal FakeHostProcessToolsEnvironmentProbe Environment { get; }

        internal HostProcessToolsRuntimePolicy Policy { get; }

        internal static Harness Create()
        {

            CountingHostProcessToolsAuthorityStore authority = new();

            FakeHostProcessToolsMarkerStore markers = new();

            FakeHostProcessToolsEnvironmentProbe environment = new()
            {
                Edition = ArcanumEdition.Development,

                EscapeHatchOptIn = false,
            };

            HostProcessToolsRuntimePolicy policy = new();

            HostProcessToolsStartupGate gate = new(
                markers,
                authority,
                environment,
                new HostProcessToolsMarkerPairJoiner(),
                policy);

            return new Harness(gate, authority, markers, environment, policy);

        }

        /// <summary>Drives the real transition so the two markers agree the way production makes them.</summary>
        internal void Taint()
        {

            HostProcessToolsTransitionService service = new(
                Authority,
                Markers,
                new FakeHostProcessToolsEnvironmentProbe(),
                new FakeHostProcessToolsInstallationLockSource(),
                new HostProcessToolsMarkerPairJoiner(),
                HostProcessToolsTestGate.Shared);

            Result<HostProcessToolsTransitionResult> result = service
                .EnableAsync(new HostProcessToolsTransitionRequest(Transition), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            Assert.Equal(HostProcessToolsTransitionOutcome.Completed, result.Value.Outcome);

            Authority.ReadCount = 0;

        }

    }

    /// <summary>The authority fake plus a read counter, for the ordering assertions.</summary>
    private sealed class CountingHostProcessToolsAuthorityStore : IHostProcessToolsAuthorityStore
    {

        private readonly FakeHostProcessToolsAuthorityStore _inner = new();

        internal int ReadCount { get; set; }

        /// <summary>Makes the durable read fail the way an unopenable Grimoire does.</summary>
        internal bool TryReadFailure { get; set; }

        /// <summary>Returns a row whose shape <c>ToEvidence()</c> refuses to validate.</summary>
        internal bool InvalidRow { get; set; }

        internal HostProcessToolsAuthorityRow Row => _inner.Row;

        public Task<Result<HostProcessToolsAuthorityRow>> ReadAsync(CancellationToken cancellationToken)
        {

            ReadCount++;

            return _inner.ReadAsync(cancellationToken);

        }

        public Task<Result<HostProcessToolsAuthorityRow?>> TryReadAsync(CancellationToken cancellationToken)
        {

            ReadCount++;

            if (TryReadFailure)
            {

                return Task.FromResult<Result<HostProcessToolsAuthorityRow?>>(new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "The authority row could not be read."));

            }

            if (InvalidRow)
            {

                // Tainted with no transition identity: the exact shape the evidence constructor
                // refuses, which is how a row reaches the gate's validation failure.
                return Task.FromResult<Result<HostProcessToolsAuthorityRow?>>(
                    _inner.Row with { State = CovenantHostToolsState.HostToolsTainted });

            }

            return _inner.TryReadAsync(cancellationToken);

        }

        public Task<Result<HostProcessToolsProtectedInventory>> InventoryProtectedStateAsync(
            CancellationToken cancellationToken) =>
            _inner.InventoryProtectedStateAsync(cancellationToken);

        public Task<Result> CommitPendingAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            _inner.CommitPendingAsync(expected, transitionId, cancellationToken);

        public Task<Result> CommitTaintedAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            _inner.CommitTaintedAsync(expected, transitionId, cancellationToken);

        public Task<Result> CompensateToCleanAsync(
            HostProcessToolsAuthorityRow expected,
            Guid transitionId,
            CancellationToken cancellationToken) =>
            _inner.CompensateToCleanAsync(expected, transitionId, cancellationToken);

    }

}
