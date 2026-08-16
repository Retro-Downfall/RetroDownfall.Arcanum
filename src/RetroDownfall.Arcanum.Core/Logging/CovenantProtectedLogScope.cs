using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Logging;

/// <summary>
/// The only shape Covenant-derived work is allowed to take in a log, metric, trace, or progress
/// record.
/// </summary>
/// <remarks>
/// Every field here is a decision, a count, or an opaque identity. No Covenant key, authored
/// fragment, compiled fragment, attachment text, raw content hash, or raw search text can be
/// represented, because these records travel to sinks — ring buffers, files, metrics backends,
/// unauthenticated status routes — that have no authority to hold protected content (§10.12).
///
/// <para>A type rather than a convention. Guidance to "avoid logging Covenant content" is advice
/// that a later change silently stops following; a value that cannot carry content is a guarantee
/// that survives whoever writes the next log line.</para>
/// </remarks>
public sealed record CovenantProtectedLogScope
{

    private CovenantProtectedLogScope(
        ContentSensitivity sensitivity,
        GenerationProvenanceMode provenanceMode,
        int generationCount,
        CovenantDiagnosticTag? evidenceTag,
        Guid? sessionId,
        Guid? campaignId)
    {

        Sensitivity = sensitivity;

        ProvenanceMode = provenanceMode;

        GenerationCount = generationCount;

        EvidenceTag = evidenceTag;

        SessionId = sessionId;

        CampaignId = campaignId;

    }

    /// <summary>Whether the work this record describes touched Covenant-derived content.</summary>
    public ContentSensitivity Sensitivity { get; }

    /// <summary>Whether provenance was exact or had overflowed to the diagnostic bitset.</summary>
    public GenerationProvenanceMode ProvenanceMode { get; }

    /// <summary>
    /// How many source generations contributed, or zero once provenance overflowed.
    /// </summary>
    /// <remarks>
    /// A count rather than the identities. The count is enough to see that history spans a restore
    /// or a reset; the identities would let a log reader correlate protected artifacts across
    /// Sessions.
    /// </remarks>
    public int GenerationCount { get; }

    /// <summary>An opaque, installation-keyed correlation tag, when one was minted.</summary>
    public CovenantDiagnosticTag? EvidenceTag { get; }

    /// <summary>The owning Session, which is already an ordinary identity in these records.</summary>
    public Guid? SessionId { get; }

    /// <summary>The owning Campaign, on the same footing.</summary>
    public Guid? CampaignId { get; }

    /// <summary>The scope for work that touched nothing protected.</summary>
    public static CovenantProtectedLogScope None { get; } = new(
        ContentSensitivity.None,
        GenerationProvenanceMode.Exact,
        0,
        null,
        null,
        null);

    /// <summary>
    /// Reduces a real label to the content-free facts a log may carry.
    /// </summary>
    /// <remarks>
    /// Deliberately the only construction path from a label. It drops the content digest, the
    /// sensitivity digest, the label digest, and the producing plan and admission digests — all of
    /// which are correlatable evidence about protected content even though none of them is readable.
    /// </remarks>
    public static CovenantProtectedLogScope FromLabel(
        ArtifactSensitivityLabel label,
        CovenantDiagnosticTag? evidenceTag = null)
    {

        ArgumentNullException.ThrowIfNull(label);

        return new CovenantProtectedLogScope(
            label.Sensitivity,
            label.Provenance.Mode,
            label.Provenance.Mode is GenerationProvenanceMode.Exact
                ? label.Provenance.ExactGenerationIds.Length
                : 0,
            evidenceTag,
            label.SessionId,
            label.CampaignId);

    }

    /// <summary>Reduces a sensitivity and its provenance to the same content-free facts.</summary>
    public static CovenantProtectedLogScope FromSensitivity(
        ContentSensitivity sensitivity,
        GenerationProvenance provenance,
        Guid? sessionId = null,
        Guid? campaignId = null,
        CovenantDiagnosticTag? evidenceTag = null)
    {

        ArgumentNullException.ThrowIfNull(provenance);

        ContentSensitivityAlgebra.Validate(sensitivity, provenance, nameof(provenance));

        return new CovenantProtectedLogScope(
            sensitivity,
            provenance.Mode,
            provenance.Mode is GenerationProvenanceMode.Exact
                ? provenance.ExactGenerationIds.Length
                : 0,
            evidenceTag,
            sessionId,
            campaignId);

    }

    /// <summary>
    /// The one rendering a log message may interpolate.
    /// </summary>
    /// <remarks>
    /// Fixed vocabulary and no free text, so a structured sink and a plain-text sink disclose the
    /// same nothing.
    /// </remarks>
    public override string ToString() =>
        Sensitivity is ContentSensitivity.None
            ? "sensitivity=none"
            : $"sensitivity=covenant-derived provenance={ProvenanceMode.ToString().ToLowerInvariant()} generations={GenerationCount}";

}
