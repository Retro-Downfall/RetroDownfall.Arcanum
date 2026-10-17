using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The semantic model the Build scans read for a syntax tree: the process's one model of that tree in that compilation, the same one the
/// hosted-producer checker reads (<see cref="HostedGrimoireProducerInventory.ProcessSemanticModel"/>). Roslyn returns a fresh model on every
/// request, and a model binds lazily and keeps what it has bound, so a scan that asked for a model of its own would bind every tree of the
/// production compilations again and the test host would hold a second copy of everything bound. The limit and registration scans are several
/// whole-tree walks, so they share the models and what each has already bound; a class that is done with them lets them go
/// (<see cref="SemanticModelScope"/>).
/// </summary>
internal static class SharedSemanticModels
{
    internal static SemanticModel Of(Compilation compilation, SyntaxTree tree) =>
        HostedGrimoireProducerInventory.ProcessSemanticModel((CSharpCompilation)compilation, tree);
}
