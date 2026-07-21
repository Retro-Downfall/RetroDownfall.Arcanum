namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Why a chat-provider call is being made on the turn pipeline.</summary>
public enum ModelCallPurpose
{
    MainInference = 0,
    ToolContinuation = 1,
    ToolCompatibilityRetry = 2,
    SpellRouting = 3,
    LexiconExtraction = 4,
    StructuredOutputRetry = 5,
}

