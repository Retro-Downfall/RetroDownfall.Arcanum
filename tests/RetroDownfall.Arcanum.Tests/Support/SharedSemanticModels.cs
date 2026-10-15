using System.Runtime.CompilerServices;

using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// One semantic model per syntax tree for the whole run. Roslyn returns a fresh model on every request, and a model binds lazily, so a scan
/// that asks for the model of every tree of the production compilations binds all of them again; the limit and registration scans are several
/// whole-tree walks, so they share the models and what each has already bound.
/// </summary>
internal static class SharedSemanticModels
{
    private static readonly ConditionalWeakTable<SyntaxTree, SemanticModel> Models = new();

    internal static SemanticModel Of(Compilation compilation, SyntaxTree tree) => Models.GetValue(tree, key => compilation.GetSemanticModel(key));
}
