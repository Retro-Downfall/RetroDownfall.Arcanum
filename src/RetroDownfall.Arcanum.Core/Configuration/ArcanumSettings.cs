using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ArcanumSettings
{

    /// <summary>
    /// Runtime edition / hardening mode. Default <see cref="ArcanumEdition.Local"/>. Overridable by
    /// <c>ARCANUM_EDITION</c>. See <see cref="Environment.ArcanumEnvironment.ResolveEdition"/>.
    /// </summary>
    public ArcanumEdition Edition { get; set; } = ArcanumEdition.Local;

    public HostSettings Host { get; set; } = new();

    public ProviderSettings[] Providers { get; set; } = [];

    public string? DefaultModel { get; set; }

    public string? FastModel { get; set; }

    public SecuritySettings Security { get; set; } = new();

    public WorkspaceSettings Workspaces { get; set; } = new();

    public FeatureSettings Features { get; set; } = new();

    public IntegrationSettings Integrations { get; set; } = new();

    public ExecutionSettings Execution { get; set; } = new();

    public CostSettings Cost { get; set; } = new();

    public DaemonSettings Daemon { get; set; } = new();

    public CliSettings Cli { get; set; } = new();

}

