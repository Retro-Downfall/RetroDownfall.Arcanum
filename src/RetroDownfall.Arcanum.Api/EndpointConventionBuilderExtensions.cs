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
    /// <param name="multipartBodyLengthLimit">
    /// Multipart ceiling for this endpoint. Without it, <c>IFormFile</c> binding reads the form under the
    /// framework default of 128 MiB and throws <see cref="System.IO.InvalidDataException"/> during binding,
    /// which makes any upload above that ceiling fail with an empty <c>400</c> before the handler — and the
    /// handler's own envelope check unreachable. Callers pass their code-owned envelope plus an allowance
    /// for multipart boundaries and sibling form fields.
    /// </param>
    public static TBuilder WithFileUploadRequestBody<TBuilder>(this TBuilder builder, long multipartBodyLengthLimit)
        where TBuilder : IEndpointConventionBuilder
    {

        const long TenGibibytes = 10L * 1024L * 1024L * 1024L;

        _ = builder.WithMetadata(new RequestSizeLimitAttribute(TenGibibytes));

        return builder.WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = multipartBodyLengthLimit,
        });

    }

}
