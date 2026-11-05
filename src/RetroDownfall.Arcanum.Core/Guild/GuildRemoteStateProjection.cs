using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>A transport observation cannot create terminal completion evidence.</summary>
public sealed record GuildRemoteStateProjection(CommissionState State, bool ReconciliationRequired, string? ReasonCode, string? RemoteStateDiagnostic)
{
    /// <summary>Representation bound for a diagnostic observation, not a work/retry/lifetime ceiling.</summary>
    public const int MaximumDiagnosticCharacters = 128;

    public static GuildRemoteStateProjection Observe(string? remoteState)
    {
        CommissionState? state = remoteState switch
        {
            "submitted" => CommissionState.Submitted,
            "working" => CommissionState.Working,
            "input-required" => CommissionState.InputRequired,
            "completed" => CommissionState.Completed,
            "failed" => CommissionState.Failed,
            "canceled" => CommissionState.Canceled,
            "rejected" => CommissionState.Rejected,
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

        return new(CommissionState.Working, true, ErrorCodes.Guild.UnsupportedTransportState, diagnostic);
    }
}
