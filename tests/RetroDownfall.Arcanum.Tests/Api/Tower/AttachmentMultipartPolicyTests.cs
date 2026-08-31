using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

public sealed class AttachmentMultipartPolicyTests
{

    [Theory]

    [InlineData("text/plain")]
    [InlineData("text/x-python")]
    [InlineData("application/json")]
    [InlineData("application/xml")]
    [InlineData("application/yaml")]
    [InlineData("application/toml")]

    public void ResolveSnapshotMimeType_preserves_only_the_current_declared_textual_allowlist(
        string declaredMimeType)
    {

        Assert.Equal(
            declaredMimeType,
            SessionEndpoints.ResolveSnapshotMimeType(declaredMimeType, "application/octet-stream"));

    }

    [Theory]

    [InlineData("application/ld+json")]
    [InlineData("application/x-httpd-php")]
    [InlineData("application/x-javascript")]
    [InlineData("application/x-ndjson")]
    [InlineData("application/x-sh")]
    [InlineData("application/x-yaml")]
    [InlineData("application/pdf")]

    public void ResolveSnapshotMimeType_rejects_unallowlisted_declared_application_types(
        string declaredMimeType)
    {

        Assert.Equal(
            "application/octet-stream",
            SessionEndpoints.ResolveSnapshotMimeType(declaredMimeType, "application/octet-stream"));

    }

    [Fact]

    public void ResolveSnapshotMimeType_prefers_detected_mime_type_over_the_declared_header()
    {

        Assert.Equal(
            "application/pdf",
            SessionEndpoints.ResolveSnapshotMimeType("text/plain", "application/pdf"));

    }

    [Fact]

    public void Multipart_parser_enforces_the_attachment_read_limit()
    {

        const long maximumReadBytes = 1_048_576L;

        Microsoft.AspNetCore.Http.Features.FormOptions options =
            SessionEndpoints.CreateAttachmentFormOptions(maximumReadBytes);

        Assert.Equal(maximumReadBytes, options.MultipartBodyLengthLimit);

    }

    [Fact]

    public void Multipart_request_limit_adds_only_the_bounded_protocol_envelope()

    {

        const long maximumReadBytes = 1_048_576L;

        long requestLimit = SessionEndpoints.ResolveAttachmentMultipartRequestLimit(

            maximumReadBytes);

        Assert.Equal(maximumReadBytes + 65_536L, requestLimit);

    }

    [Fact]

    public void Multipart_transport_limit_reserves_exactly_one_sentinel_byte()

    {

        const long aggregateRequestLimit = 1_114_112L;

        long transportLimit = SessionEndpoints.ResolveAttachmentMultipartTransportLimit(

            aggregateRequestLimit);

        Assert.Equal(aggregateRequestLimit + 1L, transportLimit);

    }

    [Theory]

    [InlineData("Multipart body length limit 1048576 exceeded.", ErrorCodes.Attachment.TooLarge)]

    [InlineData("Malformed multipart content.", ErrorCodes.Attachment.InvalidRequest)]

    public void Multipart_read_errors_preserve_payload_size_semantics(
        string message,
        string expectedCode)
    {

        string actualCode = SessionEndpoints.ResolveAttachmentFormErrorCode(
            new InvalidDataException(message));

        Assert.Equal(expectedCode, actualCode);

    }

}
