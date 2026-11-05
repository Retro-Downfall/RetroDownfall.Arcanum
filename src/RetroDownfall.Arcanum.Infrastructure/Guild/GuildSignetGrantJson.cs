using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>A bounded canonical representation, never authentication or a bearer presentation.</summary>
internal static class GuildSignetGrantJson
{
    internal const int MaximumBytes = GuildStore.MaximumContentBytes;

    internal static Result<string> Encode(GuildGrant grant)
    {
        // Every character and array member consumes at least one output byte. This inexpensive
        // lower bound refuses oversized input strings/collections before JSON allocates escaping
        // buffers; the exact UTF-8 check below enforces the persisted record's byte boundary.
        long minimum = 0;

        foreach (GuildWorkspaceGrant item in grant.Workspaces)
        {
            minimum += 37L + item.RelativeRoot.Length;

            if (minimum > MaximumBytes)
            {
                return TooLarge;
            }
        }

        foreach (GuildToolGrant item in grant.Tools)
        {
            minimum += 1L + item.SourceRegistrationId.Length + item.Name.Length;

            if (minimum > MaximumBytes)
            {
                return TooLarge;
            }
        }

        minimum += grant.Memory.Length + (long)grant.Delegation.Length;

        if (minimum > MaximumBytes)
        {
            return TooLarge;
        }

        string json = JsonSerializer.Serialize(grant, GuildJsonContext.Default.GuildGrant);

        return Encoding.UTF8.GetByteCount(json) <= MaximumBytes ? Result<string>.Success(json) : Result<string>.Failure(TooLarge);
    }

    internal static Result RequireEnvelope(string grant, GuildSignetOwnerBinding owners)
    {
        long minimum = grant.Length + (long)(owners.PeerId?.Length ?? 0) + (owners.PeerGrantRevision?.Length ?? 0);

        if (minimum > MaximumBytes)
        {
            return TooLarge;
        }

        long bytes = Encoding.UTF8.GetByteCount(grant) + (long)(owners.PeerId is null ? 0 : Encoding.UTF8.GetByteCount(owners.PeerId))
            + (owners.PeerGrantRevision is null ? 0 : Encoding.UTF8.GetByteCount(owners.PeerGrantRevision));

        return bytes <= MaximumBytes ? Result.Success() : Result.Failure(TooLarge);
    }

    internal static Error TooLarge => new(ErrorCodes.Guild.PayloadTooLarge, "The Guild grant exceeds its authority-record materialization boundary.");
}
