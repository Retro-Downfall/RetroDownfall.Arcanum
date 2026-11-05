using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class AiReflectionIlIntegrityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_empty_body_on_an_ordinary_managed_method_fails_closed(bool fatHeader)
    {
        ProbeImage probe = CreateProbe("empty_body_" + fatHeader);

        int bodyOffset = probe.IlFileOffset - 1;

        Assert.Equal(2, probe.Image[bodyOffset] & 3);

        if (fatHeader)
        {
            // Replace the tiny header with a valid twelve-byte fat header declaring no IL.
            probe.Image.AsSpan(bodyOffset, 12).Clear();

            BinaryPrimitives.WriteUInt16LittleEndian(probe.Image.AsSpan(bodyOffset, 2), 0x3003);
        }
        else
        {
            // A tiny header stores CodeSize in its upper six bits.
            probe.Image[bodyOffset] = 0x02;
        }

        AssertInvalidIl(probe, 0);
    }

    [Fact]
    public void A_missing_body_on_an_ordinary_managed_method_fails_closed()
    {
        ProbeImage probe = CreateProbe("missing_body");

        using PEReader pe = new(new MemoryStream(probe.Image, writable: false));

        MetadataReader metadata = pe.GetMetadataReader();

        MethodDefinitionHandle method = Assert.Single(metadata.MethodDefinitions, handle =>
            metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Probe");

        int rowOffset = pe.PEHeaders.MetadataStartOffset + metadata.GetTableMetadataOffset(TableIndex.MethodDef)
            + (MetadataTokens.GetRowNumber(method) - 1) * metadata.GetTableRowSize(TableIndex.MethodDef);

        BinaryPrimitives.WriteInt32LittleEndian(probe.Image.AsSpan(rowOffset, sizeof(int)), 0);

        AssertInvalidIl(probe, 0);
    }

    [Fact]
    public void Legitimate_abstract_native_and_runtime_methods_need_no_il_body()
    {
        byte[] image = AiReflectionFixtures.Compile("legitimate_bodyless", """
            [System.Runtime.InteropServices.DllImport("unused-test-library")]
            public static extern int Probe();
            public abstract class AbstractMember { public abstract void Execute(); }
            public delegate void RuntimeDelegate();
            """);

        Assert.Empty(AiReflectionBoundaryScanner.FindForbiddenReferences(image));
    }

    [Fact]
    public void Runtime_generated_field_accessors_remain_supported()
    {
        byte[] image = AiReflectionFixtures.Compile("field_accessor", """
            [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "Value")]
            public static extern ref int Probe(Holder instance);
            public sealed class Holder { public int Value; }
            """);

        Assert.Empty(AiReflectionBoundaryScanner.FindForbiddenReferences(image));
    }

    [Fact]
    public void Method_accessors_cannot_claim_the_field_only_bodyless_exception()
    {
        byte[] image = AiReflectionFixtures.Compile("method_accessor", """
            [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = "Create")]
            public static extern object Probe(object instance);
            """);

        Assert.Throws<InvalidDataException>(() => AiReflectionBoundaryScanner.FindForbiddenReferences(image));
    }

    [Fact]
    public void Unknown_opcode_fails_with_assembly_caller_and_il_offset()
    {
        ProbeImage probe = CreateProbe("unknown_opcode");

        // 0x24 is not an ECMA-335 opcode.
        probe.Image[probe.IlFileOffset] = 0x24;

        AssertInvalidIl(probe, 0);
    }

    [Fact]
    public void Truncated_method_operand_fails_with_assembly_caller_and_il_offset()
    {
        ProbeImage probe = CreateProbe("truncated_operand");

        int offset = probe.IlLength - 1;

        Assert.Equal(0x2a, probe.Image[probe.IlFileOffset + offset]);

        // Replace the final ret with call, leaving no bytes for its four-byte operand.
        probe.Image[probe.IlFileOffset + offset] = 0x28;

        AssertInvalidIl(probe, offset);
    }

    [Fact]
    public void Invalid_method_token_fails_with_assembly_caller_and_il_offset()
    {
        ProbeImage probe = CreateProbe("invalid_method_token");

        // A MemberRef token whose row is outside this fixture's metadata table.
        BinaryPrimitives.WriteInt32LittleEndian(
            probe.Image.AsSpan(probe.IlFileOffset + probe.CallOffset + 1, sizeof(int)),
            0x0affffff);

        AssertInvalidIl(probe, probe.CallOffset);
    }

    [Fact]
    public void Missing_or_invalid_pe_image_fails_closed()
    {
        Assert.Throws<InvalidDataException>(() =>
            AiReflectionBoundaryScanner.FindForbiddenReferences([]));

        Assert.Throws<InvalidDataException>(() =>
            AiReflectionBoundaryScanner.FindForbiddenReferences("not a PE image"u8.ToArray()));
    }

    [Fact]
    public void Ldtoken_of_forbidden_generic_method_is_detected_without_a_call_instruction()
    {
        ProbeImage probe = CreateProbe("methodspec_ldtoken");

        // call and ldtoken both have a four-byte metadata-token operand. This intentionally
        // changes stack semantics: the boundary inspects references, not IL verifiability.
        probe.Image[probe.IlFileOffset + probe.CallOffset] = 0xd0;

        IReadOnlyList<AiReflectionViolation> violations =
            AiReflectionBoundaryScanner.FindForbiddenReferences(probe.Image);

        AiReflectionViolation violation = Assert.Single(violations);

        Assert.Equal(probe.AssemblyName, violation.Assembly);

        Assert.Contains("Probe", violation.Caller, StringComparison.Ordinal);

        Assert.Contains("Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema", violation.Callee, StringComparison.Ordinal);

        Assert.Equal(probe.CallOffset, violation.Offset);
    }

    private static void AssertInvalidIl(ProbeImage probe, int offset)
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            AiReflectionBoundaryScanner.FindForbiddenReferences(probe.Image));

        Assert.Contains(probe.AssemblyName, exception.Message, StringComparison.Ordinal);

        Assert.Contains("Probe", exception.Message, StringComparison.Ordinal);

        Assert.Contains($"IL_{offset:x4}", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProbeImage CreateProbe(string id)
    {
        // Compile caches fixture bytes; mutations must never alter that shared cached image.
        byte[] image = (byte[])AiReflectionFixtures.Compile(
            "integrity_" + id,
            "public static object Probe() => ChatResponseFormat.ForJsonSchema<string>();").Clone();

        using MemoryStream stream = new(image, writable: false);

        using PEReader pe = new(stream);

        MetadataReader metadata = pe.GetMetadataReader();

        MethodDefinition method = metadata.GetMethodDefinition(Assert.Single(
            metadata.MethodDefinitions, handle =>
                metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Probe"));

        int rva = method.RelativeVirtualAddress;

        SectionHeader section = Assert.Single(pe.PEHeaders.SectionHeaders, candidate =>
            rva >= candidate.VirtualAddress
            && (long)rva < (long)candidate.VirtualAddress + candidate.SizeOfRawData);

        int bodyOffset = checked(section.PointerToRawData + rva - section.VirtualAddress);

        Assert.InRange(bodyOffset, 0, image.Length - 1);

        int headerSize;

        if ((image[bodyOffset] & 3) == 2)
        {
            headerSize = 1;
        }
        else
        {
            Assert.Equal(3, image[bodyOffset] & 3);

            Assert.InRange(bodyOffset, 0, image.Length - sizeof(ushort));

            headerSize = (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(bodyOffset, sizeof(ushort))) >> 12) * 4;

            Assert.True(headerSize >= 12, "A fat method header must contain at least twelve bytes.");
        }

        int ilFileOffset = checked(bodyOffset + headerSize);

        byte[] il = pe.GetMethodBody(rva).GetILBytes()!;

        Assert.NotEmpty(il);

        Assert.InRange(ilFileOffset, 0, image.Length - il.Length);

        Assert.Equal(il, image.AsSpan(ilFileOffset, il.Length).ToArray());

        List<int> calls = [];

        for (int offset = 0; offset <= il.Length - 5; offset++)
        {
            if (il[offset] != 0x28)
            {
                continue;
            }

            int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset + 1, sizeof(int)));

            if ((token & unchecked((int)0xff000000)) != 0x2b000000)
            {
                continue;
            }

            MethodSpecification specification = metadata.GetMethodSpecification((MethodSpecificationHandle)MetadataTokens.EntityHandle(token));

            Assert.Equal(HandleKind.MemberReference, specification.Method.Kind);

            MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)specification.Method);

            if (metadata.GetString(member.Name) == "ForJsonSchema")
            {
                calls.Add(offset);
            }
        }

        return new ProbeImage(
            image,
            metadata.GetString(metadata.GetAssemblyDefinition().Name),
            ilFileOffset,
            il.Length,
            Assert.Single(calls));
    }

    private sealed record ProbeImage(
        byte[] Image,
        string AssemblyName,
        int IlFileOffset,
        int IlLength,
        int CallOffset);
}
