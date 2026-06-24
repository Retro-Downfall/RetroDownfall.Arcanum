using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PingRequestBoundsValidatorTests
{

    [Fact]
    public void Validate_RejectsOversizedPrompt()
    {

        ArcanumSettings settings = new()
        {
            Intelligence = new IntelligenceSettings { MaxPingPromptChars = 8 },
        };

        PingRequest request = new(Prompt: new string('x', 9));

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.PromptTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsTooManyStatelessMessages()
    {

        ArcanumSettings settings = new()
        {
            Intelligence = new IntelligenceSettings { MaxStatelessMessages = 1 },
        };

        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage("user", "one"),
                new CoreChatMessage("user", "two"),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.TooManyStatelessMessages", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsOversizedStatelessMessageContent()
    {

        ArcanumSettings settings = new()
        {
            Sessions = new SessionSettings { MaxEntryContentBytes = 1024 },
        };

        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages: [new CoreChatMessage("user", new string('x', 1025))]);

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    [Fact]
    public void ValidateOpenApiMessageCount_RejectsExcessMessages()
    {

        ArcanumSettings settings = new()
        {
            Intelligence = new IntelligenceSettings { MaxOpenApiMessages = 2 },
        };

        Result result = PingRequestBoundsValidator.ValidateOpenApiMessageCount(3, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.TooManyMessages", result.Error.Code);

    }

}

