using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Memory;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal sealed partial class ConclaveArchmage
{
    public bool SupportsMemory => memorySearch is not null;

    private async Task<Result<GuildLocalChildToolResult>> SearchGuildMemoryAsync(GuildLocalChildToolCall call, CancellationToken ct)
    {
        if (memorySearch is null) return UnavailableGuild();

        JsonElement arguments = call.Arguments;

        if (!GuildTaskArguments.Closed(arguments, "query", "scope", "limit", "continuation")
            || !GuildTaskArguments.Text(arguments, "query", out string? query) || string.IsNullOrWhiteSpace(query)
            || !GuildTaskArguments.OptionalText(arguments, "scope", out string? requestedScope)
            || !GuildTaskArguments.OptionalText(arguments, "continuation", out string? continuation)) return GuildTaskArguments.Invalid;

        MemorySearchScope scope = MemorySearchScope.All;

        if (requestedScope is not null && (!Enum.TryParse(requestedScope, true, out scope)
            || !Enum.IsDefined(scope) || int.TryParse(requestedScope, out _))) return GuildTaskArguments.Invalid;

        int limit = 20;

        if (arguments.TryGetProperty("limit", out JsonElement requestedLimit)
            && (requestedLimit.ValueKind != JsonValueKind.Number || !requestedLimit.TryGetInt32(out limit)
                || limit is < 1 or > 10_000)) return GuildTaskArguments.Invalid;

        long allocation = Math.Min(call.Admission.ResponseAllocationBytes,
            Math.Min(8 * 1024 * 1024, ArcanumSettingClamps.ToolOutputCapBytes(settings.CurrentValue.ResolveIntelligence().ToolOutputCapBytes)));

        if (allocation < 1024) return new Error(ErrorCodes.Guild.PayloadTooLarge, "Memory search has no usable response allocation.");

        // The private execution binding is recreated at this boundary as MCP transfer crosses a
        // server task. Neither a model argument nor SessionAttachmentToolAmbient chooses its owner.
        using IDisposable invocation = call.Admission.Registry.Parent.Store.EnterProviderInvocation(call.Admission.Registry.Parent);

        var result = await memorySearch.SearchForInvocationAsync(new(query, scope, call.ParentQuest.SessionId,
            WorkspaceId: null, Limit: limit, Continuation: continuation), allocation, ct).ConfigureAwait(false);

        if (result.IsFailure) return result.Error;

        var bounded = MemorySearchToolResponseFormatter.Format(result.Value, allocation);

        if (bounded.IsFailure) return bounded.Error;

        call.Admission.RecordCommitted(call.ParentQuest);

        return new GuildLocalChildToolResult(call.ParentQuest,
            Projection: JsonSerializer.SerializeToElement(bounded.Value, ArcanumCoreJsonContext.Default.MemorySearchResponse));
    }
}
