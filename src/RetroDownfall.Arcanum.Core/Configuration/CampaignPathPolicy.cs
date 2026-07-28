using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Shared campaign and workspace registration path validation against
/// <see cref="SecuritySettings.CampaignRoots"/>.
/// </summary>
public static class CampaignPathPolicy
{

    public static Result<string> ValidateAndNormalizePath(
        string path,
        ArcanumSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Result<string>.Failure(new Error(ErrorCodes.Campaign.InvalidPath, "Campaign path is required."));
        }

        string normalized;

        try
        {
            normalized = Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return Result<string>.Failure(new Error(ErrorCodes.Campaign.InvalidPath, "The campaign path is invalid."));
        }

        if (!Directory.Exists(normalized))
        {
            return Result<string>.Failure(new Error(ErrorCodes.Campaign.InvalidPath, "The campaign path does not exist or is not a directory."));
        }

        string[] allowedRoots = settings.ResolveCampaignRoots();

        Result<string> allowed = WorkspaceRootPolicy.EnforceAllowedRoots(
            normalized,
            allowedRoots,
            ErrorCodes.Campaign.PathNotAllowed,
            "The campaign path is outside Arcanum:Security:CampaignRoots.");

        if (allowed.IsFailure)
        {
            return allowed;
        }

        return Result<string>.Success(normalized);
    }

}
