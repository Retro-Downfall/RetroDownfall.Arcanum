using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class InferenceProviderFailureMessageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_failure_does_not_invent_a_connectivity_diagnosis(bool hasException)
    {
        string message = hasException
            ? InferenceProviderFailureMessage.Build("Fireworks", new InvalidOperationException("PRIVATE_UNKNOWN_BODY"))
            : InferenceProviderFailureMessage.Build("Fireworks");

        Assert.DoesNotContain("unreachable", message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("PRIVATE_UNKNOWN_BODY", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_without_status_keeps_unknown_guidance()
    {
        string message = InferenceProviderFailureMessage.Build("Fireworks");

        Assert.Equal(
            "Inference failed. See server logs for details.",
            message);
    }

    [Fact]
    public void Build_with_http_status_surfaces_status_instead_of_unreachable()
    {
        string message = InferenceProviderFailureMessage.Build("Fireworks", httpStatus: 500);

        Assert.Equal(
            "Provider 'Fireworks' returned HTTP 500. The provider reported a server error. Retry later or check its service status.",
            message);

        Assert.DoesNotContain("unreachable", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryGetHttpStatus_returns_null_for_non_http_exceptions()
    {
        Assert.Null(InferenceProviderFailureMessage.TryGetHttpStatus(new InvalidOperationException("boom")));

        Assert.Null(InferenceProviderFailureMessage.TryGetHttpStatus(null));
    }
}
