using A2A;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Resolves what the Agent Card ("Heraldry") advertises, and enforces it on inbound Sendings.
/// </summary>
/// <remarks>
/// <para>
/// The card used to hard-code a single skill and <c>text/plain</c> in and out, with no operator control
/// beyond name and description — so an Arcanum that could do more had no way to say so, and a peer that
/// asked for something else was silently answered as text anyway (issue #63).
/// </para>
/// <para>
/// Defaults are unchanged on purpose: an operator who configures nothing gets exactly the card Arcanum
/// served before, so existing peers keep working. Additions are additive and versioned by the card's own
/// <c>Version</c> field (issue #12's backward-compatibility constraint).
/// </para>
/// </remarks>
public static class A2AAgentCardPolicy
{

    /// <summary>The modality Arcanum has always spoken, and still the default in and out.</summary>
    public const string TextPlain = "text/plain";

    /// <summary>Identifier of the skill Arcanum advertises when an operator declares none.</summary>
    public const string DefaultSkillId = "apprentice-goal-execution";

    private const string DefaultSkillName = "Autonomous goal execution";

    private const string DefaultSkillDescription =
        "Delegates a natural-language goal to an Arcanum Apprentice: an autonomous sub-agent with file, command, and tool access inside a sandboxed workspace.";

    /// <summary>Media types Arcanum accepts on an inbound Sending.</summary>
    public static string[] ResolveInputModes(ConclaveA2ASettings a2a) =>
        Normalize(a2a.InputModes) is { Length: > 0 } declared ? declared : [TextPlain];

    /// <summary>Media types Arcanum can answer an inbound Sending with.</summary>
    public static string[] ResolveOutputModes(ConclaveA2ASettings a2a) =>
        Normalize(a2a.OutputModes) is { Length: > 0 } declared ? declared : [TextPlain];

    /// <summary>
    /// Skills advertised on the card. Falls back to the single historical skill so an operator who
    /// declares nothing serves a card identical to the pre-#63 one.
    /// </summary>
    public static AgentSkill[] ResolveSkills(ConclaveA2ASettings a2a)
    {

        string[] cardInputModes = ResolveInputModes(a2a);

        string[] cardOutputModes = ResolveOutputModes(a2a);

        A2ASkillSettings[] declared = [.. (a2a.Skills ?? []).Where(static s => !string.IsNullOrWhiteSpace(s.Id))];

        if (declared.Length == 0)
        {

            return
            [
                new AgentSkill
                {
                    Id = DefaultSkillId,
                    Name = DefaultSkillName,
                    Description = DefaultSkillDescription,
                    InputModes = [.. cardInputModes],
                    OutputModes = [.. cardOutputModes],
                },
            ];

        }

        return
        [
            .. declared.Select(skill => new AgentSkill
            {
                Id = skill.Id!.Trim(),
                Name = string.IsNullOrWhiteSpace(skill.Name) ? skill.Id!.Trim() : skill.Name!.Trim(),
                Description = string.IsNullOrWhiteSpace(skill.Description)
                    ? DefaultSkillDescription
                    : skill.Description!.Trim(),
                InputModes = [.. Normalize(skill.InputModes) is { Length: > 0 } skillIn ? skillIn : cardInputModes],
                OutputModes = [.. Normalize(skill.OutputModes) is { Length: > 0 } skillOut ? skillOut : cardOutputModes],
            }),
        ];

    }

    /// <summary>
    /// Checks the output modalities a peer said it would accept against what this instance advertises.
    /// </summary>
    /// <param name="acceptedOutputModes">
    /// The peer's <c>SendMessageConfiguration.AcceptedOutputModes</c>. Absent or empty means the peer
    /// stated no preference, which is not a mismatch.
    /// </param>
    /// <returns>
    /// Success carrying the mode that will be used, or a failure naming both sides of the mismatch so the
    /// peer learns what to ask for instead of receiving a silent downgrade to text.
    /// </returns>
    public static Result<string> ValidateRequestedModes(
        ArcanumSettings settings,
        IReadOnlyList<string>? acceptedOutputModes) =>
        ValidateRequestedModes(settings.ResolveA2A(), acceptedOutputModes);

    /// <inheritdoc cref="ValidateRequestedModes(ArcanumSettings, IReadOnlyList{string}?)"/>
    public static Result<string> ValidateRequestedModes(
        ConclaveA2ASettings a2a,
        IReadOnlyList<string>? acceptedOutputModes)
    {

        string[] supported = ResolveOutputModes(a2a);

        string[] requested = Normalize(acceptedOutputModes);

        if (requested.Length == 0)
        {

            return Result<string>.Success(supported[0]);

        }

        foreach (string candidate in requested)
        {

            foreach (string mode in supported)
            {

                if (string.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase))
                {

                    return Result<string>.Success(mode);

                }

            }

        }

        return Result<string>.Failure(new Error(
            ErrorCodes.Sending.TaskRejected,
            $"This agent cannot produce any of the requested output modes ({string.Join(", ", requested)}). "
            + $"It advertises {string.Join(", ", supported)} on its Agent Card."));

    }

    private static string[] Normalize(IReadOnlyList<string>? values)
    {

        if (values is null)
        {

            return [];

        }

        List<string> normalized = [];

        foreach (string value in values)
        {

            if (string.IsNullOrWhiteSpace(value))
            {

                continue;

            }

            string trimmed = value.Trim();

            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {

                normalized.Add(trimmed);

            }

        }

        return [.. normalized];

    }

}
