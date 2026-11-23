using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Counts text using the pinned Qwen BPE vocabulary and merge order in an installed Ollama bundle.</summary>
internal sealed partial class OllamaQwenTokenizer
{
    internal const string EncodingId = "qwen3.8-flash-next";

    private const string TokenizerDigest = "0997f410c57a1f4e53b09e4be8f4a172d90edd9564368fb0847030937229b9f3";

    private const string TokenizerConfigDigest = "b11349aafa7cdc6a320767cf7ceb29ed82f7eda5d65e8e0819e76f0ce947bf27";

    private const string PromptTemplateDigest = "b507b9c2f6ca642bffcd06665ea7c91f235fd32daeefdf875a0f938db05fb315";

    private const int ManifestMaxBytes = 1_048_576;

    private const int TokenizerLength = 12_809_320;

    private static readonly char[] ByteAlphabet = CreateByteAlphabet();

    private readonly object _loadLock = new();

    private InferenceTextCounter? _counter;

    private ManifestProfile? _lastManifest;

    internal string? FindQualifiedManifestDigest(string model)
    {
        FileInfo? manifest = FindManifest(model);

        return manifest is not null && IsQualifiedManifest(manifest) ? manifest.Name[7..] : null;
    }

    internal InferenceTextCounter? TryResolve(string model, string expectedDigest)
    {
        FileInfo? manifest = FindManifest(model);

        if (manifest is null || !manifest.Name.AsSpan(7).Equals(expectedDigest, StringComparison.Ordinal)
            || !IsQualifiedManifest(manifest))
        {
            return null;
        }

        if (Volatile.Read(ref _counter) is { } loaded)
        {
            return loaded;
        }

        lock (_loadLock)
        {
            try
            {
                return _counter ??= Load(manifest.DirectoryName!);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return null;
            }
        }
    }

    private static FileInfo? FindManifest(string model)
    {
        if (!TryParseTag(model, out string modelNamespace, out string modelName, out string tag))
        {
            return null;
        }

        string? root = Environment.GetEnvironmentVariable("OLLAMA_MODELS");

        if (string.IsNullOrWhiteSpace(root))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (userProfile.Length == 0)
            {
                return null;
            }

            root = Path.Combine(userProfile, ".ollama", "models");
        }

        if (!Path.IsPathFullyQualified(root))
        {
            return null;
        }

        try
        {
            string blobs = Path.GetFullPath(Path.Combine(root, "blobs"));

            string name = Path.Combine(root, "manifests-v2", "ollama.com", modelNamespace, modelName, tag);

            if (File.ResolveLinkTarget(name, returnFinalTarget: false) is FileInfo manifest
                && manifest.DirectoryName == blobs
                && manifest.Name.Length == 71 && manifest.Name.StartsWith("sha256-", StringComparison.Ordinal)
                && !manifest.Name.AsSpan(7).ContainsAnyExcept("0123456789abcdef")
                && manifest.Exists)
            {
                return manifest;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    private bool IsQualifiedManifest(FileInfo manifest)
    {
        if (Volatile.Read(ref _lastManifest) is { } cached && cached.Path == manifest.FullName)
        {
            return cached.Qualified;
        }

        lock (_loadLock)
        {
            if (_lastManifest is { } resolved && resolved.Path == manifest.FullName)
            {
                return resolved.Qualified;
            }

            try
            {
                // Authenticate the content-addressed manifest before using its layer declarations.
                if (ReadPinned(manifest.FullName, ManifestMaxBytes, manifest.Name[7..]) is not { } manifestBytes)
                {
                    return false;
                }

                bool qualified = UsesQualifiedTemplate(manifestBytes);

                Volatile.Write(ref _lastManifest, new(manifest.FullName, qualified));

                return qualified;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }
    }

    private static bool TryParseTag(string model, out string modelNamespace, out string modelName, out string tag)
    {
        string[] path = model.Split('/');

        string[] name = path[^1].Split(':');

        modelNamespace = path.Length >= 2 ? path[^2] : "library";

        modelName = name[0];

        tag = name.Length == 2 ? name[1] : "latest";

        return path.Length <= 3 && (path.Length != 3 || path[0] == "ollama.com")
            && name.Length <= 2 && IsModelSegment(modelNamespace) && IsModelSegment(modelName) && IsModelSegment(tag);
    }

    private static bool IsModelSegment(string segment) =>
        segment.Length is > 0 and <= 128
        && char.IsAsciiLetterOrDigit(segment[0])
        && segment.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');

    private static bool UsesQualifiedTemplate(byte[] manifest)
    {
        using JsonDocument document = JsonDocument.Parse(manifest);

        JsonElement root = document.RootElement;

        if (root.GetProperty("mediaType").GetString() != "application/vnd.docker.distribution.manifest.v2+json"
            || root.GetProperty("runner").GetString() != "mlx"
            || root.GetProperty("format").GetString() != "safetensors")
        {
            return false;
        }

        bool tokenizer = false;

        bool tokenizerConfig = false;

        bool promptTemplate = false;

        foreach (JsonElement layer in root.GetProperty("layers").EnumerateArray())
        {
            string? name = layer.TryGetProperty("name", out JsonElement nameProperty) ? nameProperty.GetString() : null;

            string? mediaType = layer.GetProperty("mediaType").GetString();

            string? digest = layer.GetProperty("digest").GetString();

            if (name == "tokenizer.json")
            {
                if (tokenizer || mediaType != "application/vnd.ollama.image.json" || digest != $"sha256:{TokenizerDigest}")
                {
                    return false;
                }

                tokenizer = true;
            }

            if (name == "tokenizer_config.json")
            {
                if (tokenizerConfig || mediaType != "application/vnd.ollama.image.json" || digest != $"sha256:{TokenizerConfigDigest}")
                {
                    return false;
                }

                tokenizerConfig = true;
            }

            if (mediaType == "application/vnd.ollama.image.template")
            {
                if (promptTemplate || digest != $"sha256:{PromptTemplateDigest}")
                {
                    return false;
                }

                promptTemplate = true;
            }

            if (name == "chat_template.jinja" || mediaType == "application/vnd.ollama.image.system")
            {
                return false;
            }
        }

        return tokenizer && tokenizerConfig && promptTemplate;
    }

    private static InferenceTextCounter? Load(string blobs)
    {
        if (ReadPinned(Path.Combine(blobs, $"sha256-{TokenizerConfigDigest}"), 65_536, TokenizerConfigDigest) is null
            || ReadPinned(Path.Combine(blobs, $"sha256-{PromptTemplateDigest}"), 256, PromptTemplateDigest) is null
            || ReadPinned(Path.Combine(blobs, $"sha256-{TokenizerDigest}"), TokenizerLength, TokenizerDigest)
                is not { Length: TokenizerLength } bytes)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(bytes);

        JsonElement model = document.RootElement.GetProperty("model");

        using MemoryStream vocabulary = new(Encoding.UTF8.GetBytes(model.GetProperty("vocab").GetRawText()));

        using MemoryStream merges = new();

        using (StreamWriter writer = new(merges, Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteLine("#version: 0.2");

            foreach (JsonElement entry in model.GetProperty("merges").EnumerateArray())
            {
                writer.WriteLine(entry.GetString());
            }
        }

        merges.Position = 0;

        BpeTokenizer tokenizer = BpeTokenizer.Create(vocabulary, merges);

        return new InferenceTextCounter(text => CountText(tokenizer, text));
    }

    private static byte[]? ReadPinned(string path, int maxBytes, string digest)
    {
        if (SecureFileReader.TryOpenRegularFile(path, expectedIdentity: null, out FileStream? stream, out _) != SecureFileOpenStatus.Success)
        {
            return null;
        }

        using (stream)
        {
            long length = stream!.Length;

            if (length <= 0 || length > maxBytes)
            {
                return null;
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>((int)length);

            stream.ReadExactly(bytes);

            return stream.ReadByte() == -1 && SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(digest))
                ? bytes
                : null;
        }
    }

    private static int CountText(BpeTokenizer tokenizer, string text)
    {
        string normalized;

        try
        {
            normalized = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetByteCount(text);
        }

        // .NET's regex Unicode categories operate on UTF-16. A supplementary scalar may split
        // a Qwen regex match differently, so count that match by bytes for conservative admission.
        int count = 0;

        foreach (ValueMatch match in Pretokenizer().EnumerateMatches(normalized))
        {
            ReadOnlySpan<char> span = normalized.AsSpan(match.Index, match.Length);

            byte[] bytes = Encoding.UTF8.GetBytes(span.ToString());

            if (span.ContainsAnyInRange('\ud800', '\udfff'))
            {
                count += bytes.Length;

                continue;
            }

            string remapped = string.Create(bytes.Length, bytes, static (destination, source) =>
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = ByteAlphabet[source[i]];
                }
            });

            count += tokenizer.CountTokens(remapped, considerPreTokenization: false, considerNormalization: false);
        }

        return count;
    }

    private static char[] CreateByteAlphabet()
    {
        char[] alphabet = new char[256];

        int extension = 256;

        for (int i = 0; i < alphabet.Length; i++)
        {
            alphabet[i] = (char)(i is >= 33 and <= 126 or >= 161 and <= 172 or >= 174 ? i : extension++);
        }

        return alphabet;
    }

    // The pinned asset uses NFC, then this split, then a byte-level alphabet without a prefix space.
    [GeneratedRegex(@"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?[\p{L}\p{M}]+|\p{N}| ?[^\s\p{L}\p{M}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Pretokenizer();

    private sealed record ManifestProfile(string Path, bool Qualified);
}
