using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #89 — the exact wire value of <c>X-Arcanum-Context-Policy</c>, decided before binding.
/// </summary>
/// <remarks>
/// One value is legal: a single lowercase ASCII <c>none</c>. Everything else is a 400, including
/// <c>NONE</c>, <c>None</c>, and <c>none,none</c>. The strictness is the point: this header suppresses
/// durable memory injection, and a caller that wrote <c>None</c> intending suppression and silently
/// got <c>Default</c> would send content it believed it had excluded. A refusal is recoverable; a
/// permissive parse is a disclosure.
///
/// <para>Duplicate headers are refused rather than merged. HTTP allows repetition and every merge rule
/// — first wins, last wins, comma-join — is a guess about which of two disagreeing clients meant it.
/// </para>
/// </remarks>
public sealed class CovenantContextPolicyHeaderTests
{

    [Fact]
    public void Absent_header_is_the_default_policy()
    {

        Assert.Equal(
            CovenantContextPolicy.Default,
            AssertSuccess(CovenantContextPolicyParser.Parse(NewRequest())));

    }

    [Fact]
    public void Exactly_lowercase_none_selects_the_no_context_policy()
    {

        Assert.Equal(
            CovenantContextPolicy.None,
            AssertSuccess(CovenantContextPolicyParser.Parse(NewRequest("none"))));

    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("None")]
    [InlineData("nOnE")]
    [InlineData("default")]
    [InlineData("none,none")]
    [InlineData("none, none")]
    [InlineData("none ")]
    [InlineData(" none")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("all")]
    public void Every_other_value_is_refused(string value)
    {

        Assert.True(CovenantContextPolicyParser.Parse(NewRequest(value)).IsFailure);

    }

    [Fact]
    public void Two_headers_are_refused_rather_than_merged()
    {

        Assert.True(CovenantContextPolicyParser.Parse(NewRequest("none", "none")).IsFailure);

    }

    [Fact]
    public void The_header_name_is_the_documented_one()
    {

        Assert.Equal("X-Arcanum-Context-Policy", ArcanumApiHeaders.ContextPolicy);

    }

    [Fact]
    public void An_explicit_none_is_recorded_as_an_irrevocable_request_feature()
    {

        DefaultHttpContext context = new();

        context.Request.Headers[ArcanumApiHeaders.ContextPolicy] = "none";

        CovenantRequestFeatures.RecordContextPolicy(context, CovenantContextPolicy.None);

        Assert.Equal(CovenantContextPolicy.None, CovenantRequestFeatures.ContextPolicy(context));

        // Later code reads it and cannot replace it. A downstream filter that could relax the policy
        // would make "the operator asked for no context" a suggestion rather than a decision.
        Assert.Throws<InvalidOperationException>(() =>
            CovenantRequestFeatures.RecordContextPolicy(context, CovenantContextPolicy.Default));

        Assert.Equal(CovenantContextPolicy.None, CovenantRequestFeatures.ContextPolicy(context));

    }

    [Fact]
    public void A_request_with_no_recorded_feature_reads_as_default()
    {

        Assert.Equal(CovenantContextPolicy.Default, CovenantRequestFeatures.ContextPolicy(new DefaultHttpContext()));

    }

    private static HttpRequest NewRequest(params string[] values)
    {

        DefaultHttpContext context = new();

        if (values.Length > 0)
        {

            context.Request.Headers[ArcanumApiHeaders.ContextPolicy] = values;

        }

        return context.Request;

    }

    private static CovenantContextPolicy AssertSuccess(
        RetroDownfall.Arcanum.Core.Primitives.Result<CovenantContextPolicy> result)
    {

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : string.Empty);

        return result.Value;

    }

}
