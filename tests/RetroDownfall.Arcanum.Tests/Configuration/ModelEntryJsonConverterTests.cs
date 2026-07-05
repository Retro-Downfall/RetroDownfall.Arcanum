using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ModelEntryJsonConverterTests
{

    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = ConfigurationJsonContext.Default,
    };

    private readonly ModelEntryJsonConverter _converter = new();

    [Fact]
    public void Read_BareString_DefaultsSupportsVisionFalse()
    {

        Utf8JsonReader reader = CreateReader("\"gpt-4o-mini\"");

        reader.Read();

        ModelEntry? entry = _converter.Read(ref reader, typeof(ModelEntry), Options);

        Assert.NotNull(entry);

        Assert.Equal("gpt-4o-mini", entry!.Name);

        Assert.False(entry.SupportsVision);

    }

    [Fact]
    public void Read_ObjectForm_ReadsSupportsVisionTrue()
    {

        Utf8JsonReader reader = CreateReader("""{"name":"gpt-4o","supportsVision":true}""");

        reader.Read();

        ModelEntry? entry = _converter.Read(ref reader, typeof(ModelEntry), Options);

        Assert.NotNull(entry);

        Assert.Equal("gpt-4o", entry!.Name);

        Assert.True(entry.SupportsVision);

    }

    [Fact]
    public void Read_ObjectFormMissingSupportsVision_DefaultsFalse()
    {

        Utf8JsonReader reader = CreateReader("""{"name":"local.gguf"}""");

        reader.Read();

        ModelEntry? entry = _converter.Read(ref reader, typeof(ModelEntry), Options);

        Assert.NotNull(entry);

        Assert.Equal("local.gguf", entry!.Name);

        Assert.False(entry.SupportsVision);

    }

    [Fact]
    public void Read_InvalidToken_ThrowsJsonException()
    {

        JsonException ex = Assert.Throws<JsonException>(() =>
        {

            Utf8JsonReader local = CreateReader("42");

            local.Read();

            _ = _converter.Read(ref local, typeof(ModelEntry), Options);

        });

        Assert.Contains("models", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void Write_AlwaysWritesObjectForm()
    {

        using MemoryStream stream = new();

        using Utf8JsonWriter writer = new(stream);

        _converter.Write(writer, new ModelEntry("gpt-4o", SupportsVision: true), Options);

        writer.Flush();

        string json = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Equal("""{"name":"gpt-4o","supportsVision":true}""", json);

    }

    [Fact]
    public void ImplicitConversion_FromString_DefaultsSupportsVisionFalse()
    {

        ModelEntry entry = "llama3";

        Assert.Equal("llama3", entry.Name);

        Assert.False(entry.SupportsVision);

    }

    [Fact]
    public void RoundTrip_ProviderSettingsWithMixedModelForms_PreservesSupportsVision()
    {

        string json = """
            {
              "name": "openai",
              "type": "OpenAICompatible",
              "endpoint": "https://api.openai.com/v1",
              "models": [ { "name": "gpt-4o", "supportsVision": true }, "gpt-4o-mini" ],
              "contextWindowLimit": 128000
            }
            """;

        ProviderSettings? provider = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.ProviderSettings);

        Assert.NotNull(provider);

        Assert.Equal(2, provider!.Models.Count);

        Assert.Equal("gpt-4o", provider.Models[0].Name);

        Assert.True(provider.Models[0].SupportsVision);

        Assert.Equal("gpt-4o-mini", provider.Models[1].Name);

        Assert.False(provider.Models[1].SupportsVision);

        string roundTripped = JsonSerializer.Serialize(provider, ConfigurationJsonContext.Default.ProviderSettings);

        ProviderSettings? reparsed = JsonSerializer.Deserialize(roundTripped, ConfigurationJsonContext.Default.ProviderSettings);

        Assert.NotNull(reparsed);

        Assert.True(reparsed!.Models[0].SupportsVision);

        Assert.False(reparsed.Models[1].SupportsVision);

    }

    private static Utf8JsonReader CreateReader(string json)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(json);

        return new Utf8JsonReader(bytes);

    }

}
