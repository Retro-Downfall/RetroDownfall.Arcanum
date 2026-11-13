using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.SqliteReaderSmoke;

internal static class EnumComparerProbe
{
    internal static void Run()
    {
        Verify(MessageRoleValueComparer.Default, MessageRoleValueComparer.DefaultWithStructuralComparisons,
            MessageRole.User, MessageRole.Assistant, (MessageRole)(-7), expectedHash: -7);

        Verify(WorkspaceTypeValueComparer.Default, WorkspaceTypeValueComparer.DefaultWithStructuralComparisons,
            WorkspaceType.Spell, WorkspaceType.Custom, (WorkspaceType)91, expectedHash: 91);
    }

    private static void Verify<TEnum>(ValueComparer comparer, ValueComparer structuralComparer, TEnum left, TEnum right, TEnum undefined, int expectedHash)
        where TEnum : struct, Enum
    {
        if (!ReferenceEquals(comparer, structuralComparer)
            || comparer.Type != typeof(TEnum)
            || !comparer.Equals(undefined, undefined)
            || comparer.Equals(left, right)
            || !comparer.Equals(null, null)
            || comparer.Equals(left, null)
            || comparer.Equals(null, right)
            || comparer.GetHashCode(undefined) != expectedHash
            || comparer.GetHashCode(null) != 0
            || comparer.Snapshot(null) is not null
            || comparer.Snapshot(undefined) is not TEnum snapshot
            || !snapshot.Equals(undefined))
        {
            throw new InvalidDataException("Enum comparison, null, undefined-value or snapshot behavior changed.");
        }

        Func<TEnum, TEnum, bool> equal = ((Expression<Func<TEnum, TEnum, bool>>)comparer.EqualsExpression)
            .Compile(preferInterpretation: true);

        Func<TEnum, int> hash = ((Expression<Func<TEnum, int>>)comparer.HashCodeExpression)
            .Compile(preferInterpretation: true);

        Func<TEnum, TEnum> copy = ((Expression<Func<TEnum, TEnum>>)comparer.SnapshotExpression)
            .Compile(preferInterpretation: true);

        Func<object?, object?, bool> objectEqual = ((Expression<Func<object?, object?, bool>>)comparer.ObjectEqualsExpression)
            .Compile(preferInterpretation: true);

        if (!equal(undefined, undefined) || equal(left, right) || hash(undefined) != expectedHash
            || !copy(undefined).Equals(undefined) || !objectEqual(null, null)
            || objectEqual(left, null) || objectEqual(null, right) || objectEqual(left, right)
            || !objectEqual(undefined, undefined))
        {
            throw new InvalidDataException("Enum comparison expressions changed their value semantics.");
        }
    }
}
