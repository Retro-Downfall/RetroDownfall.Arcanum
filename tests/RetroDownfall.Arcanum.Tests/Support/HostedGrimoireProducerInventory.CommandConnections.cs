using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // DbCommand.Connection is a borrowed observation, not ownership of the connection.
        // An exact SqliteCommand's metadata getter returns SqliteConnection, but that
        // connection could be a subclass. Only the existing scan-verified premise closes
        // that final runtime type; an abstract Db* declaration alone proves nothing.
        private CleanupProvenance? ExactSqliteCommandConnectionProvenance(
            AuthoredMember member,
            MemberAccessExpressionSyntax access,
            IPropertySymbol property,
            CleanupProvenanceContext context)
        {
            if (!SqliteSealedByUse
                || property is not { IsStatic: false, Name: "Connection", Parameters.Length: 0, GetMethod: not null }
                || !(IsExactFrameworkType(property.ContainingType, "System.Data.Common.DbCommand", typeof(System.Data.Common.DbCommand).Assembly.GetName())
                    && IsExactFrameworkType(property.Type, "System.Data.Common.DbConnection", typeof(System.Data.Common.DbConnection).Assembly.GetName())
                    || IsExactFrameworkType(property.ContainingType, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())
                    && IsExactFrameworkType(property.Type, "Microsoft.Data.Sqlite.SqliteConnection", typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName())))
            {
                return null;
            }

            CleanupProvenance receiver = CleanupProvenanceOf(member, access.Expression, context);

            if (!receiver.Complete
                || receiver.Candidates is not [CleanupTypeCandidate candidate]
                || !IsExactFrameworkType(candidate.Type, "Microsoft.Data.Sqlite.SqliteCommand", typeof(Microsoft.Data.Sqlite.SqliteCommand).Assembly.GetName())
                || candidate.Type.ContainingAssembly.GetTypeByMetadataName("Microsoft.Data.Sqlite.SqliteConnection") is not { } connection)
            {
                return null;
            }

            return ExactCleanupProvenance(member, connection);
        }
    }
}
