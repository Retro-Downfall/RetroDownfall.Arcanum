using Microsoft.EntityFrameworkCore.Metadata;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Generated;

/// <summary>
/// Stable compiled-model customizations retained when EF regenerates the optimizer-owned sources.
/// </summary>
/// <remarks>
/// <c>dotnet ef dbcontext optimize --nativeaot</c> recreates each entity file but deliberately leaves
/// implementations of its partial <c>Customize</c> hooks alone. Keeping the UTC persistence boundary
/// here makes regeneration repeatable rather than relying on edits inside generated output.
/// </remarks>
public partial class CampaignEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType)
    {
        runtimeEntityType.FindProperty("CreatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;

        runtimeEntityType.FindProperty("UpdatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;
    }
}

public partial class SessionEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType)
    {
        runtimeEntityType.FindProperty("CreatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;

        runtimeEntityType.FindProperty("UpdatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;

        runtimeEntityType.FindProperty("LastSummarizedMessageAt")!.TypeMapping = UtcDateTimeTypeMapping.Default;
    }
}

public partial class EntryEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType) =>
        runtimeEntityType.FindProperty("CreatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;
}

public partial class PromptEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType)
    {
        runtimeEntityType.FindProperty("CreatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;

        runtimeEntityType.FindProperty("UpdatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;
    }
}

public partial class ApprenticeEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType)
    {
        runtimeEntityType.FindProperty("CreatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;

        runtimeEntityType.FindProperty("UpdatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;
    }
}

public partial class WorkspaceContextEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType) =>
        runtimeEntityType.FindProperty("CreatedAt")!.TypeMapping = UtcDateTimeOffsetTypeMapping.Default;
}

public partial class MageSettingEntityType
{
    static partial void Customize(RuntimeEntityType runtimeEntityType) =>
        runtimeEntityType.FindProperty("UpdatedAt")!.TypeMapping = UtcDateTimeTypeMapping.Default;
}
