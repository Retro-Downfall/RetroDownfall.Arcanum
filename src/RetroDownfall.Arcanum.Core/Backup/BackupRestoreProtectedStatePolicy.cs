namespace RetroDownfall.Arcanum.Core.Backup;

/// <summary>
/// What a restore is permitted to do next, once its protected-state mode has been weighed against
/// what the archive actually carries.
/// </summary>
/// <remarks>
/// Three arms rather than a Boolean, because "preserve it" and "remove it" are opposite destructive
/// acts and neither may be reached by defaulting. <see cref="PurgeStaging"/> is an instruction rather
/// than a permission: the staged generation has to be emptied of the Covenant family before the
/// replacement, so a caller that read this as "you may proceed" would publish exactly the data the
/// operator asked to be gone.
/// </remarks>
public enum BackupRestoreProtectedStateOutcome : byte
{

    /// <summary>Continue with the archive's protected state exactly as it arrived.</summary>
    Proceed = 1,

    /// <summary>Continue only after the Covenant family and every label are removed from staging.</summary>
    PurgeStaging = 2,

    /// <summary>Refuse. Nothing is staged, nothing is displaced, and no owner is acquired.</summary>
    Refuse = 3,

}

/// <summary>
/// What an archive carries in the way of protected state, in counts and one taint bit.
/// </summary>
/// <remarks>
/// Content-free by construction. The decision below only needs to know whether there is protected
/// state at all and whether the machine that produced it could still prove a clean authority state;
/// carrying an identity, a key, or a Session here would put archive content into every refusal message
/// and durable phase record the restore writes (§10.19.10).
///
/// <para>The three counts are kept apart because they answer different questions. A canonical row is
/// Covenant memory the archive would reinstate; an accelerator row is that same memory's search
/// projection, which carries the authored and compiled text itself; a label is the only durable record
/// that some <em>other</em> artifact is Covenant-derived. An archive can carry any of the three without
/// the others, and every one of them is protected state.</para>
/// </remarks>
public sealed record BackupRestoreProtectedStateInventory(
    long CanonicalRows,
    long ProtectedArtifacts,
    bool SourceAuthorityTainted,
    long AcceleratorRows = 0)
{

    /// <summary>What an archive with no readable Covenant tier contributes.</summary>
    public static BackupRestoreProtectedStateInventory None { get; } = new(0, 0, false);

    /// <summary>Whether this archive would reinstate Covenant memory, its index, or a label.</summary>
    public bool CarriesProtectedState =>
        CanonicalRows > 0 || AcceleratorRows > 0 || ProtectedArtifacts > 0;

}

/// <summary>
/// One protected-state decision, with the typed refusal to report when it is one.
/// </summary>
public sealed record BackupRestoreProtectedStateDecision(
    BackupRestoreProtectedStateOutcome Outcome,
    BackupVerifyIssue? Blocker)
{

    /// <summary>Whether this decision stops the restore.</summary>
    public bool IsRefusal => Outcome is BackupRestoreProtectedStateOutcome.Refuse;

}

/// <summary>
/// The sole authority on what <see cref="BackupProtectedStateMode"/> authorizes against one archive.
/// </summary>
/// <remarks>
/// Pure, and in Core, because the same decision has to be reachable from the plan a rehearsal returns,
/// from the restore that executes it, and from an operator surface that has to explain the refusal
/// before anything is created. Two implementations that agreed on the day they were written would, at
/// their first divergence, let one surface promise a refusal the other does not make.
///
/// <para>The two entry points are the two moments a restore can answer. <see cref="EvaluateRequest"/>
/// needs nothing but the request and whether this installation runs the Covenant arm at all, so it
/// answers before the archive is opened. <see cref="EvaluateArchive"/> needs the extracted snapshot,
/// so it answers after extraction and <em>before</em> the staged generation is composed or an
/// exclusive owner exists.</para>
/// </remarks>
public static class BackupRestoreProtectedStatePolicy
{

    /// <summary>A protected-state mode was requested for a restore that displaces nothing.</summary>
    public const string ModeNotApplicableCode = "backup.restore_protected_state_mode_not_applicable";

    /// <summary>A protected-state mode was requested on an installation with no Covenant arm.</summary>
    public const string CovenantRequiredCode = "backup.restore_protected_state_covenant_required";

    /// <summary>A destructive protected-state choice was not separately confirmed.</summary>
    public const string ConfirmationRequiredCode =
        "backup.restore_protected_state_confirmation_required";

    /// <summary>The archive carries protected state and no explicit mode authorizes it.</summary>
    public const string ProtectedStatePresentCode = "backup.restore_protected_state_present";

    /// <summary>The archive's own authority state is not clean and it carries protected state.</summary>
    public const string SourceTaintedCode = "backup.restore_protected_state_source_tainted";

    /// <summary>The wire spelling every refusal names as the one supported continuation.</summary>
    private const string PurgeWireValue = "purge-protected-state";

    private const string Unmodified = "The current installation was not modified.";

    /// <summary>
    /// Decides whether the requested mode is even applicable to this restore, ignoring confirmation.
    /// </summary>
    /// <param name="request">The restore as the operator asked for it.</param>
    /// <param name="covenantArmActive">
    /// Whether this restore runs the Covenant arm — the feature gate is on <em>and</em> the conflict
    /// mode displaces an installation. A restore that does not enter that arm has no staged Covenant
    /// tier to preserve or remove.
    /// </param>
    /// <remarks>
    /// This is the arm a <em>plan</em> may report, and confirmation is deliberately outside it. A
    /// rehearsal is what an operator surface reads in order to compose the confirmation it is about to
    /// ask for, so a plan that already refused for want of that answer could never be used to obtain it.
    /// </remarks>
    public static BackupRestoreProtectedStateDecision EvaluateRequestShape(
        BackupRestoreRequest request,
        bool covenantArmActive)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.ProtectedStateMode is BackupProtectedStateMode.Reject)
        {

            // The default is what every caller that predates this slice sends, and it authorizes no
            // effect of its own. Refusing here would change the pre-Covenant restore path.
            return Proceed;

        }

        // Applicability comes first because it is the most fundamental refusal: the mode names an
        // effect only a replacement performs, so on any other conflict mode there is nothing for the
        // operator's answer to be an answer about.
        if (request.ConflictMode is not BackupRestoreConflictMode.ReplaceInstallation)
        {

            return Refuse(
                ModeNotApplicableCode,
                "A protected-state mode applies only to a replace-installation restore. A new profile "
                + "root displaces nothing, and a selective Session import carries its own protected "
                + "transfer. Re-run without the protected-state mode, or with "
                + "--conflict-mode replace-installation. "
                + Unmodified);

        }

        return covenantArmActive
            ? Proceed
            : Refuse(
                CovenantRequiredCode,
                "This installation does not run the Covenant restore arm, so a protected-state mode "
                + "would authorize nothing. Enable Arcanum:Features:Covenant before restoring with an "
                + "explicit protected-state mode. "
                + Unmodified);

    }

    /// <summary>
    /// Decides everything that can be decided before the archive has been read, confirmation included.
    /// </summary>
    /// <remarks>
    /// The mutating path's entry point. It is <see cref="EvaluateRequestShape"/> plus the one question a
    /// rehearsal cannot answer for itself, so the two can never disagree about applicability.
    /// </remarks>
    public static BackupRestoreProtectedStateDecision EvaluateRequest(
        BackupRestoreRequest request,
        bool covenantArmActive)
    {

        BackupRestoreProtectedStateDecision shape = EvaluateRequestShape(request, covenantArmActive);

        if (shape.IsRefusal || request.ProtectedStateMode is BackupProtectedStateMode.Reject)
        {

            return shape;

        }

        return request.ProtectedStateConfirmed
            ? Proceed
            : Refuse(
                ConfirmationRequiredCode,
                "Preserving or purging the archive's protected state is a separate destructive choice "
                + "from replacing the installation, and it requires its own confirmation. "
                + Unmodified);

    }

    /// <summary>
    /// Decides what the requested mode authorizes against the protected state the archive holds.
    /// </summary>
    /// <remarks>
    /// Reached only for a restore whose <see cref="EvaluateRequest"/> already proceeded, which is why
    /// it takes the mode rather than the whole request: everything else about the request has already
    /// been weighed, and a second reading of the confirmation flags here would be a second place that
    /// could disagree about whether the operator answered.
    /// </remarks>
    public static BackupRestoreProtectedStateDecision EvaluateArchive(
        BackupProtectedStateMode mode,
        BackupRestoreProtectedStateInventory inventory)
    {

        ArgumentNullException.ThrowIfNull(inventory);

        if (mode is BackupProtectedStateMode.PurgeProtectedState)
        {

            // Unconditional, including over an archive that carries nothing. An operator who asked for
            // a purge is entitled to a report that says zero rather than to a silently different path,
            // and running the removal over an empty family is what makes a retry idempotent.
            return new BackupRestoreProtectedStateDecision(
                BackupRestoreProtectedStateOutcome.PurgeStaging,
                Blocker: null);

        }

        if (!inventory.CarriesProtectedState)
        {

            // Includes a source-tainted archive with nothing protected in it. The taint still joins
            // into this machine monotonically; there is simply nothing here to promote.
            return Proceed;

        }

        if (mode is BackupProtectedStateMode.Reject)
        {

            return Refuse(
                ProtectedStatePresentCode,
                "This archive carries Covenant canonical rows or protected artifacts, and a restore "
                + "will not reinstate protected state by default. Re-run with an explicit, separately "
                + "confirmed protected-state mode: restore-protected-state to preserve it from a clean "
                + "source, or " + PurgeWireValue + " to remove it from staging before replacement. "
                + Unmodified);

        }

        return inventory.SourceAuthorityTainted
            ? Refuse(
                SourceTaintedCode,
                "The machine this archive was taken from could not prove a clean authority state, so "
                + "its protected state cannot be preserved here: adopting it would move data that was "
                + "exposed to unsandboxed host tools into this installation. The only supported "
                + "continuation is a separately confirmed " + PurgeWireValue + " restore. "
                + Unmodified)
            : Proceed;

    }

    private static BackupRestoreProtectedStateDecision Proceed { get; } =
        new(BackupRestoreProtectedStateOutcome.Proceed, Blocker: null);

    private static BackupRestoreProtectedStateDecision Refuse(string code, string message) =>
        new(
            BackupRestoreProtectedStateOutcome.Refuse,
            new BackupVerifyIssue(code, message));

}
