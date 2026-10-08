using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Tests.Support;

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

    /// <summary>
    /// The walk above only reaches types hanging off <see cref="ArcanumSettings"/>. The runtime
    /// projections (IntelligenceSettings and the like) are code-owned, but a property on any
    /// <c>*Settings</c> type that is init-only is either silently dropped the day the type becomes
    /// bound or is an unreachable member nobody can set, so the scan covers every such type.
    /// </summary>
    [Fact]
    public void Every_Settings_type_has_no_init_only_public_properties()
    {
        List<string> initOnly = [];

        foreach (Type type in typeof(ArcanumSettings).Assembly.GetTypes()
            .Where(static type => type.IsClass
                && type.Name.EndsWith("Settings", StringComparison.Ordinal)
                && type.Namespace == typeof(ArcanumSettings).Namespace))
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (property.SetMethod is { } setter
                    && setter.ReturnParameter
                        .GetRequiredCustomModifiers()
                        .Contains(typeof(IsExternalInit)))
                {
                    initOnly.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.Empty(initOnly);
    }

    /// <summary>
    /// A property on the code-owned runtime projection that nothing reads is a setting that does
    /// nothing: it can be set, projected and documented while changing no behaviour.
    /// </summary>
    /// <remarks>
    /// <see cref="IntelligenceSettings"/> is projected in code by
    /// <see cref="ArcanumRuntimeSettings.ResolveIntelligence"/> and is not bound from configuration,
    /// so the binder cannot be what reads a property. Only a member access can: a property that is
    /// assigned in an initializer or a <c>with</c> expression but never accessed is dead, and this is
    /// how the three retired flags (and <c>DefaultReasoningEffort</c>, which was projected from the
    /// reasoning defaults and never consulted) stayed in the type unnoticed.
    /// </remarks>
    [Fact]
    public void Every_IntelligenceSettings_property_is_read_in_production()
    {
        string[] properties =
        [
            .. typeof(IntelligenceSettings)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(static property => property.Name),
        ];

        Assert.NotEmpty(properties);

        HashSet<string> read = new(StringComparer.Ordinal);

        foreach (ProductionSource source in ProductionSourceInventory.Sources())
        {
            if (source.Is("IntelligenceSettings.cs") || !properties.Any(name => source.Names("." + name)))
            {
                continue;
            }

            foreach (SyntaxNode node in CSharpSyntaxTree.ParseText(source.Text).GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case MemberAccessExpressionSyntax { Parent: AssignmentExpressionSyntax assignment } access
                        when assignment.Left == access:
                        // A write is not a read.
                        break;

                    case MemberAccessExpressionSyntax access:
                        _ = read.Add(access.Name.Identifier.ValueText);

                        break;

                    case MemberBindingExpressionSyntax binding:
                        _ = read.Add(binding.Name.Identifier.ValueText);

                        break;
                }
            }
        }

        string[] unread =
        [
            .. properties.Where(name => !read.Contains(name)).Order(StringComparer.Ordinal),
        ];

        Assert.True(
            unread.Length == 0,
            "IntelligenceSettings properties that no production code reads: " + string.Join(", ", unread));
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
