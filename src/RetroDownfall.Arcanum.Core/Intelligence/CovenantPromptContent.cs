using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// The exact Covenant section text one provider attempt renders into the system prompt.
/// </summary>
/// <remarks>
/// This is a rendering input, not a decision. Every eligibility, shadowing, ordering, and pressure
/// decision was already made by the linker and the per-attempt admission; by the time a value of
/// this type exists the only remaining question is where the bytes go. Keeping the decision and the
/// placement in separate types is what lets the admission loop re-render a shorter Proposed prefix
/// without re-running the linker (§10.13).
///
/// <para>The three strings are the compiled section bodies exactly as
/// <c>CovenantTurnSection.RenderedBytes</c> produced them: bullet fragments for the two Confirmed
/// lanes, and the complete adaptive-fenced block for Proposed. The prompt builder owns the headings
/// and the untrusted-data notice and nothing else, so a fragment can never acquire framing that the
/// compiler did not measure.</para>
/// </remarks>
public sealed class CovenantPromptContent
{

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private CovenantPromptContent(
        string globalConfirmed,
        string campaignConfirmed,
        string campaignProposed)
    {

        GlobalConfirmed = globalConfirmed;

        CampaignConfirmed = campaignConfirmed;

        CampaignProposed = campaignProposed;

    }

    /// <summary>The absent value. Rendering it produces byte-for-byte pre-Covenant output.</summary>
    public static CovenantPromptContent None { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty);

    /// <summary>Compiled Global Confirmed fragments, or empty when the section has no content.</summary>
    public string GlobalConfirmed { get; }

    /// <summary>Compiled Campaign Confirmed fragments, or empty when the section has no content.</summary>
    public string CampaignConfirmed { get; }

    /// <summary>The complete fenced Proposed block, or empty when the section has no content.</summary>
    public string CampaignProposed { get; }

    public bool HasGlobalConfirmed => GlobalConfirmed.Length > 0;

    public bool HasCampaignConfirmed => CampaignConfirmed.Length > 0;

    public bool HasConfirmed => HasGlobalConfirmed || HasCampaignConfirmed;

    public bool HasProposed => CampaignProposed.Length > 0;

    public bool IsEmpty => !HasConfirmed && !HasProposed;

    /// <summary>Renders the three sections a linked plan would inject with no pressure applied.</summary>
    public static CovenantPromptContent FromPlan(CovenantTurnPlan plan)
    {

        ArgumentNullException.ThrowIfNull(plan);

        return FromSections(
            plan.GlobalConfirmedSection,
            plan.CampaignConfirmedSection,
            plan.CampaignProposedSection);

    }

    /// <summary>
    /// Renders the sections one provider attempt actually admitted.
    /// </summary>
    /// <remarks>
    /// The receipt is the only honest source for a live call: the plan describes what was eligible,
    /// while the receipt describes what survived this attempt's context pressure.
    /// </remarks>
    public static CovenantPromptContent FromAdmission(CovenantAdmissionReceipt admission)
    {

        ArgumentNullException.ThrowIfNull(admission);

        return FromSections(
            admission.AdmittedGlobalConfirmedSection,
            admission.AdmittedCampaignConfirmedSection,
            admission.AdmittedCampaignProposedSection);

    }

    /// <summary>
    /// Renders the sections one admission decision would inject, without needing a frozen provider
    /// call to exist yet.
    /// </summary>
    /// <remarks>
    /// The pressure loop is circular without this: the provider-call envelope has to be frozen over
    /// the exact bytes that were sent, but which bytes those are is precisely what the loop is
    /// deciding. So the loop measures candidate content through here, and only the winning content
    /// is ever rendered, hashed, and frozen.
    ///
    /// <para>A Confirmed no-fit admits nothing at all, including Proposed. Confirmed capacity is
    /// guaranteed, so an attempt that cannot carry it is a capacity failure rather than an attempt
    /// that quietly proceeds with only the agent's suggestions.</para>
    /// </remarks>
    public static CovenantPromptContent FromAdmittedPrefix(
        CovenantTurnPlan plan,
        bool confirmedAdmitted,
        int admittedProposedCount)
    {

        ArgumentNullException.ThrowIfNull(plan);

        ArgumentOutOfRangeException.ThrowIfNegative(admittedProposedCount);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            admittedProposedCount,
            plan.CampaignProposedSection.Candidates.Length);

        if (!confirmedAdmitted)
        {
            return None;
        }

        CovenantTurnSection proposed =
            admittedProposedCount == plan.CampaignProposedSection.Candidates.Length
                ? plan.CampaignProposedSection
                : CovenantTurnSection.Create(
                    CovenantPlacement.CampaignProposed,
                    plan.CampaignProposedSection.Candidates.Take(admittedProposedCount));

        return FromSections(
            plan.GlobalConfirmedSection,
            plan.CampaignConfirmedSection,
            proposed);

    }

    public static CovenantPromptContent FromSections(
        CovenantTurnSection globalConfirmed,
        CovenantTurnSection campaignConfirmed,
        CovenantTurnSection campaignProposed)
    {

        ArgumentNullException.ThrowIfNull(globalConfirmed);

        ArgumentNullException.ThrowIfNull(campaignConfirmed);

        ArgumentNullException.ThrowIfNull(campaignProposed);

        RequirePlacement(globalConfirmed, CovenantPlacement.GlobalConfirmed);

        RequirePlacement(campaignConfirmed, CovenantPlacement.CampaignConfirmed);

        RequirePlacement(campaignProposed, CovenantPlacement.CampaignProposed);

        return new CovenantPromptContent(
            Decode(globalConfirmed),
            Decode(campaignConfirmed),
            Decode(campaignProposed));

    }

    private static void RequirePlacement(CovenantTurnSection section, CovenantPlacement expected)
    {

        if (section.Placement != expected)
        {
            throw new ArgumentException(
                "The Covenant prompt section has the wrong placement.",
                nameof(section));
        }

    }

    private static string Decode(CovenantTurnSection section) =>
        section.RenderedBytes.IsEmpty
            ? string.Empty
            : StrictUtf8.GetString(section.RenderedBytes.AsSpan());

}
