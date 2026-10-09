using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal sealed record TurnIdempotencyRequestIdentity(
    Guid ClientTurnId,
    CovenantDigest AcceptedBodyDigest,
    SessionTurnSurface Surface,
    SessionTurnRouteValue? Route);

/// <summary>
/// Server-owned identity and ownership token of the accepted idempotent HTTP request. These are
/// never request DTO members; a body cannot mint its own claim or override the accepted fingerprint.
/// </summary>
internal static class TurnIdempotencyAmbient
{
    private static readonly AsyncLocal<State?> CurrentLocal = new();

    public static CancellationToken OwnershipLostToken =>
        CurrentLocal.Value?.OwnershipLostToken ?? CancellationToken.None;

    public static TurnIdempotencyRequestIdentity? RequestIdentity => CurrentLocal.Value?.Identity;

    public static void PublishIdentity(
        string claimKeyHash,
        string fingerprintHash,
        SessionTurnSurface surface = SessionTurnSurface.Intelligence,
        SessionTurnRouteValue? route = null)
    {
        byte[] key = Convert.FromHexString(claimKeyHash);

        byte[] body = Convert.FromHexString(fingerprintHash);

        if (key.Length != 32 || body.Length != 32)
        {
            throw new ArgumentException("An accepted HTTP identity requires SHA-256 evidence.");
        }

        byte[] domain = Encoding.UTF8.GetBytes("Arcanum.SessionTurn.HttpIdentity.v1\0");

        byte[] input = new byte[domain.Length + key.Length];

        domain.CopyTo(input, 0);

        key.CopyTo(input, domain.Length);

        byte[] digest = SHA256.HashData(input);

        TurnIdempotencyRequestIdentity identity = new(new Guid(digest.AsSpan(0, 16), bigEndian: true), new(body), surface, route);

        CurrentLocal.Value = new State(OwnershipLostToken, identity);
    }

    public static void Publish(CancellationToken ownershipLostToken = default) =>
        CurrentLocal.Value = new State(ownershipLostToken, RequestIdentity);

    public static void Clear() => CurrentLocal.Value = null;

    private sealed record State(CancellationToken OwnershipLostToken, TurnIdempotencyRequestIdentity? Identity);
}
