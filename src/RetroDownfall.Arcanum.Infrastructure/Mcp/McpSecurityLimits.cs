using System.Buffers;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Hard limits and scrubbing helpers for MCP stdio JSON-RPC and <c>mcp.json</c> loading.
/// </summary>
internal static class McpSecurityLimits
{

    public const int MaxMcpConfigBytes = 256 * 1024;

    public const int MaxJsonRpcLineUtf8Bytes = 4 * 1024 * 1024;

    public const int MaxJsonDepth = 64;

    public const int ReadFileChunkMaxLinesPerRequest = 2000;

    public const int ReadFileChunkMaxStartLine = 1_000_000;

    public const int MaxMcpToolDescriptionUtf8Bytes = 8 * 1024;

    public const int MaxMcpToolInputSchemaUtf8Bytes = 64 * 1024;

    public static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    public static bool ExceedsMaxLineUtf8Bytes(string line)
    {

        return Encoding.UTF8.GetByteCount(line) > MaxJsonRpcLineUtf8Bytes;

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

    public static async Task<string?> ReadLineUtf8CappedAsync(
        StreamReader reader,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(reader);

        StringBuilder builder = new();

        long utf8Bytes = 0L;

        char[] singleChar = new char[1];

        while (true)
        {

            int read = await reader.ReadAsync(singleChar.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {

                if (builder.Length == 0)
                {

                    return null;

                }

                return builder.ToString();

            }

            char c = singleChar[0];

            if (c == '\n')
            {

                return builder.ToString();

            }

            if (c == '\r')
            {

                continue;

            }

            int charUtf8Bytes = Encoding.UTF8.GetByteCount(singleChar, 0, 1);

            if (utf8Bytes + charUtf8Bytes > MaxJsonRpcLineUtf8Bytes)
            {

                await DiscardUntilNewlineAsync(reader, cancellationToken).ConfigureAwait(false);

                throw new JsonException(
                    $"JSON-RPC line exceeds the maximum size of {MaxJsonRpcLineUtf8Bytes} UTF-8 bytes.");

            }

            builder.Append(c);

            utf8Bytes += charUtf8Bytes;

        }

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

    private static async Task DiscardUntilNewlineAsync(StreamReader reader, CancellationToken cancellationToken)
    {

        char[] singleChar = new char[1];

        while (true)
        {

            int read = await reader.ReadAsync(singleChar.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read <= 0)
            {

                return;

            }

            if (singleChar[0] == '\n')
            {

                return;

            }

        }

    }

}
