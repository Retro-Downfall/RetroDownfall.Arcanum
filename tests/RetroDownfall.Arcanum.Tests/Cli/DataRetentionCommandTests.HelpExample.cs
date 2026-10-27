using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DataRetentionCommandTests
{
    [Fact]
    public void Published_retention_set_example_reaches_the_handler_with_the_documented_rule_and_age()
    {
        string example = Assert.Single(CliSurfaceExamples.For("data retention set"));

        Assert.StartsWith("arcanum ", example, StringComparison.Ordinal);

        RecordingHandler handler = new(_ => ErrorResponse());

        _ = RunCommand(handler, ["--yes", .. example.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)]);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Put, request.Method);

        Assert.Equal("/api/data/retention", request.Path);

        using JsonDocument body = JsonDocument.Parse(request.Body);

        Assert.Equal("archived-sessions", body.RootElement.GetProperty("dataClass").GetString());

        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());

        Assert.Equal(90, body.RootElement.GetProperty("days").GetInt32());
    }
}
