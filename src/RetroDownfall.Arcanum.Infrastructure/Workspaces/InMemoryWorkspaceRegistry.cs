using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

[ExcludeFromCodeCoverage] // Reason: in-memory workspace registry backing store; covered via InMemoryWorkspaceRegistryTests and workspace endpoint integration tests.
public sealed class InMemoryWorkspaceRegistry : IWorkspaceRegistry
{

    private readonly Lock _lock = new();

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly Dictionary<string, WorkspaceInfo> _workspacesById = new(StringComparer.Ordinal);

    public InMemoryWorkspaceRegistry(IOptionsMonitor<ArcanumSettings> settings)
    {
        _settings = settings;
    }

    public Task<WorkspaceInfo[]> GetAllAsync(CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {

            WorkspaceInfo[] result = _workspacesById.Values
                .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.FromResult(result);
        }
    }

    public Task<WorkspaceInfo?> GetAsync(string id, CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {

            _workspacesById.TryGetValue(id, out WorkspaceInfo? workspace);

            return Task.FromResult(workspace);
        }
    }

    public Task<Result<WorkspaceInfo>> RegisterAsync(CreateWorkspaceRequest request, CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Task.FromResult<Result<WorkspaceInfo>>(
                new Error("Workspace.NameEmpty", "Workspace name cannot be empty."));
        }

        string trimmedName = request.Name.Trim();

        Result<string> pathResult = CampaignPathPolicy.ValidateAndNormalizePath(request.Path, _settings.CurrentValue);

        if (pathResult.IsFailure)
        {
            return Task.FromResult<Result<WorkspaceInfo>>(MapPathValidationError(pathResult.Error));
        }

        string normalizedPath = pathResult.Value;

        lock (_lock)
        {

            foreach (WorkspaceInfo existing in _workspacesById.Values)
            {

                if (string.Equals(existing.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<Result<WorkspaceInfo>>(
                        new Error("Workspace.NameDuplicate", "A workspace with this name is already registered."));
                }

                if (WorkspaceRootPolicy.IsSamePath(existing.Path, normalizedPath))
                {
                    return Task.FromResult<Result<WorkspaceInfo>>(
                        new Error("Workspace.PathDuplicate", "A workspace with this path is already registered."));
                }
            }

            WorkspaceInfo workspace = new(
                Guid.NewGuid().ToString("N"),
                trimmedName,
                normalizedPath,
                request.Type,
                DateTimeOffset.UtcNow);

            _workspacesById[workspace.Id] = workspace;

            return Task.FromResult<Result<WorkspaceInfo>>(workspace);
        }
    }

    public Task<Result<WorkspaceInfo>> UpdateAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {

            if (!_workspacesById.TryGetValue(id, out WorkspaceInfo? existing))
            {
                return Task.FromResult<Result<WorkspaceInfo>>(
                    new Error("Workspace.NotFound", "The workspace was not found."));
            }

            string updatedName = existing.Name;

            if (request.Name is not null)
            {

                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Task.FromResult<Result<WorkspaceInfo>>(
                        new Error("Workspace.NameEmpty", "Workspace name cannot be empty."));
                }

                updatedName = request.Name.Trim();

                foreach (WorkspaceInfo other in _workspacesById.Values)
                {

                    if (string.Equals(other.Id, id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.Equals(other.Name, updatedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult<Result<WorkspaceInfo>>(
                            new Error("Workspace.NameDuplicate", "A workspace with this name is already registered."));
                    }
                }
            }

            WorkspaceType updatedType = request.Type ?? existing.Type;

            WorkspaceInfo updated = existing with
            {
                Name = updatedName,
                Type = updatedType,
            };

            _workspacesById[id] = updated;

            return Task.FromResult<Result<WorkspaceInfo>>(updated);
        }
    }

    public Task<Result<bool>> UnregisterAsync(string id, CancellationToken ct)
    {

        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {

            if (!_workspacesById.Remove(id))
            {
                return Task.FromResult<Result<bool>>(
                    new Error("Workspace.NotFound", "The workspace was not found."));
            }

            return Task.FromResult<Result<bool>>(true);
        }
    }

    private static Error MapPathValidationError(Error error) =>
        error.Code switch
        {
            "Campaign.InvalidPath" => new Error("Workspace.PathNotFound", error.Message),
            "Campaign.PathNotAllowed" => new Error("Workspace.PathNotAllowed", error.Message),
            _ => error,
        };

}
