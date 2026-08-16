using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// The one identity an offline host-process-tools transition is replayed by.
/// </summary>
/// <remarks>
/// A caller-supplied random identity rather than a server-assigned operation ID, because the
/// transition has to be resumable across a crash that happened before any durable row existed. The
/// same identity retried resumes; a different one is refused rather than allowed to adopt a
/// transition it did not start.
/// </remarks>
public sealed record HostProcessToolsTransitionRequest(Guid TransitionId);

/// <summary>What one <c>EnableAsync</c> call durably resolved to.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<HostProcessToolsTransitionOutcome>))]
public enum HostProcessToolsTransitionOutcome : byte
{

    Completed = 1,

    AlreadyCompleted = 2,

    PendingManualRemediation = 3,

    Refused = 4,

}

/// <summary>
/// The content-free reason a transition did not complete.
/// </summary>
/// <remarks>
/// Deliberately a closed code rather than a message: this value reaches CLI output, logs, and
/// operator surfaces, where a free-text reason would eventually carry a path, an identity, or a
/// fingerprint. <see cref="None"/> is the only value a completed transition may carry.
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<HostProcessToolsTransitionBlocker>))]
public enum HostProcessToolsTransitionBlocker : byte
{

    None = 0,

    /// <summary>Not the Development edition, or the environment opt-in is absent.</summary>
    EditionOrOptInMissing = 1,

    /// <summary>Another process holds the installation lock, so the host is not stopped.</summary>
    HostRunning = 2,

    /// <summary>This process already opened Covenant and may hold decrypted residue.</summary>
    CovenantAlreadyOpened = 3,

    /// <summary>Covenant canonical rows or protected artifacts still exist.</summary>
    ProtectedStatePresent = 4,

    /// <summary>The durable authority row could not be read or validated.</summary>
    AuthorityUnreadable = 5,

    /// <summary>A different transition identity owns the pending or tainted state.</summary>
    ForeignTransitionIdentity = 6,

    /// <summary>The database and operating-system markers do not describe the same transition.</summary>
    MarkerPairMismatch = 7,

    /// <summary>The operating-system slot proved it did not accept the marker.</summary>
    MarkerWriteRefused = 8,

    /// <summary>The operating-system write may or may not have landed.</summary>
    MarkerWriteUncertain = 9,

    /// <summary>The marker read back absent, malformed, or different from what was written.</summary>
    MarkerReadbackMismatch = 10,

    /// <summary>The durable authority transition lost its compare-and-swap.</summary>
    AuthorityCommitFailed = 11,

}

/// <summary>
/// The typed, content-free answer the offline command prints and exits on.
/// </summary>
/// <remarks>
/// <see cref="RestartRequired"/> is true exactly when a durable tainted state is in force and the
/// host has not yet started under it. A blocked or refused transition never sets it, because
/// restarting resolves nothing an operator has not already been told to fix.
/// </remarks>
public sealed record HostProcessToolsTransitionResult(
    Guid TransitionId,
    HostProcessToolsTransitionOutcome Outcome,
    bool RestartRequired,
    HostProcessToolsTransitionBlocker Blocker = HostProcessToolsTransitionBlocker.None);

/// <summary>
/// The offline transition port. One method, no capabilities in its signature.
/// </summary>
/// <remarks>
/// The command line hands it a random identity and nothing else. Every lock, edition check, secret,
/// marker byte, database row, and compensation decision belongs to the implementation, so no caller —
/// present or future — can supply a forged environment, a borrowed lock, or a marker it authored.
/// </remarks>
public interface IHostProcessToolsTransitionService
{

    Task<Result<HostProcessToolsTransitionResult>> EnableAsync(
        HostProcessToolsTransitionRequest request,
        CancellationToken cancellationToken = default);

}
