using System.Reflection;
using RetroDownfall.Arcanum.Infrastructure.Data;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// Provider selection happens once per process and is then frozen. Anything that can still swap the
/// SQLite provider after the Grimoire has opened can substitute an unencrypted engine underneath a
/// database Arcanum believes is encrypted, so freezing is a security property rather than tidiness.
/// </summary>
public sealed class SqliteNativeRuntimeTests
{

    [Fact]
    public void Initialize_selects_e_sqlcipher_and_is_idempotent()
    {

        SqliteNativeRuntime.Instance.Initialize();

        SqliteNativeRuntime.Instance.Initialize();

        Assert.Equal("3.53.3", raw.sqlite3_libversion().utf8_to_string());

    }

    /// <summary>
    /// After freezing, a replacement attempt must not take effect.
    /// </summary>
    /// <remarks>
    /// SQLitePCLRaw ignores <c>SetProvider</c> on a frozen runtime rather than throwing, and exposes
    /// neither the installed provider nor the frozen flag publicly, so the state is read
    /// reflectively. If a package upgrade renames the field this fails loudly, which is correct: the
    /// freeze is a security property and silently losing the ability to observe it is worse than a
    /// broken test.
    /// </remarks>
    [Fact]
    public void Initialize_freezes_provider_against_replacement()
    {

        SqliteNativeRuntime.Instance.Initialize();

        FieldInfo frozen = typeof(raw).GetField("_frozen", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "SQLitePCLRaw no longer exposes a '_frozen' field. Re-verify how provider freezing "
                + "works in the upgraded package and update SqliteNativeRuntime accordingly.");

        Assert.True(
            (bool)frozen.GetValue(null)!,
            "The SQLite provider must be frozen once selected, so no later component can swap the "
            + "engine underneath an already-open encrypted database.");

        raw.SetProvider(new SQLite3Provider_e_sqlcipher());

        Assert.Equal("3.53.3", raw.sqlite3_libversion().utf8_to_string());

    }

    /// <summary>
    /// The whole point of the hermetic delivery is that exactly one library can satisfy the load.
    /// If it is missing, the failure has to say so without printing loader paths, which leak the
    /// installation layout into logs an operator may share.
    /// </summary>
    [Fact]
    public void Initialize_failure_does_not_search_or_disclose_paths()
    {

        SqliteNativeRuntimeUnavailableException exception = new(
            "osx-arm64",
            "libe_sqlcipher.dylib",
            new InvalidOperationException("inner detail with /Users/someone/secret/path"));

        Assert.Contains("osx-arm64", exception.Message, StringComparison.Ordinal);

        Assert.Contains("libe_sqlcipher.dylib", exception.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("/Users/", exception.Message, StringComparison.Ordinal);

        Assert.Equal("Grimoire.NativeRuntimeUnavailable", exception.ErrorCode);

    }

    [Fact]
    public void Initialized_runtime_reports_the_pinned_cipher_version()
    {

        SqliteNativeRuntime.Instance.Initialize();

        using Microsoft.Data.Sqlite.SqliteConnection connection = new("Data Source=:memory:");

        connection.Open();

        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA cipher_version;";

        Assert.Equal("4.17.0 community", Convert.ToString(command.ExecuteScalar()));

    }

}
