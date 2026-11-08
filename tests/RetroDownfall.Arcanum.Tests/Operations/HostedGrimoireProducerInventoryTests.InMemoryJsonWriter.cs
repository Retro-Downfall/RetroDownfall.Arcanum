using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("plain", true)]
    [InlineData("bounded", true)]
    [InlineData("bounded-constant", true)]
    [InlineData("forwarded", true)]
    [InlineData("callback", true)]
    [InlineData("captured-callback", true)]
    [InlineData("raw-value", true)]
    [InlineData("file", false)]
    [InlineData("unknown-stream", false)]
    [InlineData("authored-write", false)]
    [InlineData("authored-dispose", false)]
    [InlineData("custom-encoder", false)]
    [InlineData("reset", false)]
    [InlineData("escape", false)]
    [InlineData("collection-escape", false)]
    [InlineData("callback-escape", false)]
    [InlineData("ref-rebound-backend", false)]
    [InlineData("parameter-rebound-backend", false)]
    [InlineData("ref-local-rebound-backend", false)]
    [InlineData("parenthesized-ref-local-rebound-backend", false)]
    [InlineData("converted-backend", false)]
    [InlineData("converted-options", false)]
    [InlineData("implicit-in-rebound-backend", false)]
    public void In_memory_json_writer_requires_closed_backend_encoder_and_lifetime(string shape, bool safe)
    {
        string stream = shape switch
        {
            "bounded" => "new BoundedBuffer()",
            "bounded-constant" => "new BoundedConstantBuffer()",
            "authored-write" => "new EffectBuffer()",
            "authored-dispose" => "new EffectDisposeBuffer()",
            "file" => "System.IO.File.OpenWrite(\"external\")",
            "unknown-stream" => "Escaped.Stream",
            "ref-rebound-backend" => "new ReboundBuffer().Buffer",
            "converted-backend" => "(System.IO.Stream)(RepointBuffer)new System.IO.MemoryStream()",
            _ => "new System.IO.MemoryStream()",
        };

        string encoder = shape == "custom-encoder" ? "new EffectEncoder()"
            : "System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping";

        string extra = shape switch
        {
            "reset" => "writer.Reset(System.IO.File.OpenWrite(\"repointed\"));",
            "escape" => "Escaped.Writer = writer;",
            "collection-escape" => "var escaped = new System.Collections.Generic.List<System.Text.Json.Utf8JsonWriter>(); escaped.Add(writer);",
            "callback-escape" => "Escaped.Callback = () => writer.WriteString(\"escaped\", \"literal\");",
            _ => string.Empty,
        };

        string emit = shape switch
        {
            "forwarded" => "Emit(writer);",
            "callback" => "EmitWith(static output => output.WriteString(\"goal\", \"literal\"), writer);",
            "captured-callback" => "EmitCaptured(static output => output.WriteString(\"goal\", \"literal\"), writer);",
            "raw-value" => "writer.WritePropertyName(\"raw\"); writer.WriteRawValue(\"1\");",
            _ => "writer.WriteString(\"goal\", \"literal\");",
        };

        string options = "new System.Text.Json.JsonWriterOptions { Encoder = " + encoder + " }";

        if (shape == "converted-options") options = "(System.Text.Json.JsonWriterOptions)(RepointOptions)(" + options + ")";

        string body = "using var stream = " + stream + "; using var writer = new System.Text.Json.Utf8JsonWriter(stream, "
            + options + "); "
            + extra + " writer.WriteStartObject(); " + emit + " writer.WriteNumber(\"version\", 1); writer.WriteEndObject(); writer.Flush();";

        if (shape == "parameter-rebound-backend")
            body = "using var stream = new System.IO.MemoryStream(); RebindParameter(stream);";

        if (shape == "implicit-in-rebound-backend")
            body = "System.IO.Stream stream = new System.IO.MemoryStream(); Smash(stream); "
                + "using var writer = new System.Text.Json.Utf8JsonWriter(stream, default); writer.WriteStartObject(); writer.WriteEndObject(); writer.Flush();";

        if (shape is "ref-local-rebound-backend" or "parenthesized-ref-local-rebound-backend")
            body = "System.IO.Stream stream = new System.IO.MemoryStream(); ref System.IO.Stream alias = ref "
                + (shape == "parenthesized-ref-local-rebound-backend" ? "(stream)" : "stream") + "; "
                + "alias = Escaped.Stream; using var writer = new System.Text.Json.Utf8JsonWriter(stream, default); writer.WriteStartObject(); writer.WriteEndObject(); writer.Flush();";

        const string helpers = """
            internal sealed class BoundedBuffer : System.IO.MemoryStream
            {
                public override void Write(byte[] buffer, int offset, int count)
                {
                    if (Position + count > 65536) throw new BufferLimitException();
                    base.Write(buffer, offset, count);
                }
                public override void Write(System.ReadOnlySpan<byte> buffer)
                {
                    if (Position + buffer.Length > 65536) throw new BufferLimitException();
                    base.Write(buffer.ToArray(), 0, buffer.Length);
                }
            }
            internal sealed class BufferLimitException : System.Exception { }
            internal static class BufferBounds { internal const int MaximumBytes = 65536; }
            internal sealed class BoundedConstantBuffer : System.IO.MemoryStream
            {
                public override void Write(byte[] buffer, int offset, int count)
                {
                    if (Position + count > BufferBounds.MaximumBytes) throw new BufferLimitException();
                    base.Write(buffer, offset, count);
                }
                public override void Write(System.ReadOnlySpan<byte> buffer)
                {
                    if (Position + buffer.Length > BufferBounds.MaximumBytes) throw new BufferLimitException();
                    base.Write(buffer.ToArray(), 0, buffer.Length);
                }
            }
            internal sealed class EffectBuffer : System.IO.MemoryStream
            {
                public override void Write(byte[] buffer, int offset, int count)
                {
                    System.IO.File.Delete("write-effect");
                    base.Write(buffer, offset, count);
                }
            }
            internal sealed class EffectDisposeBuffer : System.IO.MemoryStream
            {
                protected override void Dispose(bool disposing)
                {
                    System.IO.File.Delete("dispose-effect");
                    base.Dispose(disposing);
                }
            }
            internal sealed class EffectEncoder : System.Text.Encodings.Web.JavaScriptEncoder
            {
                public override int MaxOutputCharactersPerInputCharacter => 6;
                public override bool WillEncode(int scalar) { System.IO.File.Delete("encoder-effect"); return false; }
                public override unsafe int FindFirstCharacterToEncode(char* text, int length) => -1;
                public override unsafe bool TryEncodeUnicodeScalar(int scalar, char* output, int length, out int written)
                { written = 0; return false; }
            }
            internal static class Escaped
            {
                internal static System.IO.Stream Stream = null!;
                internal static System.Text.Json.Utf8JsonWriter? Writer;
                internal static System.Action? Callback;
            }
            internal sealed class ReboundBuffer
            {
                internal readonly System.IO.Stream Buffer = new System.IO.MemoryStream();
                internal ReboundBuffer() { Replace(ref Buffer); }
                private static void Replace(ref System.IO.Stream buffer) { buffer = System.IO.File.OpenWrite("rebound"); }
            }
            internal sealed class RepointBuffer
            {
                public static explicit operator RepointBuffer(System.IO.MemoryStream buffer) => new RepointBuffer();
                public static explicit operator System.IO.Stream(RepointBuffer buffer) => System.IO.File.OpenWrite("converted");
            }
            internal sealed class RepointOptions
            {
                public static explicit operator RepointOptions(System.Text.Json.JsonWriterOptions options) => new RepointOptions();
                public static explicit operator System.Text.Json.JsonWriterOptions(RepointOptions options) =>
                    new System.Text.Json.JsonWriterOptions { Encoder = new EffectEncoder() };
            }
            """;

        string source = FixtureSource(body, helpers)
            .Replace("public Task StartAsync(CancellationToken token)",
                "private static void Emit(System.Text.Json.Utf8JsonWriter writer) => writer.WriteString(\"goal\", \"literal\"); "
                + "private static void EmitWith(System.Action<System.Text.Json.Utf8JsonWriter> action, System.Text.Json.Utf8JsonWriter writer) => action(writer); "
                + "private static void EmitCaptured(System.Action<System.Text.Json.Utf8JsonWriter> callback, System.Text.Json.Utf8JsonWriter writer) => EmitWith(output => callback(output), writer); "
                + "private static void RebindParameter(System.IO.Stream buffer) { buffer = Escaped.Stream; using var writer = new System.Text.Json.Utf8JsonWriter(buffer, default); writer.WriteStartObject(); writer.WriteEndObject(); writer.Flush(); } "
                + "private static void Smash(in System.IO.Stream buffer) { System.Runtime.CompilerServices.Unsafe.AsRef(in buffer) = Escaped.Stream; } "
                + "public Task StartAsync(CancellationToken token)",
                StringComparison.Ordinal);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api")
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (safe) Assert.Empty(discovery.Diagnostics);
        else Assert.NotEmpty(discovery.Diagnostics);

        if (shape is "authored-write" or "authored-dispose" or "custom-encoder")
        {
            // A disposal diagnostic alone cannot protect an opaque writer callback. Either
            // expose its actual I/O without admission, or refuse the writer construction.
            bool unadmittedCallbackEffect = discovery.Items.Any(static site => site.Callee == "System.IO.File.Delete"
                && (site.WorkFrontierCapsuleId is null || site.EffectFrontierCapsuleId is null));

            bool refusedBackend = discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Text.Json.Utf8JsonWriter..ctor");

            Assert.True(unadmittedCallbackEffect || refusedBackend,
                "An authored backend or encoder must expose its File.Delete effect or be explicitly refused at writer construction.");
        }

        if (shape is "ref-rebound-backend" or "parameter-rebound-backend" or "ref-local-rebound-backend"
            or "parenthesized-ref-local-rebound-backend" or "converted-backend" or "converted-options" or "implicit-in-rebound-backend")
            Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.Text.Json.Utf8JsonWriter..ctor");
    }
}
