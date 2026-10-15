using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The references to the trusted platform assemblies that analyzer fixtures compile against.
/// </summary>
/// <remarks>
/// A reference copies its assembly's metadata into native memory the first time a compilation reads it, and holds it for the life of the
/// reference. A fixture that needs no platform symbols of its own therefore reads <see cref="Shared"/>, the one set of the process, so the
/// test host holds one image of each platform assembly and not one per class. A fixture whose subject is that two compilations do not share
/// their platform symbols (the retargeted case) asks for <see cref="Separate"/>, a set of its own that goes when the fixture lets it go.
/// </remarks>
internal static class TrustedPlatformReferences
{
    private const string Variable = "TRUSTED_PLATFORM_ASSEMBLIES";

    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> Set = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The set every fixture reads unless its subject is a set of its own.</summary>
    internal static ImmutableArray<PortableExecutableReference> Shared => Set.Value;

    /// <summary>A set that shares no reference, and so no platform symbol, with <see cref="Shared"/> or with any other set.</summary>
    internal static ImmutableArray<PortableExecutableReference> Separate() => Create();

    private static ImmutableArray<PortableExecutableReference> Create() =>
        [.. ((string)AppContext.GetData(Variable)!).Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path))];
}
