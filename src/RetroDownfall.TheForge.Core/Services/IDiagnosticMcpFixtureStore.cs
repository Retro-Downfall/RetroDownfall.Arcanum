using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;

namespace RetroDownfall.TheForge.Core.Services;

/// <summary>Load/save The Forge-local Diagnostic MCP Invocation fixtures.</summary>
public interface IDiagnosticMcpFixtureStore
{

    string StorePath { get; }

    Task<DiagnosticMcpFixtureStoreDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DiagnosticMcpFixtureStoreDocument document, CancellationToken cancellationToken = default);

}
