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

using RetroDownfall.Arcanum.Core.Intelligence.Models;

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

    private static McpToolsCallResultWire? TryRejectIfWriteExceedsLimit(
        string content,
        int maxFileWriteMb,
        string toolName) =>
        TryRejectIfWriteExceedsLimit(Encoding.UTF8.GetByteCount(content), maxFileWriteMb, toolName);

    private static McpToolsCallResultWire? TryRejectIfWriteExceedsLimit(
        long byteCount,
        int maxFileWriteMb,
        string toolName)
    {
        long maxBytes = (long)maxFileWriteMb * 1024L * 1024L;

        if (byteCount <= maxBytes)
        {
            return null;
        }

        return ToolError(
            $"{toolName}: write size ({byteCount} bytes) exceeds the Sanctum MaxFileWriteMb limit ({maxFileWriteMb} MiB).", denied: true);
    }

    private McpToolsCallResultWire? TryRequireWorkspaceRoot()
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            return ToolError(WorkspaceNotConfiguredMessage, denied: true);
        }

        return null;
    }

    private static McpToolsCallResultWire?
        TryRequirePersistedToolInvocation()
    {
        if (PersistedToolInvocationAmbient.Current
            is not { IsValid: true })
        {
            return ToolError(
                "This mutating filesystem tool requires a bound persisted assistant-turn context.", denied: true);
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
            error = ToolError(ToolOutcomeCode.InvalidArguments);

            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = ToolError(ToolOutcomeCode.WorkspacePolicyDenied);

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

            error = ToolError(ToolOutcomeCode.InvalidArguments);

            return false;
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(root, resolved, out _))
        {
            error = ToolError(ToolOutcomeCode.WorkspacePolicyDenied);

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
            error = ToolError(ToolOutcomeCode.WorkspacePolicyDenied);

            return false;
        }

        return true;
    }

    private static McpToolsCallResultWire PrefixToolError(string toolName, McpToolsCallResultWire error)
    {
        string text = error.Content is { Length: > 0 } content && !string.IsNullOrEmpty(content[0].Text)
            ? $"{toolName}: {content[0].Text}"
            : $"{toolName}: operation failed.";

        return error with { Content = [new McpToolContentTextWire { Text = text }] };
    }

    private static McpToolsCallResultWire ToolError(ToolOutcomeCode code) =>
        McpToolFailure.Native(code);

    private static McpToolsCallResultWire ToolError(string text, bool denied = false) =>
        McpToolFailure.Reported(text, denied);

    private static McpToolsCallResultWire ToolError(Error error) =>
        IsToolFaultCode(error.Code)
            ? ToolFault("An internal error occurred during tool execution.")
            : ToolError(error.Message, denied: error.Code is ErrorCodes.Lexicon.RetiredMutationRefused
            or ErrorCodes.Lexicon.SuppressedNameRefused
            or ErrorCodes.Lexicon.PinnedMutationRefused
            or ErrorCodes.Lexicon.ProtectedMutationRefused
            or ErrorCodes.Covenant.ForbiddenAuthority);

    private static bool IsToolFaultCode(string code) => code is ErrorCodes.Covenant.MaintenanceFailed
        or ErrorCodes.Covenant.IntegrityFailure
        or ErrorCodes.Covenant.WriteFailed;

    private static McpToolsCallResultWire ToolFault(string text) =>
        McpToolFailure.Fault(text);

    internal static int CountOccurrences(string source, string value)
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
