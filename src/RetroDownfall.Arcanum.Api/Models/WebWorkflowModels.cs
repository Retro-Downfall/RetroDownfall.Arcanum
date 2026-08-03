using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Models;

public sealed record WebSearchWorkflowRequest
{

    public string Query { get; init; } = string.Empty;

    public int ResultCount { get; init; } = 5;

    public string? Freshness { get; init; }

    public string[] IncludeDomains { get; init; } = [];

    public string[] ExcludeDomains { get; init; } = [];

    public Guid? AttachToSessionId { get; init; }

}

public sealed record WebBrowseWorkflowRequest
{

    public string Url { get; init; } = string.Empty;

    public string RenderMode { get; init; } = "static";

    public Guid? AttachToSessionId { get; init; }

}

public sealed record WebResearchWorkflowRequest
{

    public string Question { get; init; } = string.Empty;

    public int? SourceTarget { get; init; }

    public string? Model { get; init; }

    public int TokenBudget { get; init; } = 2_000;

    public decimal? CostBudgetUsd { get; init; }

    public Guid? ContinueSessionId { get; init; }

    public Guid? AttachToSessionId { get; init; }

    public string WorkingDirectory { get; init; } = string.Empty;

    public Guid? CampaignId { get; init; }

    public List<AttachedFileDto>? AttachedFiles { get; init; }

    public List<ScryingFocusDto>? ScryingFoci { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public long? Seed { get; init; }

    public string? ResponseFormat { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    public bool UnattendedMode { get; init; }

}

public sealed record WebWorkflowCitation
{

    public int Index { get; init; }

    public string Url { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string? PublishedDate { get; init; }

}

public sealed record WebWorkflowLink
{

    public string Text { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

}

public sealed record WebWorkflowUsage
{

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public long TotalTokens { get; init; }

    public int SearchQueries { get; init; }

    public decimal? CostUsd { get; init; }

}

public sealed record WebSearchWorkflowResult
{

    public string Answer { get; init; } = string.Empty;

    public WebWorkflowCitation[] Citations { get; init; } = [];

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public bool Truncated { get; init; }

    public WebWorkflowUsage Usage { get; init; } = new();

    public Guid? AttachmentId { get; init; }

}

public sealed record WebBrowseWorkflowResult
{

    public string? Title { get; init; }

    public string Markdown { get; init; } = string.Empty;

    public string FinalUrl { get; init; } = string.Empty;

    public WebWorkflowLink[] Links { get; init; } = [];

    public string Provider { get; init; } = string.Empty;

    public string RenderMode { get; init; } = "static";

    public bool Truncated { get; init; }

    public Guid? AttachmentId { get; init; }

}

public sealed record WebResearchWorkflowResult
{

    public string Answer { get; init; } = string.Empty;

    public WebWorkflowCitation[] Citations { get; init; } = [];

    public string Provider { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public Guid? SessionId { get; init; }

    public bool Truncated { get; init; }

    public WebWorkflowUsage Usage { get; init; } = new();

    public Guid? AttachmentId { get; init; }

}

[JsonConverter(typeof(JsonStringEnumConverter<WebResearchStreamFrameType>))]

public enum WebResearchStreamFrameType
{

    Limits,

    Progress,

    Result,

    Error,

}

public sealed record WebResearchStreamFrame
{

    public WebResearchStreamFrameType Type { get; init; }

    public string? Stage { get; init; }

    public string? Message { get; init; }

    public string? Code { get; init; }

    public WebResearchWorkflowResult? Result { get; init; }

}
