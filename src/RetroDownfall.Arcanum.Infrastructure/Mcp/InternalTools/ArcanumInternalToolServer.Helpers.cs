using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;


namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed partial class ArcanumInternalToolServer
{

    private McpToolsCallResultWire CapToolTextResult(string text, string toolName)
    {

        long effectiveCap = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            _settings.ToolOutputCapBytes,
            _maxJsonRpcLineBytes);

        long byteCount = Encoding.UTF8.GetByteCount(text);

        if (byteCount > effectiveCap)
        {

            return ToolError(
                $"{toolName}: output too large ({byteCount} UTF-8 bytes; limit {effectiveCap}). Narrow the range and retry.");

        }

        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = false,
        };

    }

    private async Task<ResourceLimits> ResolveResourceLimitsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        ISanctumGuard sanctumGuard = scope.ServiceProvider.GetRequiredService<ISanctumGuard>();

        return await sanctumGuard
            .GetEffectiveResourceLimitsForWorkspaceAsync(_workspaceRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    private McpToolsCallResultWire? TryRejectIfFileExceedsReadLimit(string absolutePath, string toolName)
    {
        long maxBytes = _maxFileReadSizeBytes;

        try
        {
            long length = new FileInfo(absolutePath).Length;

            if (length > maxBytes)
            {
                return ToolError(
                    $"{toolName}: file size ({length} bytes) exceeds the maximum read limit ({maxBytes} bytes).");
            }
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "{ToolName}: could not inspect file size.", toolName);

            return ToolError($"{toolName}: could not inspect the file size.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "{ToolName}: access denied inspecting file size.", toolName);

            return ToolError($"{toolName}: access denied.");
        }

        return null;
    }

    private static McpToolsCallResultWire? TryRejectIfWriteExceedsLimit(
        string content,
        int maxFileWriteMb,
        string toolName)
    {
        long maxBytes = (long)maxFileWriteMb * 1024L * 1024L;

        long byteCount = Encoding.UTF8.GetByteCount(content);

        if (byteCount <= maxBytes)
        {
            return null;
        }

        return ToolError(
            $"{toolName}: write size ({byteCount} bytes) exceeds the Sanctum MaxFileWriteMb limit ({maxFileWriteMb} MiB).");
    }

    private McpToolsCallResultWire? TryRequireWorkspaceRoot()
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            return ToolError(WorkspaceNotConfiguredMessage);
        }

        return null;
    }

    private bool TryResolveSandboxedPath(
        string relativePath,
        [NotNullWhen(true)] out string? absolutePath,
        out McpToolsCallResultWire? error)
    {
        absolutePath = null;

        error = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = ToolError("A non-empty relativePath is required.");

            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = ToolError(
                $"Paths must be relative to the workspace root; rooted paths are not allowed (got: '{relativePath}').");

            return false;
        }

        string root = _workspaceRoot!;

        string resolved;

        try
        {
            resolved = Path.GetFullPath(Path.Combine(root, relativePath.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            _logger?.LogError(ex, "Path resolution failed for relative path.");

            error = ToolError("Could not resolve the specified relative path.");

            return false;
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, resolved, out _))
        {
            error = ToolError(PathEscapesSandboxMessage);

            return false;
        }

        absolutePath = resolved;

        return true;
    }

    private bool TryRevalidateBeforeIo(
        string absolutePath,
        out McpToolsCallResultWire? error)
    {
        error = null;

        if (!WorkspacePathPolicy.RevalidatePathBeforeIo(_workspaceRoot!, absolutePath))
        {
            error = ToolError(PathEscapesSandboxMessage);

            return false;
        }

        return true;
    }
    private static McpToolsCallResultWire PrefixToolError(string toolName, McpToolsCallResultWire error)
    {

        string text = error.Content is { Length: > 0 } content && !string.IsNullOrEmpty(content[0].Text)
            ? $"{toolName}: {content[0].Text}"
            : $"{toolName}: operation failed.";

        return ToolError(text);

    }

    private static McpToolsCallResultWire ToolError(string text)
    {
        return new McpToolsCallResultWire
        {
            Content =
            [
                new McpToolContentTextWire { Text = text },
            ],
            IsError = true,
        };
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;

        int index = 0;

        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;

            index += value.Length;
        }

        return count;
    }
}
