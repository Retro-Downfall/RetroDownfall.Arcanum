using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one derivation of what a search health report says, shared by the status route and the search page.
/// </summary>
/// <remarks>
/// <see cref="CovenantSearchHealthDto"/> reaches an operator through both routes, and two producers each
/// computing their own answer gave one frozen contract two meanings: after a restore that kept its heads,
/// status asked for a rebuild while a search page said to wait for a synchronization no pass would ever
/// complete. Both routes pass the facts they hold to these two functions and report what comes back, so
/// they cannot drift. The facts are the same persisted ones. Status reads them from the published
/// availability snapshot and a page reads them from the rows it was answered from.
/// </remarks>
internal static class CovenantSearchHealthRule
{
    /// <summary>
    /// The four states search can actually be in.
    /// </summary>
    /// <param name="acceleratorUnavailable">Whether the accelerator tier is absent or failed.</param>
    /// <param name="acceleratorDegraded">Whether the accelerator tier is installed but reported degraded.</param>
    /// <param name="synchronized">Whether the accelerator is answering queries by the rule search applies.</param>
    internal static CovenantSearchHealthState State(
        bool acceleratorUnavailable,
        bool acceleratorDegraded,
        bool synchronized) =>
        acceleratorUnavailable
            ? CovenantSearchHealthState.Unavailable
            : acceleratorDegraded
                ? CovenantSearchHealthState.Degraded
                : synchronized
                    ? CovenantSearchHealthState.Healthy
                    : CovenantSearchHealthState.Synchronizing;

    /// <summary>
    /// The one remediation the facts actually call for, most specific first.
    /// </summary>
    /// <remarks>
    /// <para>Order matters. An unavailable accelerator cannot be waited out. A synchronized one asks for
    /// nothing, whatever debt is still recorded: a fresh installation and a Covenant reset both record a
    /// full rebuild as owed, and the outbox then adopts their empty projection, keeps it current, and
    /// clears the debt in the pass that publishes a tuple equal to the canonical sequence. A synchronized
    /// accelerator is answering every query, so a rebuild would repair nothing, and that holds as well for
    /// the debt an installation adopted by an earlier build still carries beside a current tuple until its
    /// next pass clears it.</para>
    ///
    /// <para>Where search is not synchronized, a published applied tuple for this dataset means the outbox
    /// continues from it, so the answer is to wait whatever debt is still recorded: during an adoption
    /// spread over several bounded passes that debt is the one recorded when the dataset was created or
    /// reset, and it clears only when the last pass reaches the canonical sequence. It does not describe the
    /// delta waiting behind the tuple. Without a published tuple for this dataset the recorded debt
    /// decides: a restore that kept its heads, and a rebuild abandoned part way, are states the outbox will
    /// not adopt, so only a rebuild can complete them.</para>
    /// </remarks>
    /// <param name="acceleratorUnavailable">Whether the accelerator tier is absent or failed.</param>
    /// <param name="synchronized">Whether the accelerator is answering queries by the rule search applies.</param>
    /// <param name="outboxCanContinue">
    /// Whether the accelerator is healthy and its applied tuple names this dataset, so pending deltas apply
    /// from where it stands.
    /// </param>
    /// <param name="rebuildOwed">Whether the persisted rebuild state is anything but idle.</param>
    internal static CovenantSearchRebuildGuidance Guidance(
        bool acceleratorUnavailable,
        bool synchronized,
        bool outboxCanContinue,
        bool rebuildOwed) =>
        acceleratorUnavailable
            ? CovenantSearchRebuildGuidance.AcceleratorUnavailable
            : synchronized
                ? CovenantSearchRebuildGuidance.None
                : outboxCanContinue || !rebuildOwed
                    ? CovenantSearchRebuildGuidance.WaitForSynchronization
                    : CovenantSearchRebuildGuidance.RebuildRequired;
}
