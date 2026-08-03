using System.Security.Cryptography;

using System.Text;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class RunAttachmentStagerTests : IDisposable
{

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-tests",
        $"run-attachment-stager-{Guid.NewGuid():N}");

    private readonly string _workspace;

    public RunAttachmentStagerTests()
    {

        _workspace = Path.Combine(_tempDirectory, "workspace");

        Directory.CreateDirectory(_workspace);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_tempDirectory, recursive: true);

        }
        catch (IOException)
        {

        }

    }

    [Fact]

    public async Task StageAsync_resolves_repeated_relative_and_explicit_absolute_text_paths_without_extension_allowlist()
    {

        string relativePath = WriteText(_workspace, "notes.unusual", "relative text");

        string absolutePath = WriteText(_tempDirectory, "outside.data", "absolute text");

        RunAttachmentStager stager = CreateStager();

        RunAttachmentStageResult result = await stager.StageAsync(
            ["@notes.unusual", "@" + absolutePath],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);

        Assert.Empty(result.ScryingFoci);

        Assert.Equal(2, result.AttachedFiles.Count);

        Assert.Equal("relative text", result.AttachedFiles[0].Content);

        Assert.Equal("absolute text", result.AttachedFiles[1].Content);

        Assert.Equal(Path.GetRelativePath(_workspace, relativePath), result.AttachedFiles[0].RelativePath);

        Assert.Equal(Path.GetRelativePath(_workspace, absolutePath), result.AttachedFiles[1].RelativePath);

        Assert.Equal(2, result.Metadata.Count);

        Assert.All(
            result.Metadata,
            metadata => Assert.Matches("^[0-9a-f]{64}$", metadata.Sha256));

    }

    [Fact]

    public async Task StageAsync_splits_multibyte_text_on_utf8_boundaries_without_losing_content()
    {

        string content = new string('a', RunAttachmentStager.MaxAttachedFileChunkBytes - 1)
            + "\U0001F600"
            + new string('b', 64);

        _ = WriteText(_workspace, "boundary.txt", content);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            ["@boundary.txt"],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal(2, result.AttachedFiles.Count);

        Assert.All(
            result.AttachedFiles,
            file => Assert.InRange(
                Encoding.UTF8.GetByteCount(file.Content),
                0,
                RunAttachmentStager.MaxAttachedFileChunkBytes));

        Assert.Equal(content, string.Concat(result.AttachedFiles.Select(file => file.Content)));

        Assert.DoesNotContain(
            result.AttachedFiles,
            file => file.Content.Length > 0
                && char.IsHighSurrogate(file.Content[^1]));

        Assert.Equal(2, result.Metadata.Single().ChunkCount);

    }

    [Fact]

    public async Task StageAsync_preserves_exact_ten_mebibyte_piped_content_in_server_sized_chunks()
    {

        string content = new('p', RunInputReader.MaxRedirectedInputBytes);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            [],
            _workspace,
            content,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal(10, result.AttachedFiles.Count);

        Assert.Equal(content, string.Concat(result.AttachedFiles.Select(file => file.Content)));

        Assert.All(
            result.AttachedFiles,
            file => Assert.Equal(
                RunAttachmentStager.MaxAttachedFileChunkBytes,
                Encoding.UTF8.GetByteCount(file.Content)));

        RunAttachmentMetadata metadata = Assert.Single(result.Metadata);

        Assert.Equal("stdin", metadata.Source);

        Assert.Equal(RunInputReader.MaxRedirectedInputBytes, metadata.ByteCount);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            metadata.Sha256);

    }

    [Fact]

    public async Task StageAsync_stages_images_through_existing_scrying_pipeline_and_hashes_staged_bytes()
    {

        byte[] png = PngBytes(64);

        string path = Path.Combine(_workspace, "focus.png");

        File.WriteAllBytes(path, png);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            ["@focus.png"],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);

        Assert.Empty(result.AttachedFiles);

        ScryingFocusDto focus = Assert.Single(result.ScryingFoci);

        Assert.Equal("image/png", focus.MimeType);

        RunAttachmentMetadata metadata = Assert.Single(result.Metadata);

        Assert.Equal("image", metadata.Kind);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
            metadata.Sha256);

    }

    [Theory]

    [InlineData("")]

    [InlineData("notes.txt")]

    [InlineData("@missing.txt")]

    public async Task StageAsync_strictly_rejects_invalid_or_missing_explicit_with_values(string value)
    {

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            [value],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Empty(result.AttachedFiles);

        Assert.Empty(result.ScryingFoci);

        Assert.False(string.IsNullOrWhiteSpace(result.Error));

    }

    [Fact]

    public async Task StageAsync_accepts_text_file_above_stdin_limit_when_within_server_aggregate()
    {

        string path = Path.Combine(_workspace, "oversized.txt");

        await File.WriteAllTextAsync(
            path,
            new string('x', RunInputReader.MaxRedirectedInputBytes + 1),
            CancellationToken.None);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            ["@oversized.txt"],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(11, result.AttachedFiles.Count);

        Assert.Equal(
            RunInputReader.MaxRedirectedInputBytes + 1,
            result.AttachedFiles.Sum(
                static file => System.Text.Encoding.UTF8.GetByteCount(file.Content)));

    }

    [Fact]

    public async Task StageAsync_rejects_invalid_utf8_text_without_partial_output()
    {

        string path = Path.Combine(_workspace, "invalid.bin");

        await File.WriteAllBytesAsync(
            path,
            [0xC3, 0x28],
            CancellationToken.None);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            ["@invalid.bin"],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Empty(result.AttachedFiles);

        Assert.Contains("valid UTF-8", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task StageAsync_propagates_existing_scrying_image_size_failure_without_partial_output()
    {

        string path = Path.Combine(_workspace, "oversized.png");

        await File.WriteAllBytesAsync(
            path,
            PngBytes((int)ArcanumRuntimeDefaults.Scrying.MaxImageBytes + 1),
            CancellationToken.None);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            ["@oversized.png"],
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Empty(result.ScryingFoci);

        Assert.Contains("maximum size", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task StageAsync_rejects_excess_image_before_staging_it_without_partial_output()
    {

        int maximumImages = ArcanumSettingClamps.ScryingMaxImagesPerRequest(
            ArcanumRuntimeDefaults.Scrying.MaxImagesPerRequest);

        List<string> values = [];

        for (int index = 0; index < maximumImages; index++)
        {

            string name = $"focus-{index:D2}.png";

            await File.WriteAllBytesAsync(
                Path.Combine(_workspace, name),
                PngBytes(64),
                CancellationToken.None);

            values.Add("@" + name);

        }

        string excessName = "focus-excess.png";

        await File.WriteAllBytesAsync(
            Path.Combine(_workspace, excessName),
            PngBytes((int)ArcanumRuntimeDefaults.Scrying.MaxImageBytes + 1),
            CancellationToken.None);

        values.Add("@" + excessName);

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            values,
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Empty(result.AttachedFiles);

        Assert.Empty(result.ScryingFoci);

        Assert.Empty(result.Metadata);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(
            $"maximum of {maximumImages} images",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "maximum size",
            result.Error,
            StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public async Task StageAsync_accepts_files_beyond_the_former_count_ceiling()
    {

        const int formerFileCountCeiling = 32;

        List<string> values = [];

        for (int index = 0; index <= formerFileCountCeiling; index++)
        {

            string name = $"file-{index:D2}.txt";

            _ = WriteText(_workspace, name, "x");

            values.Add("@" + name);

        }

        RunAttachmentStageResult result = await CreateStager().StageAsync(
            values,
            _workspace,
            pipedContent: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal(formerFileCountCeiling + 1, result.AttachedFiles.Count);

    }

    private static RunAttachmentStager CreateStager() =>
        new(Options.Create(new ArcanumSettings()));

    private static string WriteText(
        string directory,
        string name,
        string content)
    {

        string path = Path.Combine(directory, name);

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;

    }

    private static byte[] PngBytes(int length)
    {

        byte[] bytes = new byte[Math.Max(8, length)];

        bytes[0] = 0x89;

        bytes[1] = 0x50;

        bytes[2] = 0x4E;

        bytes[3] = 0x47;

        bytes[4] = 0x0D;

        bytes[5] = 0x0A;

        bytes[6] = 0x1A;

        bytes[7] = 0x0A;

        return bytes;

    }

}
