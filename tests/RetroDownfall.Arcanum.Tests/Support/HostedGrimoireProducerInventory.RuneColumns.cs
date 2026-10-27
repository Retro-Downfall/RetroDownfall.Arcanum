using Microsoft.CodeAnalysis;

using System.Collections.Immutable;

using System.Runtime.CompilerServices;

using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    // These are the complete implementation images whose transitive numeric
    // GetColumns/GetWidth bodies and initializers were audited. An unsigned
    // name/version, displayed path or module identifier cannot bind that proof.
    private static readonly ConditionalWeakTable<MetadataReference, string> reviewedRuneImages = new();

    internal static MetadataReference CreateProductionMetadataReference(string file)
    {
        string? expected = Path.GetFileName(file) switch
        {
            "Terminal.Gui.dll" => "77AD96C1E5B8E11A0B63A7C459FF8933E283BCE27B5F2E6B2349A3F110C26A3D",
            "Wcwidth.dll" => "D7B4366F0024CA0A5516D6D83417487DD421E4DF35006BCA6982FCA8E20F9B8B",
            _ => null,
        };

        if (expected is null)
        {
            return MetadataReference.CreateFromFile(file);
        }

        ImmutableArray<byte> image = ImmutableArray.CreateRange(File.ReadAllBytes(file));

        PortableExecutableReference reference = MetadataReference.CreateFromImage(image, filePath: file);

        if (Convert.ToHexString(SHA256.HashData(image.AsSpan())) == expected)
        {
            reviewedRuneImages.Add(reference, Path.GetFileNameWithoutExtension(file));
        }

        // The compiler and the proof own the same immutable byte snapshot. A
        // different package image still compiles, but does not inherit the leaf.
        return reference;
    }

    private sealed partial class ProducerGraph
    {
        private readonly Dictionary<IAssemblySymbol, bool> reviewedRuneImageClosures = new(ReferenceEqualityComparer.Instance);

        // Pinned Terminal.Gui delegates this exact Rune overload to Wcwidth's
        // numeric tables under a lock. No supplied formatter, callback or I/O runs.
        // Argument and authored conversion traversal remain independent.
        private bool IsReviewedRuneColumnCalculation(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            return definition is { IsStatic: true, MethodKind: MethodKind.Ordinary, Arity: 0, Name: "GetColumns", ReturnType.SpecialType: SpecialType.System_Int32, Parameters: [{ RefKind: RefKind.None } rune] }
                && IsExactFrameworkType(definition.ContainingType,
                    "Terminal.Gui.Text.RuneExtensions", typeof(Terminal.Gui.Text.RuneExtensions).Assembly.GetName())
                && IsExactFrameworkType(rune.Type, "System.Text.Rune", typeof(System.Text.Rune).Assembly.GetName())
                && HasReviewedRuneImageClosure(definition.ContainingAssembly);
        }

        private bool HasReviewedRuneImageClosure(IAssemblySymbol assembly)
        {
            if (reviewedRuneImageClosures.TryGetValue(assembly, out bool reviewed))
            {
                return reviewed;
            }

            reviewed = analysedCompilations.Any(compilation =>
                HasReviewedRuneImage(compilation, assembly, "Terminal.Gui")
                && assembly.Modules.SelectMany(static module => module.ReferencedAssemblySymbols)
                    .Any(dependency => dependency.Name == "Wcwidth"
                        && HasReviewedRuneImage(compilation, dependency, "Wcwidth")));

            reviewedRuneImageClosures.Add(assembly, reviewed);

            return reviewed;
        }

        private static bool HasReviewedRuneImage(Compilation compilation, IAssemblySymbol assembly, string expectedName) =>
            compilation.References.Any(reference =>
                ReferenceEquals(compilation.GetAssemblyOrModuleSymbol(reference), assembly)
                && reviewedRuneImages.TryGetValue(reference, out string? reviewedName)
                && reviewedName == expectedName);
    }
}
