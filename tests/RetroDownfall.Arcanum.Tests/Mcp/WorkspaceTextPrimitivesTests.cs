using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class WorkspaceTextPrimitivesTests
{

    [Fact]
    public void Decode_strict_utf8_with_bom_round_trips_exact_bytes()
    {

        byte[] original =
        [
            0xEF, 0xBB, 0xBF,
            .. Encoding.UTF8.GetBytes("sigil \u2728\r\nsecond\n"),
        ];

        WorkspaceTextFile document = WorkspaceTextFile.Decode(original);

        Assert.True(document.HasUtf8Bom);

        Assert.Equal(original, document.Encode());

    }

    [Theory]
    [MemberData(nameof(RejectedContent))]
    public void Decode_rejects_binary_or_non_utf8_content(byte[] bytes, int expected)
    {

        WorkspaceTextDecodingException exception = Assert.Throws<WorkspaceTextDecodingException>(
            () => WorkspaceTextFile.Decode(bytes));

        Assert.Equal((WorkspaceTextRejection)expected, exception.Rejection);

    }

    [Fact]
    public void InsertLines_preserves_untouched_mixed_delimiters_and_uses_dominant_newline()
    {

        byte[] original = Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\nfour\rfive");

        WorkspaceTextFile document = WorkspaceTextFile.Decode(original);

        WorkspaceTextFile edited = document.InsertLines(2, ["inserted-a", "inserted-b"]);

        Assert.Equal("\r\n", document.DominantNewline);

        Assert.Equal(
            Encoding.UTF8.GetBytes("one\r\ntwo\r\ninserted-a\r\ninserted-b\r\nthree\nfour\rfive"),
            edited.Encode());

    }

    [Fact]
    public void InsertLines_at_end_preserves_final_newline_shape()
    {

        WorkspaceTextFile withFinalNewline = WorkspaceTextFile.Decode(Encoding.UTF8.GetBytes("one\r\n"));

        WorkspaceTextFile withoutFinalNewline = WorkspaceTextFile.Decode(Encoding.UTF8.GetBytes("one"));

        Assert.Equal(
            Encoding.UTF8.GetBytes("one\r\ntwo\r\n"),
            withFinalNewline.InsertLines(1, ["two"]).Encode());

        Assert.Equal(
            Encoding.UTF8.GetBytes("one\ntwo"),
            withoutFinalNewline.InsertLines(1, ["two"]).Encode());

    }

    [Fact]
    public void Normalize_relative_paths_is_platform_independent_and_rejects_escapes()
    {

        Assert.True(
            WorkspaceRelativePath.TryNormalize(@"src\Arcana/./Sigil.cs", out string? normalized));

        Assert.Equal("src/Arcana/Sigil.cs", normalized);

        Assert.False(WorkspaceRelativePath.TryNormalize("../outside.cs", out _));

        Assert.False(WorkspaceRelativePath.TryNormalize("/absolute.cs", out _));

        Assert.False(WorkspaceRelativePath.TryNormalize(@"C:\absolute.cs", out _));

        Assert.False(WorkspaceRelativePath.TryNormalize("nul\0path.cs", out _));

    }

    [Fact]
    public void Canonical_aliases_cover_macos_case_and_unicode_normalization()
    {

        string composed = WorkspaceRelativePath.GetCanonicalAliasForTests(
            "Résumé/File.cs",
            WorkspacePathAliasPlatform.MacOS);

        string decomposed = WorkspaceRelativePath.GetCanonicalAliasForTests(
            "RE\u0301SUME\u0301/file.cs",
            WorkspacePathAliasPlatform.MacOS);

        Assert.Equal(composed, decomposed);

    }

    [Fact]
    public void Canonical_aliases_cover_windows_case_and_trailing_dots_and_spaces()
    {

        string ordinary = WorkspaceRelativePath.GetCanonicalAliasForTests(
            "Folder/File",
            WorkspacePathAliasPlatform.Windows);

        string aliased = WorkspaceRelativePath.GetCanonicalAliasForTests(
            "FOLDER/file. ",
            WorkspacePathAliasPlatform.Windows);

        Assert.Equal(ordinary, aliased);

    }

    public static TheoryData<byte[], int> RejectedContent =>
        new()
        {
            { [0xFF, 0xFE, 0x41, 0x00], (int)WorkspaceTextRejection.UnsupportedEncoding },
            { [0xC3, 0x28], (int)WorkspaceTextRejection.InvalidUtf8 },
            { Encoding.UTF8.GetBytes("text\0payload"), (int)WorkspaceTextRejection.BinaryContent },
        };

}
