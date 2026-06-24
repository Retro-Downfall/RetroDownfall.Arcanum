using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationBootstrapperTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_missing_file_does_not_throw()
    {

        string path = Path.Combine(_workspace.Root, "missing-arcanum.json");

        ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path);

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_valid_json_does_not_throw()
    {

        string path = Path.Combine(_workspace.Root, "valid-arcanum.json");

        File.WriteAllText(path, """{"providers":[]}""");

        ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path);

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_invalid_json_throws()
    {

        string path = Path.Combine(_workspace.Root, "invalid-arcanum.json");

        File.WriteAllText(path, "{not-json");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("arcanum.json is invalid", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_null_root_throws()
    {

        string path = Path.Combine(_workspace.Root, "null-root-arcanum.json");

        File.WriteAllText(path, "null");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("arcanum.json is invalid", ex.Message, StringComparison.Ordinal);

    }

}
