using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed record AiReflectionFixtureCase(string Id, string Body, string Callee, string[] ParameterTypes);

internal static class AiReflectionFixtures
{
    private static readonly ConcurrentDictionary<string, byte[]> Images = new(StringComparer.Ordinal);

    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> References = new(() =>
        [.. TrustedPlatformReferences.Shared.Concat(new[]
        {
            typeof(AIFunctionFactory).Assembly.Location,
            typeof(ChatClientStructuredOutputExtensions).Assembly.Location,
            typeof(ArcanumCoreJsonContext).Assembly.Location,
        }.Select(static path => MetadataReference.CreateFromFile(path))).DistinctBy(static reference => reference.FilePath)]);

    // This is a literal inventory of the pinned API, independent of the scanner's matching policy.
    internal static IReadOnlyList<AiReflectionFixtureCase> Forbidden { get; } =
    [
        new("factory_delegate_options", "public static object Probe() => AIFunctionFactory.Create((Action)(() => { }), new AIFunctionFactoryOptions());", "Microsoft.Extensions.AI.AIFunctionFactory.Create", ["System.Delegate", "Microsoft.Extensions.AI.AIFunctionFactoryOptions"]),
        new("factory_delegate_names", "public static object Probe() => AIFunctionFactory.Create((Action)(() => { }), null, null, null);", "Microsoft.Extensions.AI.AIFunctionFactory.Create", ["System.Delegate", "System.String", "System.Text.Json.JsonSerializerOptions"]),
        new("factory_method_options", "public static object Probe() => AIFunctionFactory.Create((MethodInfo)null!, (object)null!, new AIFunctionFactoryOptions());", "Microsoft.Extensions.AI.AIFunctionFactory.Create", ["System.Reflection.MethodInfo", "System.Object", "Microsoft.Extensions.AI.AIFunctionFactoryOptions"]),
        new("factory_method_names", "public static object Probe() => AIFunctionFactory.Create((MethodInfo)null!, (object)null!, null, null, null);", "Microsoft.Extensions.AI.AIFunctionFactory.Create", ["System.Reflection.MethodInfo", "System.Object", "System.String", "System.Text.Json.JsonSerializerOptions"]),
        new("factory_method_instance", "public static object Probe() => AIFunctionFactory.Create((MethodInfo)null!, (Func<AIFunctionArguments, object>)(_ => new object()), null);", "Microsoft.Extensions.AI.AIFunctionFactory.Create", ["System.Reflection.MethodInfo", "System.Func", "Microsoft.Extensions.AI.AIFunctionArguments", "System.Object", "Microsoft.Extensions.AI.AIFunctionFactoryOptions"]),
        new("function_schema", "public static object Probe() => AIJsonUtilities.CreateFunctionJsonSchema((MethodBase)null!, null, null, null, null);", "Microsoft.Extensions.AI.AIJsonUtilities.CreateFunctionJsonSchema", ["System.Reflection.MethodBase", "System.String", "System.Text.Json.JsonSerializerOptions", "Microsoft.Extensions.AI.AIJsonSchemaCreateOptions"]),
        new("type_schema", "public static object Probe() => AIJsonUtilities.CreateJsonSchema(typeof(string), null, false, null, null, null);", "Microsoft.Extensions.AI.AIJsonUtilities.CreateJsonSchema", ["System.Type", "System.String", "System.Boolean", "System.Object", "System.Text.Json.JsonSerializerOptions", "Microsoft.Extensions.AI.AIJsonSchemaCreateOptions"]),
        new("response_generic_schema", "public static object Probe() => ChatResponseFormat.ForJsonSchema<string>(null, null, null);", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions", "System.String"]),
        new("response_type_schema", "public static object Probe() => ChatResponseFormat.ForJsonSchema(typeof(string), null, null, null);", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Type", "System.Text.Json.JsonSerializerOptions", "System.String"]),
        new("structured_messages_default", "public static object Probe(IChatClient client) => client.GetResponseAsync<string>(Array.Empty<ChatMessage>(), (ChatOptions)null!, null, default);", "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync", ["Microsoft.Extensions.AI.IChatClient", "System.Collections.Generic.IEnumerable", "Microsoft.Extensions.AI.ChatMessage", "Microsoft.Extensions.AI.ChatOptions", "System.Nullable", "System.Boolean", "System.Threading.CancellationToken"]),
        new("structured_string_default", "public static object Probe(IChatClient client) => client.GetResponseAsync<string>(\"hello\", (ChatOptions)null!, null, default);", "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync", ["Microsoft.Extensions.AI.IChatClient", "System.String", "Microsoft.Extensions.AI.ChatOptions", "System.Nullable", "System.Boolean", "System.Threading.CancellationToken"]),
        new("structured_message_default", "public static object Probe(IChatClient client) => client.GetResponseAsync<string>(new ChatMessage(ChatRole.User, \"hello\"), (ChatOptions)null!, null, default);", "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync", ["Microsoft.Extensions.AI.IChatClient", "Microsoft.Extensions.AI.ChatMessage", "Microsoft.Extensions.AI.ChatOptions", "System.Nullable", "System.Boolean", "System.Threading.CancellationToken"]),
        new("structured_string_options", "public static object Probe(IChatClient client) => client.GetResponseAsync<string>(\"hello\", new JsonSerializerOptions(), null, null, default);", "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync", ["Microsoft.Extensions.AI.IChatClient", "System.String", "System.Text.Json.JsonSerializerOptions", "Microsoft.Extensions.AI.ChatOptions", "System.Nullable", "System.Boolean", "System.Threading.CancellationToken"]),
        new("structured_message_options", "public static object Probe(IChatClient client) => client.GetResponseAsync<string>(new ChatMessage(ChatRole.User, \"hello\"), new JsonSerializerOptions(), null, null, default);", "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync", ["Microsoft.Extensions.AI.IChatClient", "Microsoft.Extensions.AI.ChatMessage", "System.Text.Json.JsonSerializerOptions", "Microsoft.Extensions.AI.ChatOptions", "System.Nullable", "System.Boolean", "System.Threading.CancellationToken"]),
        new("structured_messages_options", "public static object Probe(IChatClient client) => client.GetResponseAsync<string>(Array.Empty<ChatMessage>(), new JsonSerializerOptions(), null, null, default);", "Microsoft.Extensions.AI.ChatClientStructuredOutputExtensions.GetResponseAsync", ["Microsoft.Extensions.AI.IChatClient", "System.Collections.Generic.IEnumerable", "Microsoft.Extensions.AI.ChatMessage", "System.Text.Json.JsonSerializerOptions", "Microsoft.Extensions.AI.ChatOptions", "System.Nullable", "System.Boolean", "System.Threading.CancellationToken"]),
    ];

    internal static IReadOnlyList<AiReflectionFixtureCase> Adversarial { get; } =
    [
        new("alias", "public static object Probe() => FactoryAlias.Create((Action)(() => { }));", "Microsoft.Extensions.AI.AIFunctionFactory.Create", ["System.Delegate"]),
        new("using_static", "public static object Probe() => ForJsonSchema<string>();", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("wrapper", "public static object Probe() => Wrapper(); private static object Wrapper() => ChatResponseFormat.ForJsonSchema<string>();", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("open_generic_methodspec", "public static object Probe<T>() => ChatResponseFormat.ForJsonSchema<T>();", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("method_group_ldftn", "public static object Probe() { Func<JsonSerializerOptions, string, string, ChatResponseFormatJson> factory = ChatResponseFormat.ForJsonSchema<string>; return factory; }", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("async", "public static async Task<object> Probe() { await Task.Yield(); return ChatResponseFormat.ForJsonSchema<string>(); }", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("iterator", "public static IEnumerable<object> Probe() { yield return ChatResponseFormat.ForJsonSchema<string>(); }", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("lambda", "public static Func<object> Probe() => () => ChatResponseFormat.ForJsonSchema<string>();", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("local_function", "public static object Probe() { object Local() => ChatResponseFormat.ForJsonSchema<string>(); return Local(); }", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
        new("switch_and_long_operands", "public static object Probe(int value) { long number = 0x1122334455667788; double fraction = 123.456; switch (value) { case 0: number++; break; case 1: number += 2; break; case 2: number += 3; break; case 3: number += 4; break; case 4: number += 5; break; default: number += 6; break; } Console.WriteLine(number + fraction); return ChatResponseFormat.ForJsonSchema<string>(); }", "Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", ["System.Text.Json.JsonSerializerOptions"]),
    ];

    internal static byte[] Compile(string id, string body) => Images.GetOrAdd(id, _ => Emit(
        "AiReflectionFixture_" + id,
        """
        using System;
        using System.Collections.Generic;
        using System.Reflection;
        using System.Text.Json;
        using System.Threading.Tasks;
        using Microsoft.Extensions.AI;
        using FactoryAlias = Microsoft.Extensions.AI.AIFunctionFactory;
        using static Microsoft.Extensions.AI.ChatResponseFormat;
        public static class Fixture
        {
        """ + body + "\n}", References.Value));

    internal static byte[] CompileLookalike()
    {
        byte[] dependency = Emit("Unrelated.AI", """
            namespace Microsoft.Extensions.AI;
            public static class AIFunctionFactory
            {
                public static object Create(System.Delegate method, string name = null, string description = null, System.Text.Json.JsonSerializerOptions serializerOptions = null) => new object();
            }
            public static class ChatResponseFormat
            {
                public static object ForJsonSchema<T>(System.Text.Json.JsonSerializerOptions serializerOptions = null, string schemaName = null, string schemaDescription = null) => new object();
            }
            """, TrustedPlatformReferences.Shared);

        return Emit("AiReflectionFixture_lookalike", """
            public static class Fixture
            {
                public static object Probe() => Microsoft.Extensions.AI.AIFunctionFactory.Create((System.Action)(() => { }));
                public static object Other() => Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<string>();
            }
            """, [.. TrustedPlatformReferences.Shared.Where(static reference => !Path.GetFileName(reference.FilePath ?? string.Empty).StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal)), MetadataReference.CreateFromImage(dependency)]);
    }

    private static byte[] Emit(string assemblyName, string source, ImmutableArray<PortableExecutableReference> references)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using MemoryStream stream = new();

        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);

        Assert.True(result.Success, "Fixture " + assemblyName + " must compile against the pinned API:\n" + string.Join("\n", result.Diagnostics));

        return stream.ToArray();
    }
}
