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
    public void Validate_RejectsTooManyContentPartsPerMessage()
    {

        // W6.5: the per-message content-part cap (previously only enforced on /v1) now also bounds
        // the native stateless path.
        ArcanumSettings settings = new()
        {
            Intelligence = new IntelligenceSettings { MaxContentPartsPerMessage = 2 },
        };

        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage(
                    "user",
                    string.Empty,
                    ContentParts:
                    [
                        new CoreContentPart("text", "a", null, null),
                        new CoreContentPart("text", "b", null, null),
                        new CoreContentPart("text", "c", null, null),
                    ]),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.TooManyContentParts", result.Error.Code);

    }

    [Fact]
    public void ValidateOpenApiMessageCount_NullIntelligence_DoesNotThrow()
    {

        ArcanumSettings settings = new() { Intelligence = null! };

        Result result = PingRequestBoundsValidator.ValidateOpenApiMessageCount(1, settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_RejectsReasoningEffortAndBudgetTogether()
    {

        ArcanumSettings settings = SettingsWithReasoning(new ReasoningCapabilities
        {
            ControlSupport = ReasoningControlSupport.EffortAndBudget,
            WireDialect = ReasoningWireDialect.OpenRouter,
        });

        PingRequest request = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(
                ReasoningEffortLevel.High,
                BudgetTokens: 4096,
                Output: null));

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive,
            result.Error.Code);

    }

    [Theory]
    [InlineData(true, ErrorCodes.Validation.InvalidReasoningEffort)]
    [InlineData(false, ErrorCodes.Validation.InvalidReasoningOutput)]
    public void Validate_RejectsUndefinedReasoningRequestEnums(
        bool invalidEffort,
        string expectedCode)
    {

        ReasoningRequestOptions options = invalidEffort
            ? new ReasoningRequestOptions(Effort: (ReasoningEffortLevel)99)
            : new ReasoningRequestOptions(Output: (ReasoningOutputMode)99);

        Result result = ReasoningRequestValidator.Validate(options);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);

    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_097_153)]
    public void Validate_RejectsReasoningBudgetOutsideGlobalBounds(int budgetTokens)
    {

        ArcanumSettings settings = SettingsWithReasoning(new ReasoningCapabilities
        {
            ControlSupport = ReasoningControlSupport.Budget,
            WireDialect = ReasoningWireDialect.OpenRouter,
        });

        PingRequest request = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(BudgetTokens: budgetTokens));

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.InvalidReasoningBudget, result.Error.Code);

    }

    [Fact]
    public void ValidateForModel_RejectsExplicitEffortForUnsupportedModel()
    {

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(Effort: ReasoningEffortLevel.Minimal),
            capabilities: null,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, result.Error.Code);

    }

    [Fact]
    public void ValidateForModel_RejectsExplicitBudgetWhenModelSupportsOnlyEffort()
    {

        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Effort,
            WireDialect = ReasoningWireDialect.Standard,
        };

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(BudgetTokens: 2048),
            capabilities,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, result.Error.Code);

    }

    [Fact]
    public void ValidateForModel_RejectsBudgetAboveModelCapability()
    {

        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.Budget,
            WireDialect = ReasoningWireDialect.AnthropicThinking,
            MaxBudgetTokens = 4096,
        };

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(BudgetTokens: 4097),
            capabilities,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(
            ErrorCodes.Validation.ReasoningBudgetExceedsModelLimit,
            result.Error.Code);

    }

    [Theory]
    [InlineData(ReasoningOutputMode.Summary)]
    [InlineData(ReasoningOutputMode.Full)]
    public void ValidateForModel_RejectsReasoningOutputThatModelMayNotReturn(ReasoningOutputMode output)
    {

        ReasoningCapabilities capabilities = new()
        {
            SupportsSummary = true,
            SupportsFull = true,
            AllowsClientOutput = false,
        };

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(Output: output),
            capabilities,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningOutput, result.Error.Code);

    }

    [Fact]
    public void Validate_AllowsExplicitNoneOutputWithoutReasoningCapability()
    {

        ArcanumSettings settings = SettingsWithReasoning(reasoning: null);

        PingRequest request = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(Output: ReasoningOutputMode.None));

        Result result = PingRequestBoundsValidator.Validate(request, settings);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ValidateForModel_AllowsSupportedReasoningControlsAndOutput()
    {

        ReasoningCapabilities capabilities = new()
        {
            ControlSupport = ReasoningControlSupport.EffortAndBudget,
            SupportsSummary = true,
            SupportsStreaming = true,
            ReportsReasoningTokens = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.OpenRouter,
            MaxBudgetTokens = 16_384,
        };

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(
                Effort: ReasoningEffortLevel.High,
                Output: ReasoningOutputMode.Summary),
            capabilities,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsSuccess);

    }

    private static ArcanumSettings SettingsWithReasoning(ReasoningCapabilities? reasoning) =>
        new()
        {
            DefaultModel = "reasoner",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "test",
                    Models = [new ModelEntry("reasoner") { Reasoning = reasoning }],
                },
            ],
        };

}

