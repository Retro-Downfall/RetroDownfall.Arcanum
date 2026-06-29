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
        bool stripUserEnvironment,
        IReadOnlySet<string>? inheritAllowlist = null,
        Func<string, string?>? hostEnvironmentReader = null)
    {

        if (stripUserEnvironment)
        {

            return BuildChildProcessEnvironment(source, inheritAllowlist, hostEnvironmentReader);

        }

        if (source is null || source.Count == 0)
        {

            return null;

        }

        Dictionary<string, string> scrubbed = BuildChildProcessEnvironment(source, inheritAllowlist, hostEnvironmentReader);

        return scrubbed.Count == 0 ? null : scrubbed;

    }

    /// <summary>
    /// Builds the explicit environment block for an MCP child process. Blocked keys are removed
    /// unless the caller explicitly opted in via <paramref name="inheritAllowlist"/>; allowlisted
    /// names absent from <paramref name="source"/> are inherited from the host process (via
    /// <paramref name="hostEnvironmentReader"/>, defaulting to <see cref="Environment.GetEnvironmentVariable(string)"/>).
    /// Values from <paramref name="source"/> always win over inherited host values.
    /// </summary>
    public static Dictionary<string, string> BuildChildProcessEnvironment(
        IReadOnlyDictionary<string, string>? source,
        IReadOnlySet<string>? inheritAllowlist = null,
        Func<string, string?>? hostEnvironmentReader = null)
    {

        Dictionary<string, string> result = new(StringComparer.Ordinal);

        if (source is not null)
        {

            foreach (KeyValuePair<string, string> kv in source)
            {

                if (string.IsNullOrEmpty(kv.Key))
                {

                    continue;

                }

                if (IsBlockedEnvironmentVariable(kv.Key) && !IsInheritAllowed(kv.Key, inheritAllowlist))
                {

                    continue;

                }

                result[kv.Key] = kv.Value;

            }

        }

        if (inheritAllowlist is { Count: > 0 })
        {

            Func<string, string?> reader = hostEnvironmentReader ?? Environment.GetEnvironmentVariable;

            foreach (string name in inheritAllowlist)
            {

                if (string.IsNullOrEmpty(name) || result.ContainsKey(name))
                {

                    continue;

                }

                string? value = reader(name);

                if (!string.IsNullOrEmpty(value))
                {

                    result[name] = value;

                }

            }

        }

        return result;

    }

    private static bool IsInheritAllowed(string key, IReadOnlySet<string>? inheritAllowlist) =>
        inheritAllowlist is not null && inheritAllowlist.Contains(key);

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

        Encoder encoder = Encoding.UTF8.GetEncoder();

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

            int start = _bufferIndex;

            int newlineIndex = Array.IndexOf(_buffer, '\n', start, _bufferLength - start);

            int segmentLength = newlineIndex >= 0
                ? newlineIndex - start
                : _bufferLength - start;

            int segmentBytes = encoder.GetByteCount(_buffer, start, segmentLength, newlineIndex >= 0);

            if (utf8Bytes + segmentBytes > maxJsonRpcLineBytes)
            {

                await DiscardUntilNewlineAsync(cancellationToken).ConfigureAwait(false);

                throw new JsonException(
                    $"JSON-RPC line exceeds the maximum size of {maxJsonRpcLineBytes} UTF-8 bytes.");

            }

            for (int i = 0; i < segmentLength; i++)
            {

                char c = _buffer[start + i];

                if (c != '\r')
                {

                    builder.Append(c);

                }

            }

            utf8Bytes += segmentBytes;

            _bufferIndex = start + segmentLength;

            if (newlineIndex >= 0)
            {

                _bufferIndex++;

                return builder.ToString();

            }

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
