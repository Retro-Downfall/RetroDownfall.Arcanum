using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Tower;

/// <summary>
/// The refusals a Session's Campaign binding can be read with, so that a consumer can tell "this Session has no binding row" from every
/// other way the binding can fail to be read intact.
/// </summary>
/// <remarks>
/// A Session row can exist with no binding row (the legacy paths that create a Session before any Campaign question is asked), and the
/// reader reports it as <see cref="ErrorCodes.Covenant.IntegrityFailure"/>, the code every surface already answers with a 503. A malformed
/// binding row (an unknown kind code) fails with the very same code. The two are different facts: the first says the Session was never
/// bound to a Campaign, the second says the stored answer cannot be trusted. A consumer that must decide whether a Session constrains a turn
/// (the Sanctum) has to treat the first as "claims nothing" and the second as "cannot be read", so the first is recognised here, by the one
/// <see cref="Error"/> every producer returns, instead of by comparing a code both of them share. The wire code is unchanged.
/// </remarks>
public static class SessionCampaignBindingErrors
{
    /// <summary>The Session exists and has no immutable Campaign binding row.</summary>
    public static readonly Error NoBinding = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "This Session has no immutable Campaign binding.");

    /// <summary>Whether <paramref name="error"/> is the refusal for a Session that exists and has no binding row.</summary>
    public static bool IsNoBinding(Error error) =>
        string.Equals(error.Code, NoBinding.Code, StringComparison.Ordinal)
        && string.Equals(error.Message, NoBinding.Message, StringComparison.Ordinal);
}
