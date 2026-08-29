namespace RetroDownfall.Arcanum.Tests.Collections;

/// <summary>
/// For tests whose subject is the state of the process-wide SQLite connection pool.
/// </summary>
/// <remarks>
/// <c>SqliteConnection.ClearAllPools()</c> is global to the process, not scoped to a connection
/// string, so a drain running in one test empties the pool another test is asserting about. A test
/// that measures what the pool is holding therefore cannot run beside anything that drains, and the
/// symptom is a failure that appears only under a wide filter and never when the class runs alone.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteConnectionPoolCollection
{

    public const string Name = "SqliteConnectionPool";

}
