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

    [Fact]
    public void Validate_StatelessContent_MeasuredInUtf8BytesNotChars()
    {

        // MaxEntryContentBytes clamps to a 1024 floor, so size the payload around that.
        ArcanumSettings settings = new()
        {
            Sessions = new SessionSettings { MaxEntryContentBytes = 1024 },
        };

        // 600 'é' = 600 UTF-16 chars (the old char check, 600 <= 1024, passed) but 1200 UTF-8 bytes
        // (the new byte check, 1200 > 1024, must reject).
        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages: [new CoreChatMessage("user", new string('\u00e9', 600))]);

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_ToolCallArguments_CountTowardEntryBudget()
    {

        ArcanumSettings settings = new()
        {
            Sessions = new SessionSettings { MaxEntryContentBytes = 1024 },
        };

        // Empty Content but a large tool-call ArgumentsJson payload (> the 1024 floor): the old
        // check (Content.Length == 0) bypassed the cap; the byte budget must now include tool-call
        // arguments.
        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage(
                    "assistant",
                    string.Empty,
                    ToolCalls: [new CoreToolCall("call-1", "fn", new string('x', 2000))]),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsOversizedAdditionalSystemPrompt()
    {

        ArcanumSettings settings = new()
        {
            Intelligence = new IntelligenceSettings { MaxPingPromptChars = 8 },
        };

        PingRequest request = new(Prompt: "hi", AdditionalSystemPrompt: new string('x', 9));

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AdditionalSystemPromptTooLong", result.Error.Code);

    }

    [Fact]
    public void ValidateOpenApiMessageCount_NullIntelligence_DoesNotThrow()
    {

        ArcanumSettings settings = new() { Intelligence = null! };

        Result result = PingRequestBoundsValidator.ValidateOpenApiMessageCount(1, settings);

        Assert.True(result.IsSuccess);

    }

}

