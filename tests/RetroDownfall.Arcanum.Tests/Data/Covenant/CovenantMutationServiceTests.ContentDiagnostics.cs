using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed partial class CovenantMutationServiceTests
{
    [Theory]
    [InlineData("format", "U+200B", 3)]
    [InlineData("control", "U+0000", 3)]
    [InlineData("default-whitespace", "whitespace", 0)]
    [InlineData("unicode", "Unicode", 0)]
    public async Task Operator_content_refusals_locate_the_policy_failure_without_internal_parameter_names(string kind, string guidance, int scalarPosition)
    {
        await using CovenantServiceHarness harness = await CovenantServiceHarness.StartAsync(Token);

        string content = kind switch
        {
            "format" => "😀A\u200BPRIVATE_CONTENT_CANARY",
            "control" => "😀A\0PRIVATE_CONTENT_CANARY",
            "default-whitespace" => "0.7\n",
            _ => "A\ud800PRIVATE_CONTENT_CANARY",
        };

        string key = kind == "default-whitespace" ? "defaults.inference.temperature" : "preference.content";

        await using CovenantInstallationReadLease read = (await harness.Gate.AcquireInstallationReadAsync(Token)).Value;

        Result<CovenantMutationPreflightDto> refused = await harness.Service.PrepareSetAsync(
            new(CovenantScope.Global, null, key, content, 0, Guid.CreateVersion7(), false), read, Token);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, refused.Error.Code);

        Assert.Contains("content", refused.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(guidance, refused.Error.Message, StringComparison.OrdinalIgnoreCase);

        if (scalarPosition > 0)
        {
            Assert.Matches($"(?i)scalar(?: position)? {scalarPosition}", refused.Error.Message);
        }

        Assert.DoesNotContain("(Parameter", refused.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("PRIVATE_CONTENT_CANARY", refused.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, await CountAsync(harness.Fixture, "SELECT COUNT(*) FROM covenant_heads;"));
    }
}
