using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Requests soft residency on the actual chat request, without a separate model-loading call.</summary>
internal sealed class OllamaForegroundRetentionPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (ProviderWorkAmbient.Owner is { IsActive: true } && message.Request.Content is { } content)
        {
            using MemoryStream buffer = new();

            content.WriteTo(buffer, message.CancellationToken);

            Apply(message, buffer);
        }

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (ProviderWorkAmbient.Owner is { IsActive: true } && message.Request.Content is { } content)
        {
            using MemoryStream buffer = new();

            await content.WriteToAsync(buffer, message.CancellationToken).ConfigureAwait(false);

            Apply(message, buffer);
        }

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private static void Apply(PipelineMessage message, MemoryStream buffer)
    {
        message.CancellationToken.ThrowIfCancellationRequested();

        using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));

        using MemoryStream rewritten = new();

        using (Utf8JsonWriter writer = new(rewritten))
        {
            writer.WriteStartObject();

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("keep_alive")) property.WriteTo(writer);
            }

            // Ollama may still evict this runner under memory pressure. This prevents its idle
            // timeout during long tool gaps; it does not claim a hard pin or saved KV state.
            writer.WriteNumber("keep_alive", -1);

            writer.WriteEndObject();
        }

        message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(rewritten.ToArray()));
    }
}
