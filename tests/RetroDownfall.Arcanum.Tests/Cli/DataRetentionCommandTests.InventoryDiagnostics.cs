using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DataRetentionCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Status_renders_an_unmeasured_Covenant_inventory_with_its_error_code(bool json)
    {
        string status = JsonSerializer.Serialize(CreateStatus(), ArcanumJsonContext.Default.DataRetentionStatus);

        status = status[..^1] + ",\"covenantInventoryErrorCode\":\"Covenant.Unavailable\"}";

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"isSuccess\":true,\"data\":" + status + "}", Encoding.UTF8, "application/json"),
        });

        var result = RunCommand(handler, ["data", "status", json ? "--json" : "--plain"]);

        Assert.Equal(0, result.ExitCode);

        if (json)
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);

            Assert.True(document.RootElement.TryGetProperty("covenantInventoryErrorCode", out var code), result.Output);

            Assert.Equal("Covenant.Unavailable", code.GetString());
        }
        else Assert.Contains("Covenant inventory: unmeasured (Covenant.Unavailable)", result.Output, StringComparison.Ordinal);
    }
}
