using System.Text;
using System.Text.Json;
using A2A;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>Host-only proof of a single designated remote output; it is not a replacement for the inspectable response.</summary>
internal sealed record GuildFinalOutput(byte[] Utf8, string MimeType)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static GuildFinalOutput? FromTask(AgentTask task) => task.Status.State == TaskState.Completed
        && task.Artifacts is { Count: 1 } artifacts && artifacts[0] is { } artifact ? FromParts(artifact.Parts) : null;

    internal static GuildFinalOutput? FromParts(IReadOnlyList<Part>? parts)
    {
        if (parts is not { Count: 1 }) return null;

        if (parts[0] is not { } part) return null;

        string? text;

        string mime;

        if (part.ContentCase == PartContentCase.Text && part.Text is { } value
            && part.Url is null && part.Raw is null && part.Data is null)
        {
            text = value;

            mime = "text/plain";
        }
        else if (part.ContentCase == PartContentCase.Data && part.Data is JsonElement data
            && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            && part.Text is null && part.Url is null && part.Raw is null)
        {
            text = data.GetRawText();

            mime = "application/json";
        }
        else return null;

        try
        {
            if (StrictUtf8.GetByteCount(text) > GuildStore.MaximumContentBytes) return null;

            return new(StrictUtf8.GetBytes(text), mime);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }
}
