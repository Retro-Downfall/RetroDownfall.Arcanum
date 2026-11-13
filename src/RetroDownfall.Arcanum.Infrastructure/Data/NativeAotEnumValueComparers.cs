using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Closed scalar comparers preserve EF's typed snapshot contract while containing its enum metadata
/// annotation. The generic EF base roots all public enum methods, including Enum.GetValues(Type),
/// even though these fixed equality/hash/identity expressions never invoke that method.
/// </summary>
internal sealed class MessageRoleValueComparer : ValueComparer<MessageRole>
{
    public static new MessageRoleValueComparer Default { get; } = new();

    public static new MessageRoleValueComparer DefaultWithStructuralComparisons => Default;

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:System.Enum.GetValues(System.Type)",
        Justification = "The closed MessageRole generic-base annotation roots Enum.GetValues(Type) metadata; this empty constructor only supplies scalar equality, integer hash and identity snapshot expressions. Constructor syntax is guarded and actual Native AOT tracking/persistence exercises the closed enum.")]
    private MessageRoleValueComparer()
        : base(static (left, right) => left == right, static value => (int)value, static value => value)
    {
    }
}

internal sealed class WorkspaceTypeValueComparer : ValueComparer<WorkspaceType>
{
    public static new WorkspaceTypeValueComparer Default { get; } = new();

    public static new WorkspaceTypeValueComparer DefaultWithStructuralComparisons => Default;

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:System.Enum.GetValues(System.Type)",
        Justification = "The closed WorkspaceType generic-base annotation roots Enum.GetValues(Type) metadata; this empty constructor only supplies scalar equality, integer hash and identity snapshot expressions. Constructor syntax is guarded and actual Native AOT tracking/persistence exercises the closed enum.")]
    private WorkspaceTypeValueComparer()
        : base(static (left, right) => left == right, static value => (int)value, static value => value)
    {
    }
}
