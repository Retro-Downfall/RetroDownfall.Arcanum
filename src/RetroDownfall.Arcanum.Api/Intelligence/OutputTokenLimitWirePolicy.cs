using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Applies the captured provider wire contract after SDK serialization, once before retries.</summary>
internal sealed class OutputTokenLimitWirePolicy(OutputTokenLimitWireDialect dialect) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (message.Request.Content is { } content)
        {
            using MemoryStream buffer = new();

            content.WriteTo(buffer, message.CancellationToken);

            Apply(message, buffer);
        }

        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        if (message.Request.Content is { } content)
        {
            using MemoryStream buffer = new();

            await content.WriteToAsync(buffer, message.CancellationToken).ConfigureAwait(false);

            Apply(message, buffer);
        }

        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Apply(PipelineMessage message, MemoryStream buffer)
    {
        message.CancellationToken.ThrowIfCancellationRequested();

        using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));

        JsonElement root = document.RootElement;

        int modern = 0;

        int legacy = 0;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.NameEquals("max_completion_tokens")) modern++;

            if (property.NameEquals("max_tokens")) legacy++;
        }

        if (modern > 1 || legacy > 1 || modern + legacy > 1)
        {
            throw new InvalidOperationException("Ambiguous provider output-token limit fields.");
        }

        if (dialect != OutputTokenLimitWireDialect.MaxTokens || modern == 0) return;

        using MemoryStream rewritten = new();

        using (Utf8JsonWriter writer = new(rewritten))
        {
            writer.WriteStartObject();

            foreach (JsonProperty property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.NameEquals("max_completion_tokens") ? "max_tokens" : property.Name);

                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        message.Request.Content = BinaryContent.Create(BinaryData.FromBytes(rewritten.ToArray()));
    }
}
