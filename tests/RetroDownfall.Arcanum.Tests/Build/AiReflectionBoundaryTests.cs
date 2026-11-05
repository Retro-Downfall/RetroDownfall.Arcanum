using System.Reflection;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class AiReflectionBoundaryTests
{
    public static IEnumerable<object[]> ForbiddenCases => AiReflectionFixtures.Forbidden.Select(static fixture => new object[] { fixture.Id });

    public static IEnumerable<object[]> AdversarialCases => AiReflectionFixtures.Adversarial.Select(static fixture => new object[] { fixture.Id });

    [Theory]
    [MemberData(nameof(ForbiddenCases))]
    public void Every_pinned_reflection_overload_is_rejected_with_actionable_diagnostics(string id)
    {
        AiReflectionFixtureCase fixture = Assert.Single(AiReflectionFixtures.Forbidden, candidate => candidate.Id == id);

        AssertViolation(fixture);
    }

    [Theory]
    [MemberData(nameof(AdversarialCases))]
    public void Compiled_references_survive_source_spelling_and_generated_method_changes(string id)
    {
        AiReflectionFixtureCase fixture = Assert.Single(AiReflectionFixtures.Adversarial, candidate => candidate.Id == id);

        AssertViolation(fixture);
    }

    [Fact]
    public void Literal_inventory_contains_all_fifteen_pinned_forbidden_overloads()
    {
        Assert.Equal(15, AiReflectionFixtures.Forbidden.Count);

        Assert.Equal(15, AiReflectionFixtures.Forbidden.Select(static fixture => fixture.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Pinned_package_metadata_has_exactly_the_reviewed_schema_and_function_overloads()
    {
        Type[] types = [typeof(AIFunctionFactory), typeof(AIJsonUtilities), typeof(ChatResponseFormat), typeof(ChatClientStructuredOutputExtensions)];

        foreach (Assembly assembly in types.Select(static type => type.Assembly).Distinct())
        {
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

            Assert.Equal("10.8.1", version.Split('+')[0]);
        }

        string[] actual = types.SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(static method => method.DeclaringType == typeof(AIFunctionFactory) && method.Name is "Create" or "CreateDeclaration"
                || method.DeclaringType == typeof(AIJsonUtilities) && method.Name is "CreateJsonSchema" or "CreateFunctionJsonSchema" or "TransformSchema"
                || method.DeclaringType == typeof(ChatResponseFormat) && method.Name == "ForJsonSchema"
                || method.DeclaringType == typeof(ChatClientStructuredOutputExtensions) && method.Name == "GetResponseAsync")
            .Select(static method => method.DeclaringType!.Name + "." + method.Name + "`" + method.GetGenericArguments().Length + "(" + string.Join(",", method.GetParameters().Select(static parameter => InventoryTypeName(parameter.ParameterType))) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Keep this literal list independent of the scanner, so a package surface change requires review even when no fixture calls it.
        string[] expected =
        [
            "AIFunctionFactory.Create`0(System.Delegate,Microsoft.Extensions.AI.AIFunctionFactoryOptions)",
            "AIFunctionFactory.Create`0(System.Delegate,System.String,System.String,System.Text.Json.JsonSerializerOptions)",
            "AIFunctionFactory.Create`0(System.Reflection.MethodInfo,System.Object,Microsoft.Extensions.AI.AIFunctionFactoryOptions)",
            "AIFunctionFactory.Create`0(System.Reflection.MethodInfo,System.Object,System.String,System.String,System.Text.Json.JsonSerializerOptions)",
            "AIFunctionFactory.Create`0(System.Reflection.MethodInfo,System.Func`2<Microsoft.Extensions.AI.AIFunctionArguments,System.Object>,Microsoft.Extensions.AI.AIFunctionFactoryOptions)",
            "AIFunctionFactory.CreateDeclaration`0(System.String,System.String,System.Text.Json.JsonElement,System.Nullable`1<System.Text.Json.JsonElement>)",
            "AIJsonUtilities.CreateFunctionJsonSchema`0(System.Reflection.MethodBase,System.String,System.String,System.Text.Json.JsonSerializerOptions,Microsoft.Extensions.AI.AIJsonSchemaCreateOptions)",
            "AIJsonUtilities.CreateJsonSchema`0(System.Type,System.String,System.Boolean,System.Object,System.Text.Json.JsonSerializerOptions,Microsoft.Extensions.AI.AIJsonSchemaCreateOptions)",
            "AIJsonUtilities.TransformSchema`0(System.Text.Json.JsonElement,Microsoft.Extensions.AI.AIJsonSchemaTransformOptions)",
            "ChatResponseFormat.ForJsonSchema`0(System.Text.Json.JsonElement,System.String,System.String)",
            "ChatResponseFormat.ForJsonSchema`0(System.Type,System.Text.Json.JsonSerializerOptions,System.String,System.String)",
            "ChatResponseFormat.ForJsonSchema`1(System.Text.Json.JsonSerializerOptions,System.String,System.String)",
            "ChatClientStructuredOutputExtensions.GetResponseAsync`1(Microsoft.Extensions.AI.IChatClient,System.Collections.Generic.IEnumerable`1<Microsoft.Extensions.AI.ChatMessage>,Microsoft.Extensions.AI.ChatOptions,System.Nullable`1<System.Boolean>,System.Threading.CancellationToken)",
            "ChatClientStructuredOutputExtensions.GetResponseAsync`1(Microsoft.Extensions.AI.IChatClient,System.String,Microsoft.Extensions.AI.ChatOptions,System.Nullable`1<System.Boolean>,System.Threading.CancellationToken)",
            "ChatClientStructuredOutputExtensions.GetResponseAsync`1(Microsoft.Extensions.AI.IChatClient,Microsoft.Extensions.AI.ChatMessage,Microsoft.Extensions.AI.ChatOptions,System.Nullable`1<System.Boolean>,System.Threading.CancellationToken)",
            "ChatClientStructuredOutputExtensions.GetResponseAsync`1(Microsoft.Extensions.AI.IChatClient,System.String,System.Text.Json.JsonSerializerOptions,Microsoft.Extensions.AI.ChatOptions,System.Nullable`1<System.Boolean>,System.Threading.CancellationToken)",
            "ChatClientStructuredOutputExtensions.GetResponseAsync`1(Microsoft.Extensions.AI.IChatClient,Microsoft.Extensions.AI.ChatMessage,System.Text.Json.JsonSerializerOptions,Microsoft.Extensions.AI.ChatOptions,System.Nullable`1<System.Boolean>,System.Threading.CancellationToken)",
            "ChatClientStructuredOutputExtensions.GetResponseAsync`1(Microsoft.Extensions.AI.IChatClient,System.Collections.Generic.IEnumerable`1<Microsoft.Extensions.AI.ChatMessage>,System.Text.Json.JsonSerializerOptions,Microsoft.Extensions.AI.ChatOptions,System.Nullable`1<System.Boolean>,System.Threading.CancellationToken)",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void Explicit_schemas_and_generated_json_type_info_remain_allowed()
    {
        byte[] image = AiReflectionFixtures.Compile("safe", """
            public static object Probe(JsonElement schema)
            {
                _ = ChatResponseFormat.ForJsonSchema(schema, "response", "explicit");
                _ = AIJsonUtilities.TransformSchema(schema, new AIJsonSchemaTransformOptions());
                _ = AIFunctionFactory.CreateDeclaration("tool", "explicit", schema, schema);
                return JsonSerializer.Serialize("generated", RetroDownfall.Arcanum.Core.Serialization.ArcanumCoreJsonContext.Default.String);
            }
            """);

        Assert.Empty(AiReflectionBoundaryScanner.FindForbiddenReferences(image));
    }

    [Fact]
    public void Strings_comments_and_unrelated_method_names_are_not_references()
    {
        byte[] image = AiReflectionFixtures.Compile("text", """
            // Microsoft.Extensions.AI.AIFunctionFactory.Create and ChatResponseFormat.ForJsonSchema<string>()
            public static string Probe() => "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync<T>";
            public static object ForJsonSchema<T>() => new object();
            public static object Other() => ForJsonSchema<string>();
            """);

        Assert.Empty(AiReflectionBoundaryScanner.FindForbiddenReferences(image));
    }

    [Fact]
    public void Same_type_and_method_names_in_an_unrelated_assembly_are_allowed()
    {
        Assert.Empty(AiReflectionBoundaryScanner.FindForbiddenReferences(AiReflectionFixtures.CompileLookalike()));
    }

    private static void AssertViolation(AiReflectionFixtureCase fixture)
    {
        byte[] image = AiReflectionFixtures.Compile(fixture.Id, fixture.Body);

        AiReflectionViolation violation = Assert.Single(AiReflectionBoundaryScanner.FindForbiddenReferences(image));

        Assert.Equal("AiReflectionFixture_" + fixture.Id, violation.Assembly);

        Assert.Contains("Fixture", violation.Caller, StringComparison.Ordinal);

        Assert.Contains(fixture.Id == "wrapper" ? "Wrapper" : "Probe", violation.Caller, StringComparison.Ordinal);

        Assert.Contains(fixture.Callee, violation.Callee, StringComparison.Ordinal);

        foreach (string parameterType in fixture.ParameterTypes)
        {
            Assert.Contains(parameterType, violation.Callee, StringComparison.Ordinal);
        }

        Assert.True(violation.Offset >= 0);

        string diagnostic = violation.ToString();

        Assert.Contains(violation.Assembly, diagnostic, StringComparison.Ordinal);

        Assert.Contains(violation.Caller, diagnostic, StringComparison.Ordinal);

        Assert.Contains(violation.Callee, diagnostic, StringComparison.Ordinal);

        Assert.Contains("IL_" + violation.Offset.ToString("X4"), diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static string InventoryTypeName(Type type) => type.IsGenericType
        ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(InventoryTypeName)) + ">"
        : type.FullName ?? type.Name;
}
