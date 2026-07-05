using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ScryingFocusStagerTests : IDisposable
{

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"scrying-stager-{Guid.NewGuid():N}");

    private static readonly string[] DefaultAllowedMimeTypes =
    [
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
        "image/bmp",
    ];

    public ScryingFocusStagerTests()
    {

        Directory.CreateDirectory(_tempDir);

    }

    public void Dispose()
    {

        try
        {

            Directory.Delete(_tempDir, recursive: true);

        }
        catch (IOException)
        {
        }

    }

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("photo.gif", true)]
    [InlineData("photo.webp", true)]
    [InlineData("photo.bmp", true)]
    [InlineData("notes.txt", false)]
    [InlineData("archive.tar.gz", false)]
    [InlineData("noextension", false)]
    public void IsImagePath_RecognizesImageExtensionsCaseInsensitively(string path, bool expected)
    {

        bool actual = ScryingFocusStager.IsImagePath(path);

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void CheckSize_FileWithinLimit_ReturnsNoError()
    {

        string path = WriteFile("small.png", PngMagicBytes(64));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.CheckSize(path, maxImageBytes: 1024);

        Assert.Null(result.Error);

        Assert.Equal(64, result.FileSizeBytes);

    }

    [Fact]
    public void CheckSize_FileExceedsLimit_ReturnsError()
    {

        string path = WriteFile("big.png", PngMagicBytes(2048));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.CheckSize(path, maxImageBytes: 1024);

        Assert.NotNull(result.Error);

        Assert.Contains("1024", result.Error, StringComparison.Ordinal);

        Assert.Equal(2048, result.FileSizeBytes);

    }

    [Fact]
    public void Stage_ValidPngFile_ReturnsFocusWithDetectedMimeAndBase64()
    {

        byte[] bytes = PngMagicBytes(32);

        string path = WriteFile("valid.png", bytes);

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, DefaultAllowedMimeTypes);

        Assert.Null(result.Error);

        Assert.NotNull(result.Focus);

        Assert.Equal("image/png", result.Focus!.MimeType);

        Assert.Equal(Convert.ToBase64String(bytes), result.Focus.Data);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Stage_DetectsMimeTypeFromExtensionWhenMagicBytesInconclusive()
    {

        // Too short for any magic-byte signature to match; falls back to extension detection.
        string path = WriteFile("tiny.bmp", [0x01, 0x02]);

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, DefaultAllowedMimeTypes);

        Assert.Null(result.Error);

        Assert.Equal("image/bmp", result.Focus!.MimeType);

    }

    [Fact]
    public void Stage_MimeTypeNotInAllowList_ReturnsUnsupportedError()
    {

        string path = WriteFile("valid.png", PngMagicBytes(32));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, ["image/jpeg"]);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);

        Assert.Contains("image/png", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Stage_OversizedFile_ReturnsErrorWithoutReadingContents()
    {

        string path = WriteFile("big.png", PngMagicBytes(4096));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1024, DefaultAllowedMimeTypes);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);

        Assert.Contains("1024", result.Error, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1_048_576, "1 MiB")]
    public void FormatByteCount_FormatsHumanReadableSizes(long bytes, string expected)
    {

        string actual = ScryingFocusStager.FormatByteCount(bytes);

        Assert.Equal(expected, actual);

    }

    private string WriteFile(string fileName, byte[] contents)
    {

        string path = Path.Combine(_tempDir, fileName);

        File.WriteAllBytes(path, contents);

        return path;

    }

    private static byte[] PngMagicBytes(int totalLength)
    {

        byte[] bytes = new byte[Math.Max(totalLength, 8)];

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
