using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>A transport observation cannot create terminal completion evidence.</summary>
public sealed record GuildRemoteStateProjection(QuestState State, bool ReconciliationRequired, string? ReasonCode, string? RemoteStateDiagnostic)
{
    /// <summary>Representation bound for a diagnostic observation, not a work/retry/lifetime ceiling.</summary>
    public const int MaximumDiagnosticCharacters = 128;

    public static GuildRemoteStateProjection Observe(string? remoteState)
    {
        QuestState? state = remoteState switch
        {
            "submitted" => QuestState.Submitted,
            "working" => QuestState.Working,
            "input-required" => QuestState.InputRequired,
            "completed" => QuestState.Completed,
            "failed" => QuestState.Failed,
            "canceled" => QuestState.Canceled,
            "rejected" => QuestState.Rejected,
            _ => null
        };

        if (state is { } known)
            return new(known, false, null, null);

        string value = remoteState ?? "<missing>";

        int length = Math.Min(value.Length, MaximumDiagnosticCharacters);

        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;

        string diagnostic = string.Create(length, value, static (destination, source) =>
        {
            for (int index = 0; index < destination.Length; index++)
                destination[index] = char.IsControl(source[index]) ? '?' : source[index];
        });

        return new(QuestState.Working, true, ErrorCodes.Guild.UnsupportedTransportState, diagnostic);
    }
}
