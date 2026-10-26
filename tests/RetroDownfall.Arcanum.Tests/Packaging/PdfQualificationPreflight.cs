using System.Net;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>Executed only inside the opt-in live harness, after operator authorization of dependency traffic.</summary>
internal static class PdfQualificationPreflight
{
    public static int? AdvertisedWindow(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array
            || !capabilities.EnumerateArray().Any(static item => item.ValueKind == JsonValueKind.String && item.GetString() == "embedding")
            || !root.TryGetProperty("model_info", out var info) || info.ValueKind != JsonValueKind.Object) return null;

        int[] windows = info.EnumerateObject().Where(static property => property.Name.EndsWith(".context_length", StringComparison.Ordinal))
            .Select(static property => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int value) ? value : 0)
            .Distinct().ToArray();

        return windows is [>= 1024 and <= 9_999_999] ? windows[0] : null;
    }

    public static async Task VerifyAsync(Uri endpoint, PdfQualificationOptions options, CancellationToken cancellationToken)
    {
        using var handler = LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler();

        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        using MemoryStream body = new();

        using (Utf8JsonWriter writer = new(body))
        {
            writer.WriteStartObject();

            writer.WriteString("model", options.Model);

            writer.WriteEndObject();
        }

        using ByteArrayContent content = new(body.ToArray());

        content.Headers.ContentType = new("application/json");

        using HttpResponseMessage response = await client.PostAsync(new Uri(endpoint, "../api/show"), content, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        byte[]? bytes = await LocalOllamaQualificationGuards.ReadCappedResponseBytesAsync(response.Content, 1024 * 1024, timeout.Token);

        Assert.NotNull(bytes);

        using JsonDocument show = JsonDocument.Parse(bytes);

        Assert.Equal(options.Window, AdvertisedWindow(show.RootElement));

        int measured = await LongRestQualificationPreflight.MeasureEmbeddingDimensionsAsync(endpoint, options.Model, timeout.Token);

        Assert.Equal(options.Dimensions, measured);
    }
}
