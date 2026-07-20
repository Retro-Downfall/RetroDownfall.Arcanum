namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Persisted The Forge settings, round-tripped to <c>~/.config/arcanum/the-forge.json</c>
/// (<see cref="RetroDownfall.Arcanum.Core.Storage.ArcanumPaths.GrimoireDirectory"/>).
///
/// Property-style (not positional) to match Arcanum's own <c>ArcanumSettings</c> convention: a real
/// public parameterless constructor is required for <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>'s
/// default <c>OptionsFactory</c>, which creates the default instance via
/// <c>Activator.CreateInstance&lt;TOptions&gt;()</c> before binding — a positional record whose
/// primary constructor parameters merely all have default values does NOT satisfy that (the
/// compiler only synthesizes the default arguments at C# call sites, not a reflection-visible
/// zero-parameter constructor).
/// </summary>
public sealed record TheForgeSettings
{

    public string BaseUrl { get; init; } = "http://localhost:5001";

    public string? ApiKey { get; init; }

    public string Theme { get; init; } = "light";

    public Guid? LastCampaignId { get; init; }

    public string? LayoutState { get; init; }

    public bool AutoConnect { get; init; } = true;

    public Guid? ActiveSessionId { get; init; }

    /// <summary>Max retained suite run history entries per suite (default 100).</summary>
    public int MaxTrialSuiteRunHistory { get; init; } = 100;

    /// <summary>Max retained Comparison Workbench runs (default 100).</summary>
    public int MaxComparisonRunHistory { get; init; } = 100;

    /// <summary>Max retained local inference traces (default 100).</summary>
    public int MaxInferenceTraceHistory { get; init; } = 100;

    /// <summary>Max retained Diagnostic MCP Invocation fixtures (default 100; user-managed by name).</summary>
    public int MaxDiagnosticMcpFixtures { get; init; } = 100;

}
