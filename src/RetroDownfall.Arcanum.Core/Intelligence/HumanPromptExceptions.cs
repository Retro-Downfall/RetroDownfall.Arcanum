namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Maps a legacy/external MCP human-prompt timeout result. Arcanum's own human-prompt registry has
/// no total wait deadline; it waits for an operator response or caller cancellation.
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
