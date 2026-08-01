using RetroDownfall.Arcanum.Api.TheForge;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class AttachmentMultipartPolicyTests
{

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
