using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

namespace RetroDownfall.Arcanum.Tests.Support;

// The Premise Ledger. A premise is an opt-in, scan-verified closed-world fact. It is requested only by the
// Production Discovery Entry (and by the ClosedWorldPremises fixtures through DiscoverProducerSitesUnderPremises);
// every other fixture runs with None and keeps today's strict, per-site behavior. A premise that cannot be
// verified is simply not granted, so an unverifiable universe degrades to the strict behavior it replaces.
[Flags]
internal enum HostedProducerPremises : byte
{
    None = 0,

    // No subclass of SqliteConnection, SqliteCommand or SqliteTransaction exists in any analysed source, in
    // Microsoft.Data.Sqlite, or in any reference that references it. A static Sqlite-typed expression is then
    // its own exact runtime type, so the virtual SqliteConnection.CreateCommand and the Sqlite-typed cleanup
    // resolve to the reviewed framework contract. Abstract Db* types are never mapped to Sqlite.
    SqliteSealedByUse = 1,
}

internal sealed record HostedProducerPremiseLedger(
    HostedProducerPremises Requested,
    HostedProducerPremises Granted,
    IReadOnlyList<HostedProducerInventoryDiagnostic> Violations)
{
    internal static HostedProducerPremiseLedger None { get; } = new(HostedProducerPremises.None, HostedProducerPremises.None, []);

    internal bool Holds(HostedProducerPremises premise) => premise != HostedProducerPremises.None && (Granted & premise) == premise;
}

internal static partial class HostedGrimoireProducerInventory
{
    private static readonly string[] SqliteSealedFamily =
    [
        "Microsoft.Data.Sqlite.SqliteConnection",
        "Microsoft.Data.Sqlite.SqliteCommand",
        "Microsoft.Data.Sqlite.SqliteTransaction",
    ];

    private static int premiseVerifications;

    // Counts verifications so a test can prove a discovery verifies once and shares the verdict.
    internal static int PremiseVerifications => Volatile.Read(ref premiseVerifications);

    // An unbudgeted, finite enumeration of every type of the assemblies that can hold a subclass. It never
    // consults the traversal budgets, and a premise it cannot decide is not granted.
    internal static HostedProducerPremiseLedger VerifyPremises(IReadOnlyList<CSharpCompilation> compilations, HostedProducerPremises requested)
    {
        _ = Interlocked.Increment(ref premiseVerifications);

        HostedProducerPremises granted = HostedProducerPremises.None;

        List<HostedProducerInventoryDiagnostic> violations = [];

        if ((requested & HostedProducerPremises.SqliteSealedByUse) != 0)
        {
            string[] findings = VerifySqliteSealedByUse(compilations);

            if (findings.Length == 0)
            {
                granted |= HostedProducerPremises.SqliteSealedByUse;
            }
            else
            {
                violations.AddRange(findings.Select(static finding => new HostedProducerInventoryDiagnostic("HOSTED_PREMISE_VIOLATED", "SqliteSealedByUse", finding)));
            }
        }

        return new(requested, granted, violations);
    }

    private static string[] VerifySqliteSealedByUse(IReadOnlyList<CSharpCompilation> compilations)
    {
        string sqliteAssembly = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Name!;

        HashSet<string> findings = new(StringComparer.Ordinal);

        foreach (CSharpCompilation compilation in compilations)
        {
            List<IAssemblySymbol> resolved = [];

            foreach (MetadataReference reference in compilation.References)
            {
                ISymbol? bound = compilation.GetAssemblyOrModuleSymbol(reference);

                if (bound is IAssemblySymbol assembly)
                {
                    resolved.Add(assembly);
                }
                else if (bound is not null)
                {
                    // A module reference contributes types to the assembly yet cannot be enumerated as one.
                    findings.Add($"Reference '{reference.Display}' of '{compilation.AssemblyName}' is a module, not an assembly whose types can be enumerated (unresolved reference).");
                }

                // A reference the compilation never bound (a duplicate of an assembly identity it already
                // holds) contributes no symbol, so it cannot hold a subclass any analysed code can see.
            }

            bool sqliteResolved = resolved.Any(assembly => assembly.Identity.Name == sqliteAssembly);

            List<IAssemblySymbol> scanned = [compilation.Assembly];

            foreach (IAssemblySymbol assembly in resolved)
            {
                // A source assembly of an analysed compilation is scanned once, as that compilation's own assembly.
                if (compilations.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.Assembly, assembly)))
                {
                    continue;
                }

                bool referencesSqlite = assembly.Modules.Any(module => module.ReferencedAssemblies.Any(identity => identity.Name == sqliteAssembly));

                if (assembly.Identity.Name == sqliteAssembly || referencesSqlite)
                {
                    scanned.Add(assembly);
                }

                if (referencesSqlite && !sqliteResolved)
                {
                    findings.Add($"Reference '{assembly.Identity.Name}' of '{compilation.AssemblyName}' references {sqliteAssembly}, which the analysed compilation does not resolve (unresolved reference).");
                }
            }

            foreach (IAssemblySymbol assembly in scanned)
            {
                foreach (INamedTypeSymbol type in SourceTypes(assembly.GlobalNamespace))
                {
                    if (type.TypeKind != TypeKind.Class)
                    {
                        continue;
                    }

                    for (INamedTypeSymbol? parent = type.BaseType; parent is not null; parent = parent.BaseType)
                    {
                        if (parent.TypeKind == TypeKind.Error)
                        {
                            findings.Add($"'{type.ToDisplayString()}' of '{assembly.Identity.Name}' has an unresolved base type, so derivation from a Sqlite family type cannot be excluded (unresolved reference).");

                            break;
                        }

                        if (SqliteSealedFamily.Contains(TypeKey(parent), StringComparer.Ordinal))
                        {
                            findings.Add($"'{type.ToDisplayString()}' of '{assembly.Identity.Name}' derives from {TypeKey(parent)}, so a virtual call on a Sqlite-typed receiver may dispatch to it.");

                            break;
                        }
                    }
                }
            }
        }

        return [.. findings.Order(StringComparer.Ordinal)];
    }

    private sealed partial class ProducerGraph
    {
        private bool SqliteSealedByUse => premises.Holds(HostedProducerPremises.SqliteSealedByUse);

        // A static type that is exactly one of the three Sqlite family classes. Under SqliteSealedByUse it is
        // also the exact runtime type of every value of that static type. Abstract DbConnection, DbCommand,
        // DbTransaction and DbDataReader are never accepted here.
        private static bool IsSqliteSealedFamilyClass(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } named
            && SqliteSealedFamily.Contains(TypeKey(named), StringComparer.Ordinal)
            && FrameworkAssemblyIdentityMatches(named.ContainingAssembly.Identity, typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName())
            && !named.Locations.Any(static location => location.IsInSource);
    }
}
