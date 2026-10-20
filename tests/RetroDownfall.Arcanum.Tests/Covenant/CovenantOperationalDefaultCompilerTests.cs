using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantOperationalDefaultCompilerTests
{
    [Theory]
    [InlineData("defaults.authentication.enabled")]
    [InlineData("defaults.ward.audit")]
    [InlineData("defaults.tools.advertisement")]
    [InlineData("defaults.tools.capabilities")]
    [InlineData("defaults.egress.allowed")]
    [InlineData("defaults.retention.days")]
    [InlineData("defaults.inference.max-output-tokens")]
    [InlineData("defaults.platform.security")]
    [InlineData("defaults.inference.unknown")]
    [InlineData("defaults.inference.temperature.other-consumer")]
    [InlineData("defaults.")]
    public void Reserved_namespace_rejects_unknown_kinds_and_security_targets(string key)
    {
        Assert.Throws<ArgumentException>(() => new CovenantCompiler().Compile(key, "1"));
    }

    [Theory]
    [InlineData("defaults.inference.temperature", "-0.1")]
    [InlineData("defaults.inference.temperature", "2.0001")]
    [InlineData("defaults.inference.top-p", "1.001")]
    [InlineData("defaults.inference.top-p", "-1")]
    [InlineData("defaults.inference.temperature", "NaN")]
    [InlineData("defaults.inference.temperature", "Infinity")]
    [InlineData("defaults.inference.temperature", "1e999")]
    [InlineData("defaults.inference.temperature", " 1")]
    [InlineData("defaults.inference.temperature", "1 ")]
    [InlineData("defaults.inference.temperature", "+1")]
    [InlineData("defaults.inference.temperature", ".5")]
    [InlineData("defaults.inference.temperature", "01")]
    [InlineData("defaults.inference.temperature", "1.")]
    [InlineData("defaults.inference.temperature", "1e0")]
    [InlineData("defaults.inference.temperature", "１")]
    [InlineData("defaults.inference.temperature", "1; consumer=Tools")]
    [InlineData("defaults.inference.temperature", "{\"value\":1,\"consumer\":\"Tools\"}")]
    [InlineData("defaults.inference.temperature", "1\n0")]
    public void Reserved_values_require_bounded_strict_decimal_scalars(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => new CovenantCompiler().Compile(key, value));
    }

    [Theory]
    [InlineData("defaults.inference.temperature", "0")]
    [InlineData("defaults.inference.temperature", "2")]
    [InlineData("defaults.inference.temperature", "0.25")]
    [InlineData("defaults.inference.top-p", "0")]
    [InlineData("defaults.inference.top-p", "1")]
    [InlineData("defaults.inference.top-p", "0.95")]
    public void Valid_scalars_keep_the_existing_compiler_fragment(string key, string value)
    {
        CovenantCompiledContent compiled = new CovenantCompiler().Compile(key, value);

        Assert.Equal($"- {key}: \"{value}\"\n", compiled.Fragment);
    }

    [Fact]
    public void Ordinary_preferences_keep_the_existing_compiler_behavior()
    {
        CovenantCompiledContent compiled = new CovenantCompiler().Compile("response.style", "  terse\nanswers ");

        Assert.Equal("- response.style: \"terse answers\"\n", compiled.Fragment);
    }
}
