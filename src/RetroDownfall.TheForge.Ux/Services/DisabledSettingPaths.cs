namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Canonical Arcanum configuration paths surfaced in disabled-state banners across The Forge.
/// Values match <c>arcanum.json</c> colon notation exactly (e.g. <c>Arcanum:Features:Embeddings</c>).
/// </summary>
public static class DisabledSettingPaths
{

    public const string EmbeddingsEnabled = "Arcanum:Features:Embeddings";

    public const string SessionSearchEnabled = "Arcanum:Features:SessionSearch";

    public const string CodebaseRetrievalEnabled = "Arcanum:Features:CodebaseRetrieval";

    public const string SagaEnabled = "Arcanum:Features:Saga";

    public const string EnableFileWrite = "Arcanum:Workspaces:EnableFileWrite";

    public const string AllowMemoryManagement = "Arcanum:Features:MemoryManagement";

    public const string BudgetEnabled = "Arcanum:Cost:Budget:Enabled";

    public const string InferenceAuditLogEnabled = "Arcanum:Host:AuditLog:Enabled";

    public const string GuardrailsAuditLogEnabled = "Arcanum:Security:Guardrails:AuditLog:Enabled";

    public static readonly string[] SessionDivination = [EmbeddingsEnabled, SessionSearchEnabled];

    public static readonly string[] WorkspaceDivination = [EmbeddingsEnabled, CodebaseRetrievalEnabled];

    public static readonly string[] WorkspaceIndexing = [EmbeddingsEnabled, CodebaseRetrievalEnabled];

    public static readonly string[] SagaDivination = [EmbeddingsEnabled, SagaEnabled];

    public static readonly string[] WorkspaceFileWrite = [EnableFileWrite];

    public static readonly string[] SessionMemoryManagement = [AllowMemoryManagement];

    public static readonly string[] Budget = [BudgetEnabled];

    public static readonly string[] InferenceAudit = [InferenceAuditLogEnabled];

    public static readonly string[] GuardrailsAudit = [GuardrailsAuditLogEnabled];

    public static string JoinForClipboard(IReadOnlyList<string> paths) =>
        string.Join(Environment.NewLine, paths);

    public static string FormatEnableMessage(string featureLabel, IReadOnlyList<string> paths) =>
        paths.Count switch
        {

            0 => $"{featureLabel} is disabled.",

            1 => $"{featureLabel} is disabled — enable {paths[0]} server-side.",

            _ => $"{featureLabel} is disabled — enable {string.Join(" and ", paths)} server-side.",

        };

}
