namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Raised when no human response arrives before the hard ask_human / elicitation ceiling.
/// Callers should surface this as an expected tool or elicitation result, not as an unexpected
/// infrastructure fault.
/// </summary>
public sealed class HumanPromptTimeoutException : Exception
{

    public const string DefaultMessage =
        "No human response was received before the ask_human timeout. Continue without operator input or explain what is needed.";

    public HumanPromptTimeoutException()
        : base(DefaultMessage)
    {
    }

    public HumanPromptTimeoutException(string message)
        : base(message)
    {
    }

}

/// <summary>
/// Raised when too many concurrent human-prompt waiters are registered.
/// </summary>
public sealed class HumanPromptCapExceededException : Exception
{

    public const string DefaultMessage =
        "Too many ask_human prompts are already waiting for a response. Answer or cancel outstanding prompts, then try again.";

    public HumanPromptCapExceededException()
        : base(DefaultMessage)
    {
    }

    public HumanPromptCapExceededException(string message)
        : base(message)
    {
    }

}
