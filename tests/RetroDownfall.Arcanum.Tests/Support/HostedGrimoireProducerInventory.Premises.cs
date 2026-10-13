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

    // No subclass of SqliteConnection, SqliteCommand or SqliteTransaction exists in any analysed source or in
    // any reference the analysed compilations resolve. A static Sqlite-typed expression is then its own exact
    // runtime type, so the virtual SqliteConnection.CreateCommand and the Sqlite-typed cleanup resolve to the
    // reviewed framework contract. Abstract Db* types are never mapped to Sqlite.
    SqliteSealedByUse = 1,

    // No type declared in an analysed source assembly implements an enumeration contract or declares a
    // GetEnumerator or GetAsyncEnumerator member. A value statically typed as a framework collection interface
    // (IEnumerable<T>, IReadOnlyList<T>, ...) then has no authored runtime type, so enumerating it reaches no
    // authored enumeration member. Compiler-generated iterators stay traversed where they are produced.
    AuthoredEnumerablesAbsentByUse = 2,
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

    // An unbudgeted, finite enumeration of every type of every assembly the analysed compilations resolve. It
    // never consults the traversal budgets, and a premise it cannot decide is not granted.
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

        if ((requested & HostedProducerPremises.AuthoredEnumerablesAbsentByUse) != 0)
        {
            string[] findings = VerifyAuthoredEnumerablesAbsent(compilations);

            if (findings.Length == 0)
            {
                granted |= HostedProducerPremises.AuthoredEnumerablesAbsentByUse;
            }
            else
            {
                violations.AddRange(findings.Select(static finding => new HostedProducerInventoryDiagnostic("HOSTED_PREMISE_VIOLATED", "AuthoredEnumerablesAbsentByUse", finding)));
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

            List<MetadataReference> unbound = [];

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
                else
                {
                    unbound.Add(reference);
                }
            }

            // A reference that binds no symbol is either an image the compiler could not load or an image whose
            // identity the compiler already holds from another reference. Only the second contributes nothing
            // analysed code can see; an image whose identity cannot be read, or is not exactly a loaded
            // assembly's, is unresolved.
            foreach (MetadataReference reference in unbound)
            {
                if (!IsExactDuplicateOfLoadedAssembly(reference, resolved))
                {
                    findings.Add($"Reference '{reference.Display}' of '{compilation.AssemblyName}' was not loaded by the compilation and is not an exact duplicate of a loaded assembly, so its types cannot be enumerated (unresolved reference).");
                }
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

                // Every resolved reference is scanned, not only the ones that name the provider. A subclass
                // whose base is reached through a type-forwarding facade names only the facade in its own
                // metadata, yet the compilation resolves that base to the provider type.
                scanned.Add(assembly);

                bool referencesSqlite = assembly.Modules.Any(module => module.ReferencedAssemblies.Any(identity => identity.Name == sqliteAssembly));

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

    // True when the reference's own metadata names exactly the identity (name, version, culture, public key
    // token) of an assembly the compilation loaded. An image that cannot be read is never a duplicate.
    private static bool IsExactDuplicateOfLoadedAssembly(MetadataReference reference, IReadOnlyList<IAssemblySymbol> loaded)
    {
        System.Reflection.AssemblyName? declared = TryReadDeclaredIdentity(reference);

        if (declared is null)
        {
            return false;
        }

        byte[] token = declared.GetPublicKeyToken() ?? [];

        foreach (IAssemblySymbol assembly in loaded)
        {
            AssemblyIdentity identity = assembly.Identity;

            if (string.Equals(identity.Name, declared.Name, StringComparison.Ordinal)
                && identity.Version == declared.Version
                && string.Equals(identity.CultureName, declared.CultureName ?? string.Empty, StringComparison.Ordinal)
                && !identity.PublicKeyToken.IsDefault
                && identity.PublicKeyToken.AsSpan().SequenceEqual(token))
            {
                return true;
            }
        }

        return false;
    }

    private static System.Reflection.AssemblyName? TryReadDeclaredIdentity(MetadataReference reference)
    {
        try
        {
            if (reference is PortableExecutableReference portable
                && portable.GetMetadata() is AssemblyMetadata assemblyMetadata
                && assemblyMetadata.GetModules() is { Length: > 0 } modules)
            {
                return modules[0].GetMetadataReader().GetAssemblyDefinition().GetAssemblyName();
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException or IOException)
        {
            // An image the compiler could not load cannot be read here either.
        }

        return null;
    }

    private sealed partial class ProducerGraph
    {
        private bool SqliteSealedByUse => premises.Holds(HostedProducerPremises.SqliteSealedByUse);

        private bool AuthoredEnumerablesAbsentByUse => premises.Holds(HostedProducerPremises.AuthoredEnumerablesAbsentByUse);

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
