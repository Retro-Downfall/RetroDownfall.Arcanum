using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class InferenceProviderFailureMessageTests
{

    [Fact]
    public void Build_without_status_keeps_unreachable_guidance()
    {

        string message = InferenceProviderFailureMessage.Build("Fireworks");

        Assert.Equal(
            "Provider 'Fireworks' is unreachable. Verify the service is running and Arcanum:Providers is configured correctly.",
            message);

    }

    [Fact]
    public void Build_with_http_status_surfaces_status_instead_of_unreachable()
    {

        string message = InferenceProviderFailureMessage.Build("Fireworks", httpStatus: 500);

        Assert.Equal(
            "Provider 'Fireworks' returned HTTP 500. Check the model, API key, and request; see server logs for detail.",
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
