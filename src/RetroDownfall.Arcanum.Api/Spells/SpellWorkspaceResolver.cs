using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Spells;

public sealed class SpellWorkspaceResolver(
    IHostWorkspaceContext hostContext,
    IOptions<ArcanumSettings> settings)
{

    public Result<string?> Resolve(string? workspaceQuery)
    {
        if (!string.IsNullOrWhiteSpace(workspaceQuery))
        {
            if (!WorkspacePathPolicy.TryNormalizeWorkspace(workspaceQuery, out string? normalized, out string? errorMessage))
            {
                return Result<string?>.Failure(new Error(ErrorCodes.Spell.InvalidWorkspace, errorMessage ?? "Invalid workspace path."));
            }

            if (!Directory.Exists(normalized))
            {
                return Result<string?>.Failure(new Error(ErrorCodes.Spell.InvalidWorkspace, "Workspace directory does not exist."));
            }

            return EnforceAllowlist(normalized);
        }

        string? fromContext = hostContext.WorkspacePath;

        if (!string.IsNullOrWhiteSpace(fromContext) && Directory.Exists(fromContext))
        {
            Result<string?> allowed = EnforceAllowlist(fromContext);

            if (allowed.IsFailure)
            {
                return allowed;
            }

            return Result<string?>.Success(fromContext);
        }

        string? configured = settings.Value.Host.Workspace;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                string fullPath = Path.GetFullPath(configured.Trim());

                if (Directory.Exists(fullPath))
                {
                    Result<string?> allowed = EnforceAllowlist(fullPath);

                    if (allowed.IsFailure)
                    {
                        return allowed;
                    }

                    return Result<string?>.Success(fullPath);
                }
            }
            catch (Exception)
            {
                // fall through
            }
        }

        try
        {
            string cwd = Path.GetFullPath(Environment.CurrentDirectory);

            if (Directory.Exists(cwd))
            {
                Result<string?> allowed = EnforceAllowlist(cwd);

                if (allowed.IsFailure)
                {
                    return allowed;
                }

                return Result<string?>.Success(cwd);
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return Result<string?>.Success(null);
    }

    public Result<string> ResolveRequired(string? workspaceQuery)
    {
        Result<string?> resolved = Resolve(workspaceQuery);

        if (resolved.IsFailure)
        {
            return Result<string>.Failure(resolved.Error);
        }

        if (string.IsNullOrWhiteSpace(resolved.Value))
        {
            return Result<string>.Failure(new Error(ErrorCodes.Spell.NoWorkspace, "A workspace directory is required for this operation."));
        }

        return Result<string>.Success(resolved.Value);
    }

    private Result<string?> EnforceAllowlist(string normalizedWorkspace)
    {
        string[] allowedRoots = settings.Value.Spells?.AllowedWorkspaceRoots ?? [];

        Result<string> allowed = WorkspaceRootPolicy.EnforceAllowedRoots(
            normalizedWorkspace,
            allowedRoots,
            ErrorCodes.Spell.PathNotAllowed,
            "The specified workspace is outside the configured Spells.AllowedWorkspaceRoots.");

        if (allowed.IsFailure)
        {
            return Result<string?>.Failure(allowed.Error);
        }

        return Result<string?>.Success(normalizedWorkspace);
    }

}
