using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal static class SkillJsonBoundsValidator
{

    public static string? Validate(SkillMetadata? metadata, int maxDependencies, int maxDeclaredTools)
    {

        if (metadata is null)
        {

            return null;

        }

        if ((metadata.Dependencies?.Count ?? 0) > maxDependencies)
        {

            return $"dependencies exceeds the maximum of {maxDependencies}.";

        }

        if ((metadata.DeclaredTools?.Count ?? 0) > maxDeclaredTools)
        {

            return $"declaredTools exceeds the maximum of {maxDeclaredTools}.";

        }

        return null;

    }

    public static string? ValidateCreate(CreateSpellRequest request, int maxDependencies, int maxDeclaredTools)
    {

        int depCount = request.Dependencies?.Length ?? 0;

        if (depCount > maxDependencies)
        {

            return $"dependencies exceeds the maximum of {maxDependencies}.";

        }

        int toolsCount = request.DeclaredTools?.Length ?? 0;

        if (toolsCount > maxDeclaredTools)
        {

            return $"declaredTools exceeds the maximum of {maxDeclaredTools}.";

        }

        return null;

    }

    public static string? ValidateUpdate(UpdateSpellRequest request, int maxDependencies, int maxDeclaredTools)
    {

        if (request.Dependencies is { } deps && deps.Length > maxDependencies)
        {

            return $"dependencies exceeds the maximum of {maxDependencies}.";

        }

        if (request.DeclaredTools is { } tools && tools.Length > maxDeclaredTools)
        {

            return $"declaredTools exceeds the maximum of {maxDeclaredTools}.";

        }

        return null;

    }

}

