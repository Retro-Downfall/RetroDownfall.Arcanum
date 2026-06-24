using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class LlamaAdditionalArgumentsPolicyTests
{

    [Fact]
    public void ContainsReservedBindingArgument_detects_standalone_host_token()
    {

        Assert.True(LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(["--host"], out string? token));

        Assert.Equal("--host", token);

    }

    [Fact]
    public void ContainsReservedBindingArgument_detects_standalone_port_token()
    {

        Assert.True(LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(["--port"], out string? token));

        Assert.Equal("--port", token);

    }

    [Fact]
    public void ContainsReservedBindingArgument_detects_host_equals_form()
    {

        Assert.True(LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(["--host=0.0.0.0"], out string? token));

        Assert.Equal("--host=0.0.0.0", token);

    }

    [Fact]
    public void ContainsReservedBindingArgument_detects_port_equals_form()
    {

        Assert.True(LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(["--port=9999"], out string? token));

        Assert.Equal("--port=9999", token);

    }

    [Fact]
    public void ContainsReservedBindingArgument_detects_host_among_other_args()
    {

        Assert.True(LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(
            ["-m", "model.gguf", "--host", "127.0.0.1"],
            out string? token));

        Assert.Equal("--host", token);

    }

    [Fact]
    public void ContainsReservedBindingArgument_allows_safe_arguments()
    {

        string[] args = ["--threads", "8", "--parallel", "2"];

        bool rejected = LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(args, out string? token);

        Assert.False(rejected);

        Assert.Null(token);

    }

}
