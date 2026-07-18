namespace RetroDownfall.TheForge.Core.Models.DiagnosticMcp;

/// <summary>Root document for <c>~/.config/arcanum/the-forge-diagnostic-mcp-fixtures.json</c>.</summary>
public sealed record DiagnosticMcpFixtureStoreDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DiagnosticMcpFixtureRecord> Fixtures);

/// <summary>A saved diagnostic invocation fixture — operator-named; bounded history unless user-managed by name.</summary>
public sealed record DiagnosticMcpFixtureRecord(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ToolName,
    string? ServerName,
    string? WorkingDirectory,
    string ArgumentsJson,
    string? LastResultJson,
    DateTimeOffset? LastInvokedAt);
