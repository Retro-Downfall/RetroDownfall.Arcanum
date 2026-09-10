using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class EfNativeAotCompiledBoundaryTests
{
    [Fact]
    public void Shipping_assemblies_have_no_compiled_references_to_entity_framework_query_apis()
    {
        List<EfQueryReference> violations = ShippingAssemblyPaths()
            .SelectMany(EfNativeAotCompiledBoundaryScanner.FindForbiddenReferences)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Compiled shipping assemblies reference forbidden EF query APIs:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void Compiled_inventory_includes_the_shipping_executable_assembly()
    {
        Assert.Contains(
            typeof(ArcanumApiCredentialLease).Assembly.Location,
            ShippingAssemblyPaths(),
            StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("System.Linq.Queryable", "Where")]
    [InlineData("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "AsNoTracking")]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalQueryableExtensions", "FromSqlRaw")]
    [InlineData("Microsoft.EntityFrameworkCore.EF", "CompileQuery")]
    [InlineData("Microsoft.EntityFrameworkCore.EF", "CompileAsyncQuery")]
    [InlineData("Microsoft.EntityFrameworkCore.DbContext", "Find")]
    [InlineData("Microsoft.EntityFrameworkCore.DbContext", "FindAsync")]
    [InlineData("Microsoft.EntityFrameworkCore.DbSet`1", "Find")]
    [InlineData("Microsoft.EntityFrameworkCore.DbSet`1", "FindAsync")]
    [InlineData("Microsoft.EntityFrameworkCore.DbSet`1", "GetAsyncEnumerator")]
    [InlineData("Microsoft.EntityFrameworkCore.DbSet`1", "AsAsyncEnumerable")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "Reload")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "ReloadAsync")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "GetDatabaseValues")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "GetDatabaseValuesAsync")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.NavigationEntry", "Load")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.NavigationEntry", "LoadAsync")]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking.NavigationEntry", "Query")]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlRaw")]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSqlRawAsync")]
    public void Compiled_detector_rejects_each_forbidden_query_api_family(
        string expectedType,
        string expectedMethod)
    {
        IReadOnlyList<EfQueryReference> violations =
            EfNativeAotCompiledBoundaryScanner.FindForbiddenReferences(
                typeof(EfNativeAotCompiledBoundaryTests).Assembly.Location);

        Assert.Contains(
            violations,
            violation => violation.DeclaringType == expectedType
                && violation.Method == expectedMethod);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IQueryable<int> ForbiddenQueryableFixture(IQueryable<int> source) =>
        source.Where(static value => value > 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForbiddenEntityFrameworkFixture(ArcanumDbContext db)
    {
        _ = db.Sessions.AsNoTracking();
        _ = db.Sessions.FromSqlRaw("SELECT * FROM Sessions;");
        _ = EF.CompileQuery((ArcanumDbContext context) => context.Sessions);
        _ = EF.CompileAsyncQuery((ArcanumDbContext context) => context.Sessions);
        _ = db.Find<Session>(Guid.Empty);
        _ = db.FindAsync<Session>(Guid.Empty);
        _ = db.Sessions.Find(Guid.Empty);
        _ = db.Sessions.FindAsync(Guid.Empty);
        _ = db.Database.ExecuteSqlRaw("DELETE FROM Sessions;");
        _ = db.Database.ExecuteSqlRawAsync("DELETE FROM Sessions;");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForbiddenImplicitDatabaseReadFixture(
        ArcanumDbContext db,
        Session session)
    {
        _ = db.Sessions.GetAsyncEnumerator();
        _ = db.Sessions.AsAsyncEnumerable();
        db.Entry(session).Reload();
        _ = db.Entry(session).ReloadAsync();
        _ = db.Entry(session).GetDatabaseValues();
        _ = db.Entry(session).GetDatabaseValuesAsync();
        db.Entry(session).Collection("Entries").Load();
        _ = db.Entry(session).Collection("Entries").LoadAsync();
        _ = db.Entry(session).Collection("Entries").Query();
    }

    private static string[] ShippingAssemblyPaths() =>
    [
        typeof(ArcanumDbContext).Assembly.Location,
        typeof(ApiBootstrapper).Assembly.Location,
        typeof(ArcanumApiCredentialLease).Assembly.Location,
    ];
}

internal sealed record EfQueryReference(
    string Assembly,
    string DeclaringType,
    string Method)
{
    public override string ToString() => $"{Assembly}: {DeclaringType}.{Method}";
}

internal static class EfNativeAotCompiledBoundaryScanner
{
    private static readonly HashSet<string> RelationalDatabaseQueryOperations =
        new(StringComparer.Ordinal)
        {
            "ExecuteSqlInterpolated",
            "ExecuteSqlInterpolatedAsync",
            "ExecuteSqlRaw",
            "ExecuteSqlRawAsync",
            "SqlQuery",
            "SqlQueryRaw",
        };

    private static readonly HashSet<string> CompiledQueryOperations =
        new(StringComparer.Ordinal)
        {
            "CompileAsyncQuery",
            "CompileQuery",
        };

    private static readonly HashSet<string> DbContextQueryOperations =
        new(StringComparer.Ordinal)
        {
            "Find",
            "FindAsync",
        };

    private static readonly HashSet<string> DbSetQueryOperations =
        new(DbContextQueryOperations, StringComparer.Ordinal)
        {
            "AsAsyncEnumerable",
            "GetAsyncEnumerator",
            "GetEnumerator",
        };

    private static readonly HashSet<string> EntityEntryQueryOperations =
        new(StringComparer.Ordinal)
        {
            "GetDatabaseValues",
            "GetDatabaseValuesAsync",
            "Reload",
            "ReloadAsync",
        };

    private static readonly HashSet<string> NavigationEntryQueryOperations =
        new(StringComparer.Ordinal)
        {
            "Load",
            "LoadAsync",
            "Query",
        };

    private static readonly TypeNameProvider TypeNames = new();

    public static IReadOnlyList<EfQueryReference> FindForbiddenReferences(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream);

        if (!pe.HasMetadata)
        {
            throw new InvalidDataException($"Assembly has no managed metadata: {assemblyPath}");
        }

        MetadataReader metadata = pe.GetMetadataReader();
        string assemblyName = Path.GetFileName(assemblyPath);
        List<EfQueryReference> violations = [];

        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference reference = metadata.GetMemberReference(handle);
            string? declaringType = ReadDeclaringType(metadata, reference.Parent);

            if (declaringType is null)
            {
                continue;
            }

            string method = metadata.GetString(reference.Name);

            if (IsForbidden(declaringType, method))
            {
                violations.Add(new EfQueryReference(assemblyName, declaringType, method));
            }
        }

        return violations
            .Distinct()
            .OrderBy(static violation => violation.DeclaringType, StringComparer.Ordinal)
            .ThenBy(static violation => violation.Method, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsForbidden(string declaringType, string method)
    {
        if (declaringType is "System.Linq.Queryable"
            or "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions"
            or "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions")
        {
            return true;
        }

        if (declaringType == "Microsoft.EntityFrameworkCore.EF")
        {
            return CompiledQueryOperations.Contains(method);
        }

        if (declaringType == "Microsoft.EntityFrameworkCore.DbContext")
        {
            return DbContextQueryOperations.Contains(method);
        }

        if (declaringType.StartsWith(
                "Microsoft.EntityFrameworkCore.DbSet`",
                StringComparison.Ordinal))
        {
            return DbSetQueryOperations.Contains(method);
        }

        if (declaringType == "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry"
            || declaringType.StartsWith(
                "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry`",
                StringComparison.Ordinal))
        {
            return EntityEntryQueryOperations.Contains(method);
        }

        if (declaringType == "Microsoft.EntityFrameworkCore.ChangeTracking.NavigationEntry"
            || declaringType.StartsWith(
                "Microsoft.EntityFrameworkCore.ChangeTracking.CollectionEntry",
                StringComparison.Ordinal)
            || declaringType.StartsWith(
                "Microsoft.EntityFrameworkCore.ChangeTracking.ReferenceEntry",
                StringComparison.Ordinal))
        {
            return NavigationEntryQueryOperations.Contains(method);
        }

        return declaringType == "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"
            && RelationalDatabaseQueryOperations.Contains(method);
    }

    private static string? ReadDeclaringType(MetadataReader metadata, EntityHandle parent) =>
        parent.Kind switch
        {
            HandleKind.TypeDefinition => TypeNames.GetTypeFromDefinition(
                metadata,
                (TypeDefinitionHandle)parent,
                rawTypeKind: 0),
            HandleKind.TypeReference => TypeNames.GetTypeFromReference(
                metadata,
                (TypeReferenceHandle)parent,
                rawTypeKind: 0),
            HandleKind.TypeSpecification => metadata
                .GetTypeSpecification((TypeSpecificationHandle)parent)
                .DecodeSignature(TypeNames, genericContext: null),
            _ => null,
        };

    private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType;

        public string GetByReferenceType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";

        public string GetGenericInstantiation(
            string genericType,
            ImmutableArray<string> typeArguments) => genericType;

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetModifiedType(
            string modifier,
            string unmodifiedType,
            bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => elementType;

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);

            return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);

            return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static string FullName(string namespaceName, string typeName) =>
            string.IsNullOrEmpty(namespaceName)
                ? typeName
                : $"{namespaceName}.{typeName}";
    }
}
