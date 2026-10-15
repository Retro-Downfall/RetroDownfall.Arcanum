using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Sanctum;

public sealed record SanctumResult
{
    public bool Allowed { get; init; }

    public string? DenyReason { get; init; }

    public SanctumBreach? Breach { get; init; }

    /// <summary>
    /// Why the guard refused. Meaningful only when <see cref="Allowed"/> is <see langword="false"/>, and kept off the wire: a
    /// caller decides whether to continue from <see cref="SanctumEnforcement.AllowsContinuation"/>, never from this value directly.
    /// </summary>
    [JsonIgnore]
    public SanctumDenialKind DenialKind { get; init; }
}

/// <summary>
/// What a refusal means for <see cref="SanctumMode.AuditOnly"/>, which records every breach and lets the operation go on. The default is
/// <see cref="Policy"/>, so a hand-built <c>Allowed = false</c> verdict is an ordinary policy refusal.
/// </summary>
public enum SanctumDenialKind
{
    /// <summary>The Sanctum's policy forbids the operation. This is the only refusal <see cref="SanctumMode.AuditOnly"/> overrides.</summary>
    Policy,

    /// <summary>
    /// The guard could not evaluate any policy: the campaign identifier is malformed or no longer names a campaign, so there is no configuration
    /// left to consult. Every mode refuses, because an audit-only decision cannot be honoured for a Sanctum that cannot be read.
    /// </summary>
    PolicyUnavailable,

    /// <summary>
    /// The policy forbids the operation and the breach could not be recorded. In <see cref="SanctumMode.AuditOnly"/> the breach row is the only
    /// control, so an unrecorded breach is refused in every mode rather than allowed on a log line alone.
    /// </summary>
    AuditRecordFailed,
}
