using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed partial class CovenantMutationToolTests
{
    [Theory]
    [InlineData("key", ErrorCodes.Covenant.InvalidKey, "key")]
    [InlineData("size", ErrorCodes.Covenant.InvalidContent, "2048")]
    [InlineData("format", ErrorCodes.Covenant.InvalidContent, "U+200B")]
    [InlineData("whitespace", ErrorCodes.Covenant.InvalidContent, "whitespace")]
    public async Task An_eligible_proposal_explains_the_actual_key_or_content_refusal_without_staging(string kind, string code, string guidance)
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability();

        string key = kind == "key" ? "PRIVATE_KEY_CANARY Invalid" : "response.style";

        string content = kind switch
        {
            "size" => new string('x', 2049),
            "format" => "😀A\u200BPRIVATE_CONTENT_CANARY",
            "whitespace" => " \t\n ",
            _ => "PRIVATE_CONTENT_CANARY",
        };

        McpToolsCallResultWire result = await session.CallProposeAsync(key, content);

        Assert.True(result.IsError);

        CovenantMutationFailureResultWire failure = Failure(result);

        Assert.Equal(code, failure.Code);

        Assert.Contains(guidance, failure.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("PRIVATE_KEY_CANARY", failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("PRIVATE_CONTENT_CANARY", failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("(Parameter", failure.Message, StringComparison.Ordinal);

        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Fact]
    public async Task An_eligible_retirement_of_a_malformed_key_names_a_key_failure_without_staging()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterRetirementCapability();

        McpToolsCallResultWire result = await session.CallRetireAsync("PRIVATE_KEY_CANARY Invalid", nameof(CovenantLane.Proposed));

        Assert.Equal(ErrorCodes.Covenant.InvalidKey, Failure(result).Code);

        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compiler_diagnostics_log_only_closed_policy_even_for_an_unknown_private_exception(bool unknown)
    {
        TestCapturingLogger<ArcanumInternalToolServer> logger = new();

        await using CovenantToolSession session = await CovenantToolSession.CreateAsync(
            compiler: unknown ? new PrivateFailureCompiler() : new CovenantCompiler(), logger: logger);

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync("preference.privacy", "😀A\u200BPRIVATE_CONTENT_CANARY");

        CovenantMutationFailureResultWire failure = Failure(result);

        Assert.Equal(ErrorCodes.Covenant.InvalidContent, failure.Code);

        Assert.Equal(0, session.Collector.StagedCount);

        TestLogEntry entry = Assert.Single(logger.Entries,
            static item => item.Properties?.Any(static property => property.Key == "Reason") == true);

        Assert.Null(entry.Exception);

        Assert.Equal(unknown ? CovenantCompilationFailureReason.Unknown : CovenantCompilationFailureReason.FormatScalar,
            Assert.Single(entry.Properties!, static property => property.Key == "Reason").Value);

        foreach (string value in entry.Properties!.Select(static property => property.Value?.ToString() ?? string.Empty)
            .Append(entry.Message).Append(failure.Message))
        {
            Assert.DoesNotContain("PRIVATE_", value, StringComparison.Ordinal);

            Assert.DoesNotContain("/profile", value, StringComparison.Ordinal);
        }

        if (unknown)
        {
            Assert.DoesNotContain("U+200B", failure.Message, StringComparison.Ordinal);
        }
    }

    private sealed class PrivateFailureCompiler : ICovenantCompiler
    {
        public CovenantCompiledContent Compile(string key, string authoredContent) =>
            throw new ArgumentException("PRIVATE_EXCEPTION_CANARY /profile U+200B scalar position 999", "PRIVATE_PARAMETER_CANARY");

        public string RenderProposedSection(IReadOnlyList<CovenantCompiledContent> fragments) =>
            new CovenantCompiler().RenderProposedSection(fragments);
    }
}
