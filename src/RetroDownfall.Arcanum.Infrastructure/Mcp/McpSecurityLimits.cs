using System.Buffers;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Hard limits and scrubbing helpers for MCP stdio JSON-RPC and <c>mcp.json</c> loading.
/// </summary>
internal static class McpSecurityLimits
{

    public const int MaxMcpConfigBytes = 256 * 1024;

    public const int MaxJsonDepth = 64;

    public const int ReadFileChunkMaxLinesPerRequest = 2000;

    public const int ReadFileChunkMaxStartLine = 1_000_000;

    public const int MaxMcpToolDescriptionUtf8Bytes = 8 * 1024;

    public const int MaxMcpToolInputSchemaUtf8Bytes = 64 * 1024;

    public static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    public static bool ExceedsMaxLineUtf8Bytes(string line, int maxJsonRpcLineBytes)
    {

        return Encoding.UTF8.GetByteCount(line) > maxJsonRpcLineBytes;

    }

    public static string BoundToolDescription(string description)
    {

        if (string.IsNullOrEmpty(description))
        {

            return string.Empty;

        }

        return TruncateUtf8(description, MaxMcpToolDescriptionUtf8Bytes);

    }

    public static JsonElement BoundToolInputSchema(JsonElement schema, McpJsonSerializerContext json)
    {

        string raw = schema.GetRawText();

        if (Encoding.UTF8.GetByteCount(raw) <= MaxMcpToolInputSchemaUtf8Bytes)
        {

            return schema.Clone();

        }

        return JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), json.McpEmptyJsonObject);

    }

    public static string TruncateUtf8(string text, long maxUtf8Bytes)
    {

        if (string.IsNullOrEmpty(text) || maxUtf8Bytes <= 0L)
        {

            return string.Empty;

        }

        long byteCount = Encoding.UTF8.GetByteCount(text);

        if (byteCount <= maxUtf8Bytes)
        {

            return text;

        }

        int safeCharCount = ChooseSafeCharCount(text, maxUtf8Bytes);

        string prefix = safeCharCount <= 0 ? string.Empty : text[..safeCharCount];

        return prefix + $"\n[truncated: exceeded {maxUtf8Bytes} bytes]";

    }

    private static int ChooseSafeCharCount(string text, long maxUtf8Bytes)
    {

        long running = 0L;

        for (int i = 0; i < text.Length; i++)
        {

            int charByteSize = Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));

            if (running + charByteSize > maxUtf8Bytes)
            {

                return i;

            }

            running += charByteSize;

        }

        return text.Length;

    }

    public static IReadOnlyDictionary<string, string>? ScrubProcessEnvironment(
        IReadOnlyDictionary<string, string>? source,
        bool stripUserEnvironment)
    {

        if (stripUserEnvironment)
        {

            return BuildChildProcessEnvironment(source);

        }

        if (source is null || source.Count == 0)
        {

            return null;

        }

        Dictionary<string, string> scrubbed = BuildChildProcessEnvironment(source);

        return scrubbed.Count == 0 ? null : scrubbed;

    }

    /// <summary>
    /// Builds the explicit environment block for an MCP child process (blocked keys removed).
    /// </summary>
    public static Dictionary<string, string> BuildChildProcessEnvironment(
        IReadOnlyDictionary<string, string>? source)
    {

        Dictionary<string, string> result = new(StringComparer.Ordinal);

        if (source is null || source.Count == 0)
        {

            return result;

        }

        foreach (KeyValuePair<string, string> kv in source)
        {

            if (string.IsNullOrEmpty(kv.Key))
            {

                continue;

            }

            if (IsBlockedEnvironmentVariable(kv.Key))
            {

                continue;

            }

            result[kv.Key] = kv.Value;

        }

        return result;

    }

    public static bool IsBlockedEnvironmentVariable(string key)
    {

        if (string.IsNullOrEmpty(key))
        {

            return true;

        }

        if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.StartsWith("LD_", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("PYTHONPATH", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PERL5LIB", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RUBYLIB", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GEM_PATH", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GEM_HOME", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("JAVA_TOOL_OPTIONS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("_JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("JDK_JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("GCONV_PATH", StringComparison.OrdinalIgnoreCase)
            || key.Equals("LOCPATH", StringComparison.OrdinalIgnoreCase)
            || key.Equals("HOSTALIASES", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RES_OPTIONS", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("BASH_ENV", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ENV", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("SSLKEYLOGFILE", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("GIT_SSH_COMMAND", StringComparison.OrdinalIgnoreCase)
            || key.Equals("GIT_ASKPASS", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SSH_ASKPASS", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        if (key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("http_proxy", StringComparison.OrdinalIgnoreCase)
            || key.Equals("https_proxy", StringComparison.OrdinalIgnoreCase)
            || key.Equals("all_proxy", StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        return false;

    }

}

/// <summary>
/// Buffered, UTF-8-capped newline reader for MCP stdio that preserves unconsumed chunk data between lines.
/// </summary>
internal sealed class McpStdioLineReader
{

    private readonly StreamReader _reader;

    private char[] _buffer = new char[4096];

    private int _bufferLength;

    private int _bufferIndex;

    public McpStdioLineReader(StreamReader reader)
    {

        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;

    }

    public async Task<string?> ReadLineUtf8CappedAsync(
        int maxJsonRpcLineBytes,
        CancellationToken cancellationToken = default)
    {

        if (maxJsonRpcLineBytes < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxJsonRpcLineBytes));

        }

        StringBuilder builder = new();

        long utf8Bytes = 0L;

        while (true)
        {

            if (_bufferIndex >= _bufferLength)
            {

                _bufferLength = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

                _bufferIndex = 0;

                if (_bufferLength <= 0)
                {

                    if (builder.Length == 0)
                    {

                        return null;

                    }

                    return builder.ToString();

                }

            }

            char c = _buffer[_bufferIndex++];

            if (c == '\n')
            {

                return builder.ToString();

            }

            if (c == '\r')
            {

                continue;

            }

            int charUtf8Bytes = Encoding.UTF8.GetByteCount(new ReadOnlySpan<char>(ref c));

            if (utf8Bytes + charUtf8Bytes > maxJsonRpcLineBytes)
            {

                await DiscardUntilNewlineAsync(cancellationToken).ConfigureAwait(false);

                throw new JsonException(
                    $"JSON-RPC line exceeds the maximum size of {maxJsonRpcLineBytes} UTF-8 bytes.");

            }

            builder.Append(c);

            utf8Bytes += charUtf8Bytes;

        }

    }

    private async Task DiscardUntilNewlineAsync(CancellationToken cancellationToken)
    {

        while (true)
        {

            if (_bufferIndex >= _bufferLength)
            {

                _bufferLength = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

                _bufferIndex = 0;

                if (_bufferLength <= 0)
                {

                    return;

                }

            }

            if (_buffer[_bufferIndex++] == '\n')
            {

                return;

            }

        }

    }

}
