using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Resolves only declared runtimes and inspects them without requesting model loads or inference.</summary>
public sealed class LocalRuntimeInspector(IEnumerable<ILocalRuntimeAdapter> adapters)
{
    public Task<Result<LocalRuntimeInspection>> InspectAsync(ArcanumSettings settings, string runtimeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(runtimeId) || runtimeId != runtimeId.Trim())
            return Task.FromResult(Result<LocalRuntimeInspection>.Failure(new(ErrorCodes.LocalRuntime.InvalidConfiguration, "A nonblank, unpadded runtime identity is required.")));

        LocalRuntimeSettings[] matches = (settings.LocalRuntimes ?? [])
            .Where(runtime => runtime is not null && string.Equals(runtime.Id, runtimeId, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();

        if (matches.Length == 0)
            return Task.FromResult(Result<LocalRuntimeInspection>.Failure(new(ErrorCodes.LocalRuntime.NotFound, "The configured local runtime was not found.")));

        if (matches.Length != 1)
            return Task.FromResult(Result<LocalRuntimeInspection>.Failure(new(ErrorCodes.LocalRuntime.InvalidConfiguration, "The configured runtime identity is ambiguous.")));

        LocalRuntimeSettings runtime = matches[0] with { };

        ILocalRuntimeAdapter[] implementations = adapters.Where(adapter => adapter.AdapterId == runtime.Adapter).Take(2).ToArray();

        if (!LocalRuntimeAdapterCatalog.IsKnown(runtime.Adapter) || implementations.Length != 1)
            return Task.FromResult(Result<LocalRuntimeInspection>.Failure(new(ErrorCodes.LocalRuntime.AdapterUnavailable, "The configured runtime adapter is unavailable.")));

        return implementations[0].InspectAsync(runtime, cancellationToken);
    }
}
