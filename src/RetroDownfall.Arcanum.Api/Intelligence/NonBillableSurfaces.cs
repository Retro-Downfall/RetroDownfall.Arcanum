namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Closed list of surfaces that may touch configuration or do local work without creating an
/// inference run or billable ledger row (ADR 0002). Everything that hits a provider for tokens is
/// billable and must go through turn accounting.
/// </summary>
internal static class NonBillableSurfaces
{

    public const string GetModels = "GET /v1/models (and GET /api/models)";

    public const string ProviderTest = "POST /api/providers/test";

    public const string ManaPreflight = "POST /api/intelligence/mana";

    /// <summary>
    /// Familiar readiness. Asks the operator's CLI for its own status, never for a completion, so it
    /// spends nothing against their subscription.
    /// </summary>
    public const string FamiliarProbe = "GET /api/providers/{name}/familiar-probe";

}
