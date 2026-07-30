using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
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

        Assert.Null(entry.WireDialect);

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

        Assert.Null(entry.WireDialect);

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
    public void Read_ObjectForm_ReadsNestedReasoningFacts()
    {

        const string json =
            """
            {
              "name": "reasoner",
              "reasoning": {
                "wireDialect": "openRouter",
                "maxBudgetTokens": 65536
              }
            }
            """;

        Utf8JsonReader reader = CreateReader(json);

        reader.Read();

        ModelEntry? entry = _converter.Read(ref reader, typeof(ModelEntry), Options);

        Assert.Equal(ReasoningWireDialect.OpenRouter, entry?.WireDialect);
        Assert.Equal(65_536, entry?.MaxBudgetTokens);

    }

    [Theory]
    [InlineData("""{"name":"reasoner","reasoning":{"wireDialect":0}}""")]
    [InlineData("""{"name":"reasoner","reasoning":{"wireDialect":99}}""")]
    public void Read_ObjectForm_RejectsNumericReasoningDialect(string json)
    {
        Assert.Throws<JsonException>(() =>
        {
            Utf8JsonReader reader = CreateReader(json);
            reader.Read();
            _ = _converter.Read(ref reader, typeof(ModelEntry), Options);
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public void ConfigurationSourceContext_RejectsNumericControlSupport(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                json,
                ConfigurationJsonContext.Default.ReasoningControlSupport));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public void ConfigurationSourceContext_RejectsNumericWireDialect(string json)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(
                json,
                ConfigurationJsonContext.Default.ReasoningWireDialect));
    }

    [Theory]
    [InlineData(ReasoningControlSupport.None, "\"none\"")]
    [InlineData(ReasoningControlSupport.Effort, "\"effort\"")]
    [InlineData(ReasoningControlSupport.Budget, "\"budget\"")]
    [InlineData(ReasoningControlSupport.EffortAndBudget, "\"effortAndBudget\"")]
    public void ConfigurationSourceContext_PreservesControlSupportWireNames(
        ReasoningControlSupport value,
        string expected)
    {
        Assert.Equal(
            expected,
            JsonSerializer.Serialize(
                value,
                ConfigurationJsonContext.Default.ReasoningControlSupport));
    }

    [Theory]
    [InlineData(ReasoningWireDialect.Standard, "\"standard\"")]
    [InlineData(ReasoningWireDialect.OpenRouter, "\"openRouter\"")]
    [InlineData(ReasoningWireDialect.TopLevelReasoningBudget, "\"topLevelReasoningBudget\"")]
    [InlineData(ReasoningWireDialect.AnthropicThinking, "\"anthropicThinking\"")]
    public void ConfigurationSourceContext_PreservesWireDialectNames(
        ReasoningWireDialect value,
        string expected)
    {
        Assert.Equal(
            expected,
            JsonSerializer.Serialize(
                value,
                ConfigurationJsonContext.Default.ReasoningWireDialect));
    }

    [Fact]
    public void Write_WithReasoning_WritesNestedFactsObject()
    {

        ModelEntry entry = new("reasoner")
        {
            WireDialect = ReasoningWireDialect.TopLevelReasoningBudget,
            MaxBudgetTokens = 32_768,
        };

        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            _converter.Write(writer, entry, Options);
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());

        JsonElement reasoning = document.RootElement.GetProperty("reasoning");

        Assert.Equal("topLevelReasoningBudget", reasoning.GetProperty("wireDialect").GetString());
        Assert.Equal(32_768, reasoning.GetProperty("maxBudgetTokens").GetInt32());
        Assert.False(reasoning.TryGetProperty("controlSupport", out _));

    }

    [Fact]
    public void ImplicitConversion_FromString_DefaultsSupportsVisionFalse()
    {

        ModelEntry entry = "llama3";

        Assert.Equal("llama3", entry.Name);

        Assert.False(entry.SupportsVision);

        Assert.Null(entry.WireDialect);

    }

    [Fact]
    public void RoundTrip_ProviderSettingsWithMixedModelForms_PreservesSupportsVision()
    {

        string json = """
            {
              "name": "openai",
              "type": "OpenAICompatible",
              "endpoint": "https://api.openai.com/v1",
              "models": [
                {
                  "name": "gpt-4o",
                  "supportsVision": true,
                  "reasoning": {
                    "wireDialect": "standard"
                  }
                },
                "gpt-4o-mini"
              ],
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

        Assert.Null(provider.Models[1].WireDialect);

        Assert.Equal(ReasoningWireDialect.Standard, provider.Models[0].WireDialect);

        string roundTripped = JsonSerializer.Serialize(provider, ConfigurationJsonContext.Default.ProviderSettings);

        ProviderSettings? reparsed = JsonSerializer.Deserialize(roundTripped, ConfigurationJsonContext.Default.ProviderSettings);

        Assert.NotNull(reparsed);

        Assert.True(reparsed!.Models[0].SupportsVision);

        Assert.False(reparsed.Models[1].SupportsVision);

        Assert.Equal(provider.Models[0].WireDialect, reparsed.Models[0].WireDialect);
        Assert.Null(reparsed.Models[1].WireDialect);

    }

    [Theory]
    [InlineData(typeof(ReasoningEffortLevel))]
    [InlineData(typeof(ReasoningOutputMode))]
    [InlineData(typeof(ReasoningContentSegment))]
    [InlineData(typeof(ReasoningCapabilities))]
    [InlineData(typeof(ReasoningControlSupport))]
    [InlineData(typeof(ReasoningWireDialect))]
    public void ConfigurationJsonContext_RegistersReasoningCapabilityTypes(Type type)
    {

        Assert.NotNull(ConfigurationJsonContext.Default.GetTypeInfo(type));

    }

    private static Utf8JsonReader CreateReader(string json)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(json);

        return new Utf8JsonReader(bytes);

    }

}
