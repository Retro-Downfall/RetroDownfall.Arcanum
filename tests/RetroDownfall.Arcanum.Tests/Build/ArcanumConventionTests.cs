using System.Reflection;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Api.Serialization;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// House-style rules that are cheap to state and easy to break one type at a time.
/// </summary>
public sealed class ArcanumConventionTests
{
    /// <summary>
    /// Namespaces whose types speak somebody else's wire: the OpenAI-compatible <c>/v1</c> surface, the
    /// Familiar provider frames, and the A2A protocol. They carry <c>[JsonPropertyName]</c> because the spec
    /// names the members, not because the naming policy would not do.
    /// </summary>
    private static readonly string[] ExternalWireNamespaceMarkers = [".OpenAi", ".Familiars", ".A2A"];

    /// <summary>
    /// The individual types outside those namespaces whose member names another party owns, each with the
    /// party. Named one by one, by full name, so a new type cannot inherit an exemption by its namespace.
    /// </summary>
    private static readonly Dictionary<string, string> ExternallyNamedTypes = new(StringComparer.Ordinal)
    {
        // Parsed from a model's output: the member names are the JSON contract the prompt asks it to follow.
        ["RetroDownfall.Arcanum.Api.Intelligence.SemanticSpellResponse"] = "model output contract",
        ["RetroDownfall.Arcanum.Api.Intelligence.LexiconEntityExtractionResponse"] = "model output contract",

        // OpenAI's usage object: /v1 writes it as OpenAI names it, and the native result frame carries the
        // same snake_case names (API.md, NDJSON streaming).
        ["RetroDownfall.Arcanum.Core.Intelligence.Models.ChatCompletionUsage"] = "OpenAI usage object",

        // The arcanum.json file: its root key is the Arcanum configuration section, which the configuration
        // binder addresses as "Arcanum".
        ["RetroDownfall.Arcanum.Core.Configuration.ArcanumConfigurationFile"] = "configuration file format",
    };

    /// <summary>
    /// An <c>/api</c> payload is named by the camelCase policy on <see cref="ArcanumJsonContext"/>; a member
    /// that restates its own name with <c>[JsonPropertyName]</c> is a second source of truth that drifts
    /// (AGENTS.md rule 4). The attribute stays only on the external wires named above.
    /// </summary>
    /// <remarks>
    /// Every first-party type reachable from the context is checked, whichever assembly declares it: a Core
    /// type registered on the context is as much an <c>/api</c> wire type as an Api one, and the native
    /// inference frame (<c>IntelligenceEvent</c>) is one.
    /// </remarks>
    [Fact]
    public void ApiWireTypes_DoNotUseJsonPropertyName()
    {
        Queue<Type> pending = new(
            typeof(ArcanumJsonContext).CustomAttributes
                .Where(static attribute => attribute.AttributeType == typeof(JsonSerializableAttribute))
                .Select(static attribute => (Type)attribute.ConstructorArguments[0].Value!));

        HashSet<Type> visited = [];

        List<string> offenders = [];

        while (pending.TryDequeue(out Type? type))
        {
            if (!visited.Add(type))
            {
                continue;
            }

            if (type.HasElementType)
            {
                pending.Enqueue(type.GetElementType()!);

                continue;
            }

            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    pending.Enqueue(argument);
                }
            }

            if (!IsFirstParty(type) || IsExternalWire(type))
            {
                continue;
            }

            foreach (MemberInfo member in type.GetMembers(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (member.GetCustomAttribute<JsonPropertyNameAttribute>() is { } attribute)
                {
                    offenders.Add($"{type.FullName}.{member.Name} [JsonPropertyName(\"{attribute.Name}\")]");
                }

                if (member is PropertyInfo property)
                {
                    pending.Enqueue(property.PropertyType);
                }
            }

            foreach (ConstructorInfo constructor in type.GetConstructors())
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    if (parameter.GetCustomAttribute<JsonPropertyNameAttribute>() is { } parameterAttribute)
                    {
                        offenders.Add(
                            $"{type.FullName} constructor parameter {parameter.Name} [JsonPropertyName(\"{parameterAttribute.Name}\")]");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Api wire types that restate their member names with [JsonPropertyName] instead of leaving them to "
            + $"the camelCase policy: {string.Join("; ", offenders.Order(StringComparer.Ordinal))}");
    }

    private static bool IsFirstParty(Type type) =>
        (type.Assembly.GetName().Name ?? string.Empty).StartsWith("RetroDownfall.Arcanum.", StringComparison.Ordinal);

    private static bool IsExternalWire(Type type) =>
        ExternallyNamedTypes.ContainsKey(type.FullName ?? string.Empty)
        || ExternalWireNamespaceMarkers.Any(marker =>
            (type.Namespace ?? string.Empty).Contains(marker, StringComparison.Ordinal));

    /// <summary>
    /// Each named exemption is a real type that really is reachable, so the list cannot keep a stale entry
    /// or a misspelling that exempts nothing.
    /// </summary>
    [Fact]
    public void Every_externally_named_type_exists_and_carries_the_attribute()
    {
        Assembly[] firstParty =
        [
            typeof(ArcanumJsonContext).Assembly,
            typeof(RetroDownfall.Arcanum.Core.Intelligence.Models.ChatCompletionUsage).Assembly,
        ];

        foreach (string fullName in ExternallyNamedTypes.Keys)
        {
            Type? type = firstParty.Select(assembly => assembly.GetType(fullName)).FirstOrDefault(static found => found is not null);

            Assert.True(type is not null, $"{fullName} is exempted but does not exist.");

            Assert.Contains(
                type!.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                static member => member.GetCustomAttribute<JsonPropertyNameAttribute>() is not null);
        }
    }
}
