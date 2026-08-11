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

}
