using System.Reflection;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

// The operators that build or execute an EF query are classified by exact framework identity and by name. A name list
// that is not read from the assemblies lags them: a member a later framework version adds would silently be treated as
// none of the classes. These fixtures read the public surface of every operator class and require a deliberate verdict
// for each member, so adding one to the framework fails here until it is reviewed.
public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static readonly (Type Type, string Key)[] EfOperatorClasses =
    [
        (typeof(Enumerable), "System.Linq.Enumerable"),
        (typeof(Queryable), "System.Linq.Queryable"),
        (typeof(System.Linq.AsyncEnumerable), "System.Linq.AsyncEnumerable"),
        (typeof(System.Threading.Tasks.TaskAsyncEnumerableExtensions), "System.Threading.Tasks.TaskAsyncEnumerableExtensions"),
        (typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions), "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions"),
        (typeof(Microsoft.EntityFrameworkCore.RelationalQueryableExtensions), "Microsoft.EntityFrameworkCore.RelationalQueryableExtensions"),
    ];

    [Fact]
    public void EveryPublicOperatorOfTheQueryClassesHasAReviewedVerdict()
    {
        foreach ((Type type, string key) in EfOperatorClasses)
        {
            string[] names =
            [
                .. type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(static method => method.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            Assert.NotEmpty(names);

            foreach (string name in names)
            {
                string verdict = HostedGrimoireProducerInventory.EfOperatorVerdict(key, name);

                output.WriteLine("OPERATOR " + key + "." + name + " " + verdict);

                Assert.True(verdict is "Builder" or "Terminal" or "Refused", key + "." + name + " has no reviewed verdict (" + verdict + ").");
            }
        }
    }

    [Theory]
    [InlineData("System.Linq.Enumerable", "Where", "Builder")]
    [InlineData("System.Linq.Enumerable", "OrderBy", "Builder")]
    [InlineData("System.Linq.Enumerable", "CountBy", "Builder")]
    [InlineData("System.Linq.Enumerable", "LeftJoin", "Builder")]
    [InlineData("System.Linq.Enumerable", "ToList", "Terminal")]
    [InlineData("System.Linq.Enumerable", "First", "Terminal")]
    [InlineData("System.Linq.Enumerable", "Count", "Terminal")]
    [InlineData("System.Linq.Enumerable", "Repeat", "Refused")]
    [InlineData("System.Linq.Enumerable", "TryGetNonEnumeratedCount", "Refused")]
    [InlineData("System.Linq.Queryable", "Where", "Builder")]
    [InlineData("System.Linq.Queryable", "First", "Terminal")]
    [InlineData("System.Linq.AsyncEnumerable", "Where", "Builder")]
    [InlineData("System.Linq.AsyncEnumerable", "ToListAsync", "Terminal")]
    [InlineData("System.Threading.Tasks.TaskAsyncEnumerableExtensions", "ConfigureAwait", "Builder")]
    [InlineData("System.Threading.Tasks.TaskAsyncEnumerableExtensions", "WithCancellation", "Builder")]
    [InlineData("System.Threading.Tasks.TaskAsyncEnumerableExtensions", "ToBlockingEnumerable", "Refused")]
    [InlineData("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "Include", "Builder")]
    [InlineData("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "AsAsyncEnumerable", "Builder")]
    [InlineData("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ToListAsync", "Terminal")]
    [InlineData("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteDelete", "Terminal")]
    [InlineData("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ToQueryString", "Refused")]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalQueryableExtensions", "FromSqlRaw", "Builder")]
    [InlineData("Microsoft.EntityFrameworkCore.RelationalQueryableExtensions", "CreateDbCommand", "Refused")]
    [InlineData("System.Linq.Enumerable", "ThisOperatorDoesNotExist", "Unknown")]
    [InlineData("System.Linq.ParallelEnumerable", "ToList", "Unknown")]
    public void TheVerdictOfAnOperatorIsExactAndNeverGuessed(string key, string name, string verdict)
    {
        Assert.Equal(verdict, HostedGrimoireProducerInventory.EfOperatorVerdict(key, name));
    }
}
