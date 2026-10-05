using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Options for the ad-hoc shapes a test builds or parses for itself: an anonymous object, a bare
/// <see cref="JsonElement"/>, a type no production context registers.
/// </summary>
/// <remarks>
/// The test project turns <c>JsonSerializerIsReflectionEnabledByDefault</c> off so that a payload the
/// production host forgot to register fails the test that sends it, as it would in the Native AOT binary.
/// That switch applies to every <see cref="JsonSerializer"/> call without a resolver, including a test's own,
/// so a test that really does want reflection says so here, by name, rather than the whole project
/// quietly keeping it. Nothing the host serializes goes through these options, and no production type is
/// registered with them.
/// </remarks>
internal static class AdHocJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// <see cref="AIFunctionFactory"/> options for a test tool whose parameters are not among the shapes the
    /// library registers itself (an array, say): its own context first, reflection after it.
    /// </summary>
    public static JsonSerializerOptions AIFunctionOptions { get; } = new(AIJsonUtilities.DefaultOptions)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AIJsonUtilities.DefaultOptions.TypeInfoResolver!,
            new DefaultJsonTypeInfoResolver()),
    };
}
