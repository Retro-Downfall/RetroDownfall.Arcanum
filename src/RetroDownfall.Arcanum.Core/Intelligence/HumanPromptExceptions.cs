namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Represents a human-prompt timeout from a code-owned invocation. External MCP error text does
/// not construct this exception. Arcanum's own registry has no total wait deadline; it waits for
/// an operator response or caller cancellation.
/// </summary>
public sealed class HumanPromptTimeoutException : Exception
{

    public const string DefaultMessage =
        "No operator response was received before the human prompt timed out.";

    public HumanPromptTimeoutException()
        : base(DefaultMessage)
    {
    }

    public HumanPromptTimeoutException(string message)
        : base(message)
    {
    }

}
