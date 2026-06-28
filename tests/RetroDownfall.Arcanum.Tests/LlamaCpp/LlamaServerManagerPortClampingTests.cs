using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

namespace RetroDownfall.Arcanum.Tests.LlamaCpp;

public sealed class LlamaServerManagerPortClampingTests
{

    // W2.5 Fix 1 + Fix 2: ClampPort is the single runtime defense-in-depth clamp
    // used both for the computed llama-server port (PortStart + offset can exceed
    // 65535 even though each input is individually clamped to 1..65535 by
    // ArcanumSettingClamps) and for an out-of-range portOverride.
    // ConfigurationValidator (W1.1) rejects PortStart+PortRange-1 > 65535 at load
    // time; this is the runtime clamp for inputs that bypass that validator.

    [Theory]

    [InlineData(70000, 65535)]

    [InlineData(65536, 65535)]

    [InlineData(0, 1)]

    [InlineData(-1, 1)]

    [InlineData(1, 1)]

    [InlineData(8080, 8080)]

    [InlineData(65535, 65535)]

    public void ClampPort_ClampsToValidPortRange(int input, int expected)
    {

        Assert.Equal(expected, LlamaServerManager.ClampPort(input));

    }

    [Fact]

    public void ComputeCandidatePort_NeverExceeds65535WhenRangeOverflows()
    {

        // PortStart=40000, PortRange=30000: the raw sum 40000 + 29999 = 69999
        // exceeds 65535, so without the clamp IsPortFree/Process.Start/health
        // probes would target an invalid port. Every candidate across the full
        // range sweep must land in 1..65535.

        const int portStart = 40000;

        const int portRange = 30000;

        for (int startOffset = 0; startOffset < portRange; startOffset++)
        {

            int port = LlamaServerManager.ComputeCandidatePort(portStart, portRange, startOffset, i: 0);

            Assert.InRange(port, 1, 65535);

        }

    }

    [Fact]

    public void ComputeCandidatePort_PreservesValueWhenInRange()
    {

        // For a non-overflowing range the clamp is a no-op, so the sweep still
        // covers every port exactly once (no silent port loss from clamping).

        const int portStart = 40000;

        const int portRange = 1000;

        for (int i = 0; i < portRange; i++)
        {

            int port = LlamaServerManager.ComputeCandidatePort(portStart, portRange, startOffset: 0, i);

            Assert.Equal(portStart + i, port);

        }

    }

}
