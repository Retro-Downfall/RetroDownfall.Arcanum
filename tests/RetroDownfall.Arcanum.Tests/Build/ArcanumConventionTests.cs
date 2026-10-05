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
    /// Types registered on the context to <em>parse a model's output</em>, whose member names are the JSON
    /// contract the prompt asks the model to follow, not an <c>/api</c> wire shape.
    /// </summary>
    private static readonly HashSet<string> ModelOutputContractTypes =
    [
        "SemanticSpellResponse",
        "LexiconEntityExtractionResponse",
    ];

    /// <summary>
    /// An <c>/api</c> payload is named by the camelCase policy on <see cref="ArcanumJsonContext"/>; a member
    /// that restates its own name with <c>[JsonPropertyName]</c> is a second source of truth that drifts
    /// (AGENTS.md rule 4). The attribute stays only on the external wires named above.
    /// </summary>
    [Fact]
    public void ApiWireTypes_DoNotUseJsonPropertyName()
    {
        Assembly api = typeof(ArcanumJsonContext).Assembly;

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

            if (type.Assembly != api || IsExternalWire(type))
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

    private static bool IsExternalWire(Type type) =>
        ModelOutputContractTypes.Contains(type.Name)
        || ExternalWireNamespaceMarkers.Any(marker =>
            (type.Namespace ?? string.Empty).Contains(marker, StringComparison.Ordinal));
}
