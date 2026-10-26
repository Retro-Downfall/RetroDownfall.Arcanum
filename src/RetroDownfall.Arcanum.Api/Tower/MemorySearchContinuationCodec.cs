using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class MemorySearchContinuationCodec
{
    internal static string Binding(string query, MemorySearchRequest request, string? workspacePath, MemoryScope scope)
    {
        bool usesWorkspace = request.Scope is MemorySearchScope.Workspace or MemorySearchScope.All;

        using MemoryStream stream = new();

        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (string value in new[] { query, request.Scope.ToString(), request.SessionId?.ToString("D") ?? "",
                usesWorkspace ? request.WorkspaceId?.Trim() ?? "" : "", usesWorkspace ? workspacePath ?? "" : "", scope.Kind.ToString(), scope.CampaignId?.ToString("D") ?? "", "literal-order-v1" }) writer.Write(value);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal static string Write(string binding, MemorySearchPosition position) => "ms1." + Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new MemorySearchCursor(1, binding, position), ArcanumJsonContext.Default.MemorySearchCursor))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool AppliesTo(MemorySearchScope scope, int phase) => scope switch
    {
        MemorySearchScope.All => phase is >= 0 and <= 5,
        MemorySearchScope.Session => phase is 0 or 1,
        MemorySearchScope.Attachments => phase == 2,
        MemorySearchScope.Workspace => phase == 3,
        MemorySearchScope.Saga => phase == 4,
        MemorySearchScope.Lexicon => phase == 5,
        _ => false,
    };

    internal static bool TryRead(string token, string binding, out MemorySearchPosition position)
    {
        position = new(0);

        if (token.Length is < 5 or > 32768 || !token.StartsWith("ms1.", StringComparison.Ordinal)) return false;

        try
        {
            string text = token[4..];

            if (text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;

            text = text.Replace('-', '+').Replace('_', '/');

            text = text.PadRight((text.Length + 3) / 4 * 4, '=');

            MemorySearchCursor? cursor = JsonSerializer.Deserialize(Convert.FromBase64String(text), ArcanumJsonContext.Default.MemorySearchCursor);

            if (cursor is not { Version: 1, Position: { } p } || cursor.Binding != binding || p.Phase is < 0 or > 5
                || p.Time?.Length > (p.Phase == 5 ? 512 : 128) || p.Id?.Length > 512 || p.Path?.Length > 4096 || p.Tier?.Length > 256
                || (p.Phase != 3 && p.Path is not null)
                || (p.Phase != 5 && p.Tier is not null)
                || (p.Phase is 1 or 4 or 5 && p.Number != 0)
                || (p.Id is not null && p.Time is null && p.Phase != 5)
                || (p.Phase == 5 && p.Time is not null)
                || (p.Id is not null && p.Phase == 3 && p.Path is null)
                || (p.Id is not null && p.Phase == 5 && p.Tier is null)
                || (p.Id is null && (p.Time is not null || p.Path is not null || p.Tier is not null || p.Number != 0))) return false;

            position = p;

            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return false;
        }
    }
}
