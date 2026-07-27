using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class DisabledSettingPathsTests
{

    [Fact]
    public void Disabled_banners_reference_only_retained_configuration_paths()
    {

        Assert.Equal("Arcanum:Features:Embeddings", DisabledSettingPaths.EmbeddingsEnabled);
        Assert.Equal("Arcanum:Features:SessionSearch", DisabledSettingPaths.SessionSearchEnabled);
        Assert.Equal("Arcanum:Features:CodebaseRetrieval", DisabledSettingPaths.CodebaseRetrievalEnabled);
        Assert.Equal("Arcanum:Features:Saga", DisabledSettingPaths.SagaEnabled);
        Assert.Equal("Arcanum:Features:MemoryManagement", DisabledSettingPaths.AllowMemoryManagement);
        Assert.Equal("Arcanum:Workspaces:EnableFileWrite", DisabledSettingPaths.EnableFileWrite);
        Assert.Equal("Arcanum:Cost:Budget:Enabled", DisabledSettingPaths.BudgetEnabled);
        Assert.Equal("Arcanum:Host:AuditLog:Enabled", DisabledSettingPaths.InferenceAuditLogEnabled);
        Assert.Equal(
            "Arcanum:Security:Guardrails:AuditLog:Enabled",
            DisabledSettingPaths.GuardrailsAuditLogEnabled);

    }

}
