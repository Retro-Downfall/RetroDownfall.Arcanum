using System.Buffers.Binary;
using System.Net;
using System.Security;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Tests.Packaging;

internal static class LocalOllamaQualificationGuards
{
    public const string VisionAttachmentName = "opaque-input.dat";

    public const string VisionPrompt =
        "The current pinned durable-fact file supersedes the older token in this transcript. "
        + "Inspect the explicitly attached image and recall the conversation marker. "
        + "Reply with exactly MARKER=<conversation marker>; TOKEN=<corrected file token>; "
        + "OBJECT=<object>; COLOR=<dominant object color>; SHAPE=<shape>; "
        + "SIDES=<number of sides>; TEXT=<visible text> and no other text.";

    private static readonly string[] ForbiddenVisionAnswerTokens =
    [
        "stop",
        "sign",
        "red",
        "octagon",
        "octagonal",
        "eight",
        "8",
    ];

    public static bool IsGitHubActions(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    public static bool IsLocalOllamaEndpoint(string? raw, out Uri? endpoint)
    {
        endpoint = null;

        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        string literalHost = parsed.Host.Trim('[', ']');

        if (parsed.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(literalHost, out IPAddress? address)
            || !IPAddress.IsLoopback(address)
            || parsed.Port <= 0
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment)
            || parsed.AbsolutePath is not ("/v1" or "/v1/"))
        {
            return false;
        }

        endpoint = parsed.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? parsed
            : new Uri(parsed.AbsoluteUri + "/");
        return true;
    }

    /// <summary>
    /// The real-model qualifier may contact only the literal loopback endpoint it validated. A
    /// redirect or ambient proxy would turn that local preflight into an unreviewed network hop.
    /// </summary>
    public static SocketsHttpHandler CreateLoopbackOnlyHttpHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            UseCookies = false,
        };

    public static async Task<byte[]?> ReadCappedResponseBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (content.Headers.ContentLength is { } declaredLength
            && declaredLength > maxBytes)
        {
            return null;
        }

        await using Stream stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using MemoryStream output = new(Math.Min(maxBytes, 81_920));

        byte[] buffer = new byte[Math.Min(maxBytes, 81_920)];

        int total = 0;

        while (true)
        {
            int read = await stream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return output.ToArray();
            }

            if (total > maxBytes - read)
            {
                return null;
            }

            await output
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            total += read;
        }
    }

    public static bool IsQualifiedImage(string? path, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);

            if (stream.Length is < 4 or > 20L * 1024L * 1024L)
            {
                return false;
            }

            Span<byte> prefix = stackalloc byte[3];

            if (stream.Read(prefix) != prefix.Length
                || prefix[0] != 0xFF
                || prefix[1] != 0xD8
                || prefix[2] != 0xFF)
            {
                return false;
            }

            stream.Position = 0;
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsExpectedVisionAnswer(
        string? answer,
        string conversationMarker,
        string correctedToken)
    {
        if (string.IsNullOrEmpty(answer)
            || string.IsNullOrEmpty(conversationMarker)
            || string.IsNullOrEmpty(correctedToken))
        {
            return false;
        }

        string[] fields = answer.Split("; ", StringSplitOptions.None);

        return fields.Length == 7
            && string.Equals(fields[0], $"MARKER={conversationMarker}", StringComparison.Ordinal)
            && string.Equals(fields[1], $"TOKEN={correctedToken}", StringComparison.Ordinal)
            && IsExpectedVisionField(fields[2], "OBJECT", "STOP SIGN")
            && IsExpectedVisionField(fields[3], "COLOR", "RED")
            && IsExpectedVisionField(fields[4], "SHAPE", "OCTAGON", "OCTAGONAL")
            && IsExpectedVisionField(fields[5], "SIDES", "8")
            && IsExpectedVisionField(fields[6], "TEXT", "STOP");
    }

    public static bool IsNativeBinary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            FileInfo file = new(path);

            if (!file.Exists
                || file.Length < 4
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || file.LinkTarget is not null)
            {
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[4];

            if (stream.Read(prefix) != prefix.Length
                || (prefix[0] == (byte)'#' && prefix[1] == (byte)'!'))
            {
                return false;
            }

            return IsMachOMagic(prefix) || IsPeImage(stream, prefix);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or SecurityException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsExpectedVisionField(
        string field,
        string name,
        params ReadOnlySpan<string> expectedValues)
    {
        string prefix = name + "=";

        if (!field.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> value = field.AsSpan(prefix.Length);

        foreach (string expectedValue in expectedValues)
        {
            if (value.Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsVisionAnswerLeakage(params string?[] providerVisibleValues)
    {
        foreach (string? value in providerVisibleValues)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            ReadOnlySpan<char> remaining = value.AsSpan();

            while (!remaining.IsEmpty)
            {
                int tokenStart = 0;

                while (tokenStart < remaining.Length
                    && !char.IsLetterOrDigit(remaining[tokenStart]))
                {
                    tokenStart++;
                }

                remaining = remaining[tokenStart..];

                if (remaining.IsEmpty)
                {
                    break;
                }

                int tokenLength = 0;

                while (tokenLength < remaining.Length
                    && char.IsLetterOrDigit(remaining[tokenLength]))
                {
                    tokenLength++;
                }

                ReadOnlySpan<char> token = remaining[..tokenLength];

                foreach (string forbidden in ForbiddenVisionAnswerTokens)
                {
                    if (token.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                remaining = remaining[tokenLength..];
            }
        }

        return false;
    }

    public static string Redact(
        string value,
        string providerKey,
        string? masterApiKey)
    {
        ArgumentNullException.ThrowIfNull(value);

        List<string> secrets = [];

        if (!string.IsNullOrEmpty(providerKey))
        {
            secrets.Add(providerKey);
        }

        if (!string.IsNullOrEmpty(masterApiKey)
            && !string.Equals(masterApiKey, providerKey, StringComparison.Ordinal))
        {
            secrets.Add(masterApiKey);
        }

        secrets.Sort(static (left, right) => right.Length.CompareTo(left.Length));

        const string preferredReplacement = "[redacted]";

        string replacement = secrets.Any(
            secret => preferredReplacement.Contains(secret, StringComparison.Ordinal))
                ? string.Empty
                : preferredReplacement;

        string redacted = value;

        foreach (string secret in secrets)
        {
            redacted = redacted.Replace(secret, replacement, StringComparison.Ordinal);
        }

        return redacted;
    }

    public static InvalidOperationException CreateSanitizedFailure(
        string context,
        Exception failure,
        string providerKey,
        string? masterApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentNullException.ThrowIfNull(failure);

        string failureType = failure.GetType().FullName ?? failure.GetType().Name;
        string safeContext = Redact(context, providerKey, masterApiKey);
        string detail = Redact(failure.Message, providerKey, masterApiKey);

        return new InvalidOperationException(
            $"{safeContext} Failure type: {failureType}. {detail}");
    }

    private static bool IsMachOMagic(ReadOnlySpan<byte> prefix) =>
        BinaryPrimitives.ReadUInt32BigEndian(prefix) is
            0xFEEDFACEu
                or 0xCEFAEDFEu
                or 0xFEEDFACFu
                or 0xCFFAEDFEu
                or 0xCAFEBABEu
                or 0xBEBAFECAu
                or 0xCAFEBABFu
                or 0xBFBAFECAu;

    private static bool IsPeImage(
        FileStream stream,
        ReadOnlySpan<byte> prefix)
    {
        if (prefix[0] != (byte)'M' || prefix[1] != (byte)'Z')
        {
            return false;
        }

        stream.Position = 0x3C;
        Span<byte> offsetBytes = stackalloc byte[4];

        if (stream.Read(offsetBytes) != offsetBytes.Length)
        {
            return false;
        }

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(offsetBytes);

        if (peOffset < 0 || peOffset > stream.Length - 4)
        {
            return false;
        }

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];

        return stream.Read(signature) == signature.Length
            && signature[0] == (byte)'P'
            && signature[1] == (byte)'E'
            && signature[2] == 0
            && signature[3] == 0;
    }

    public static void DeleteDirectoryWithRetries(string path) =>
        DeleteDirectoryWithRetries(
            path,
            Directory.Exists,
            static (directory, recursive) => Directory.Delete(directory, recursive),
            Thread.Sleep);

    internal static void DeleteDirectoryWithRetries(
        string path,
        Func<string, bool> directoryExists,
        Action<string, bool> deleteDirectory,
        Action<TimeSpan> delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        ArgumentNullException.ThrowIfNull(directoryExists);

        ArgumentNullException.ThrowIfNull(deleteDirectory);

        ArgumentNullException.ThrowIfNull(delay);

        Exception? lastFailure = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!directoryExists(path))
                {
                    return;
                }

                deleteDirectory(path, true);

                if (!directoryExists(path))
                {
                    return;
                }

                lastFailure = new IOException(
                    $"Temporary directory still exists after cleanup attempt {attempt}: {path}");
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                lastFailure = exception;
            }

            if (attempt < 3)
            {
                delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        throw new IOException(
            $"Could not remove local qualification temporary directory after three attempts: {path}",
            lastFailure);
    }
}
