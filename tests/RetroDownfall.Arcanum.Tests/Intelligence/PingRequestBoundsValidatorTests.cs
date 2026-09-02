using System.Reflection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class PingRequestBoundsValidatorTests
{

    // ArcanumSettings has no Intelligence/Sessions member for these bounds to read (Arcanum:
    // Intelligence is a rejected configuration section -- ConfigurationValidator.cs), so there is
    // no settings value that could make either method's refusal differ. The durable guard is
    // therefore a closed-arity pin: both entry points take exactly the request/count they validate
    // and nothing else, so a settings-shaped parameter cannot be reintroduced unnoticed.
    [Fact]
    public void Validate_TakesNoSettingsParameter()
    {

        MethodInfo method = typeof(PingRequestBoundsValidator).GetMethod(nameof(PingRequestBoundsValidator.Validate))!;

        Assert.Single(method.GetParameters());

    }

    [Fact]
    public void ValidateOpenApiMessageCount_TakesNoSettingsParameter()
    {

        MethodInfo method = typeof(PingRequestBoundsValidator).GetMethod(nameof(PingRequestBoundsValidator.ValidateOpenApiMessageCount))!;

        Assert.Single(method.GetParameters());

    }

    [Fact]
    public void Validate_RejectsOversizedPrompt()
    {
        int maxPromptChars = ArcanumSettingClamps.MaxPingPromptChars(
            ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars);
        PingRequest request = new(Prompt: new string('x', maxPromptChars + 1));

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.PromptTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsTooManyStatelessMessages()
    {
        int maxMessages = ArcanumSettingClamps.MaxStatelessMessages(
            ArcanumRuntimeDefaults.Intelligence.MaxStatelessMessages);
        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages: Enumerable.Range(0, maxMessages + 1)
                .Select(static index => new CoreChatMessage("user", index.ToString()))
                .ToList());

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.TooManyStatelessMessages", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsOversizedStatelessMessageContent()
    {
        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes);
        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages: [new CoreChatMessage("user", new string('x', maxEntryBytes + 1))]);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    [Fact]
    public void ValidateOpenApiMessageCount_RejectsExcessMessages()
    {
        int maxMessages = ArcanumSettingClamps.MaxOpenApiMessages(
            ArcanumRuntimeDefaults.Intelligence.MaxOpenApiMessages);
        Result result = PingRequestBoundsValidator.ValidateOpenApiMessageCount(maxMessages + 1);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.TooManyMessages", result.Error.Code);

    }

    [Fact]
    public void Validate_StatelessContent_MeasuredInUtf8BytesNotChars()
    {
        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes);
        int characterCount = (maxEntryBytes / 2) + 1;
        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages: [new CoreChatMessage("user", new string('\u00e9', characterCount))]);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_ToolCallArguments_CountTowardEntryBudget()
    {
        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes);
        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage(
                    "assistant",
                    string.Empty,
                    ToolCalls:
                    [
                        new CoreToolCall(
                            "call-1",
                            "fn",
                            new string('x', maxEntryBytes + 1)),
                    ]),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    /// <summary>
    /// <c>Content</c> is the flattened text of the same message <c>ContentParts</c> carries in
    /// structured form, and the mapper sends exactly one of the two. Adding them charged a caller twice
    /// for one payload, so the byte-identical JSON-string form of a request passed while the parts form
    /// was refused.
    /// </summary>
    [Fact]
    public void Validate_ContentAndItsFlattenedParts_AreChargedOnceNotTwice()
    {
        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes);

        // Comfortably under the cap on its own, comfortably over it when double-counted.
        string body = new('x', (maxEntryBytes * 3) / 5);

        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage(
                    "user",
                    body,
                    ContentParts: [new CoreContentPart("text", body, null, null)]),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsSuccess);

    }

    /// <summary>
    /// The counterpart: for <c>role = tool</c> the mapper branches on role before it looks at
    /// <c>ContentParts</c>, so it is <c>Content</c> that reaches the provider. Letting a tiny part
    /// stand in for the whole message would be a cap bypass, which is why this is a max and not a
    /// "parts win" skip.
    /// </summary>
    [Fact]
    public void Validate_ToolRoleContent_IsStillMeasuredWhenAPartIsPresent()
    {
        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            ArcanumRuntimeDefaults.Sessions.MaxEntryContentBytes);

        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage(
                    "tool",
                    new string('x', maxEntryBytes + 1),
                    ToolCallId: "call-1",
                    ContentParts: [new CoreContentPart("text", "tiny", null, null)]),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.StatelessMessageTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsOversizedAdditionalSystemPrompt()
    {
        int maxPromptChars = ArcanumSettingClamps.MaxPingPromptChars(
            ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars);
        PingRequest request = new(
            Prompt: "hi",
            AdditionalSystemPrompt: new string('x', maxPromptChars + 1));

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.AdditionalSystemPromptTooLong", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsTooManyContentPartsPerMessage()
    {

        // W6.5: the per-message content-part cap (previously only enforced on /v1) now also bounds
        // the native stateless path.
        int maxContentParts = ArcanumSettingClamps.MaxContentPartsPerMessage(
            ArcanumRuntimeDefaults.Intelligence.MaxContentPartsPerMessage);

        PingRequest request = new(
            Prompt: string.Empty,
            StatelessMessages:
            [
                new CoreChatMessage(
                    "user",
                    string.Empty,
                    ContentParts: Enumerable.Range(0, maxContentParts + 1)
                        .Select(static index =>
                            new CoreContentPart("text", index.ToString(), null, null))
                        .ToList()),
            ]);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.Equal("Validation.TooManyContentParts", result.Error.Code);

    }

    [Fact]
    public void Validate_RejectsReasoningEffortAndBudgetTogether()
    {

        PingRequest request = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(
                ReasoningEffortLevel.High,
                BudgetTokens: 4096,
                Output: null));

        Result result = PingRequestBoundsValidator.Validate(request);

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

        PingRequest request = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(BudgetTokens: budgetTokens));

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.InvalidReasoningBudget, result.Error.Code);

    }

    [Fact]
    public void ValidateForModel_RejectsExplicitEffortForUnsupportedModel()
    {

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(Effort: ReasoningEffortLevel.Minimal),
            modelEntry: null,
            featuresReasoningEnabled: true,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, result.Error.Code);

    }

    [Fact]
    public void ValidateForModel_RejectsExplicitBudgetWhenModelSupportsOnlyEffort()
    {

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(BudgetTokens: 2048),
            new ModelEntry("reasoner")
            {
                Reasoning = new ModelReasoningSettings { WireDialect = ReasoningWireDialect.Standard },
            },
            true,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningControl, result.Error.Code);

    }

    [Fact]
    public void ValidateForModel_RejectsBudgetAboveModelCapability()
    {

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(BudgetTokens: 4097),
            new ModelEntry("reasoner")
            {
                Reasoning = new ModelReasoningSettings
                {
                    WireDialect = ReasoningWireDialect.OpenRouter,
                    MaxBudgetTokens = 4096,
                },
            },
            true,
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

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(Output: output),
            modelEntry: null,
            featuresReasoningEnabled: true,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.UnsupportedReasoningOutput, result.Error.Code);

    }

    [Fact]
    public void Validate_AllowsExplicitNoneOutputWithoutReasoningCapability()
    {

        PingRequest request = new(
            Prompt: "hello",
            Reasoning: new ReasoningRequestOptions(Output: ReasoningOutputMode.None));

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void ValidateForModel_AllowsSupportedReasoningControlsAndOutput()
    {

        Result result = ReasoningRequestValidator.ValidateForModel(
            new ReasoningRequestOptions(
                Effort: ReasoningEffortLevel.High,
                Output: ReasoningOutputMode.Summary),
            new ModelEntry("reasoner")
            {
                Reasoning = new ModelReasoningSettings { WireDialect = ReasoningWireDialect.OpenRouter },
            },
            true,
            modelName: "reasoner",
            providerName: "test");

        Assert.True(result.IsSuccess);

    }

}
