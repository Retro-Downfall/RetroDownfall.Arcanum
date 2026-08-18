using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ScryingValidatorTests
{

    private static ScryingSettings Scrying() =>
        new()
        {
            Enabled = true,
            MaxImageBytes = 1_048_576L,
            MaxImagesPerRequest = 10,
            AllowedMimeTypes = ["image/png"],
        };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequestImages_RejectsScryingFocusWithBlankMimeType(string? mimeType)
    {

        PingRequest request = new(
            Prompt: "x",
            ScryingFoci: [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), mimeType!)]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.UnsupportedMimeType, result.Error.Code);

    }

    [Fact]
    public void ValidateRequestImages_RejectsDataUriImagePartWithBlankMimeType()
    {

        PingRequest request = new(
            Prompt: "x",
            StatelessMessages:
            [
                new CoreChatMessage(
                    "user",
                    "look",
                    ContentParts: [new CoreContentPart("image_url", null, "data:;base64,AQID", null)]),
            ]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.UnsupportedMimeType, result.Error.Code);

    }

    [Fact]
    public void ValidateRequestImages_AcceptsAllowedScryingFocusMimeType()
    {

        PingRequest request = new(
            Prompt: "x",
            ScryingFoci: [new ScryingFocusDto(Convert.ToBase64String([1, 2, 3]), "image/png")]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ValidateRequestImages_AcceptsHttpImageUrlWithoutMimeCheck()
    {

        PingRequest request = new(
            Prompt: "x",
            StatelessMessages:
            [
                new CoreChatMessage(
                    "user",
                    "look",
                    ContentParts:
                    [
                        new CoreContentPart("image_url", null, "https://example.invalid/i.png", null),
                    ]),
            ]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ValidateRequestImages_RejectsMalformedBase64ScryingFocus()
    {

        PingRequest request = new(
            Prompt: "x",
            ScryingFoci: [new ScryingFocusDto("not*valid*base64", "image/png")]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.InvalidImageData, result.Error.Code);

    }

    [Fact]
    public void ValidateRequestImages_RejectsMalformedBase64DataUriImagePart()
    {

        PingRequest request = new(
            Prompt: "x",
            StatelessMessages:
            [
                new CoreChatMessage(
                    "user",
                    "look",
                    ContentParts:
                    [
                        new CoreContentPart("image_url", null, "data:image/png;base64,not*valid*base64", null),
                    ]),
            ]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.InvalidImageData, result.Error.Code);

    }

    /// <summary>
    /// Embedded whitespace stays acceptable: a client that line-wraps its base64 payload is sending
    /// well-formed data, and the downstream decode accepts it.
    /// </summary>
    [Fact]
    public void ValidateRequestImages_AcceptsBase64WithEmbeddedWhitespace()
    {

        PingRequest request = new(
            Prompt: "x",
            ScryingFoci: [new ScryingFocusDto("AQ\nID", "image/png")]);

        Result result = ScryingValidator.ValidateRequestImages(request, Scrying());

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ValidateRequestImages_RejectsScryingFocusOverTheSizeCap()
    {

        ScryingSettings scrying = new()
        {
            Enabled = true,
            MaxImageBytes = 1_024L,
            MaxImagesPerRequest = 10,
            AllowedMimeTypes = ["image/png"],
        };

        PingRequest request = new(
            Prompt: "x",
            ScryingFoci: [new ScryingFocusDto(Convert.ToBase64String(new byte[4_096]), "image/png")]);

        Result result = ScryingValidator.ValidateRequestImages(request, scrying);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Scrying.ImageTooLarge, result.Error.Code);

    }

    /// <summary>
    /// Base64 well-formedness is a boolean question, and answering it must not allocate a decode
    /// buffer the size of the image. The payload is decoded for real further downstream; a scratch
    /// buffer here puts a large-object-heap array per focus on the hottest authenticated path for a
    /// check whose only output is a bool.
    /// </summary>
    [Fact]
    public void ValidateRequestImages_ValidatesBase64WithoutAllocatingADecodeBuffer()
    {

        string base64 = Convert.ToBase64String(new byte[900_000]);

        ScryingSettings scrying = Scrying();

        PingRequest request = new(
            Prompt: "x",
            ScryingFoci: [new ScryingFocusDto(base64, "image/png")]);

        _ = ScryingValidator.ValidateRequestImages(request, scrying);

        long before = GC.GetAllocatedBytesForCurrentThread();

        Result result = ScryingValidator.ValidateRequestImages(request, scrying);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.IsSuccess);

        Assert.True(
            allocated < 16_384L,
            $"Validating a {base64.Length}-char base64 focus allocated {allocated} bytes.");

    }

}
