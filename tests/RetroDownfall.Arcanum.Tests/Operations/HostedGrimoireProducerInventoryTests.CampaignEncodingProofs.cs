using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("encoder-loop", "System.Text.Encoder.Convert")]
    [InlineData("guid-sort", "System.Array.Sort")]
    [InlineData("occurrence-sort", "System.Array.Sort")]
    public void CampaignEncoding_production_reference_shapes_preserve_owned_receivers_and_comparers(string shape, string callee)
    {
        string extra = shape switch
        {
            "encoder-loop" => """
                sealed class EncodingOwner
                {
                    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
                    public void WriteUtf8(string value)
                    {
                        int byteCount = StrictUtf8.GetByteCount(value);
                        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(System.Math.Min(byteCount, 4096));
                        try
                        {
                            System.Text.Encoder encoder = StrictUtf8.GetEncoder();
                            System.ReadOnlySpan<char> remaining = value.AsSpan();
                            while (!remaining.IsEmpty)
                            {
                                encoder.Convert(remaining, rented, true, out int charsUsed, out int bytesUsed, out _);
                                _ = rented.AsSpan(0, bytesUsed);
                                remaining = remaining[charsUsed..];
                            }
                        }
                        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
                    }
                }
                """,
            "guid-sort" => """
                static class SortOwner
                {
                    public static void Run()
                    {
                        System.Guid[] exact = [];
                        exact = new System.Guid[] { System.Guid.Empty };
                        System.Array.Sort(exact, RawGuidComparer.Instance);
                        _ = RawGuidComparer.Instance.Compare(exact[0], exact[0]);
                    }
                    private sealed class RawGuidComparer : System.Collections.Generic.IComparer<System.Guid>
                    {
                        public static RawGuidComparer Instance { get; } = new();
                        public int Compare(System.Guid left, System.Guid right)
                        {
                            System.Span<byte> leftBytes = stackalloc byte[16];
                            System.Span<byte> rightBytes = stackalloc byte[16];
                            left.TryWriteBytes(leftBytes, bigEndian: true, out _);
                            right.TryWriteBytes(rightBytes, bigEndian: true, out _);
                            return leftBytes.SequenceCompareTo(rightBytes);
                        }
                    }
                }
                """,
            _ => """
                static class SortOwner
                {
                    public static void Run()
                    {
                        MaterializationOccurrenceDigestInput[] occurrences = [new(Container.Message, 0, 0, Occurrence.Text, 0, 1)];
                        System.Array.Sort(occurrences, MaterializationOccurrenceComparer.Instance);
                    }
                    private enum Container : byte { Message = 1 }
                    private enum Occurrence : byte { Text = 1 }
                    private sealed record MaterializationOccurrenceDigestInput(Container Container, uint? MessageIndex,
                        uint? ContentPartIndex, Occurrence Occurrence, uint? Utf16Start, uint Length);
                    private sealed class MaterializationOccurrenceComparer : System.Collections.Generic.IComparer<MaterializationOccurrenceDigestInput>
                    {
                        public static MaterializationOccurrenceComparer Instance { get; } = new();
                        public int Compare(MaterializationOccurrenceDigestInput? left, MaterializationOccurrenceDigestInput? right)
                        {
                            int result = Code(left!.Container, nameof(left.Container)).CompareTo(Code(right!.Container, nameof(right.Container)));
                            if (result == 0) result = CompareNullable(left.MessageIndex, right.MessageIndex);
                            if (result == 0) result = CompareNullable(left.ContentPartIndex, right.ContentPartIndex);
                            if (result == 0) result = CompareNullable(left.Utf16Start, right.Utf16Start);
                            return result != 0 ? result : Code(left.Occurrence, nameof(left.Occurrence)).CompareTo(Code(right.Occurrence, nameof(right.Occurrence)));
                        }
                    }
                    private static int CompareNullable(uint? left, uint? right) =>
                        left.HasValue == right.HasValue
                            ? left.GetValueOrDefault().CompareTo(right.GetValueOrDefault())
                            : left.HasValue ? 1 : -1;
                    private static uint Code<TEnum>(TEnum value, string parameterName) where TEnum : struct, System.Enum
                    {
                        if (System.Runtime.CompilerServices.Unsafe.SizeOf<TEnum>() != sizeof(byte)
                            || !(typeof(TEnum) == typeof(Container) || typeof(TEnum) == typeof(Occurrence)))
                        {
                            throw new System.ArgumentException("Unsupported enum", parameterName);
                        }
                        return System.Runtime.CompilerServices.Unsafe.As<TEnum, byte>(ref value);
                    }
                }
                """,
        };

        string body = shape == "encoder-loop" ? "new EncodingOwner().WriteUtf8(\"text\");" : "SortOwner.Run();";

        var compilation = CompileWithProductionReferencePack(FixtureSource(body, extra), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        foreach (InvocationExpressionSyntax call in compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            SemanticModel model = compilation.GetSemanticModel(call.SyntaxTree);

            if (model.GetSymbolInfo(call).Symbol is IMethodSymbol method && method.Name is "Convert" or "GetEncoder" or "Sort" or "SequenceCompareTo")
            {
                output.WriteLine("{0}: {1}; assembly={2}", shape, method.ToDisplayString(), method.ContainingAssembly.Identity);
            }
        }

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [], []);

        Assert.False(HasCampaignEncodingDiagnostic(discovery, callee), string.Join(System.Environment.NewLine, discovery.Diagnostics));
    }

    [Fact]
    public void CampaignEncoding_guid_comparer_preserves_ownership_across_production_compilation_references()
    {
        string source = FixtureSource("""
            _ = RetroDownfall.Arcanum.Core.Covenant.CovenantDigests.Sensitivity(
                new RetroDownfall.Arcanum.Core.Covenant.SensitivityDigestInput(
                    RetroDownfall.Arcanum.Core.Covenant.ContentSensitivity.CovenantDerived,
                    RetroDownfall.Arcanum.Core.Covenant.GenerationProvenanceMode.Exact,
                    [new System.Guid("11111111-1111-1111-1111-111111111111")], default));
            """);

        HostedProducerProductionOverlay overlay = HostedGrimoireProducerInventory.GetProductionOverlay(
            source, HostedProducerProductionOverlayKind.EncodingCore);

        var compilation = overlay.Fixture;

        var core = overlay.Production.Single(static candidate => candidate.AssemblyName == "RetroDownfall.Arcanum.Core");

        Assert.Same(overlay, HostedGrimoireProducerInventory.GetProductionOverlay(
            source, HostedProducerProductionOverlayKind.EncodingCore));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains(compilation.References.OfType<CompilationReference>(), reference =>
            ReferenceEquals(reference.Compilation, core));

        HostedProducerDiscovery<HostedProducerSite> discovery = overlay.Discovery;

        Assert.False(HasCampaignEncodingDiagnostic(discovery, "System.Array.Sort"), string.Join(System.Environment.NewLine, discovery.Diagnostics));
    }

    [Fact]
    public void CampaignEncoding_type_equality_keeps_caller_type_callbacks_unclassified()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new int[] { 2, 1 }, ExactComparer.Instance);
                private sealed class ExactComparer : System.Collections.Generic.IComparer<int>
                {
                    public static ExactComparer Instance { get; } = new();
                    public int Compare(int left, int right) =>
                        TypeSource.Left == TypeSource.Right ? left.CompareTo(right) : right.CompareTo(left);
                }
            }
            static class TypeSource
            {
                public static System.Type Left { get; } = new EffectfulType();
                public static System.Type Right { get; } = new EffectfulType();
            }
            sealed class EffectfulType() : System.Reflection.TypeDelegator(typeof(int))
            {
                public override bool Equals(System.Type? other)
                {
                    System.IO.File.WriteAllText("proof.txt", "effect");
                    return false;
                }
                public override bool Equals(object? other) => false;
                public override int GetHashCode() => 0;
            }
            """);

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [], []);

        Assert.True(HasCampaignEncodingDiagnostic(discovery, "System.Array.Sort"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CampaignEncoding_encoder_requires_untouched_owned_strict_utf8_factory(bool replaceFallback)
    {
        string mutation = replaceFallback
            ? "encoder.Fallback = new EffectfulFallback();"
            : string.Empty;
        string source = FixtureSource("EncodingOwner.Run();", $$"""
            static class EncodingOwner
            {
                private static readonly System.Text.UTF8Encoding Strict = new(false, true);
                public static void Run()
                {
                    System.Text.Encoder encoder = Strict.GetEncoder();
                    {{mutation}}
                    encoder.Convert("text".AsSpan(), new byte[32], true, out _, out _, out _);
                }
            }
            sealed class EffectfulFallback : System.Text.EncoderFallback
            {
                public override int MaxCharCount => 1;
                public override System.Text.EncoderFallbackBuffer CreateFallbackBuffer()
                {
                    System.IO.File.WriteAllText("proof.txt", "effect");
                    throw new System.NotSupportedException();
                }
            }
            """);

        Assert.Equal(replaceFallback, HasCampaignEncodingDiagnostic(Discover(source), "System.Text.Encoder.Convert"));
    }

    [Theory]
    [InlineData("encoder = Other();")]
    [InlineData("System.Text.Encoder alias = encoder; alias.Fallback = new EffectfulFallback();")]
    [InlineData("Escape(encoder);")]
    public void CampaignEncoding_encoder_rejects_reassignment_alias_mutation_and_escape(string mutation)
    {
        string source = FixtureSource("EncodingOwner.Run();", $$"""
            static class EncodingOwner
            {
                private static readonly System.Text.UTF8Encoding Strict = new(false, true);
                public static void Run()
                {
                    System.Text.Encoder encoder = Strict.GetEncoder();
                    {{mutation}}
                    encoder.Convert("text".AsSpan(), new byte[32], true, out _, out _, out _);
                }
                static System.Text.Encoder Other() => null!;
                static void Escape(System.Text.Encoder encoder) { }
            }
            sealed class EffectfulFallback : System.Text.EncoderFallback
            {
                public override int MaxCharCount => 1;
                public override System.Text.EncoderFallbackBuffer CreateFallbackBuffer() => throw new System.NotSupportedException();
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Text.Encoder.Convert"));
    }

    [Fact]
    public void CampaignEncoding_encoder_rejects_custom_utf8_subclass()
    {
        string source = FixtureSource("EncodingOwner.Run();", """
            static class EncodingOwner
            {
                private static readonly CustomUtf8 Strict = new();
                public static void Run()
                {
                    var encoder = Strict.GetEncoder();
                    encoder.Convert("text".AsSpan(), new byte[32], true, out _, out _, out _);
                }
            }
            sealed class CustomUtf8 : System.Text.UTF8Encoding
            {
                public override System.Text.Encoder GetEncoder() => null!;
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Text.Encoder.Convert"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CampaignEncoding_sort_resolves_exact_comparer_callback_effects(bool effectful)
    {
        string effect = effectful ? "System.IO.File.WriteAllText(\"proof.txt\", \"effect\");" : string.Empty;
        string source = FixtureSource("SortOwner.Run();", $$"""
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new int[] { 2, 1 }, ExactComparer.Instance);
                private sealed class ExactComparer : System.Collections.Generic.IComparer<int>
                {
                    public static ExactComparer Instance { get; } = new();
                    public int Compare(int left, int right)
                    {
                        {{effect}}
                        return left.CompareTo(right);
                    }
                }
            }
            """);

        Assert.Equal(effectful, HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_accepts_owned_guid_byte_order_comparer()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new System.Guid[] { System.Guid.Empty }, ExactComparer.Instance);
                private sealed class ExactComparer : System.Collections.Generic.IComparer<System.Guid>
                {
                    public static ExactComparer Instance { get; } = new();
                    public int Compare(System.Guid left, System.Guid right)
                    {
                        System.Span<byte> leftBytes = stackalloc byte[16];
                        System.Span<byte> rightBytes = stackalloc byte[16];
                        left.TryWriteBytes(leftBytes, bigEndian: true, out _);
                        right.TryWriteBytes(rightBytes, bigEndian: true, out _);
                        return leftBytes.SequenceCompareTo(rightBytes);
                    }
                }
            }
            """);

        Assert.False(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_accepts_owned_sealed_scalar_record_comparer()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new Occurrence[] { new(1, 2, null) }, ExactComparer.Instance);
                private sealed record Occurrence(byte Kind, uint? Index, uint? Start);
                private sealed class ExactComparer : System.Collections.Generic.IComparer<Occurrence>
                {
                    public static ExactComparer Instance { get; } = new();
                    public int Compare(Occurrence? left, Occurrence? right)
                    {
                        int result = left!.Kind.CompareTo(right!.Kind);
                        if (result == 0) result = CompareNullable(left.Index, right.Index);
                        return result != 0 ? result : CompareNullable(left.Start, right.Start);
                    }
                    private static int CompareNullable(uint? left, uint? right) =>
                        left.HasValue == right.HasValue
                            ? left.GetValueOrDefault().CompareTo(right.GetValueOrDefault())
                            : left.HasValue ? 1 : -1;
                }
            }
            """);

        Assert.False(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_rejects_opaque_authored_virtual_helper_dispatch()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new int[] { 2, 1 }, new ExactComparer(CampaignInput.UnknownHelper()));
                private sealed class ExactComparer(Helper helper) : System.Collections.Generic.IComparer<int>
                {
                    private readonly Helper _helper = helper;
                    public int Compare(int left, int right) => _helper.Read(left, right);
                }
                private class Helper
                {
                    public virtual int Read(int left, int right) => left.CompareTo(right);
                }
                private sealed class EffectfulHelper : Helper
                {
                    public override int Read(int left, int right)
                    {
                        System.IO.File.WriteAllText("proof.txt", "virtual helper effect");
                        return left.CompareTo(right);
                    }
                }
                private static class CampaignInput
                {
                    public static Helper UnknownHelper() => new EffectfulHelper();
                }
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_rejects_authored_conversion_to_effectful_comparer()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new int[] { 2, 1 },
                    (System.Collections.Generic.IComparer<int>)(ForeignComparer)new ExactComparer());
                private sealed class ExactComparer : System.Collections.Generic.IComparer<int>
                {
                    public int Compare(int left, int right) => left.CompareTo(right);
                }
                private sealed class ForeignComparer : System.Collections.Generic.IComparer<int>
                {
                    public static explicit operator ForeignComparer(ExactComparer value) => new();
                    public int Compare(int left, int right)
                    {
                        System.IO.File.WriteAllText("proof.txt", "converted comparer effect");
                        return left.CompareTo(right);
                    }
                }
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_rejects_authored_comparer_property_effect()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run() => System.Array.Sort(new int[] { 2, 1 }, ExactComparer.Instance);
                private sealed class ExactComparer : System.Collections.Generic.IComparer<int>
                {
                    public static ExactComparer Instance { get; } = new();
                    public int Compare(int left, int right) => Probe + left.CompareTo(right);
                    private int Probe
                    {
                        get
                        {
                            System.IO.File.WriteAllText("proof.txt", "comparer getter effect");
                            return 0;
                        }
                    }
                }
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_rejects_null_before_later_owned_comparer_assignment()
    {
        string source = FixtureSource("SortOwner.Run();", """
            static class SortOwner
            {
                public static void Run()
                {
                    System.Collections.Generic.IComparer<Occurrence>? comparer = null;
                    System.Array.Sort(new Occurrence[] { new(1), new(2) }, comparer);
                    comparer = ExactComparer.Instance;
                }
                private sealed record Occurrence(byte Kind) : System.IComparable<Occurrence>
                {
                    public int CompareTo(Occurrence? other)
                    {
                        System.IO.File.WriteAllText("proof.txt", "default comparer effect");
                        return Kind.CompareTo(other!.Kind);
                    }
                }
                private sealed class ExactComparer : System.Collections.Generic.IComparer<Occurrence>
                {
                    public static ExactComparer Instance { get; } = new();
                    public int Compare(Occurrence? left, Occurrence? right) => left!.Kind.CompareTo(right!.Kind);
                }
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Fact]
    public void CampaignEncoding_sort_rejects_opaque_comparer_even_for_primitive_array()
    {
        string source = FixtureSource("SortOwner.Run(null!);", """
            static class SortOwner
            {
                public static void Run(System.Collections.Generic.IComparer<int> comparer) =>
                    System.Array.Sort(new int[] { 2, 1 }, comparer);
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Array.Sort"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CampaignEncoding_reader_requires_span_constructor_even_after_parsevalue(bool parseValue)
    {
        string parse = parseValue ? "using var document = System.Text.Json.JsonDocument.ParseValue(ref reader);" : string.Empty;
        string source = FixtureSource($$"""
            byte[] bytes = new byte[] { 49 };
            var reader = new System.Text.Json.Utf8JsonReader(bytes.AsSpan(), new System.Text.Json.JsonReaderOptions());
            {{parse}}
            _ = reader.Read();
            """);

        Assert.False(HasCampaignEncodingDiagnostic(Discover(source), "System.Text.Json.Utf8JsonReader.Read"));
    }

    [Fact]
    public void CampaignEncoding_reader_rejects_sequence_storage_and_opaque_ref_mutation()
    {
        string source = FixtureSource("ReaderOwner.Run();", """
            static class ReaderOwner
            {
                public static void Run()
                {
                    var reader = new System.Text.Json.Utf8JsonReader(new System.Buffers.ReadOnlySequence<byte>(new byte[] { 49 }));
                    _ = reader.Read();
                    var other = new System.Text.Json.Utf8JsonReader(new byte[] { 49 });
                    Replace(ref other);
                    _ = other.Read();
                }
                static void Replace(ref System.Text.Json.Utf8JsonReader reader) { }
            }
            """);

        Assert.Equal(2, Discover(source).Diagnostics.Count(item => item.Code == "HOSTED_SITE_UNCLASSIFIED"
            && item.Detail == "System.Text.Json.Utf8JsonReader.Read"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CampaignEncoding_double_parse_requires_exact_invariant_provider(bool customProvider)
    {
        string provider = customProvider ? "new CustomProvider()" : "System.Globalization.CultureInfo.InvariantCulture";
        string source = FixtureSource($$"""
            _ = double.TryParse("1.25", System.Globalization.NumberStyles.Float, {{provider}}, out _);
            """, """
            sealed class CustomProvider : System.IFormatProvider
            {
                public object? GetFormat(System.Type? type)
                {
                    System.IO.File.WriteAllText("proof.txt", "effect");
                    return System.Globalization.NumberFormatInfo.InvariantInfo;
                }
            }
            """);

        Assert.Equal(customProvider, HasCampaignEncodingDiagnostic(Discover(source), "System.Double.TryParse"));
    }

    [Fact]
    public void CampaignEncoding_double_parse_rejects_authored_conversion_from_invariant_culture()
    {
        string source = FixtureSource("ParseOwner.Run();", """
            static class ParseOwner
            {
                public static void Run() => double.TryParse("1.25", System.Globalization.NumberStyles.Float,
                    (System.IFormatProvider)(EffectfulProvider)System.Globalization.CultureInfo.InvariantCulture, out _);
            }
            sealed class EffectfulProvider : System.IFormatProvider
            {
                public static explicit operator EffectfulProvider(System.Globalization.CultureInfo culture) => new();
                public object? GetFormat(System.Type? type)
                {
                    System.IO.File.WriteAllText("proof.txt", "converted provider effect");
                    return System.Globalization.NumberFormatInfo.InvariantInfo;
                }
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Double.TryParse"));
    }

    [Fact]
    public void CampaignEncoding_double_parse_rejects_null_before_later_invariant_assignment()
    {
        string source = FixtureSource("ParseOwner.Run();", """
            static class ParseOwner
            {
                public static void Run()
                {
                    System.Globalization.CultureInfo.CurrentCulture = new EffectfulCulture();
                    System.IFormatProvider? provider = null;
                    _ = double.TryParse("1.25", System.Globalization.NumberStyles.Float, provider, out _);
                    provider = System.Globalization.CultureInfo.InvariantCulture;
                }
            }
            sealed class EffectfulCulture : System.Globalization.CultureInfo
            {
                public EffectfulCulture() : base("en-US") { }
                public override System.Globalization.NumberFormatInfo NumberFormat
                {
                    get
                    {
                        System.IO.File.WriteAllText("proof.txt", "current culture effect");
                        return System.Globalization.NumberFormatInfo.InvariantInfo;
                    }
                    set => throw new System.NotSupportedException();
                }
            }
            """);

        Assert.True(HasCampaignEncodingDiagnostic(Discover(source), "System.Double.TryParse"));
    }

    [Fact]
    public void CampaignEncoding_double_parse_rejects_current_culture_or_mutated_provider_alias()
    {
        string source = FixtureSource("""
            _ = double.TryParse("1.25", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out _);
            System.IFormatProvider provider = System.Globalization.CultureInfo.InvariantCulture;
            provider = System.Globalization.CultureInfo.CurrentCulture;
            _ = double.TryParse("1.25", System.Globalization.NumberStyles.Float, provider, out _);
            """);

        Assert.Equal(2, Discover(source).Diagnostics.Count(item => item.Code == "HOSTED_SITE_UNCLASSIFIED"
            && item.Detail == "System.Double.TryParse"));
    }

    private static bool HasCampaignEncodingDiagnostic(HostedProducerDiscovery<HostedProducerSite> discovery, string callee) =>
        discovery.Diagnostics.Any(item => item.Code == "HOSTED_SITE_UNCLASSIFIED" && item.Detail == callee);
}
