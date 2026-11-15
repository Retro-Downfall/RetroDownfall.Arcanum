using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("block", true)]
    [InlineData("statement", true)]
    [InlineData("declaration", true)]
    [InlineData("two-hop", true)]
    [InlineData("local-function", true)]
    [InlineData("argument-effect", true)]
    [InlineData("helper-effect", true)]
    [InlineData("file", false)]
    [InlineData("unknown-stream", false)]
    [InlineData("stream-subclass", false)]
    [InlineData("encoding-subclass", false)]
    [InlineData("writer-subclass", false)]
    [InlineData("unknown-writer", false)]
    [InlineData("leave-closed", false)]
    [InlineData("writer-reassigned", false)]
    [InlineData("backend-reassigned", false)]
    [InlineData("backend-ref", false)]
    [InlineData("writer-ref", false)]
    [InlineData("writer-in", false)]
    [InlineData("field-escape", false)]
    [InlineData("collection-escape", false)]
    [InlineData("callback-escape", false)]
    [InlineData("helper-escape", false)]
    [InlineData("forwarded-reassigned", false)]
    [InlineData("forwarded-ref", false)]
    public void Binary_writer_requires_exact_memory_origin_encoding_and_closed_uses(string shape, bool safe)
    {
        string buffer = shape switch
        {
            "file" => "System.IO.File.OpenWrite(\"external\")",
            "unknown-stream" => "BinaryInput.Stream",
            "stream-subclass" => "new EffectBinaryBuffer()",
            _ => "new System.IO.MemoryStream()",
        };

        string encoding = shape == "encoding-subclass" ? "new EffectBinaryEncoding()" : "System.Text.Encoding.UTF8";

        string writer = shape == "writer-subclass"
            ? "new EffectBinaryWriter(bytes)"
            : "new System.IO.BinaryWriter(bytes, " + encoding + ", leaveOpen: " + (shape == "leave-closed" ? "false" : "true") + ")";

        if (shape == "unknown-writer") writer = "BinaryInput.Writer!";

        string before = shape switch
        {
            "backend-reassigned" => "bytes = BinaryInput.Stream; ",
            "backend-ref" => "ref System.IO.Stream alias = ref bytes; alias = BinaryInput.Stream; ",
            _ => string.Empty,
        };

        string emit = shape switch
        {
            "two-hop" or "local-function" or "helper-effect" or "forwarded-reassigned" or "forwarded-ref" => "Emit(writer);",
            "argument-effect" => "writer.Write(BinaryInput.Value());",
            "writer-reassigned" => "writer = BinaryInput.Writer!; writer.Write(1);",
            "writer-ref" => "Rebind(ref writer); writer.Write(1);",
            "writer-in" => "Inspect(writer); writer.Write(1);",
            "field-escape" => "BinaryInput.Writer = writer; writer.Write(1);",
            "collection-escape" => "new System.Collections.Generic.List<System.IO.BinaryWriter>().Add(writer); writer.Write(1);",
            "callback-escape" => "BinaryInput.Callback = () => writer.Write(1); writer.Write(1);",
            "helper-escape" => "Escape(writer); writer.Write(1);",
            _ => "writer.Write(1); writer.Write(2L); writer.Write(true); writer.Write(\"identity\"); writer.Write(new byte[] { 1, 2 });",
        };

        string declarations = "System.IO.Stream bytes = " + buffer + "; " + before;

        string use = shape switch
        {
            "statement" => "using (System.IO.BinaryWriter writer = " + writer + ") Emit(writer);",
            "declaration" => "using System.IO.BinaryWriter writer = " + writer + "; " + emit,
            // Reassignment/ref of using variables is a C# error. Keep these legal but
            // unowned locals as negative origin/use cases rather than testing the compiler.
            "writer-reassigned" or "writer-ref" => "System.IO.BinaryWriter writer = " + writer + "; " + emit + " writer.Dispose();",
            _ => "using (System.IO.BinaryWriter writer = " + writer + ") { " + emit + " }",
        };

        string memberHelpers = "private static void Emit(System.IO.BinaryWriter writer) => EmitCore(writer); "
            + "private static void EmitCore(System.IO.BinaryWriter writer) { writer.Write(1); writer.Write(2L); writer.Write(true); writer.Write(\"identity\"); writer.Write(new byte[] { 1, 2 }); } "
            + "private static void Escape(System.IO.BinaryWriter writer) { BinaryInput.Writer = writer; } "
            + "private static void Rebind(ref System.IO.BinaryWriter writer) { writer = BinaryInput.Writer!; } "
            + "private static void Inspect(in System.IO.BinaryWriter writer) { } ";

        const string helperOpening = "private static void EmitCore(System.IO.BinaryWriter writer) { ";

        if (shape == "forwarded-reassigned")
            memberHelpers = memberHelpers.ReplaceSdkFixtureText(helperOpening,
                helperOpening + "writer = BinaryInput.Writer!; ", StringComparison.Ordinal);
        else if (shape == "forwarded-ref")
            memberHelpers = memberHelpers.ReplaceSdkFixtureText(helperOpening,
                helperOpening + "Rebind(ref writer); ", StringComparison.Ordinal);
        else if (shape == "helper-effect")
            memberHelpers = memberHelpers.ReplaceSdkFixtureText(helperOpening,
                helperOpening + "System.IO.File.Delete(\"forwarded-helper-effect\"); ", StringComparison.Ordinal);

        if (shape == "local-function")
        {
            use = "void Identity() { " + declarations + use + " } Identity();";

            declarations = string.Empty;
        }

        const string helpers = """
            internal static class BinaryInput
            {
                internal static System.IO.Stream Stream = null!;
                internal static System.IO.BinaryWriter? Writer;
                internal static System.Action? Callback;
                internal static int Value() { System.IO.File.Delete("argument-effect"); return 1; }
            }
            internal sealed class EffectBinaryBuffer : System.IO.MemoryStream
            {
                public override void Flush() { System.IO.File.Delete("buffer-effect"); base.Flush(); }
                public override void Write(byte[] buffer, int offset, int count)
                { System.IO.File.Delete("buffer-effect"); base.Write(buffer, offset, count); }
            }
            internal sealed class EffectBinaryEncoding : System.Text.UTF8Encoding
            {
                public override int GetByteCount(string value)
                { System.IO.File.Delete("encoding-effect"); return base.GetByteCount(value); }
            }
            internal sealed class EffectBinaryWriter(System.IO.Stream stream) : System.IO.BinaryWriter(stream)
            {
                public override void Write(int value) { System.IO.File.Delete("writer-effect"); base.Write(value); }
            }
            """;

        string source = R2Source(R2Admission + declarations + use + " await Task.CompletedTask;", helpers)
            .Replace("public async Task StartAsync", memberHelpers + "public async Task StartAsync", StringComparison.Ordinal);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        bool refused = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("System.IO.BinaryWriter.", StringComparison.Ordinal));

        Assert.Equal(!safe, refused);

        if (safe)
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.Contains("BinaryWriter", StringComparison.Ordinal));
        }

        if (shape is "argument-effect" or "helper-effect")
        {
            Assert.NotEmpty(EnumerationDeleteSites(result));

            Assert.All(EnumerationDeleteSites(result), static site =>
            {
                Assert.NotNull(site.WorkFrontierCapsuleId);

                Assert.NotNull(site.EffectFrontierCapsuleId);
            });
        }
    }

    [Fact]
    public void Binary_writer_forwarding_proof_is_specific_to_each_bound_caller()
    {
        const string body = "using var bytes = new System.IO.MemoryStream(); "
            + "using var safe = new System.IO.BinaryWriter(bytes, System.Text.Encoding.UTF8, true); "
            + "BinarySink.Emit(safe); BinarySink.Emit(UnknownBinary.Writer); await Task.CompletedTask;";

        const string helpers = "static class UnknownBinary { internal static System.IO.BinaryWriter Writer = null!; } "
            + "static class BinarySink { internal static void Emit(System.IO.BinaryWriter writer) => EmitCore(writer); "
            + "private static void EmitCore(System.IO.BinaryWriter writer) => writer.Write(1); }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + body, helpers), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.BinaryWriter.Write");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.BinaryWriter..ctor");
    }

    [Fact]
    public void Safe_binary_writer_does_not_certify_an_unknown_sibling()
    {
        const string body = "using var bytes = new System.IO.MemoryStream(); "
            + "using var safe = new System.IO.BinaryWriter(bytes, System.Text.Encoding.UTF8, true); safe.Write(1); "
            + "UnknownBinary.Writer.Write(2); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(R2Admission + body,
            "static class UnknownBinary { internal static System.IO.BinaryWriter Writer = null!; }"), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.BinaryWriter.Write");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Binary_writer_spelling_does_not_certify_authored_or_foreign_symbols(bool metadata)
    {
        const string counterfeit = "namespace System.IO { public sealed class BinaryWriter : System.IDisposable { "
            + "public BinaryWriter(System.IO.Stream stream, System.Text.Encoding encoding, bool leaveOpen) { } "
            + "public void Write(int value) { System.IO.File.Delete(\"counterfeit-writer\"); } public void Dispose() { } } }";

        string body = R2Admission + "using var bytes = new System.IO.MemoryStream(); using var writer = new "
            + (metadata ? "counterfeit::" : string.Empty)
            + "System.IO.BinaryWriter(bytes, System.Text.Encoding.UTF8, true); writer.Write(1); await Task.CompletedTask;";

        CSharpCompilation compilation;

        if (metadata)
        {
            CSharpCompilation foreign = CompileWithProductionReferencePack(counterfeit, "RetroDownfall.Arcanum.Api")
                .WithAssemblyName("Untrusted.BinaryWriter");

            using MemoryStream image = new();

            Assert.True(foreign.Emit(image).Success);

            compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(body), "RetroDownfall.Arcanum.Api")
                .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));
        }
        else
        {
            compilation = CompileWithProductionReferencePack(R2Source(body, counterfeit), "RetroDownfall.Arcanum.Api");
        }

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        if (metadata)
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.IO.BinaryWriter.Write");
        }
        else
        {
            Assert.NotEmpty(EnumerationDeleteSites(result));
        }
    }
}
