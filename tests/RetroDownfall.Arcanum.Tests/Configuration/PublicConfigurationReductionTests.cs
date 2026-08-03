using System.Reflection;

using Microsoft.Extensions.Configuration;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class PublicConfigurationReductionTests
{

    [Theory]

    [InlineData(typeof(EmbeddingIntegrationSettings), "CodebaseIndexing")]

    [InlineData(typeof(EmbeddingIntegrationSettings), "AttachmentIndexing")]

    [InlineData(typeof(ExecutionSettings), "MaxPendingApprenticeStarts")]

    [InlineData(typeof(RetentionSettings), "MaxItemsPerSweep")]

    [InlineData(typeof(RetentionSettings), "CheckpointInterval")]

    [InlineData(typeof(HostAuditPolicySettings), "RetentionDays")]

    [InlineData(typeof(GuardrailsAuditPolicySettings), "RetentionDays")]

    public void Public_configuration_omits_internal_workflow_controls(
        Type settingsType,
        string propertyName)
    {

        Assert.Null(
            settingsType.GetProperty(
                propertyName,
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly));

    }

    [Theory]

    [InlineData(typeof(CodebaseEmbeddingSettings), "MaxFileSizeChars")]

    [InlineData(typeof(AttachmentEmbeddingSettings), "MaxAttachmentBytes")]

    [InlineData(typeof(AttachmentEmbeddingSettings), "MaxExtractedCharacters")]

    [InlineData(typeof(AttachmentEmbeddingSettings), "MaxChunksPerAttachment")]

    [InlineData(typeof(AttachmentEmbeddingSettings), "MaxRetries")]

    [InlineData(typeof(AttachmentEmbeddingSettings), "RetryDelaySeconds")]

    [InlineData(typeof(AttachmentEmbeddingSettings), "ProcessingTimeoutSeconds")]

    public void Automatic_indexing_mechanics_have_no_hidden_total_work_ceiling(
        Type settingsType,
        string propertyName)
    {

        Assert.Null(settingsType.GetProperty(propertyName));

    }

    [Fact]

    public void Embedding_runtime_has_no_code_owned_total_request_deadline()
    {

        Assert.Null(typeof(EmbeddingSettings).GetProperty("RequestTimeoutSeconds"));

        Assert.Null(
            typeof(ArcanumSettingClamps).GetMethod(
                "EmbeddingsRequestTimeoutSeconds",
                BindingFlags.Public | BindingFlags.Static));

    }

    [Theory]

    [InlineData("CodebaseIndexingIntegrationSettings")]

    [InlineData("AttachmentIndexingIntegrationSettings")]

    public void Removed_internal_workflow_types_do_not_remain_public(
        string removedTypeName)
    {

        Assert.Null(
            typeof(ArcanumSettings).Assembly.GetType(
                $"{typeof(ArcanumSettings).Namespace}.{removedTypeName}"));

    }

    [Theory]

    [InlineData("Host:AuditLog:RetentionDays", "host.auditLog.retentionDays")]

    [InlineData("Security:Guardrails:AuditLog:RetentionDays", "security.guardrails.auditLog.retentionDays")]

    [InlineData("Integrations:Embeddings:CodebaseIndexing:MaxWatchers", "integrations.embeddings.codebaseIndexing.maxWatchers")]

    [InlineData("Integrations:Embeddings:AttachmentIndexing:MaxRetries", "integrations.embeddings.attachmentIndexing.maxRetries")]

    [InlineData("Execution:MaxPendingApprenticeStarts", "execution.maxPendingApprenticeStarts")]

    [InlineData("Retention:MaxItemsPerSweep", "retention.maxItemsPerSweep")]

    [InlineData("Retention:CheckpointInterval", "retention.checkpointInterval")]

    public void Removed_workflow_keys_receive_actionable_migration_errors(
        string configurationPath,
        string expectedPointer)
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {

                    [$"Arcanum:{configurationPath}"] = "1",

                })
            .Build();

        Result result = new ConfigurationValidator().RejectObsoleteKeys(configuration);

        ConfigurationValidationError error = Assert.Single(
            result.Error.Details!,
            candidate => string.Equals(
                candidate.Pointer,
                expectedPointer,
                StringComparison.Ordinal));

        Assert.Contains("Remove", error.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("automatic", error.Detail, StringComparison.OrdinalIgnoreCase);

    }

}
