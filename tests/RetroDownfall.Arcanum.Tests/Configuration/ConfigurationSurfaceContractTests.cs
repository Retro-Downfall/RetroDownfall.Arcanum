using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationSurfaceContractTests
{

    private static readonly string[] RetainedRootPropertyNames =
    [
        "Cli",
        "Cost",
        "Daemon",
        "DefaultModel",
        "Edition",
        "Execution",
        "FastModel",
        "Features",
        "Host",
        "Integrations",
        "Providers",
        "Retention",
        "Security",
        "Workspaces",
    ];

    private static readonly string[] ObsoleteRootKeys =
    [
        "apprentices",
        "attachments",
        "batches",
        "budget",
        "campaigns",
        "clientToolForwarding",
        "codex",
        "codingTools",
        "commLink",
        "conclave",
        "embeddings",
        "eventBus",
        "files",
        "grimoire",
        "guardrails",
        "intelligence",
        "logs",
        "mcp",
        "metrics",
        "perception",
        "pricing",
        "prompts",
        "provingGrounds",
        "resilience",
        "scrying",
        "server",
        "sessions",
        "spells",
        "structuredOutput",
        "ward",
        "webBrowsing",
    ];

    [Fact]
    public void ArcanumSettings_root_properties_match_minimal_taxonomy()
    {

        string[] actual = typeof(ArcanumSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RetainedRootPropertyNames, actual);

    }

    [Fact]
    public void ArcanumSettings_has_no_compatibility_base_state()
    {
        Assert.Equal(typeof(object), typeof(ArcanumSettings).BaseType);
    }

    [Fact]
    public void Retained_configuration_graph_has_only_mutable_properties()
    {

        PropertyInfo[] retainedRootProperties = typeof(ArcanumSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(static property =>
                RetainedRootPropertyNames.Contains(property.Name, StringComparer.Ordinal))
            .ToArray();
        HashSet<Type> visited = [];
        List<string> immutableProperties = [];

        foreach (PropertyInfo property in retainedRootProperties)
        {
            string path = property.Name;

            AddMutabilityIssue(property, path, immutableProperties);

            foreach (Type configurationType in GetConfigurationNodeTypes(property.PropertyType))
            {
                InspectConfigurationType(
                    configurationType,
                    path,
                    visited,
                    immutableProperties);
            }
        }

        Assert.Empty(immutableProperties);

    }

    [Theory]
    [InlineData("Host", "HostSettings")]
    [InlineData("Providers", "ProviderSettings")]
    [InlineData("Security", "SecuritySettings")]
    [InlineData("Workspaces", "WorkspaceSettings")]
    [InlineData("Features", "FeatureSettings")]
    [InlineData("Integrations", "IntegrationSettings")]
    [InlineData("Execution", "ExecutionSettings")]
    [InlineData("Cost", "CostSettings")]
    [InlineData("Daemon", "DaemonSettings")]
    [InlineData("Cli", "CliSettings")]
    public void ConfigurationJsonContext_registers_retained_section(
        string rootPropertyName,
        string sectionTypeName)
    {

        Type? sectionType = typeof(ArcanumSettings).Assembly.GetType(
            $"{typeof(ArcanumSettings).Namespace}.{sectionTypeName}");
        PropertyInfo? rootProperty = typeof(ArcanumSettings).GetProperty(
            rootPropertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.True(
            sectionType is not null,
            $"Retained configuration section type '{sectionTypeName}' is missing.");
        Assert.True(
            rootProperty is not null,
            $"ArcanumSettings.{rootPropertyName} is missing.");

        Type actualSectionType = Assert.Single(
            GetConfigurationNodeTypes(rootProperty!.PropertyType));

        Assert.Equal(sectionType, actualSectionType);
        Assert.NotNull(ConfigurationJsonContext.Default.GetTypeInfo(sectionType!));

    }

    [Theory]
    [InlineData(typeof(ServerSettings))]
    [InlineData(typeof(ConclaveSettings))]
    [InlineData(typeof(IntelligenceSettings))]
    [InlineData(typeof(ApprenticeSettings))]
    [InlineData(typeof(EventBusSettings))]
    [InlineData(typeof(SessionSettings))]
    [InlineData(typeof(McpSettings))]
    [InlineData(typeof(EmbeddingSettings))]
    [InlineData(typeof(ScryingSettings))]
    [InlineData(typeof(StructuredOutputSettings))]
    [InlineData(typeof(GuardrailsSettings))]
    public void ConfigurationJsonContext_does_not_register_runtime_projection_types(Type type)
    {
        Assert.Null(ConfigurationJsonContext.Default.GetTypeInfo(type));
    }

    [Theory]
    [InlineData("IntelligenceSettings", "EnableLoreSystem")]
    [InlineData("IntelligenceSettings", "MaxToolInferenceRounds")]
    [InlineData("IntelligenceSettings", "MaxPlanSteps")]
    [InlineData("IntelligenceSettings", "InferenceTimeoutSeconds")]
    [InlineData("ApprenticeSettings", "StepTimeoutMinutes")]
    [InlineData("ApprenticeSettings", "MaxStepRetries")]
    [InlineData("ApprenticeSettings", "RetryBackoffSeconds")]
    [InlineData("ApprenticeSettings", "RetryBackoffMaxSeconds")]
    [InlineData("ApprenticeSettings", "MaxRunSteps")]
    [InlineData("ApprenticeSettings", "MaxRunDurationMinutes")]
    [InlineData("ApprenticeSettings", "MaxReweavesPerRun")]
    [InlineData("ApprenticeSettings", "MaxSimulacra")]
    [InlineData("ConclaveSettings", "MaxDelegationDepth")]
    [InlineData("ConclaveSettings", "MaxDescendantsPerRoot")]
    [InlineData("ConclaveA2ASettings", "ExternalTaskTimeoutMinutes")]
    [InlineData("ResilienceSettings", "MaxFallbackAttempts")]
    [InlineData("StructuredOutputSettings", "MaxValidationRetries")]
    public void Runtime_projection_types_do_not_restore_deleted_workflow_policy(
        string typeName,
        string propertyName)
    {
        Type? settingsType = typeof(ArcanumSettings).Assembly.GetType(
            $"{typeof(ArcanumSettings).Namespace}.{typeName}");

        Assert.NotNull(settingsType);
        Assert.Null(settingsType!.GetProperty(
            propertyName,
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void RejectObsoleteKeys_reports_removed_root_sections_together()
    {

        Dictionary<string, string?> values = ObsoleteRootKeys.ToDictionary(
            static key => $"Arcanum:{key}:configured",
            static _ => (string?)"true",
            StringComparer.Ordinal);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Result result = new ConfigurationValidator().RejectObsoleteKeys(configuration);

        Assert.True(result.IsFailure);
        Assert.Equal("Configuration.ValidationFailed", result.Error.Code);
        Assert.NotNull(result.Error.Details);

        string[] actualPointers = result.Error.Details!
            .Select(static error => error.Pointer)
            .OrderBy(static pointer => pointer, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ObsoleteRootKeys, actualPointers);

    }

    private static void InspectConfigurationType(
        Type type,
        string path,
        HashSet<Type> visited,
        List<string> immutableProperties)
    {

        if (!visited.Add(type))
        {
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            string propertyPath = $"{path}.{property.Name}";

            AddMutabilityIssue(property, propertyPath, immutableProperties);

            foreach (Type configurationType in GetConfigurationNodeTypes(property.PropertyType))
            {
                InspectConfigurationType(
                    configurationType,
                    propertyPath,
                    visited,
                    immutableProperties);
            }
        }

    }

    private static void AddMutabilityIssue(
        PropertyInfo property,
        string path,
        List<string> immutableProperties)
    {

        if (property.GetMethod is not { IsPublic: true })
        {
            immutableProperties.Add($"{path} has no public getter.");
        }

        MethodInfo? setter = property.SetMethod;

        if (setter is not { IsPublic: true })
        {
            immutableProperties.Add($"{path} has no public setter.");

            return;
        }

        if (setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Contains(typeof(IsExternalInit)))
        {
            immutableProperties.Add($"{path} has an init-only setter.");
        }

    }

    private static IEnumerable<Type> GetConfigurationNodeTypes(Type propertyType)
    {

        Type type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (type.IsArray)
        {
            foreach (Type elementType in GetConfigurationNodeTypes(type.GetElementType()!))
            {
                yield return elementType;
            }

            yield break;
        }

        Type? dictionaryType =
            FindGenericContract(type, typeof(IDictionary<,>))
            ?? FindGenericContract(type, typeof(IReadOnlyDictionary<,>));

        if (dictionaryType is not null)
        {
            foreach (Type valueType in GetConfigurationNodeTypes(
                dictionaryType.GetGenericArguments()[1]))
            {
                yield return valueType;
            }

            yield break;
        }

        Type? enumerableType = FindGenericContract(type, typeof(IEnumerable<>));

        if (enumerableType is not null)
        {
            foreach (Type elementType in GetConfigurationNodeTypes(
                enumerableType.GetGenericArguments()[0]))
            {
                yield return elementType;
            }

            yield break;
        }

        if (type.Assembly == typeof(ArcanumSettings).Assembly && !type.IsEnum)
        {
            yield return type;
        }

    }

    private static Type? FindGenericContract(Type type, Type genericTypeDefinition)
    {

        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == genericTypeDefinition)
        {
            return type;
        }

        return type.GetInterfaces().FirstOrDefault(candidate =>
            candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() == genericTypeDefinition);

    }

}
