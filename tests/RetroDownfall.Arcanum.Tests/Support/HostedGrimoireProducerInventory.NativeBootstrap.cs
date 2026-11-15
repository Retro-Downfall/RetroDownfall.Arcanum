using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private const string NativeBootstrapOwner = "RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime";

    private const string NativeBootstrapFactory = "RetroDownfall.Arcanum.Infrastructure.Data.ArcanumDbContextFactory";

    private static readonly IReadOnlyDictionary<string, TypeDeclarationSyntax> ReviewedNativeBootstrapDeclarations =
        new Dictionary<string, TypeDeclarationSyntax>(StringComparer.Ordinal)
        {
            [NativeBootstrapOwner] = NativeBootstrapDeclaration(ReviewedNativeRuntimeSource),
            ["RetroDownfall.Arcanum.Infrastructure.Data.ISqliteNativeRuntime"] = NativeBootstrapDeclaration(ReviewedNativeInterfaceSource),
            ["RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntimeUnavailableException"] = NativeBootstrapDeclaration(ReviewedNativeExceptionSource),
        };

    private static readonly MethodDeclarationSyntax ReviewedNativeFactoryMethod =
        CSharpSyntaxTree.ParseText("class Factory { " + ReviewedNativeFactoryMethodSource + " }")
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

    private static readonly IReadOnlyDictionary<string, TypeDeclarationSyntax> ReviewedNativeGeneratedDeclarations =
        new Dictionary<string, TypeDeclarationSyntax>(StringComparer.Ordinal)
        {
            ["GetNativeImageInfo"] = NativeBootstrapDeclaration(ReviewedNativeImageImportImplementation),
            ["GetModuleFileName"] = NativeBootstrapDeclaration(ReviewedNativeModuleImportImplementation),
        };

    private static TypeDeclarationSyntax NativeBootstrapDeclaration(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First();

    internal static bool ProbeCompletedProductionNativeBootstrap(IReadOnlyList<CSharpCompilation>? suppliedCompilations = null) => HostedProducerRootWorkers.OnAnalysisStack(() =>
    {
        IReadOnlyList<CSharpCompilation> compilations = suppliedCompilations ?? ProductionCompilations;

        CSharpCompilation owner = compilations.Single(static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        return new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false).NativeBootstrapIsReviewedForProbe(owner);
    });

    private sealed partial class ProducerGraph
    {
        internal bool NativeBootstrapIsReviewedForProbe(Compilation compilation) => HasReviewedSqliteNativeBootstrap(compilation);

        private readonly Dictionary<Compilation, bool> reviewedNativeBootstrapCompilations = [];

        // Process bootstrap is neither pure Lazy data nor admission-bearing Grimoire work.
        // Approval is the closed current body, dependencies, bound symbols, and design-time caller.
        // Any change falls through to the ordinary conservative producer and lifetime analysis.
        private bool IsReviewedSqliteNativeBootstrapUse(ISymbol symbol, SyntaxNode? node, AuthoredMember? context)
        {
            if (context is null || node is null
                || context.Symbol.ContainingType.Name is not ("SqliteNativeRuntime" or "ArcanumDbContextFactory")
                || !(TypeKey(context.Symbol.ContainingType) == NativeBootstrapOwner
                    || TypeKey(context.Symbol.ContainingType) == NativeBootstrapFactory
                        && context.Symbol.Name == "InitializeDesignTimeNativeRuntime")
                || context.Symbol.ContainingAssembly.Name != "RetroDownfall.Arcanum.Infrastructure"
                || node.SyntaxTree != context.Syntax.SyntaxTree
                || !context.Syntax.FullSpan.Contains(node.Span)
                || !SymbolEqualityComparer.Default.Equals(context.Model.GetSymbolInfo(node).Symbol, symbol))
                return false;

            return HasReviewedSqliteNativeBootstrap(context.Model.Compilation);
        }

        private bool IsReviewedSqliteNativeBootstrapLazyRead(AuthoredMember owner, MemberAccessExpressionSyntax read) =>
            owner.Model.GetSymbolInfo(read).Symbol is IPropertySymbol { Name: "Value" } value
            && TypeKey(value.ContainingType) == "System.Lazy`1"
            && owner.Symbol.Name is "Initialize" or "InitializeForDesignTime"
            && IsReviewedSqliteNativeBootstrapUse(value, read, owner);

        private bool HasReviewedSqliteNativeBootstrap(Compilation compilation)
        {
            if (reviewedNativeBootstrapCompilations.TryGetValue(compilation, out bool known)) return known;

            // Fail closed while proving; Compilation and its syntax trees are immutable.
            reviewedNativeBootstrapCompilations[compilation] = false;

            bool complete = ProveSqliteNativeBootstrap(compilation);

            reviewedNativeBootstrapCompilations[compilation] = complete;

            return complete;
        }

        private bool ProveSqliteNativeBootstrap(Compilation compilation)
        {
            if (compilation.AssemblyName != "RetroDownfall.Arcanum.Infrastructure") return false;

            Dictionary<INamedTypeSymbol, TypeDeclarationSyntax> declarations = new(SymbolEqualityComparer.Default);

            List<TypeDeclarationSyntax> generatedDeclarations = [];

            foreach ((string name, TypeDeclarationSyntax expected) in ReviewedNativeBootstrapDeclarations)
            {
                if (compilation.GetTypeByMetadataName(name) is not { } type
                    || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) return false;

                SyntaxNode[] syntax = type.DeclaringSyntaxReferences.Select(static reference => reference.GetSyntax()).ToArray();

                TypeDeclarationSyntax[] parts = syntax.OfType<TypeDeclarationSyntax>().ToArray();

                if (parts.Length != syntax.Length
                    || parts.Where(part => SyntaxFactory.AreEquivalent(expected, part, topLevel: false)).ToArray()
                        is not [TypeDeclarationSyntax actual]) return false;

                if (name == NativeBootstrapOwner)
                {
                    if (!TryCloseNativeImportImplementations(compilation, type, actual, parts, out generatedDeclarations)) return false;
                }
                else if (parts.Length != 1) return false;

                declarations.Add(type, actual);
            }

            INamedTypeSymbol runtime = declarations.Keys.Single(type => TypeKey(type) == NativeBootstrapOwner);

            if (compilation.GetTypeByMetadataName(NativeBootstrapFactory) is not
                { IsSealed: true, DeclaredAccessibility: Accessibility.Public } factory
                || !SymbolEqualityComparer.Default.Equals(factory.ContainingAssembly, compilation.Assembly)
                || factory.DeclaringSyntaxReferences.Length != 1
                || factory.GetMembers("InitializeDesignTimeNativeRuntime").OfType<IMethodSymbol>().ToArray() is not [IMethodSymbol factoryMethod]
                || factoryMethod is not { IsStatic: true, DeclaredAccessibility: Accessibility.Private, ReturnsVoid: false, Parameters.Length: 0 }
                || factoryMethod.ReturnType.SpecialType != SpecialType.System_Boolean
                || factoryMethod.DeclaringSyntaxReferences is not [SyntaxReference factoryReference]
                || factoryReference.GetSyntax() is not MethodDeclarationSyntax factorySyntax
                || !SyntaxFactory.AreEquivalent(ReviewedNativeFactoryMethod, factorySyntax, topLevel: false)) return false;

            HashSet<INamedTypeSymbol> ownedTypes = new(declarations.Keys, SymbolEqualityComparer.Default);

            foreach ((INamedTypeSymbol _, TypeDeclarationSyntax declaration) in declarations)
                if (!NativeBootstrapSymbolsAreClosed(declaration, compilation.GetSemanticModel(declaration.SyntaxTree), ownedTypes, factory)) return false;

            foreach (TypeDeclarationSyntax generated in generatedDeclarations)
                if (!NativeBootstrapSymbolsAreClosed(generated, compilation.GetSemanticModel(generated.SyntaxTree), ownedTypes, factory,
                    generatedImport: true)) return false;

            if (!NativeBootstrapSymbolsAreClosed(factorySyntax, compilation.GetSemanticModel(factorySyntax.SyntaxTree), ownedTypes, factory)) return false;

            if (runtime.GetMembers("InitializeForDesignTime").OfType<IMethodSymbol>().Count() != 1) return false;

            // This is the sole mutation of the asset-directory input outside the pinned owner.
            // Restrict every authored reference, including method groups, not just calls seen on one root.
            int designTimeReferences = 0;

            int owningCompilation = CompilationIdentity(compilation);

            foreach (Compilation caller in compilationIdentities.Keys.ToArray())
            foreach (SyntaxTree tree in caller.SyntaxTrees)
            {
                SimpleNameSyntax[] references = tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
                    .Where(static name => name.Identifier.ValueText == "InitializeForDesignTime").ToArray();

                if (references.Length == 0) continue;

                SemanticModel model = caller.GetSemanticModel(tree);

                foreach (SimpleNameSyntax name in references)
                {
                    if (model.GetSymbolInfo(name).Symbol is not IMethodSymbol selected
                        || TypeKey(selected.ContainingType) != NativeBootstrapOwner
                        || selected.ContainingAssembly.Name != "RetroDownfall.Arcanum.Infrastructure") continue;

                    if (AuthoredCompilationIdentity(selected, caller) != owningCompilation
                        || !ReferenceEquals(caller, compilation)) return false;

                    designTimeReferences++;

                    if (name.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } enclosing
                        || enclosing.SyntaxTree != factorySyntax.SyntaxTree || enclosing.Span != factorySyntax.Span
                        || name.Parent is not MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax invocation }
                        || invocation.ArgumentList.Arguments is not [ArgumentSyntax argument]
                        || argument.RefKindKeyword.RawKind != 0
                        || argument.Expression is not IdentifierNameSyntax { Identifier.ValueText: "assemblyDirectory" }) return false;
                }
            }

            return designTimeReferences == 1;
        }

        private static bool TryCloseNativeImportImplementations(Compilation compilation, INamedTypeSymbol owner,
            TypeDeclarationSyntax authored, TypeDeclarationSyntax[] parts, out List<TypeDeclarationSyntax> generated)
        {
            generated = [];

            // The authored-only fixture deliberately has neither implementation. Production's
            // completed-compilation positive separately requires both linked implementation parts.
            if (parts.Length is not (1 or 3)) return false;

            foreach ((string name, TypeDeclarationSyntax expected) in ReviewedNativeGeneratedDeclarations)
            {
                if (authored.Members.OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText == name).ToArray()
                        is not [MethodDeclarationSyntax definitionSyntax]
                    || compilation.GetSemanticModel(authored.SyntaxTree).GetDeclaredSymbol(definitionSyntax)
                        is not IMethodSymbol { IsPartialDefinition: true, PartialDefinitionPart: null } definition
                    || !SymbolEqualityComparer.Default.Equals(definition.ContainingType, owner)) return false;

                if (parts.Length == 1)
                {
                    if (definition.PartialImplementationPart is not null) return false;

                    continue;
                }

                if (parts.Where(part => SyntaxFactory.AreEquivalent(expected, part, topLevel: false)).ToArray()
                        is not [TypeDeclarationSyntax implementationPart]
                    || implementationPart.Members is not [MethodDeclarationSyntax implementationSyntax]
                    || compilation.GetSemanticModel(implementationPart.SyntaxTree).GetDeclaredSymbol(implementationSyntax)
                        is not IMethodSymbol implementation
                    || !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, owner)
                    || !SymbolEqualityComparer.Default.Equals(definition.PartialImplementationPart, implementation)
                    || !SymbolEqualityComparer.Default.Equals(implementation.PartialDefinitionPart, definition)) return false;

                generated.Add(implementationPart);
            }

            return parts.Length == 1 || generated.Count == 2 && generated[0] != generated[1];
        }

        private bool NativeBootstrapSymbolsAreClosed(SyntaxNode scope, SemanticModel model, HashSet<INamedTypeSymbol> ownedTypes,
            INamedTypeSymbol factory, bool generatedImport = false)
        {
            foreach (SyntaxNode syntax in scope.DescendantNodesAndSelf())
            {
                // Names cover bindings; calls/operators cover user-defined overload resolution.
                if (syntax is not (NameSyntax or InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                    or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or AssignmentExpressionSyntax or CastExpressionSyntax or AttributeSyntax)) continue;

                // Only the intrinsic global:: qualifier is structural, and only in an exact
                // pinned generated declaration. Its enclosing qualified names are still bound
                // and checked below; arbitrary aliases remain refused.
                if (generatedImport && syntax is IdentifierNameSyntax globalName
                    && globalName.Identifier.Text == "global"
                    && globalName.Parent is AliasQualifiedNameSyntax qualified
                    && ReferenceEquals(qualified.Alias, globalName)) continue;

                // Even a trusted-type alias could retarget an unchanged catch or loader token.
                if (syntax is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier) is not null) return false;

                SymbolInfo info = model.GetSymbolInfo(syntax);

                if (info.CandidateSymbols.Length != 0) return false;

                if (info.Symbol is not { } symbol)
                {
                    if (syntax is NameSyntax or InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax) return false;

                    continue;
                }

                if (symbol is IAliasSymbol alias) symbol = alias.Target;

                if (symbol is INamespaceSymbol or ILocalSymbol or IParameterSymbol or ILabelSymbol or ITypeParameterSymbol or IDiscardSymbol) continue;

                INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType;

                if (type is null) return false;

                // The factory is named only by typeof. Its other bodies/nested types are not this proof.
                if (symbol is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named, factory)) continue;

                if (NativeBootstrapOwnedType(type, ownedTypes)) continue;

                if (!IsNativeBootstrapExternalType(type, model.Compilation, generatedImport)) return false;
            }

            return true;
        }

        private static bool NativeBootstrapOwnedType(INamedTypeSymbol type, HashSet<INamedTypeSymbol> ownedTypes)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
                if (ownedTypes.Contains(current.OriginalDefinition)) return true;

            return false;
        }

        private bool IsNativeBootstrapExternalType(INamedTypeSymbol type, Compilation consumingCompilation, bool generatedImport)
        {
            type = type.OriginalDefinition;

            if (type.Locations.Any(static location => location.IsInSource)
                || AuthoredCompilationIdentity(type, consumingCompilation) is not null) return false;

            (Type? Runtime, string? Contract) expected = TypeKey(type) switch
            {
                "System.Object" => (typeof(object), "System.Runtime"),
                "System.Boolean" => (typeof(bool), "System.Runtime"),
                "System.Char" => (typeof(char), "System.Runtime"),
                "System.Int32" => (typeof(int), "System.Runtime"),
                "System.UInt32" => (typeof(uint), "System.Runtime"),
                "System.IntPtr" => (typeof(IntPtr), "System.Runtime"),
                "System.String" => (typeof(string), "System.Runtime"),
                "System.Void" => (typeof(void), "System.Runtime"),
                "System.Nullable`1" => (typeof(Nullable<>), "System.Runtime"),
                "System.Lazy`1" => (typeof(Lazy<>), "System.Runtime"),
                "System.Threading.LazyThreadSafetyMode" => (typeof(LazyThreadSafetyMode), "System.Runtime"),
                "System.Exception" => (typeof(Exception), "System.Runtime"),
                "System.DllNotFoundException" => (typeof(DllNotFoundException), "System.Runtime.InteropServices"),
                "System.EntryPointNotFoundException" => (typeof(EntryPointNotFoundException), "System.Runtime"),
                "System.BadImageFormatException" => (typeof(BadImageFormatException), "System.Runtime"),
                "System.TypeInitializationException" => (typeof(TypeInitializationException), "System.Runtime"),
                "System.InvalidOperationException" => (typeof(InvalidOperationException), "System.Runtime"),
                "System.StringComparison" => (typeof(StringComparison), "System.Runtime"),
                "System.AppContext" => (typeof(AppContext), "System.Runtime"),
                "System.OperatingSystem" => (typeof(OperatingSystem), "System.Runtime"),
                "System.Environment" => (typeof(System.Environment), "System.Runtime"),
                "System.IO.Path" => (typeof(Path), "System.Runtime"),
                "System.IO.File" => (typeof(File), "System.Runtime"),
                "System.Type" => (typeof(Type), "System.Runtime"),
                "System.Reflection.Assembly" => (typeof(Assembly), "System.Runtime"),
                "System.Runtime.InteropServices.RuntimeInformation" => (typeof(System.Runtime.InteropServices.RuntimeInformation), "System.Runtime"),
                "System.Runtime.InteropServices.Architecture" => (typeof(System.Runtime.InteropServices.Architecture), "System.Runtime"),
                "System.Runtime.InteropServices.StructLayoutAttribute" => (typeof(System.Runtime.InteropServices.StructLayoutAttribute), "System.Runtime"),
                "System.Runtime.InteropServices.LayoutKind" => (typeof(System.Runtime.InteropServices.LayoutKind), "System.Runtime"),
                "System.Runtime.InteropServices.NativeLibrary" => (typeof(System.Runtime.InteropServices.NativeLibrary), "System.Runtime.InteropServices"),
                "System.Runtime.InteropServices.Marshal" => (typeof(System.Runtime.InteropServices.Marshal), "System.Runtime.InteropServices"),
                "System.Runtime.InteropServices.LibraryImportAttribute" => (typeof(System.Runtime.InteropServices.LibraryImportAttribute), "System.Runtime.InteropServices"),
                "System.Runtime.InteropServices.DllImportSearchPath" => (typeof(System.Runtime.InteropServices.DllImportSearchPath), "System.Runtime.InteropServices"),
                "System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute" => (typeof(System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessageAttribute), "System.Runtime"),
                "System.CodeDom.Compiler.GeneratedCodeAttribute" when generatedImport => (typeof(System.CodeDom.Compiler.GeneratedCodeAttribute), "System.Runtime"),
                "System.Runtime.CompilerServices.SkipLocalsInitAttribute" when generatedImport => (typeof(System.Runtime.CompilerServices.SkipLocalsInitAttribute), "System.Runtime"),
                "System.Runtime.InteropServices.DllImportAttribute" when generatedImport => (typeof(System.Runtime.InteropServices.DllImportAttribute), "System.Runtime.InteropServices"),
                "System.Linq.Enumerable" => (typeof(Enumerable), "System.Linq"),
                "SQLitePCL.SQLite3Provider_sqlcipher" => (typeof(SQLitePCL.SQLite3Provider_sqlcipher), null),
                "SQLitePCL.raw" => (typeof(SQLitePCL.raw), null),
                _ => (null, null),
            };

            if (expected.Runtime is null) return false;

            if (AssemblyIdentityMatches(type.ContainingAssembly.Identity, expected.Runtime.Assembly.GetName())) return true;

            return expected.Contract is not null
                && AssemblyIdentityMatches(type.ContainingAssembly.Identity, Assembly.Load(new AssemblyName(expected.Contract)).GetName());
        }
    }

    // Reviewed source contracts are literal fixtures, never read from the current checkout at proof time.
    // Trivia is immaterial. Every executable/declaration token and resolved referenced type still closes.
    private const string ReviewedNativeRuntimeSource = """
        internal sealed partial class SqliteNativeRuntime : ISqliteNativeRuntime
        {
            public static SqliteNativeRuntime Instance { get; } = new();
        
            private static readonly object InitializationGate = new();
        
            private static readonly Lazy<bool> Initialized =
                new(InitializeCore, LazyThreadSafetyMode.ExecutionAndPublication);
        
            private static string _assetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        
            // The resolver and native callbacks remain valid for the whole process lifetime.
            private static IntPtr _nativeHandle;
        
            public void Initialize()
            {
                lock (InitializationGate)
                {
                    _ = Initialized.Value;
                }
            }
        
            /// <summary>
            /// EF tooling's application directory is the tool directory. Select the target assembly's
            /// delivered asset before the same one-time initialization; no search path is introduced.
            /// </summary>
            internal void InitializeForDesignTime(string assemblyDirectory)
            {
                lock (InitializationGate)
                {
                    string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assemblyDirectory));
        
                    if (Initialized.IsValueCreated && !string.Equals(_assetDirectory, directory, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The SQLCipher runtime has already selected its native asset.");
                    }
        
                    _assetDirectory = directory;
        
                    _ = Initialized.Value;
                }
            }
        
            private static bool InitializeCore()
            {
                try
                {
                    if (!(OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                        && !(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64))
                    {
                        throw new DllNotFoundException("No SQLCipher asset is delivered for this platform.");
                    }
        
                    string assetPath = Path.GetFullPath(Path.Combine(_assetDirectory, ExpectedAssetFileName()));
        
                    if (!File.Exists(assetPath))
                    {
                        throw new DllNotFoundException("The delivered SQLCipher asset is absent.");
                    }
        
                    // dyld can redirect even an absolute dlopen path. Refuse active overrides before load.
                    if (OperatingSystem.IsMacOS() && new[]
                        {
                            "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_VERSIONED_LIBRARY_PATH",
                            "DYLD_IMAGE_SUFFIX", "DYLD_ROOT_PATH",
                        }.Any(static name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
                    {
                        throw new DllNotFoundException("Native loader overrides cannot select the SQLCipher asset.");
                    }
        
                    IntPtr handle = NativeLibrary.Load(assetPath);
        
                    try
                    {
                        ValidateNativeImage(handle, assetPath);
                    }
                    catch
                    {
                        NativeLibrary.Free(handle);
        
                        throw;
                    }
        
                    _nativeHandle = handle;
        
                    NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_sqlcipher).Assembly, ResolveImport);
        
                    // SetProvider calls sqlite3_libversion_number immediately, so the resolver must exist.
                    raw.SetProvider(new SQLite3Provider_sqlcipher());
        
                    raw.FreezeProvider(true);
        
                    return true;
                }
                catch (Exception exception) when (exception is DllNotFoundException
                    or EntryPointNotFoundException
                    or BadImageFormatException
                    or TypeInitializationException)
                {
                    throw new SqliteNativeRuntimeUnavailableException(
                        RuntimeInformation.RuntimeIdentifier,
                        ExpectedAssetFileName(),
                        exception);
                }
            }
        
            private static unsafe void ValidateNativeImage(IntPtr handle, string expectedPath)
            {
                string? loadedPath;
        
                if (OperatingSystem.IsMacOS())
                {
                    IntPtr symbol = NativeLibrary.GetExport(handle, "sqlite3_libversion_number");
        
                    if (GetNativeImageInfo(symbol, out NativeImageInfo image) == 0)
                    {
                        throw new DllNotFoundException("The SQLCipher native image could not be identified.");
                    }
        
                    loadedPath = Marshal.PtrToStringUTF8(image.FileName);
                }
                else
                {
                    // Windows extended-length paths have a maximum of 32,767 UTF-16 characters.
                    const int capacity = 32768;
        
                    char* buffer = stackalloc char[capacity];
        
                    uint length = GetModuleFileName(handle, buffer, capacity);
        
                    if (length == 0 || length >= capacity)
                    {
                        throw new DllNotFoundException("The SQLCipher native image could not be identified.");
                    }
        
                    loadedPath = new string(buffer, 0, (int)length);
                }
        
                StringComparison comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        
                if (loadedPath is null || !string.Equals(Path.GetFullPath(loadedPath), expectedPath, comparison))
                {
                    throw new DllNotFoundException("The native loader substituted a different SQLCipher asset.");
                }
            }
        
            [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr")]
            private static partial int GetNativeImageInfo(IntPtr address, out NativeImageInfo image);
        
            [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
            private static unsafe partial uint GetModuleFileName(IntPtr module, char* path, uint capacity);
        
            [StructLayout(LayoutKind.Sequential)]
            private struct NativeImageInfo
            {
                public IntPtr FileName;
        
                public IntPtr BaseAddress;
        
                public IntPtr SymbolName;
        
                public IntPtr SymbolAddress;
            }
        
            private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
            {
                if (libraryName != "sqlcipher" || _nativeHandle == IntPtr.Zero)
                {
                    // Returning zero would authorize platform probing. This provider has one import only.
                    throw new DllNotFoundException("The SQLCipher provider requested an unavailable native import.");
                }
        
                return _nativeHandle;
            }
        
            private static string ExpectedAssetFileName() =>
                OperatingSystem.IsWindows() ? "e_sqlcipher.dll" : "libe_sqlcipher.dylib";
        }
        """;

    private const string ReviewedNativeInterfaceSource = """
        internal interface ISqliteNativeRuntime
        {
        
            /// <summary>
            /// Installs the hermetic SQLCipher provider if it is not installed yet. Safe to call from any
            /// thread and any number of times.
            /// </summary>
            /// <exception cref="SqliteNativeRuntimeUnavailableException">
            /// The native library for this runtime identifier could not be loaded.
            /// </exception>
            void Initialize();
        
        }
        """;

    private const string ReviewedNativeExceptionSource = """
        internal sealed class SqliteNativeRuntimeUnavailableException : Exception
        {
        
            internal SqliteNativeRuntimeUnavailableException(
                string runtimeIdentifier,
                string expectedAssetFileName,
                Exception innerException)
                : base(
                    $"The hermetic SQLCipher runtime for '{runtimeIdentifier}' could not be loaded. "
                    + $"Expected '{expectedAssetFileName}' to be delivered with the application. "
                    + "Arcanum does not search for an alternative SQLite library. "
                    + "Restore the delivered asset and remove native-loader overrides before restarting.",
                    innerException)
            {
        
                RuntimeIdentifier = runtimeIdentifier;
        
                ExpectedAssetFileName = expectedAssetFileName;
        
            }
        
            /// <summary>
            /// Stable code for logs and support, so the failure is greppable without parsing the message.
            /// </summary>
            internal string ErrorCode => "Grimoire.NativeRuntimeUnavailable";
        
            internal string RuntimeIdentifier { get; }
        
            internal string ExpectedAssetFileName { get; }
        
        }
        """;

    private const string ReviewedNativeFactoryMethodSource = """
        [UnconditionalSuppressMessage(
                "SingleFile",
                "IL3000",
                Justification = "EF invokes this design-time-only factory from the ordinary target assembly, never from Arcanum's published process.")]
            private static bool InitializeDesignTimeNativeRuntime()
            {
                string assemblyDirectory = Path.GetDirectoryName(typeof(ArcanumDbContextFactory).Assembly.Location)
                    ?? throw new InvalidOperationException("The design-time Infrastructure assembly has no directory.");
        
                SqliteNativeRuntime.Instance.InitializeForDesignTime(assemblyDirectory);
        
                return true;
            }
        """;

    private const string ReviewedNativeImageImportImplementation = """
        internal sealed partial class SqliteNativeRuntime
        {
            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.Interop.LibraryImportGenerator", "11.0.15.3018")]
            [global::System.Runtime.CompilerServices.SkipLocalsInitAttribute]
            private static partial int GetNativeImageInfo(nint address, out global::RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime.NativeImageInfo image)
            {
                unsafe
                {
                    image = default;
                    int __retVal;
                    // Pin - Pin data in preparation for calling the P/Invoke.
                    fixed (global::RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime.NativeImageInfo* __image_native = &image)
                    {
                        __retVal = __PInvoke(address, __image_native);
                    }

                    return __retVal;
                    // Local P/Invoke
                    [global::System.Runtime.InteropServices.DllImportAttribute("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr", ExactSpelling = true)]
                    static extern unsafe int __PInvoke(nint __address_native, global::RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime.NativeImageInfo* __image_native);
                }
            }
        }
        """;

    private const string ReviewedNativeModuleImportImplementation = """
        internal sealed partial class SqliteNativeRuntime
        {
            [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.Interop.LibraryImportGenerator", "11.0.15.3018")]
            [global::System.Runtime.CompilerServices.SkipLocalsInitAttribute]
            private static unsafe partial uint GetModuleFileName(nint module, char* path, uint capacity)
            {
                unsafe
                {
                    int __lastError;
                    uint __retVal;
                    {
                        global::System.Runtime.InteropServices.Marshal.SetLastSystemError(0);
                        __retVal = __PInvoke(module, path, capacity);
                        __lastError = global::System.Runtime.InteropServices.Marshal.GetLastSystemError();
                    }

                    global::System.Runtime.InteropServices.Marshal.SetLastPInvokeError(__lastError);
                    return __retVal;
                    // Local P/Invoke
                    [global::System.Runtime.InteropServices.DllImportAttribute("kernel32.dll", EntryPoint = "GetModuleFileNameW", ExactSpelling = true)]
                    static extern unsafe uint __PInvoke(nint __module_native, char* __path_native, uint __capacity_native);
                }
            }
        }
        """;
}
