using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildWorkspaceMutationOutput(string Text, ImmutableArray<string> ChangedPaths);

internal sealed record GuildWorkspaceTextMutationResult(string Status, string Path, int? Replacements = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildWorkspaceTextMutationResult))]
internal sealed partial class GuildWorkspaceMutationJsonContext : JsonSerializerContext;

internal static class GuildWorkspaceMutationTools
{
    internal static async Task<Result<GuildWorkspaceMutationOutput>> ExecuteAsync(GuildChildToolAdmission call,
        GuildWorkspaceMutationAdmission bound, IGuildWorkspaceMutationRuntime runtime, ArcanumSettings settings,
        CancellationToken ct)
    {
        string root = bound.Physical.Registration.RootPath;

        CodingToolsSettings codingTools = settings.ResolveCodingTools();

        WorkspacePatchSettings patchSettings = codingTools.Patch;

        Result<Mutation> parsed = Parse(call, patchSettings, ct);

        if (parsed.IsFailure) return parsed.Error;

        Mutation request = parsed.Value;

        if (request.Paths.IsDefaultOrEmpty || request.Paths.Any(path => !GuildWorkspaceAuthority.IsDirectFilePath(path)
            || !bound.Grant.Workspaces.Any(scope => Covers(scope.RelativeRoot, path)))) return Forbidden;

        Result live = await call.Registry.Parent.Store.RevalidateWorkspaceMutationToolAsync(call, ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        Result physical = await bound.Physical.ValidateAsync(bound.Grant, ct).ConfigureAwait(false);

        if (physical.IsFailure) return physical.Error;

        WorkspaceLeaseIdentityResolver resolver = new();

        var preflight = await resolver.ResolveAsync(root, [.. request.Paths.Select(path => new GuildPathScope(path, GuildPathScopeKind.File))], ct).ConfigureAwait(false);

        if (preflight.IsFailure) return preflight.Error;

        bool allowProtectedWrites = codingTools.AllowProtectedPathWrites;

        using IDisposable invocation = bound.Fence.EnterInvocation(bound.Authority, bound.WorkspaceId, call,
            preflight.Value, $"{call.LogicalCallId:N}:{call.ArgumentsDigest}");

        try
        {
            if (request.Patch is { } patch)
            {
                int smallestResult = JsonSerializer.SerializeToUtf8Bytes(new MinimalStructuredToolResultEnvelope(),
                    McpJsonSerializerContext.Default.MinimalStructuredToolResultEnvelope).Length;

                if (call.ResponseAllocationBytes < smallestResult) return TooLarge;

                ApplyPatchToolExecutionService executor = new(root, patchSettings, call.ResponseAllocationBytes,
                    McpJsonSerializerContext.Default, allowProtectedPathWrites: allowProtectedWrites);

                var result = await runtime.ExecutePatchAsync(call, bound.SessionId, bound.Campaign,
                    async (context, token) =>
                    {
                        Result current = await call.Registry.Parent.Store.RevalidateWorkspaceMutationToolAsync(call, token).ConfigureAwait(false);

                        if (current.IsFailure) throw new WorkspaceMutationFenceException(current.Error);

                        current = await resolver.ValidateAsync(preflight.Value, token).ConfigureAwait(false);

                        if (current.IsFailure) throw new WorkspaceMutationFenceException(current.Error);

                        return await executor.ExecuteAsync(patch, context, token).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);

                if (result.IsFailure) return result.Error;

                if (result.Value.RequiresReconciliation) return ReconciliationRequired;

                return new GuildWorkspaceMutationOutput(result.Value.SerializedResult, result.Value.WorkspaceChanged ? request.Paths : []);
            }

            ResourceLimits limits = await runtime.GetResourceLimitsAsync(root, ct).ConfigureAwait(false);

            return await ApplyTextAsync(request, bound, root, limits, allowProtectedWrites, call.ResponseAllocationBytes, ct).ConfigureAwait(false);
        }
        catch (WorkspaceMutationFenceException exception)
        {
            return exception.Error;
        }
        catch (AggregateException)
        {
            return ReconciliationRequired;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The authorized workspace file operation failed.");
        }
    }

    private static async Task<Result<GuildWorkspaceMutationOutput>> ApplyTextAsync(Mutation request, GuildWorkspaceMutationAdmission bound,
        string root, ResourceLimits limits, bool allowProtectedWrites, long outputAllocation, CancellationToken ct)
    {
        string path = request.Paths[0];

        string absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

        long maximumBytes = (long)limits.MaxFileWriteMb * 1024 * 1024;

        string text = request.Text!;

        FileContentBaseline? baseline = null;

        int? replacements = null;

        if (request.Edit is { } edit)
        {
            var scoped = await bound.Physical.OpenDirectReadScopeAsync(bound.Grant, bound.WorkspaceId, ct).ConfigureAwait(false);

            if (scoped.IsFailure) return scoped.Error;

            using var opened = scoped.Value.OpenFile(path);

            if (opened is null) return Forbidden;

            var read = await SecureFileReader.ReadUtf8TextAsync(opened.Stream,
                (int)ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes, ct, captureBaseline: true).ConfigureAwait(false);

            if (read.Status != SecureFileReadStatus.Success || read.Text is not { } source || read.Baseline is null || !opened.Matches())
                return new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The authorized edit source is unavailable.");

            int count = ArcanumInternalToolServer.CountOccurrences(source, edit.ExactSearchText);

            if (count == 0 || count > 1 && edit.ReplaceAll != true)
                return new Error(ErrorCodes.Guild.InvalidRequest, count == 0 ? "The exact text was not found." : "The exact text has multiple matches. Use replaceAll or give a unique block.");

            long projected = Encoding.UTF8.GetByteCount(source) + count *
                (Encoding.UTF8.GetByteCount(edit.ReplacementText) - (long)Encoding.UTF8.GetByteCount(edit.ExactSearchText));

            if (projected > maximumBytes) return TooLarge;

            text = source.Replace(edit.ExactSearchText, edit.ReplacementText, StringComparison.Ordinal);

            baseline = read.Baseline;

            replacements = count;
        }

        if (Encoding.UTF8.GetByteCount(text) > maximumBytes) return TooLarge;

        string output = JsonSerializer.Serialize(new GuildWorkspaceTextMutationResult("ok", path, replacements),
            GuildWorkspaceMutationJsonContext.Default.GuildWorkspaceTextMutationResult);

        if (Encoding.UTF8.GetByteCount(output) > outputAllocation) return TooLarge;

        var written = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(root, absolute, text, ct, baseline, allowProtectedWrites).ConfigureAwait(false);

        if (!written.Success)
        {
            string failure = written.Error?.Content.FirstOrDefault()?.Text ?? "";

            int separator = failure.IndexOf(':');

            if (failure.StartsWith("Guild.", StringComparison.Ordinal) && separator > 0)
                return new Error(failure[..separator], "The workspace writer could not complete its authorized effect.");

            return new Error(ErrorCodes.Guild.Forbidden, "The workspace writer declined the requested physical effect.");
        }

        return new GuildWorkspaceMutationOutput(output, request.Paths);
    }

    private static Result<Mutation> Parse(GuildChildToolAdmission admission, WorkspacePatchSettings settings, CancellationToken ct)
    {
        JsonElement arguments = admission.Arguments;

        McpJsonSerializerContext json = McpJsonSerializerContext.Default;

        try
        {
            if (admission.Tool.Name == "write_file" && GuildTaskArguments.Closed(arguments, "relativePath", "content")
                && JsonSerializer.Deserialize(arguments, json.WriteFileParams) is { RelativePath: { } path, Content: { } text })
                return new Mutation([path], text, null, null);

            if (admission.Tool.Name == "edit_file" && GuildTaskArguments.Closed(arguments, "relativePath", "exactSearchText", "replacementText", "replaceAll")
                && JsonSerializer.Deserialize(arguments, json.ReplaceTextBlockParams) is { RelativePath: { } edited, ExactSearchText.Length: > 0, ReplacementText: not null } edit)
                return new Mutation([edited], null, edit, null);

            if (admission.Tool.Name == "apply_patch" && GuildTaskArguments.Closed(arguments, "patch", "dryRun")
                && JsonSerializer.Deserialize(arguments, json.ApplyPatchParams) is { Patch: { } diff } request)
            {
                UnifiedDiffParseResult parsed = UnifiedDiffParser.Parse(diff, settings, ct);

                if (parsed.Success) return new Mutation([.. parsed.Manifest!.NormalizedPaths], null, null, request);
            }
        }
        catch (JsonException)
        {
            return GuildTaskArguments.Invalid;
        }

        return GuildTaskArguments.Invalid;
    }

    private static bool Covers(string directory, string path) => directory is "" or "."
        || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "The target is outside the Quest's granted physical write scope.");

    private static Error TooLarge => new(ErrorCodes.Guild.PayloadTooLarge, "The mutation exceeds the workspace write allocation.");

    private static Error ReconciliationRequired => new(ErrorCodes.Guild.ReconciliationRequired, "The workspace mutation requires reconciliation.");

    private sealed record Mutation(ImmutableArray<string> Paths, string? Text, ReplaceTextBlockParams? Edit, ApplyPatchParams? Patch);
}
