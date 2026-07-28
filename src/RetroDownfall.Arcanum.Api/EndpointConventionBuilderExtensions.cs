using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;

namespace RetroDownfall.Arcanum.Api;

public static class EndpointConventionBuilderExtensions
{

    /// <summary>
    /// Increases the Kestrel request body size limit for this endpoint to 16 MiB.
    /// </summary>
    public static TBuilder WithLargeRequestBody<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {

        const long SixteenMegabytes = 16L * 1024L * 1024L;

        return builder.WithMetadata(new RequestSizeLimitAttribute(SixteenMegabytes));

    }

    /// <summary>
    /// Raises the Kestrel request body size limit for this endpoint to the code-owned upload
    /// envelope's 10 GiB physical ceiling so the handler enforces the effective cap and returns a
    /// structured <c>413</c> JSON error instead of an abrupt Kestrel-level connection reset.
    /// </summary>
    public static TBuilder WithFileUploadRequestBody<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {

        const long TenGibibytes = 10L * 1024L * 1024L * 1024L;

        return builder.WithMetadata(new RequestSizeLimitAttribute(TenGibibytes));

    }

}
