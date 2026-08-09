using Xunit;

/// <summary>
/// Serializes the tests that build real Avalonia controls and let them create bindings.
///
/// Avalonia's binding-expression compiler keeps process-wide mutable state:
/// <c>ExpressionNodeFactory.CreateFromAst</c> enumerates a shared node list while another thread can
/// still be adding to it, which surfaces as
/// <c>InvalidOperationException: Collection was modified; enumeration operation may not execute</c>
/// inside <c>AvaloniaObject.Bind</c>. The throw lands on whichever view test happened to be running,
/// so it reads as that one test being flaky when the cause is two classes binding at once.
///
/// Every Compendium test that constructs a view — anything reaching <c>GenericSettingsSectionView</c>,
/// <c>PresetsPage</c>, or the <c>Labeled*</c> controls — belongs here. View-model-only tests do not.
/// </summary>
[CollectionDefinition(AvaloniaBindingCollectionName.Value, DisableParallelization = true)]
public sealed class AvaloniaBindingCollection
{
}

internal static class AvaloniaBindingCollectionName
{

    internal const string Value = "AvaloniaBinding";

}
