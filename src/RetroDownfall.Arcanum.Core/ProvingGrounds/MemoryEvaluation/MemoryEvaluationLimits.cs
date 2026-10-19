namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

public static class MemoryEvaluationLimits
{
    public const int PolicyVersion = 1;

    public const int RequestBytes = 128 * 1024;

    public const int SystemPromptBytes = 16 * 1024;

    public const int UserPromptBytes = 32 * 1024;

    public const int MinTargets = 1;

    public const int MaxTargets = 16;

    public const int ManifestCoordinates = 128;

    public const int RenderedMemoryBytes = 32 * 1024;

    public const int CompletePromptBytes = 96 * 1024;

    public const int MinChecks = 1;

    public const int MaxChecks = 16;

    public const int CheckBytes = 16 * 1024;

    public const int RegexBytes = 1024;

    public const int SchemaDepth = 16;

    public const int MinPairs = 3;

    public const int MaxPairs = 16;

    public const int DefaultPairs = 5;

    public const int MinOutputTokens = 1;

    public const int MaxOutputTokens = 4096;

    public const int DefaultOutputTokens = 512;

    public const int ProviderResponseBytes = 1024 * 1024;

    public const int RetainedTextBytes = 64 * 1024;

    public const int EvidenceBytes = 64 * 1024;

    public const int RetainedRegistrations = 1024;

    public const int ActiveEvaluations = 1;

    public const int AutomaticRetries = 0;

    public static readonly TimeSpan InitialStartGrace = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
}
