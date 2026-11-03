using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string BorrowedColumnRead = """
        Microsoft.Data.Sqlite.SqliteDataReader reader = null!;
        SQLitePCL.sqlite3_stmt statement = reader.Handle!;
        _ = SQLitePCL.raw.sqlite3_column_type(statement, 1);
        _ = SQLitePCL.raw.sqlite3_column_blob(statement, 1).Length;
        await Task.CompletedTask;
        """;

    [Theory]
    [InlineData("none", false)]
    [InlineData("work", true)]
    [InlineData("released", false)]
    public void BorrowedSqliteColumnReadsAreDatabaseAccessRequiringLiveWork(string admission, bool admitted)
    {
        string prefix = admission == "none" ? string.Empty : AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal);

        if (admission == "released")
        {
            prefix += " lease.Dispose(); ";
        }

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(prefix + BorrowedColumnRead);

        foreach (string callee in new[]
        {
            "Microsoft.Data.Sqlite.SqliteDataReader.Handle",
            "SQLitePCL.raw.sqlite3_column_type",
            "SQLitePCL.raw.sqlite3_column_blob",
        })
        {
            HostedProducerSite site = Assert.Single(result.Items, site => site.Callee == callee);

            Assert.Equal(HostedProducerSiteKind.DatabaseAccess, site.Kind);

            Assert.Equal(admitted, site.WorkFrontierCapsuleId is not null);

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee);
        }
    }

    [Fact]
    public void BorrowedSqliteColumnGrantDoesNotPermitNativeStatementExecution()
    {
        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission
            + "SQLitePCL.sqlite3_stmt statement = null!; _ = SQLitePCL.raw.sqlite3_step(statement); await Task.CompletedTask;");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "SQLitePCL.raw.sqlite3_step");
    }

    [Fact]
    public void BorrowedSqliteColumnGrantDoesNotHideSourceDefinedNativeLookalikes()
    {
        const string shadow = """
            namespace SQLitePCL
            {
                internal static class raw
                {
                    internal static int sqlite3_column_type(sqlite3_stmt statement, int ordinal)
                    {
                        System.IO.File.Delete("native-lookalike");
                        return 0;
                    }
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = TimeoutDiscovery(R2Admission
            + "SQLitePCL.sqlite3_stmt statement = null!; _ = SQLitePCL.raw.sqlite3_column_type(statement, 1); await Task.CompletedTask;", shadow);

        Assert.DoesNotContain(result.Items, static site => site.Kind == HostedProducerSiteKind.DatabaseAccess
            && site.Callee == "SQLitePCL.raw.sqlite3_column_type");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }
}
