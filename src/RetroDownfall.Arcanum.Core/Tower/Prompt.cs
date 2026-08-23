namespace RetroDownfall.Arcanum.Core.Tower;

public sealed class Prompt
{

    public Guid Id { get; set; }

    public Guid? CampaignId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Tags { get; set; } = "[]";

    public string Template { get; set; } = string.Empty;

    public string? ParameterSchema { get; set; }

    public string? DefaultParameters { get; set; }

    public string? Model { get; set; }

    public string? Provider { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? MaxOutputTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Campaign? Campaign { get; set; }

}
