namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// Wire-stable dotted error codes shared across Api, Infrastructure, and Cli layers.
/// Values are contract strings — do not rename without updating tests and JSON envelopes.
/// </summary>
public static class ErrorCodes
{

    /// <summary>Validation — client input rejected before domain work.</summary>
    public static class Validation
    {

        public const string InvalidPrompt = "Validation.InvalidPrompt";

        public const string AttachedFiles = "Validation.AttachedFiles";

        public const string InvalidBody = "Validation.InvalidBody";

    }

    /// <summary>Hub — intelligence provider / inference pipeline failures.</summary>
    public static class Hub
    {

        public const string ToolLoop = "Hub.ToolLoop";

        public const string Timeout = "Hub.Timeout";

        public const string Model = "Hub.Model";

        public const string Error = "Hub.Error";

    }

    /// <summary>Ollama — local model provider integration.</summary>
    public static class Ollama
    {

        public const string Pull = "Ollama.Pull";

        public const string ListModels = "Ollama.ListModels";

        public const string Error = "Ollama.Error";

    }

    /// <summary>Campaign — forge workspace registration and paths.</summary>
    public static class Campaign
    {

        public const string NotFound = "Campaign.NotFound";

        public const string InvalidPath = "Campaign.InvalidPath";

        public const string PathNotAllowed = "Campaign.PathNotAllowed";

        public const string MaxReached = "Campaign.MaxReached";

    }

    /// <summary>Session — grimoire conversation persistence.</summary>
    public static class Session
    {

        public const string NotFound = "Session.NotFound";

    }

    /// <summary>Grimoire — lore and knowledge store.</summary>
    public static class Grimoire
    {

        public const string LoreNotFound = "Grimoire.LoreNotFound";

    }

    /// <summary>Apprentice — autonomous agent orchestration.</summary>
    public static class Apprentice
    {

        public const string NotFound = "Apprentice.NotFound";

        public const string Disabled = "Apprentice.Disabled";

        public const string AlreadyRunning = "Apprentice.AlreadyRunning";

        public const string Running = "Apprentice.Running";

        public const string NotPaused = "Apprentice.NotPaused";

        public const string CannotReweave = "Apprentice.CannotReweave";

        public const string InvalidGuidance = "Apprentice.InvalidGuidance";

        public const string NotEscalated = "Apprentice.NotEscalated";

        public const string PendingQueueFull = "Apprentice.PendingQueueFull";

        public const string MaxReached = "Apprentice.MaxReached";

        public const string InvalidPlan = "Apprentice.InvalidPlan";

        public const string InvalidGoal = "Apprentice.InvalidGoal";

        public const string InvalidWorkspace = "Apprentice.InvalidWorkspace";

        public const string ConclaveDisabled = "Apprentice.ConclaveDisabled";

        public const string ConclaveDepthExceeded = "Apprentice.ConclaveDepthExceeded";

        public const string ConclaveBreadthExceeded = "Apprentice.ConclaveBreadthExceeded";

    }

    /// <summary>Workspace — registered filesystem roots.</summary>
    public static class Workspace
    {

        public const string NotFound = "Workspace.NotFound";

        public const string NameEmpty = "Workspace.NameEmpty";

        public const string PathNotAllowed = "Workspace.PathNotAllowed";

    }

    /// <summary>Spell — workspace spell files and execution.</summary>
    public static class Spell
    {

        public const string NotFound = "Spell.NotFound";

        public const string PathNotAllowed = "Spell.PathNotAllowed";

        public const string NoWorkspace = "Spell.NoWorkspace";

        public const string InvalidWorkspace = "Spell.InvalidWorkspace";

    }

    /// <summary>Prompt — named prompt templates.</summary>
    public static class Prompt
    {

        public const string NotFound = "Prompt.NotFound";

        public const string CodexPathNotContained = "Prompt.CodexPathNotContained";

    }

    /// <summary>Intelligence — cross-surface inference helpers.</summary>
    public static class Intelligence
    {

        public const string HumanPromptNotFound = "Intelligence.HumanPromptNotFound";

    }

    /// <summary>Mcp — Model Context Protocol servers and transport.</summary>
    public static class Mcp
    {

        public const string AmbiguousServer = "Mcp.AmbiguousServer";

        public const string MissingWorkspace = "Mcp.MissingWorkspace";

        public const string ServerNotFound = "Mcp.ServerNotFound";

    }

    /// <summary>Llama — local GGUF model cache and server lifecycle.</summary>
    public static class Llama
    {

        public const string ModelNotCached = "Llama.ModelNotCached";

    }

    /// <summary>Daemon — background job orchestration.</summary>
    public static class Daemon
    {

        public const string NotFound = "Daemon.NotFound";

    }

    /// <summary>CommLink — outbound webhook notifications. Suppressed outcomes are expected, not 5xx.</summary>
    public static class CommLink
    {

        public const string Suppressed = "CommLink.Suppressed";

    }

    /// <summary>Api — HTTP surface and streaming admission.</summary>
    public static class Api
    {

        public const string TooManyConnections = "Api.TooManyConnections";

    }

    /// <summary>RateLimit — request throttling.</summary>
    public static class RateLimit
    {

        public const string TooManyRequests = "RateLimit.TooManyRequests";

    }

    /// <summary>Connection — client transport failures.</summary>
    public static class Connection
    {

        public const string Timeout = "Connection.Timeout";

    }

    /// <summary>Security — authentication and outbound URL policy.</summary>
    public static class Security
    {

        public const string MissingApiKey = "Security.MissingApiKey";

        public const string BlockedOutboundUrl = "Security.BlockedOutboundUrl";

    }

    /// <summary>ProvingGrounds — spell/prompt/plan validation trials.</summary>
    public static class ProvingGrounds
    {

        public const string InvalidTrial = "ProvingGrounds.InvalidTrial";

        public const string TooManyInquisitors = "ProvingGrounds.TooManyInquisitors";

        public const string WorkspaceNotAllowed = "ProvingGrounds.WorkspaceNotAllowed";

        public const string SpellNotFound = "ProvingGrounds.SpellNotFound";

        public const string PromptNotFound = "ProvingGrounds.PromptNotFound";

        public const string InferenceFailed = "ProvingGrounds.InferenceFailed";

    }

}
