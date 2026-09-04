using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class TempFkDefaultProbeTests
{

    [Fact]
    public void Probe_default_foreign_keys_on_a_raw_open()
    {

        SqliteNativeRuntime.Instance.Initialize();

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");

        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());

        connection.Open();

        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA foreign_keys;";

        object? value = command.ExecuteScalar();

        connection.Close();

        SqliteConnection.ClearPool(connection);

        File.Delete(path);

        Assert.Equal("PROBE", "PROBE:foreign_keys=" + value);

    }

}
