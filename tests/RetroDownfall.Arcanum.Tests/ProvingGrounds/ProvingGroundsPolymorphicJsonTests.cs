using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.ProvingGrounds;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class ProvingGroundsPolymorphicJsonTests
{

    [Fact]
    public void InquisitorArray_RoundTrips_WithKindDiscriminator()
    {

        List<Inquisitor> inquisitors =
        [
            new RegexInquisitor("hello", ShouldMatch: true) { Label = "greeting" },
            new JsonSchemaInquisitor(JsonDocument.Parse("""{"type":"object","required":["name"]}""").RootElement),
            new SemanticInquisitor("Is the output polite?", ExpectedAnswer: true) { Label = "polite" },
        ];

        string json = JsonSerializer.Serialize(inquisitors, ArcanumJsonContext.Default.ListInquisitor);

        Assert.Contains("\"kind\":\"regex\"", json, StringComparison.Ordinal);

        Assert.Contains("\"kind\":\"jsonSchema\"", json, StringComparison.Ordinal);

        Assert.Contains("\"kind\":\"semantic\"", json, StringComparison.Ordinal);

        List<Inquisitor>? roundTripped = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ListInquisitor);

        Assert.NotNull(roundTripped);

        Assert.Equal(3, roundTripped.Count);

        Assert.IsType<RegexInquisitor>(roundTripped[0]);

        Assert.IsType<JsonSchemaInquisitor>(roundTripped[1]);

        Assert.IsType<SemanticInquisitor>(roundTripped[2]);

        RegexInquisitor regex = (RegexInquisitor)roundTripped[0];

        Assert.Equal("hello", regex.Pattern);

        Assert.Equal("greeting", regex.Label);

    }

    [Fact]
    public void Trial_RoundTrips_WithPolymorphicInquisitors()
    {

        Trial trial = new(
            TrialTargetKind.ApprenticeGoal,
            "Build a REST API",
            [
                new RegexInquisitor(@"\[\s*\{", ShouldMatch: true),
            ],
            Variables: new Dictionary<string, string> { ["scope"] = "minimal" },
            Name: "plan-shape");

        string json = JsonSerializer.Serialize(trial, ArcanumJsonContext.Default.Trial);

        Trial? roundTripped = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.Trial);

        Assert.NotNull(roundTripped);

        Assert.Equal(TrialTargetKind.ApprenticeGoal, roundTripped.TargetKind);

        Assert.Single(roundTripped.Inquisitors);

        Assert.IsType<RegexInquisitor>(roundTripped.Inquisitors[0]);

    }

}
