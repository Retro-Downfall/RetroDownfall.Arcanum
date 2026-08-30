using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// The complete, explicit authority classification of one inference invocation.
/// </summary>
/// <remarks>
/// Every <c>IArcanumIntelligenceProvider</c>, <c>IContextPreviewService</c>, and turn-facade call takes
/// one of these as a required argument with no default. That is the enforcement mechanism: a new
/// internal caller cannot compile without deciding whether it is an operator surface or
/// <see cref="None"/>, so Covenant eligibility can never again be inferred from a missing service or an
/// absent Session (§10.12).
///
/// <para>Nonserializable and absent from every source-generated JSON context. A wire type that could
/// carry this would let a request body name its own execution surface, which is the one input this
/// value exists to refuse.</para>
///
/// <para><see cref="CanReadCovenant"/>, <see cref="CanStageCovenantMutation"/>, and
/// <see cref="CanPrepareCovenantRetirement"/> are the only eligibility answers in the system.
/// Keeping the truth table here, rather than restating it at each seam, is what stops two callers
/// from disagreeing about what an unattended stateless preview may do.</para>
/// </remarks>
public sealed class ArcanumInvocationContext
{

    private ArcanumInvocationContext(
        ArcanumExecutionSurface surface,
        CanonicalCampaignContext? campaign,
        InvocationAttendance attendance,
        CovenantContextPolicy contextPolicy,
        ToolPolicy toolPolicy,
        CovenantReadAuthorityEpoch? readAuthorityEpoch)
    {

        Surface = surface;

        Campaign = campaign;

        Attendance = attendance;

        ContextPolicy = contextPolicy;

        ToolPolicy = toolPolicy;

        ReadAuthorityEpoch = readAuthorityEpoch;

    }

    /// <summary>
    /// The authority-free context every unattended, subagent, A2A, batch, recovery, and background
    /// caller passes.
    /// </summary>
    public static ArcanumInvocationContext None { get; } = new(
        ArcanumExecutionSurface.InternalBackground,
        null,
        InvocationAttendance.Unattended,
        CovenantContextPolicy.None,
        ToolPolicy.NoTools,
        null);

    /// <summary>Which kind of caller is driving this invocation.</summary>
    public ArcanumExecutionSurface Surface { get; }

    /// <summary>
    /// The canonically resolved Campaign binding, or <see langword="null"/> when none was resolved.
    /// </summary>
    /// <remarks>
    /// A Global-only context is a resolved answer and is distinct from <see langword="null"/>, which
    /// means Campaign resolution never ran for this surface at all.
    /// </remarks>
    public CanonicalCampaignContext? Campaign { get; }

    /// <summary>Whether an operator is present to answer an interactive Ward.</summary>
    public InvocationAttendance Attendance { get; }

    /// <summary>The request's context policy. <c>None</c> is irrevocable once set at the boundary.</summary>
    public CovenantContextPolicy ContextPolicy { get; }

    /// <summary>The tool policy in force for this invocation.</summary>
    public ToolPolicy ToolPolicy { get; }

    /// <summary>
    /// The clean authority epoch the authenticated boundary observed, or <see langword="null"/> when
    /// this invocation carries no Covenant authority.
    /// </summary>
    public CovenantReadAuthorityEpoch? ReadAuthorityEpoch { get; }

    /// <summary>Whether this invocation is one of the three operator-facing surfaces.</summary>
    public bool IsOperatorSurface => IsOperatorSurfaceCode(Surface);

    /// <summary>
    /// Whether this invocation may load and admit Covenant context.
    /// </summary>
    /// <remarks>
    /// Requires all three of: an operator surface, an unrevoked default context policy, and a read
    /// authority epoch issued by the authenticated boundary. Any one of them missing means no.
    /// </remarks>
    public bool CanReadCovenant =>
        IsOperatorSurface
        && ContextPolicy is CovenantContextPolicy.Default
        && ReadAuthorityEpoch is not null;

    /// <summary>
    /// Whether this invocation may expose a Covenant mutation tool and stage a proposal.
    /// </summary>
    /// <remarks>
    /// Strictly narrower than <see cref="CanReadCovenant"/>. Staging needs a durable assistant entry to
    /// publish against, so only a session-backed turn qualifies; it needs tools at all; and it needs a
    /// Campaign, because the Proposed lane is Campaign-only and there is no Global scope for an agent
    /// to write into. Proposal staging also remains limited to an attended operator turn.
    /// </remarks>
    public bool CanStageCovenantMutation =>
        Attendance is InvocationAttendance.Attended
        && CanPrepareCovenantRetirement;

    /// <summary>
    /// Whether this invocation may prepare an exact, one-call Covenant retirement.
    /// </summary>
    /// <remarks>
    /// Shares every proposal-staging authority condition except attendance. Retirement has no
    /// operator decision point, but still requires a session-backed turn, tools, read authority, and
    /// a canonical Campaign before its exact preflight and nonce can authorize one effect.
    /// </remarks>
    public bool CanPrepareCovenantRetirement =>
        CanReadCovenant
        && Surface is ArcanumExecutionSurface.SessionBackedOperatorTurn
        && ToolPolicy is not ToolPolicy.NoTools
        && Campaign is { IsCampaignBound: true };

    /// <summary>
    /// Builds an invocation context, refusing authority a surface may never carry.
    /// </summary>
    public static Result<ArcanumInvocationContext> Create(
        ArcanumExecutionSurface surface,
        CanonicalCampaignContext? campaign,
        InvocationAttendance attendance,
        CovenantContextPolicy contextPolicy,
        ToolPolicy toolPolicy,
        CovenantReadAuthorityEpoch? readAuthorityEpoch)
    {

        if (!Enum.IsDefined(surface))
        {
            return Result<ArcanumInvocationContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "An undefined execution surface cannot be invoked."));
        }

        if (!IsOperatorSurfaceCode(surface) && readAuthorityEpoch is not null)
        {
            return Result<ArcanumInvocationContext>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This execution surface cannot carry Covenant authority."));
        }

        return Result<ArcanumInvocationContext>.Success(
            new ArcanumInvocationContext(
                surface,
                campaign,
                attendance,
                contextPolicy,
                toolPolicy,
                readAuthorityEpoch));

    }

    private static bool IsOperatorSurfaceCode(ArcanumExecutionSurface surface) =>
        surface is ArcanumExecutionSurface.SessionBackedOperatorTurn
            or ArcanumExecutionSurface.StatelessOperatorTurn
            or ArcanumExecutionSurface.ContextInspection;

}
