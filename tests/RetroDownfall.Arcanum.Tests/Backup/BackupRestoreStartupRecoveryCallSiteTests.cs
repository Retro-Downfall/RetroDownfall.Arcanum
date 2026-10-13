using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// What startup restore recovery is structurally incapable of doing.
/// </summary>
/// <remarks>
/// Two of the guarantees this slice owes are properties of the call graph rather than of any single
/// result. Physical topology recovery runs before a live database has been classified, so it must not
/// be able to open one; and a restore that already closed a scope must be resumed rather than acquired,
/// because initial acquisition for an existing owner creates a second operation that also believes it
/// may reopen admission. Both fail as a new call site rather than as a wrong answer, which is why they
/// are inventory assertions over production source (§10.19.8).
/// </remarks>
public sealed class BackupRestoreStartupRecoveryCallSiteTests
{

    private const string RecoveryFileName = "BackupRestoreRecovery.cs";

    private const string TopologyFileName = "BackupRestorePhysicalTopology.cs";

    [Fact]
    public void Startup_restore_recovery_resumes_its_owner_and_never_acquires_a_new_one()
    {

        ProductionSource recovery = Source(RecoveryFileName);

        Assert.True(
            recovery.Names("ResumeExclusiveAsync"),
            "Authority recovery has to resume the exact journaled owner.");

        Assert.False(
            recovery.Names("AcquireExclusiveAsync"),
            "Initial acquisition for a journaled owner would create a second operation for a scope "
            + "that is already closed, and the gate would then hold two owners each believing it may "
            + "reopen admission.");

    }

    [Fact]
    public void Startup_restore_recovery_opens_no_sqlcipher_handle_of_its_own()
    {

        ProductionSource topology = Source(TopologyFileName);

        Assert.False(topology.Names("SqliteConnection") || topology.Names("SqliteCommand"));

        ProductionSource[] backupSources = ProductionSourceInventory.Sources().Where(static source =>
            source.RelativePath.StartsWith("src/RetroDownfall.Arcanum.Infrastructure/Backup/", StringComparison.Ordinal)).ToArray();

        // Database types in borrowed authority fields/parameters are legitimate. Every authored
        // topology/legacy helper edge still must be incapable of opening or querying a database.
        Assert.Empty(OwnedDatabaseCalls(backupSources, "BackupRestoreRecovery",
            ["ResolveAsync", "RecoverPhysicalTopologyBeforeDatabaseAsync"], forbidCommands: true));

        Assert.Empty(OwnedDatabaseCalls(backupSources, "BackupRestoreRecovery",
            ["RecoverAuthorityBeforeReadinessAsync", "CompleteLegacyCommittedBeforeReadinessAsync"], forbidCommands: false));

    }

    [Fact]
    public void Topology_call_graph_refuses_an_owned_open_hidden_behind_an_authored_helper()
    {
        ProductionSource unsafeSource = new("src/RetroDownfall.Arcanum.Infrastructure/Backup/Unsafe.cs", """
            class BackupRestoreRecovery
            {
                void ResolveAsync() => Forward();
                void Forward() => Worker.Read();
            }
            class Worker
            {
                static void Read() { var owned = new SqliteConnection(); owned.OpenAsync(); }
            }
            """);

        Assert.NotEmpty(OwnedDatabaseCalls([unsafeSource], "BackupRestoreRecovery", ["ResolveAsync"], forbidCommands: true));
    }

    private static IReadOnlyList<string> OwnedDatabaseCalls(IReadOnlyList<ProductionSource> sources,
        string rootOwner, IReadOnlyList<string> roots, bool forbidCommands)
    {
        var methods = sources.SelectMany(source => CSharpSyntaxTree.ParseText(source.Text).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()).Select(method =>
            (Owner: method.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText,
                Name: method.Identifier.ValueText, Method: method)).ToArray();

        HashSet<(string Owner, string Name)> visited = [];

        Stack<(string Owner, string Name)> pending = new(roots.Select(name => (rootOwner, name)));

        List<string> refused = [];

        while (pending.TryPop(out var key))
        {
            if (!visited.Add(key)) continue;

            foreach (var selected in methods.Where(method => (method.Owner, method.Name) == key))
            {
                foreach (ObjectCreationExpressionSyntax created in selected.Method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                {
                    if (created.Type.ToString().EndsWith("SqliteConnection", StringComparison.Ordinal)
                        || forbidCommands && created.Type.ToString().EndsWith("SqliteCommand", StringComparison.Ordinal))
                        refused.Add(key + ":" + created);
                }

                foreach (InvocationExpressionSyntax call in selected.Method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string name = call.Expression switch
                    {
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                        _ => "",
                    };

                    if (name is "OpenAsync" or "Open" || forbidCommands && name == "CreateCommand")
                        refused.Add(key + ":" + call);

                    string owner = call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
                        && methods.Any(method => method.Owner == receiver.Identifier.ValueText)
                            ? receiver.Identifier.ValueText : key.Owner;

                    if (methods.Any(method => method.Owner == owner && method.Name == name)) pending.Push((owner, name));
                }
            }
        }

        return refused;
    }

    private static ProductionSource Source(string fileName) =>
        Assert.Single(ProductionSourceInventory.Sources(), source => source.Is(fileName));

}
