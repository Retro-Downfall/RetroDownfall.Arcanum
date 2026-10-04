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

    /// <summary>
    /// What a file is comes from its leading bytes, never from its name: a text file called
    /// <c>.png</c> used to be sent to the provider as <c>image/png</c> because the extension filled in
    /// whenever the signature did not match.
    /// </summary>
    [Fact]
    public void Stage_rejects_text_content_named_png()
    {
        string path = WriteFile("notes.png", System.Text.Encoding.UTF8.GetBytes("These are meeting notes, not an image."));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, DefaultAllowedMimeTypes);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);

        Assert.Contains("not a supported image", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_does_not_let_the_extension_stand_in_for_a_missing_signature()
    {
        // Too short for any signature to match; the .bmp name is not evidence.
        string path = WriteFile("tiny.bmp", [0x01, 0x02]);

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, DefaultAllowedMimeTypes);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Stage_accepts_a_bitmap_by_its_signature_whatever_it_is_named()
    {
        string path = WriteFile("picture.dat", BmpMagicBytes(64));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, DefaultAllowedMimeTypes);

        Assert.Null(result.Error);

        Assert.Equal("image/bmp", result.Focus!.MimeType);
    }

    [Fact]
    public void Stage_rejects_text_that_merely_starts_with_the_bitmap_letters()
    {
        string path = WriteFile("memo.bmp", System.Text.Encoding.UTF8.GetBytes("BM is the abbreviation of the project's building manager."));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(path, maxImageBytes: 1_048_576, DefaultAllowedMimeTypes);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// A path the runtime refuses to open as a file path at all (an embedded NUL here) is a staging
    /// error the caller can report, not an exception that unwinds the turn.
    /// </summary>
    [Fact]
    public void Stage_reports_an_unusable_path_instead_of_throwing()
    {
        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(
            Path.Combine(_tempDir, "bad\0name.png"),
            maxImageBytes: 1_048_576,
            DefaultAllowedMimeTypes);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Stage_observes_cancellation_between_reads()
    {
        using CancellationTokenSource cancellation = new();

        CancellingReadStream stream = new(PngMagicBytes(1024 * 1024), cancellation);

        Assert.Throws<OperationCanceledException>(() => ScryingFocusStager.Stage(
            Path.Combine(_tempDir, "slow.png"),
            maxImageBytes: 8 * 1024 * 1024,
            DefaultAllowedMimeTypes,
            static _ => 1024 * 1024,
            _ => stream,
            cancellation.Token));
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

    [Fact]
    public void Stage_FileGrowsAfterSizeCheck_StopsAfterLimitSentinel()
    {
        const int maximumBytes = 1024;

        CountingReadStream stream = new(PngMagicBytes(maximumBytes * 4));

        ScryingFocusStager.StagingResult result = ScryingFocusStager.Stage(
            Path.Combine(_tempDir, "growing.png"),
            maximumBytes,
            DefaultAllowedMimeTypes,
            static _ => 64,
            _ => stream);

        Assert.Null(result.Focus);

        Assert.NotNull(result.Error);

        Assert.Contains(maximumBytes.ToString(), result.Error, StringComparison.Ordinal);

        Assert.Equal(maximumBytes + 1, stream.BytesRead);
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

    private static byte[] BmpMagicBytes(int totalLength)
    {
        byte[] bytes = new byte[Math.Max(totalLength, 26)];

        bytes[0] = 0x42;

        bytes[1] = 0x4D;

        // The DIB header size (BITMAPINFOHEADER) that follows the 14-byte file header.
        bytes[14] = 40;

        return bytes;
    }

    /// <summary>Cancels the token on its first read, as a Ctrl-C landing mid-read would.</summary>
    private sealed class CancellingReadStream(byte[] contents, CancellationTokenSource cancellation)
        : MemoryStream(contents, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, Math.Min(count, 4096));

            cancellation.Cancel();

            return read;
        }
    }

    private sealed class CountingReadStream(byte[] contents)
        : MemoryStream(contents, writable: false)
    {
        public int BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);

            BytesRead += read;

            return read;
        }
    }
}
