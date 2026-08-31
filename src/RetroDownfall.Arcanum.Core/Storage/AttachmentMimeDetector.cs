using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Small, deterministic MIME detector for refreshable attachment content. Image signatures take
/// precedence over extensions so a renamed binary cannot inherit trusted textual metadata.
/// </summary>
public static class AttachmentMimeDetector
{
    internal static IReadOnlyDictionary<string, string> ExtensionMimeTypes { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".json"] = "application/json",
            [".jsonld"] = "application/ld+json",
            [".toml"] = "application/toml",
            [".xml"] = "application/xml",
            [".php"] = "application/x-httpd-php",
            [".cjs"] = "application/x-javascript",
            [".jsonl"] = "application/x-ndjson",
            [".sh"] = "application/x-sh",
            [".yml"] = "application/x-yaml",
            [".yaml"] = "application/yaml",
            [".csv"] = "text/csv",
            [".js"] = "text/javascript",
            [".md"] = "text/markdown",
            [".markdown"] = "text/markdown",
            [".txt"] = "text/plain",
            [".fs"] = "text/plain",
            [".vb"] = "text/plain",
            [".ps1"] = "text/plain",
            [".ts"] = "text/plain",
            [".css"] = "text/plain",
            [".rst"] = "text/plain",
            [".adoc"] = "text/plain",
            [".asciidoc"] = "text/plain",
            [".tex"] = "text/plain",
            [".patch"] = "text/plain",
            [".diff"] = "text/plain",
            [".ini"] = "text/plain",
            [".srt"] = "text/plain",
            [".vtt"] = "text/plain",
            [".ics"] = "text/plain",
            [".tsv"] = "text/tab-separated-values",
            [".c"] = "text/x-c",
            [".h"] = "text/x-c",
            [".cpp"] = "text/x-c++",
            [".cc"] = "text/x-c++",
            [".cxx"] = "text/x-c++",
            [".cs"] = "text/x-csharp",
            [".go"] = "text/x-go",
            [".java"] = "text/x-java-source",
            [".kt"] = "text/x-kotlin",
            [".log"] = "text/x-log",
            [".py"] = "text/x-python",
            [".rb"] = "text/x-ruby",
            [".rs"] = "text/x-rust",
            [".bash"] = "text/x-shellscript",
            [".sql"] = "text/x-sql",
            [".xsl"] = "text/xml",
            [".cff"] = "text/yaml",
            [".html"] = "text/html",
            [".htm"] = "text/html",
            [".pdf"] = "application/pdf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".zip"] = "application/zip",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
        });

    public static string Detect(ReadOnlySpan<byte> bytes, string? fileName)
    {
        if (bytes.Length >= 5
            && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44
            && bytes[3] == 0x46 && bytes[4] == 0x2D)
        {
            return "application/pdf";
        }

        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3
            && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6
            && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        if (IsBitmapFileHeader(bytes))
        {
            return "image/bmp";
        }

        return ExtensionMimeTypes.TryGetValue(
            Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant(),
            out string? mimeType)
            ? mimeType
            : "application/octet-stream";
    }

    /// <summary>
    /// Validates the whole 14-byte <c>BITMAPFILEHEADER</c> — magic, declared file size, zero
    /// reserved fields, and a plausible pixel-data offset — rather than the two-character
    /// <c>BM</c> magic alone, so text that merely starts with "BM" is never sniffed as an image.
    /// A bitmap with an inconsistent header still resolves through the <c>.bmp</c> extension.
    /// </summary>
    private static bool IsBitmapFileHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 || bytes[0] != 0x42 || bytes[1] != 0x4D)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]) != (uint)bytes.Length)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]) != 0)
        {
            return false;
        }

        uint pixelDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);

        return pixelDataOffset >= 14 && pixelDataOffset <= (uint)bytes.Length;
    }
}
