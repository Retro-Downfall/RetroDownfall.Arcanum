namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Client-disconnect behavior for streaming inference (see ADR 0003).
/// </summary>
public enum DisconnectPolicy
{

    /// <summary>
    /// Cancel inference on client disconnect; idempotency claim → Abandoned; release unused
    /// reservation but still ledger any provider-billed partial usage.
    /// </summary>
    CancelAbandoned = 0,

    /// <summary>
    /// Keep inference running after disconnect; claim may Complete for later replay; run keeps
    /// consuming reservation and is fully ledgered.
    /// </summary>
    ContinueThenReplay = 1,

    /// <summary>
    /// Default: <see cref="ContinueThenReplay"/> when the request carries an <c>Idempotency-Key</c>;
    /// otherwise <see cref="CancelAbandoned"/>.
    /// </summary>
    Auto = 2,

}
