using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

internal static class SkillJsonBoundsValidator
{

    public static string? Validate(SkillMetadata? metadata, int maxDeclaredTools)
    {

        if (metadata is null)
        {

            return null;

        }

        if ((metadata.DeclaredTools?.Count ?? 0) > maxDeclaredTools)
        {

            return $"declaredTools exceeds the maximum of {maxDeclaredTools}.";

        }

        return null;

    }

    public static string? ValidateCreate(CreateSpellRequest request, int maxDeclaredTools)
    {

        int toolsCount = request.DeclaredTools?.Length ?? 0;

        if (toolsCount > maxDeclaredTools)
        {

            return $"declaredTools exceeds the maximum of {maxDeclaredTools}.";

        }

        return null;

    }

    public static string? ValidateUpdate(UpdateSpellRequest request, int maxDeclaredTools)
    {

        if (request.DeclaredTools is { } tools && tools.Length > maxDeclaredTools)
        {

            return $"declaredTools exceeds the maximum of {maxDeclaredTools}.";

        }

        return null;

    }

}
