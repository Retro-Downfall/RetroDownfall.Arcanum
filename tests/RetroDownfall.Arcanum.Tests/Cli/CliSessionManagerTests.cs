using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class CliSessionManagerTests : IDisposable
{

    private readonly string _sessionPath;

    private readonly string? _backupContents;

    private readonly bool _hadBackup;

    public CliSessionManagerTests()
    {

        _sessionPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "cli-session.txt");

        _hadBackup = File.Exists(_sessionPath);

        _backupContents = _hadBackup ? File.ReadAllText(_sessionPath) : null;

        if (File.Exists(_sessionPath))
        {
            File.Delete(_sessionPath);
        }

    }

    public void Dispose()
    {

        if (File.Exists(_sessionPath))
        {
            File.Delete(_sessionPath);
        }

        if (_hadBackup && _backupContents is not null)
        {
            Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

            File.WriteAllText(_sessionPath, _backupContents);
        }

    }

    [Fact]
    public void GetLastSessionId_returns_null_when_file_missing()
    {

        CliSessionManager manager = CreateManager();

        Guid? id = manager.GetLastSessionId();

        Assert.Null(id);

    }

    [Fact]
    public void SaveSessionId_round_trips_guid()
    {

        CliSessionManager manager = CreateManager();

        Guid expected = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        manager.SaveSessionId(expected);

        Guid? actual = manager.GetLastSessionId();

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void ClearSession_removes_persisted_id()
    {

        CliSessionManager manager = CreateManager();

        manager.SaveSessionId(Guid.NewGuid());

        manager.ClearSession();

        Assert.False(File.Exists(_sessionPath));

        Assert.Null(manager.GetLastSessionId());

    }

    [Fact]
    public void GetLastSessionId_warns_once_on_corrupt_file()
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(_sessionPath, "not-a-guid");

        TestConsole console = new();

        IAnsiConsole prior = AnsiConsole.Console;

        AnsiConsole.Console = console;

        try
        {
            CliSessionManager manager = CreateManager();

            Assert.Null(manager.GetLastSessionId());

            Assert.Null(manager.GetLastSessionId());

            Assert.Contains("cli-session.txt", console.Output);

            Assert.Contains("valid session id", console.Output);
        }
        finally
        {
            AnsiConsole.Console = prior;
        }

    }

    private static CliSessionManager CreateManager()
    {

        ThemeSemanticColors semantic = new();

        ThemeSemanticColors fallback = new();

        ConfiguredThemePalette palette = new(semantic, fallback);

        return new CliSessionManager(palette);

    }

}
